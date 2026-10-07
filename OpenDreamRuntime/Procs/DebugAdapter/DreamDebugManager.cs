using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using DMCompiler.Bytecode;
using DMCompiler.Json;
using OpenDreamRuntime.Objects;
using OpenDreamRuntime.Objects.Types;
using OpenDreamRuntime.Procs.DebugAdapter.Protocol;
using OpenDreamRuntime.Resources;
using Robust.Server;
using Robust.Shared.Network;
using Thread = System.Threading.Thread;

namespace OpenDreamRuntime.Procs.DebugAdapter;

internal sealed partial class DreamDebugManager : IDreamDebugManager {
    public enum StepMode {
        StepOver, // aka "next"
        StepIn,
        StepOut
    }

    // Breakpoint storage
    private const string ExceptionFilterRuntimes = "runtimes";

    private static readonly DisassembledInstruction LowInstruction = new() {Address = "0x1", Instruction = ""};

    private static readonly DisassembledInstruction HighInstruction = new()
        {Address = "0xffffffffffffffff", Instruction = ""};

    private readonly Dictionary<int, DMProc> _disassemblyProcs = new();

    private readonly Dictionary<int, WeakReference<ProcState>> _stackFramesById = new();
    private readonly Dictionary<int, Func<RequestVariables, IEnumerable<Variable>>> _variableReferences = new();

    // Setup
    private DebugAdapter? _adapter;
    private bool _breakOnRuntimes = true;

    private int _breakpointIdCounter = 1;
    [Robust.Shared.IoC.Dependency] private DreamManager _dreamManager = default!;

    // Temporary data for a given Stop
    private Exception? _exception;
    [Robust.Shared.IoC.Dependency] private DreamObjectTree _objectTree = default!;

    private Dictionary<string, Dictionary<int, FileBreakpointSlot>>? _possibleBreakpoints;
    private Dictionary<(string Type, string Proc), FunctionBreakpointSlot>? _possibleFunctionBreakpoints;
    [Robust.Shared.IoC.Dependency] private ProcScheduler _procScheduler = default!;
    [Robust.Shared.IoC.Dependency] private DreamResourceManager _resourceManager = default!;

    private ISawmill _sawmill = default!;
    [Robust.Shared.IoC.Dependency] private IBaseServer _server = default!;
    private bool _stopOnEntry;

    // State
    private bool _stopped;
    private bool _terminated;

    private int _variablesIdCounter;
    private string RootPath => _resourceManager.RootPath ?? throw new Exception("No RootPath yet!");

    // Lifecycle
    public void Initialize(int port) {
        _sawmill = Logger.GetSawmill("opendream.debugger");
        _adapter = new DebugAdapter();

        _adapter.OnClientConnected += OnClientConnected;
        _adapter.ConnectOut(port: port);
    }

    public void Update() {
        _adapter?.HandleMessages();
        if (!CanStop()) _stopped = false;
    }

    public void Shutdown() {
        _breakpointIdCounter = 0;
        _adapter?.Shutdown();
    }

    // Callbacks from the runtime
    public void HandleOutput(LogLevel logLevel, string message) {
        string category = logLevel switch {
            LogLevel.Fatal or LogLevel.Error => OutputEvent.CategoryStderr,
            _ => OutputEvent.CategoryStdout
        };

        Output(message, category);
    }

    public void HandleFirstResume(DMProcState state) {
        if (_stopOnEntry) {
            _stopOnEntry = false;
            Stop(state.Thread, new StoppedEvent {
                Reason = StoppedEvent.ReasonEntry
            });
            return;
        }

        if (_possibleFunctionBreakpoints == null)
            return;

        // Check for a function breakpoint
        List<int>? hit = null;
        if (_possibleFunctionBreakpoints.TryGetValue((state.Proc.OwningType.Path, state.Proc.Name),
                out FunctionBreakpointSlot? slot))
            foreach (ActiveBreakpoint bp in slot.Breakpoints)
                if (TestBreakpoint(bp)) {
                    hit ??= new List<int>(1);
                    hit.Add(bp.Id);
                }

        if (hit != null) {
            Output($"Function breakpoint hit at {state.Proc.OwningType.Path}::{state.Proc.Name}");
            Stop(state.Thread, new StoppedEvent {
                Reason = StoppedEvent.ReasonFunctionBreakpoint,
                HitBreakpointIds = hit
            });
        }
    }

    public void HandleInstruction(DMProcState state) {
        if (state.Thread.StepMode == null)
            return;

        var stoppedOnStep = false;

        switch (state.Thread.StepMode.Value.Granularity) {
            case SteppingGranularity.Instruction:
                switch (state.Thread.StepMode) {
                    case {Mode: StepMode.StepIn}:
                        stoppedOnStep = true;
                        break;
                    case {Mode: StepMode.StepOut, FrameId: var whenNotInStack}:
                        stoppedOnStep = !state.Thread.InspectStack().Select(p => p.Id).Contains(whenNotInStack);
                        break;
                    case {Mode: StepMode.StepOver, FrameId: var whenTop}:
                        stoppedOnStep = state.Id == whenTop ||
                                        !state.Thread.InspectStack().Select(p => p.Id).Contains(whenTop);
                        break;
                }

                break;
            case null:
            case SteppingGranularity.Line:
            case SteppingGranularity.Statement:
                if (!state.Proc.IsOnLineChange(state.ProgramCounter))
                    return;

                switch (state.Thread.StepMode) {
                    case {Mode: StepMode.StepIn}:
                        stoppedOnStep = true;
                        break;
                    case {Mode: StepMode.StepOut, FrameId: var whenNotInStack}:
                        stoppedOnStep = whenNotInStack == -1 ||
                                        !state.Thread.InspectStack().Select(p => p.Id).Contains(whenNotInStack);
                        break;
                    case {Mode: StepMode.StepOver, FrameId: var whenTop}:
                        stoppedOnStep = state.Id == whenTop || whenTop == -1 ||
                                        !state.Thread.InspectStack().Select(p => p.Id).Contains(whenTop);
                        break;
                }

                break;
        }

        if (stoppedOnStep) {
            state.Thread.StepMode = null;
            Stop(state.Thread, new StoppedEvent {
                Reason = StoppedEvent.ReasonStep
            });
        }
    }

    public ProcStatus HandleBreakpoint(DMProcState state) {
        // Subtract 1 because it was advanced before running the opcode
        (string Source, int Line) sourceLocation = state.Proc.GetSourceAtOffset(state.ProgramCounter - 1);

        if (_possibleBreakpoints == null)
            throw new Exception("Breakpoints not initialized");
        if (!_possibleBreakpoints.TryGetValue(sourceLocation.Source, out Dictionary<int, FileBreakpointSlot>? slots) ||
            !slots.TryGetValue(sourceLocation.Line, out FileBreakpointSlot? slot))
            throw new Exception($"No breakpoint slot at {sourceLocation.Source}:{sourceLocation.Line}");
        if (slot.Breakpoints.Count != 1)
            throw new Exception(
                $"Either no breakpoints or more than 1 breakpoint at {sourceLocation.Source}:{sourceLocation.Line}");

        ActiveBreakpoint breakpoint = slot.Breakpoints[0];

        if (TestBreakpoint(breakpoint)) {
            Output($"Breakpoint hit at {sourceLocation.Source}:{sourceLocation.Line}");
            Stop(state.Thread, new StoppedEvent {
                Reason = StoppedEvent.ReasonBreakpoint,
                HitBreakpointIds = new[] {breakpoint.Id}
            });
        }

        // Execute the original opcode
        unsafe {
            return DMProcState.OpcodeHandlersTable[breakpoint.OriginalOpcode](state);
        }
    }

    public void HandleException(DreamThread thread, Exception exception) {
        if (_breakOnRuntimes) {
            _exception = exception;
            Output("Stopped on exception");
            Stop(thread, new StoppedEvent {
                Reason = StoppedEvent.ReasonException
            });
        }
    }

    private int AllocVariableRef(Func<RequestVariables, IEnumerable<Variable>> func) {
        int id = ++_variablesIdCounter;
        _variableReferences[id] = func;
        return id;
    }

    private bool TestBreakpoint(ActiveBreakpoint bp) {
        return bp.Condition is null && bp.HitCondition is null;
    }

    // Utilities
    private void Output(string message, string category = OutputEvent.CategoryConsole) {
        _adapter?.SendAll(new OutputEvent(category, $"{message}\n"));
    }

    private bool CanStop() {
        return _adapter != null && _adapter.AnyClientsConnected() && !_terminated;
    }

    private void Stop(DreamThread? thread, StoppedEvent stoppedEvent) {
        if (_adapter == null || !CanStop())
            return;

        stoppedEvent.ThreadId = thread?.Id;
        stoppedEvent.AllThreadsStopped = true;
        _adapter.SendAll(stoppedEvent);

        _stopped = true;

        // We have to block the DM runtime no matter where we are in its call
        // stack. Unfortunately this also blocks the Robust engine.
        while (_stopped) {
            Update();
            Thread.Sleep(50);
        }
    }

    private void Resume() {
        _stopped = false;
        _stackFramesById.Clear();
        _variableReferences.Clear();
    }

    // DAP request handlers
    private void OnClientConnected(DebugAdapterClient client) {
        client.OnRequest += OnRequest;
    }

    private void OnRequest(DebugAdapterClient client, Request req) {
        // TODO: try/catch here?
        switch (req) {
            case RequestInitialize reqInit:
                HandleRequestInitialize(client, reqInit);
                break;
            case RequestLaunch reqLaunch:
                HandleRequestLaunch(client, reqLaunch);
                break;
            case RequestDisconnect reqDisconnect:
                HandleRequestDisconnect(client, reqDisconnect);
                break;
            case RequestSetBreakpoints reqSetBreakpoints:
                HandleRequestSetBreakpoints(client, reqSetBreakpoints);
                break;
            case RequestSetFunctionBreakpoints reqFuncBreakpoints:
                HandleRequestSetFunctionBreakpoints(client, reqFuncBreakpoints);
                break;
            case RequestSetExceptionBreakpoints requestSetExceptionBreakpoints:
                HandleRequestSetExceptionBreakpoints(client, requestSetExceptionBreakpoints);
                break;
            case RequestConfigurationDone reqConfigDone:
                HandleRequestConfigurationDone(client, reqConfigDone);
                break;
            case RequestThreads reqThreads:
                HandleRequestThreads(client, reqThreads);
                break;
            case RequestContinue reqContinue:
                HandleRequestContinue(client, reqContinue);
                break;
            case RequestPause reqPause:
                HandleRequestPause(client, reqPause);
                break;
            case RequestStackTrace requestStackTrace:
                HandleRequestStackTrace(client, requestStackTrace);
                break;
            case RequestScopes requestScopes:
                HandleRequestScopes(client, requestScopes);
                break;
            case RequestVariables requestVariables:
                HandleRequestVariables(client, requestVariables);
                break;
            case RequestExceptionInfo requestExceptionInfo:
                HandleRequestExceptionInfo(client, requestExceptionInfo);
                break;
            case RequestStepIn requestStepIn:
                HandleRequestStepIn(client, requestStepIn);
                break;
            case RequestNext requestNext:
                HandleRequestNext(client, requestNext);
                break;
            case RequestStepOut requestStepOut:
                HandleRequestStepOut(client, requestStepOut);
                break;
            case RequestDisassemble requestDisassemble:
                HandleRequestDisassemble(client, requestDisassemble);
                break;
            case RequestHotReloadInterface requestHotReloadInterface:
                HandleRequestHotReloadInterface(client, requestHotReloadInterface);
                break;
            case RequestHotReloadResource requestHotReloadResource:
                HandleRequestHotReloadResource(client, requestHotReloadResource);
                break;
            default:
                req.RespondError(client, $"Unknown request \"{req.Command}\"");
                break;
        }
    }

    private void HandleRequestInitialize(DebugAdapterClient client, RequestInitialize reqInit) {
        RequestInitialize.RequestInitializeArguments args = reqInit.Arguments;

        // Verify this is an OpenDream adapter
        if (args.AdapterId != "opendream") {
            reqInit.RespondError(client, "Expected an adapter id of \"opendream\"");
            client.Close();
            return;
        }

        reqInit.Respond(client, new Capabilities {
            SupportsConfigurationDoneRequest = true,
            SupportsFunctionBreakpoints = true,
            SupportsExceptionInfoRequest = true,
            ExceptionBreakpointFilters = new[] {
                new ExceptionBreakpointsFilter(ExceptionFilterRuntimes, "Runtime errors") {Default = true}
            },
            SupportsDisassembleRequest = true,
            SupportsSteppingGranularity = true
        });
        // ... opportunity to do stuff that might take time here if needed ...
        client.SendMessage(new InitializedEvent());
    }

    private void HandleRequestLaunch(DebugAdapterClient client, RequestLaunch reqLaunch) {
        if (reqLaunch.Arguments.JsonPath == null) {
            reqLaunch.RespondError(client, "No json_path was given");
            client.Close();
            return;
        }

        _dreamManager.PreInitialize(reqLaunch.Arguments.JsonPath);
        InitializePossibleBreakpoints();
        _possibleFunctionBreakpoints = IterateProcs()
            .Distinct()
            .ToDictionary(t => t, _ => new FunctionBreakpointSlot());

        _stopOnEntry = reqLaunch.Arguments.StopOnEntry is true;
        reqLaunch.Respond(client);
    }

    private void InitializePossibleBreakpoints() {
        _possibleBreakpoints = new Dictionary<string, Dictionary<int, FileBreakpointSlot>>();

        foreach (DMProc proc in _objectTree.Procs.Concat(new[] {_objectTree.GlobalInitProc}).OfType<DMProc>()) {
            string? source = null;
            foreach (SourceInfoJson sourceInfo in proc.SourceInfo) {
                if (sourceInfo.File != null)
                    source = _objectTree.Strings[sourceInfo.File.Value];
                if (source == null)
                    continue;

                if (!_possibleBreakpoints.TryGetValue(source, out Dictionary<int, FileBreakpointSlot>? slots)) {
                    slots = new Dictionary<int, FileBreakpointSlot>();
                    _possibleBreakpoints.Add(source, slots);
                }

                // TryAdd() because multiple procs can be defined on one line
                slots.TryAdd(sourceInfo.Line, new FileBreakpointSlot(proc));
            }
        }
    }

    private IEnumerable<(string Type, string Proc)> IterateProcs() {
        foreach (DreamProc proc in _objectTree.Procs) yield return (proc.OwningType.Path, proc.Name);
    }

    private void HandleRequestConfigurationDone(DebugAdapterClient client, RequestConfigurationDone reqConfigDone) {
        _dreamManager.StartWorld();
        reqConfigDone.Respond(client);
        if (!_terminated) client.SendMessage(new ODReadyEvent(IoCManager.Resolve<IServerNetManager>().Port));
    }

    private void HandleRequestDisconnect(DebugAdapterClient client, RequestDisconnect reqDisconnect) {
        // TODO: Don't terminate if launch type was "attach"
        reqDisconnect.Respond(client);
        _server.Shutdown("A shutdown was initiated by the debug adapter");
        _terminated = true;
        _stopped = false;
    }

    private void HandleRequestSetBreakpoints(DebugAdapterClient client, RequestSetBreakpoints reqSetBreakpoints) {
        Source source = reqSetBreakpoints.Arguments.Source;
        string? sourcePath = source.Path ?? source.Name;
        if (sourcePath == null) {
            reqSetBreakpoints.RespondError(client, "A breakpoint source was not given");
            return;
        }

        SourceBreakpoint[]? setBreakpoints = reqSetBreakpoints.Arguments.Breakpoints;
        var responseBreakpoints = new Breakpoint[setBreakpoints?.Length ?? 0];

        sourcePath = Path.GetRelativePath(RootPath, sourcePath).Replace("\\", "/");
        if (_possibleBreakpoints is null ||
            !_possibleBreakpoints.TryGetValue(sourcePath, out Dictionary<int, FileBreakpointSlot>? fileSlots)) {
            // File isn't known - every breakpoint is invalid.
            for (var i = 0; i < responseBreakpoints.Length; ++i)
                responseBreakpoints[i] = new Breakpoint($"Unknown file \"{sourcePath}\"");

            reqSetBreakpoints.Respond(client, responseBreakpoints);
            return;
        }

        // Remove all the current breakpoints
        foreach (FileBreakpointSlot slot in fileSlots.Values) slot.ClearBreakpoints();

        // We've unset old breakpoints, so set new ones if needed.
        if (setBreakpoints != null)
            for (var i = 0; i < setBreakpoints.Length; i++) {
                SourceBreakpoint setBreakpoint = setBreakpoints[i];
                if (fileSlots.TryGetValue(setBreakpoint.Line, out FileBreakpointSlot? slot) &&
                    slot.Proc.TryGetOffsetAtSource(sourcePath, setBreakpoint.Line, out int offset)) {
                    int id = ++_breakpointIdCounter;

                    slot.AddBreakpoint(offset, new ActiveBreakpoint {
                        Id = id,
                        Condition = setBreakpoint.Condition,
                        HitCondition = setBreakpoint.HitCondition,
                        LogMessage = setBreakpoint.LogMessage
                    });

                    responseBreakpoints[i] = new Breakpoint(id, source, setBreakpoint.Line);
                } else {
                    responseBreakpoints[i] = new Breakpoint($"No code on line {setBreakpoint.Line}")
                        {Line = setBreakpoint.Line};
                }
            }

        reqSetBreakpoints.Respond(client, responseBreakpoints);
    }

    private void HandleRequestSetFunctionBreakpoints(DebugAdapterClient client,
        RequestSetFunctionBreakpoints reqFuncBreakpoints) {
        FunctionBreakpoint[] input = reqFuncBreakpoints.Arguments.Breakpoints;
        var output = new Breakpoint[input.Length];

        foreach (FunctionBreakpointSlot v in _possibleFunctionBreakpoints!.Values) v.Breakpoints.Clear();

        for (var i = 0; i < input.Length; ++i) {
            FunctionBreakpoint bp = input[i];

            string name = bp.Name.Replace("/proc/", "/").Replace("/verb/", "/");
            int last = name.LastIndexOf('/');
            string type = name[..last];
            string proc = name[(last + 1)..];
            if (type == "") type = "/";

            if (_possibleFunctionBreakpoints.GetValueOrDefault((type, proc)) is FunctionBreakpointSlot slot) {
                int id = ++_breakpointIdCounter;
                output[i] = new Breakpoint(id, true);
                slot.Breakpoints.Add(new ActiveBreakpoint {
                    Id = id,
                    Condition = bp.Condition,
                    HitCondition = bp.HitCondition
                });
            } else {
                output[i] = new Breakpoint($"No proc {type}::{proc}");
            }
        }

        reqFuncBreakpoints.Respond(client, output);
    }

    private void HandleRequestSetExceptionBreakpoints(DebugAdapterClient client,
        RequestSetExceptionBreakpoints requestSetExceptionBreakpoints) {
        _breakOnRuntimes = requestSetExceptionBreakpoints.Arguments.Filters.Contains(ExceptionFilterRuntimes);
        requestSetExceptionBreakpoints.Respond(client, null);
    }

    private IEnumerable<DreamThread> InspectThreads() {
        return DreamThread.InspectExecutingThreads().Concat(_procScheduler.InspectThreads());
    }

    private void HandleRequestThreads(DebugAdapterClient client, RequestThreads reqThreads) {
        var threads = new List<Protocol.Thread>();
        foreach (DreamThread thread in InspectThreads().Distinct())
            threads.Add(new Protocol.Thread(thread.Id, thread.Name));

        if (!threads.Any()) threads.Add(new Protocol.Thread(0, "Nothing"));

        reqThreads.Respond(client, threads);
    }

    private void HandleRequestPause(DebugAdapterClient client, RequestPause reqPause) {
        // "The debug adapter first sends the response and then a stopped event (with reason pause) after the thread has been paused successfully."
        reqPause.Respond(client);
        Stop(null, new StoppedEvent {
            Reason = StoppedEvent.ReasonPause,
            Description = "Paused by request"
        });
    }

    private void HandleRequestContinue(DebugAdapterClient client, RequestContinue reqContinue) {
        Resume();
        reqContinue.Respond(client, true);
    }

    private void HandleRequestStepIn(DebugAdapterClient client, RequestStepIn requestStepIn) {
        DreamThread thread = InspectThreads().First(t => t.Id == requestStepIn.Arguments.ThreadId);
        thread.StepMode = new ThreadStepMode {
            Mode = StepMode.StepIn,
            Granularity = requestStepIn.Arguments.Granularity
        };
        Resume();
        requestStepIn.Respond(client);
    }

    private void HandleRequestNext(DebugAdapterClient client, RequestNext requestNext) {
        DreamThread thread = InspectThreads().First(t => t.Id == requestNext.Arguments.ThreadId);
        thread.StepMode = new ThreadStepMode {
            Mode = StepMode.StepOver,
            FrameId = thread.InspectStack().FirstOrDefault()?.Id ?? -1,
            Granularity = requestNext.Arguments.Granularity
        };
        Resume();
        requestNext.Respond(client);
    }

    private void HandleRequestStepOut(DebugAdapterClient client, RequestStepOut requestStepOut) {
        DreamThread thread = InspectThreads().First(t => t.Id == requestStepOut.Arguments.ThreadId);
        thread.StepMode = new ThreadStepMode {
            Mode = StepMode.StepOut,
            FrameId = thread.InspectStack().FirstOrDefault()?.Id ?? -1,
            Granularity = requestStepOut.Arguments.Granularity
        };
        Resume();
        requestStepOut.Respond(client);
    }

    private void HandleRequestExceptionInfo(DebugAdapterClient client, RequestExceptionInfo requestExceptionInfo) {
        if (_exception is null) {
            requestExceptionInfo.RespondError(client, "No exception");
            return;
        }

        // VSC shows exceptionId, description, stackTrace in that order.
        requestExceptionInfo.Respond(client, new RequestExceptionInfo.ExceptionInfoResponse {
            ExceptionId = _exception.Message,
            BreakMode = RequestExceptionInfo.ExceptionInfoResponse.BreakModeAlways
        });
    }

    private void HandleRequestStackTrace(DebugAdapterClient client, RequestStackTrace reqStackTrace) {
        DreamThread? thread = InspectThreads().FirstOrDefault(t => t.Id == reqStackTrace.Arguments.ThreadId);
        if (thread is null) {
            reqStackTrace.RespondError(client, $"No thread with ID {reqStackTrace.Arguments.ThreadId}");
            return;
        }

        var output = new List<StackFrame>();
        var nameBuilder = new StringBuilder();
        foreach (ProcState frame in thread.InspectStack()) {
            nameBuilder.Clear();
            frame.AppendStackFrame(nameBuilder);

            var outputFrame = new StackFrame {
                Id = frame.Id,
                Name = nameBuilder.ToString()
            };

            if (frame is DMProcState dm) {
                (string Source, int Line) sourceInfo = dm.Proc.GetSourceAtOffset(dm.ProgramCounter);

                outputFrame.InstructionPointerReference = EncodeInstructionPointer(dm.Proc, dm.ProgramCounter);
                outputFrame.Source = TranslateSource(sourceInfo.Source);
                outputFrame.Line = sourceInfo.Line;
            }

            output.Add(outputFrame);
            _stackFramesById[frame.Id] = new WeakReference<ProcState>(frame);
        }

        reqStackTrace.Respond(client, output, output.Count);
    }

    private Source? TranslateSource(string? source) {
        if (source is null)
            return null;

        string fName = Path.GetFileName(source);
        if (Path.IsPathRooted(source)) return new Source(fName, source);

        return new Source(fName, Path.Join(RootPath, source));
    }

    private void HandleRequestScopes(DebugAdapterClient client, RequestScopes requestScopes) {
        if (!_stackFramesById.TryGetValue(requestScopes.Arguments.FrameId, out WeakReference<ProcState>? weak) ||
            !weak.TryGetTarget(out ProcState? frame)) {
            requestScopes.RespondError(client, $"No frame with ID {requestScopes.Arguments.FrameId}");
            return;
        }

        if (frame is not DMProcState dmFrame) {
            requestScopes.RespondError(client, "Cannot inspect native frame");
            return;
        }

        var scopes = new List<Scope>(4);
        ReadOnlyMemory<DreamValue> stack = dmFrame.DebugStack();
        if (stack.Length > 0)
            // Only show the Stack as a scope if there is anything worth showing,
            // which will usually only be when stepping by instruction.
            scopes.Add(new Scope {
                Name = "Stack",
                VariablesReference = AllocVariableRef(req => ExpandStack(req, stack)),
                PresentationHint = Scope.PresentationHintRegisters
            });

        scopes.Add(new Scope {
            Name = "Arguments",
            PresentationHint = Scope.PresentationHintArguments,
            VariablesReference = AllocVariableRef(req => ExpandArguments(req, dmFrame))
        });
        scopes.Add(new Scope {
            Name = "Locals",
            PresentationHint = Scope.PresentationHintLocals,
            VariablesReference = AllocVariableRef(req => ExpandLocals(req, dmFrame))
        });
        scopes.Add(new Scope {
            Name = "Globals",
            VariablesReference = AllocVariableRef(req => ExpandGlobals(req))
        });

        requestScopes.Respond(client, scopes);
    }

    private IEnumerable<Variable> ExpandArguments(RequestVariables req, DMProcState dmFrame) {
        if (dmFrame.Proc.OwningType != _objectTree.Root)
            yield return DescribeValue("src", new DreamValue(dmFrame.Instance));

        yield return DescribeValue("usr", new DreamValue(dmFrame.Usr));

        foreach ((string name, DreamValue value) in dmFrame.DebugArguments()) yield return DescribeValue(name, value);
    }

    private IEnumerable<Variable> ExpandLocals(RequestVariables req, DMProcState dmFrame) {
        foreach ((string name, DreamValue value) in dmFrame.DebugLocals()) yield return DescribeValue(name, value);
    }

    private IEnumerable<Variable> ExpandGlobals(RequestVariables req) {
        foreach ((string name, DreamValue value) in _dreamManager.GlobalNames.Zip(_dreamManager.Globals)
                     .OrderBy(kv => kv.First)) yield return DescribeValue(name, value);
    }

    private IEnumerable<Variable> ExpandStack(RequestVariables req, ReadOnlyMemory<DreamValue> stack) {
        for (int i = stack.Length - 1; i >= 0; --i) yield return DescribeValue($"{i}", stack.Span[i]);
    }

    private Variable DescribeValue(string name, DreamValue value) {
        var varDesc = new Variable {Name = name, Value = value.ToString()};

        if (value.TryGetValueAsDreamList(out DreamList? list)) {
            if (list.GetLength() > 0) {
                varDesc.VariablesReference = AllocVariableRef(req => ExpandList(req, list));
                varDesc.IndexedVariables = list.GetLength() * (list.IsAssociative ? 2 : 1);
            }
        } else if (value.TryGetValueAsDreamObject(out DreamObject? obj) && obj != null) {
            varDesc.VariablesReference = AllocVariableRef(req => ExpandObject(req, obj));
            varDesc.NamedVariables = obj.ObjectDefinition?.Variables.Count;
        }

        return varDesc;
    }

    private IEnumerable<Variable> ExpandList(RequestVariables req, DreamList list) {
        if (list.IsAssociative) {
            Dictionary<DreamValue, DreamValue> assoc = list.GetAssociativeValues();
            foreach ((int i, DreamValue key) in list.EnumerateValues().Select((v, i) => (i + 1, v))
                         .Skip(req.Arguments.Start ?? 0).Take((req.Arguments.Count ?? int.MaxValue) / 2)) {
                assoc.TryGetValue(key, out DreamValue value);
                yield return DescribeValue($"keys[{i}]", key);
                yield return DescribeValue($"vals[{i}]", value);
            }
        } else {
            foreach ((int i, DreamValue value) in list.EnumerateValues().Select((v, i) => (i + 1, v))
                         .Skip(req.Arguments.Start ?? 0)
                         .Take(req.Arguments.Count ?? int.MaxValue)) yield return DescribeValue($"[{i}]", value);
        }
    }

    private IEnumerable<Variable> ExpandObject(RequestVariables req, DreamObject obj) {
        foreach (string name in obj.GetVariableNames().OrderBy(k => k)) {
            Variable described;

            try {
                using DreamValue value = obj.GetVariable(name);

                described = DescribeValue(name, value);
            } catch (Exception ex) {
                _sawmill.Log(LogLevel.Error, ex, $"Error in GetVariable({name})");

                described = new Variable {
                    Name = name,
                    Value = $"<error: {ex.Message}>"
                };
            }

            yield return described;
        }
    }

    private void HandleRequestVariables(DebugAdapterClient client, RequestVariables requestVariables) {
        if (!_variableReferences.TryGetValue(requestVariables.Arguments.VariablesReference,
                out Func<RequestVariables, IEnumerable<Variable>>? varFunc)) {
            // When stepping quickly, we may receive such requests for old scopes we've already dropped.
            // Fail silently instead of loudly to avoid spamming error messages.
            requestVariables.Respond(client, Enumerable.Empty<Variable>());
            return;
        }

        requestVariables.Respond(client, varFunc(requestVariables));
    }

    private void HandleRequestDisassemble(DebugAdapterClient client, RequestDisassemble requestDisassemble) {
        (DMProc? proc, uint pc) = DecodeInstructionPointer(requestDisassemble.Arguments.MemoryReference);
        // If the user scrolled really far up/down, serve nothing forever.
        if (proc == null) {
            if (pc == 0xffffffff)
                requestDisassemble.Respond(client,
                    Enumerable.Repeat(HighInstruction, requestDisassemble.Arguments.InstructionCount));
            else
                requestDisassemble.Respond(client,
                    Enumerable.Repeat(LowInstruction, requestDisassemble.Arguments.InstructionCount));

            return;
        }

        // Just disassemble the whole function...
        List<DisassembledInstruction> output = new();
        DisassembledInstruction? previousInstruction = null;
        var previousOffset = 0;
        foreach ((int offset, ITuple instruction) in
                 new ProcDecoder(_objectTree.Strings, proc.Bytecode).Disassemble()) {
            /*if (previousInstruction != null) {
                previousInstruction.InstructionBytes = BitConverter.ToString(proc.Bytecode, previousOffset, offset - previousOffset).Replace("-", " ").ToLowerInvariant();
            }*/

            previousOffset = offset;
            previousInstruction = new DisassembledInstruction {
                Address = EncodeInstructionPointer(proc, offset),
                Instruction = ProcDecoder.Format(instruction, type => _objectTree.Types[type].Path.ToString())
            };

            (string Source, int Line) sourceInfo = proc.GetSourceAtOffset(previousOffset);
            previousInstruction.Location = TranslateSource(sourceInfo.Source);
            previousInstruction.Line = sourceInfo.Line;

            output.Add(previousInstruction);
        }

        /*if (previousInstruction != null) {
            previousInstruction.InstructionBytes = BitConverter.ToString(proc.Bytecode, previousOffset).Replace("-", " ").ToLowerInvariant();
        }*/

        // ... and THEN strip everything outside the requested range.
        int requestedPoint = output.FindIndex(di => di.Address == requestDisassemble.Arguments.MemoryReference);
        requestDisassemble.Respond(client,
            DisassemblySkipTake(output, requestedPoint, requestDisassemble.Arguments.InstructionOffset ?? 0,
                requestDisassemble.Arguments.InstructionCount));
    }

    private void HandleRequestHotReloadInterface(DebugAdapterClient client,
        RequestHotReloadInterface requestHotReloadInterface) {
        _sawmill.Debug("Debug adapter triggered interface hot reload");
        try {
            _dreamManager.HotReloadInterface();
            requestHotReloadInterface.Respond(client);
        } catch (Exception e) {
            requestHotReloadInterface.RespondError(client, e.Message);
        }
    }

    private void HandleRequestHotReloadResource(DebugAdapterClient client,
        RequestHotReloadResource requestHotReloadResource) {
        if (string.IsNullOrWhiteSpace(requestHotReloadResource.Arguments.FilePath)) {
            _sawmill.Error("Debug adapter requested a resource hot reload but didn't provide a file");
            requestHotReloadResource.RespondError(client, "No file provided for a hot reload");
            return;
        }

        _sawmill.Debug("Debug adapter triggered resource hot reload for " +
                       requestHotReloadResource.Arguments.FilePath);
        try {
            _dreamManager.HotReloadResource(requestHotReloadResource.Arguments.FilePath);
            requestHotReloadResource.Respond(client);
        } catch (Exception e) {
            requestHotReloadResource.RespondError(client, e.Message);
        }
    }

    private IEnumerable<DisassembledInstruction> DisassemblySkipTake(List<DisassembledInstruction> list, int midpoint,
        int offset, int count) {
        for (int i = midpoint + offset; i < midpoint + offset + count; ++i)
            if (i < 0)
                yield return LowInstruction;
            else if (i < list.Count)
                yield return list[i];
            else
                yield return HighInstruction;
    }

    private string EncodeInstructionPointer(DMProc proc, int pc) {
        // VSCode requires that the instruction pointer is parseable as a BigInt
        // so that it can use lt/gt/eq comparisons to avoid refetching memory.
        // Otherwise it will think they're all the same and never refetch.
        int procId = proc.GetHashCode();
        _disassemblyProcs[procId] = proc;
        ulong ip = ((ulong)(uint)procId << 32) | (uint)pc;
        return "0x" + ip.ToString("x");
    }

    private (DMProc? proc, uint pc) DecodeInstructionPointer(string ip) {
        ulong ip2 = ulong.Parse(ip[2..], NumberStyles.HexNumber);
        _disassemblyProcs.TryGetValue((int)((ip2 & 0xffffffff00000000) >> 32), out DMProc? proc);
        return (proc, (uint)(ip2 & 0xffffffff));
    }

    public struct ThreadStepMode {
        public StepMode Mode;
        public int FrameId;
        public string? Granularity;
    }

    private sealed class FileBreakpointSlot(DMProc proc) {
        public readonly DMProc Proc = proc;

        private readonly List<ActiveBreakpoint> _breakpoints = new();
        public IReadOnlyList<ActiveBreakpoint> Breakpoints => _breakpoints;

        public void ClearBreakpoints() {
            // Set all the opcodes back to their original
            foreach (ActiveBreakpoint breakpoint in _breakpoints)
                Proc.Bytecode[breakpoint.BytecodeOffset] = breakpoint.OriginalOpcode;

            _breakpoints.Clear();
        }

        public void AddBreakpoint(int offset, ActiveBreakpoint breakpoint) {
            breakpoint.BytecodeOffset = offset;
            breakpoint.OriginalOpcode = Proc.Bytecode[offset];
            _breakpoints.Add(breakpoint);

            // Replace the opcode with DebuggerBreakpoint so we know when we trip it
            Proc.Bytecode[offset] = (byte)DreamProcOpcode.DebuggerBreakpoint;
        }
    }

    private sealed class FunctionBreakpointSlot {
        public readonly List<ActiveBreakpoint> Breakpoints = new();
    }

    private struct ActiveBreakpoint {
        public int Id;
        public int BytecodeOffset;
        public byte OriginalOpcode;
        public string? Condition;
        public string? HitCondition;
        public string? LogMessage;
    }
}

public interface IDreamDebugManager {
    void Initialize(int port);
    void Update();
    void Shutdown();

    void HandleOutput(LogLevel logLevel, string message);
    void HandleFirstResume(DMProcState dMProcState);
    void HandleInstruction(DMProcState dMProcState);
    ProcStatus HandleBreakpoint(DMProcState state);
    void HandleException(DreamThread dreamThread, Exception exception);
}
