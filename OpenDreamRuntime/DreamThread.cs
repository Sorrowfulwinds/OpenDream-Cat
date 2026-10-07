using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DMCompiler.DM;
using JetBrains.Annotations;
using OpenDreamRuntime.Objects;
using OpenDreamRuntime.Procs;
using OpenDreamRuntime.Procs.DebugAdapter;
using OpenDreamShared.Dream;

namespace OpenDreamRuntime;

public enum ProcStatus {
    Continue,
    Cancelled,
    Returned,
    Deferred,
    Called
}

public abstract class DreamProc {
    public readonly List<string>? ArgumentNames;

    public readonly List<DreamValueType>? ArgumentTypes;

    public readonly ProcAttributes Attributes;
    public readonly int Id;
    public readonly sbyte Invisibility;
    public readonly bool IsVerb;
    public readonly string Name;
    public readonly TreeEntry OwningType;
    public readonly string? VerbCategory = string.Empty;
    public readonly string? VerbDesc;
    public readonly int? VerbRange;
    public readonly VerbSrc? VerbSrc;

    private readonly string? _verbName;

    // This is currently publicly settable because the loading code doesn't know what our super is until after we are instantiated
    public DreamProc? SuperProc;

    public int? VerbId = null; // Null until registered as a verb in ServerVerbSystem

    protected DreamProc(int id, TreeEntry owningType, string name, DreamProc? superProc, ProcAttributes attributes,
        List<string>? argumentNames, List<DreamValueType>? argumentTypes, VerbSrc? verbSrc, int? verbRange,
        string? verbName, string? verbCategory, string? verbDesc, sbyte invisibility, bool isVerb = false) {
        Id = id;
        OwningType = owningType;
        Name = name;
        IsVerb = isVerb;
        SuperProc = superProc;
        Attributes = attributes;
        ArgumentNames = argumentNames;
        ArgumentTypes = argumentTypes;
        VerbSrc = verbSrc;
        VerbRange = verbRange;

        _verbName = verbName;
        if (verbCategory is not null)
            // (de)serialization meme to reduce JSON size
            // It's string.Empty by default but we invert it to null to prevent serialization
            // Explicit null becomes treated as string.Empty
            VerbCategory = verbCategory == string.Empty ? null : verbCategory;

        VerbDesc = verbDesc;
        Invisibility = invisibility;
    }

    public string VerbName => _verbName ?? Name;

    public abstract ProcState CreateState(DreamThread thread, DreamObject? src, DreamObject? usr,
        [HandlesResourceDisposal] DreamProcArguments arguments);

    // Execute this proc. This will behave as if the proc has `set waitfor = 0`
    [MustDisposeResource]
    public DreamValue Spawn(DreamObject src, [HandlesResourceDisposal] DreamProcArguments arguments,
        DreamObject? usr = null) {
        var context = new DreamThread(ToString());
        ProcState state = CreateState(context, src, usr, arguments);
        context.PushProcState(state);
        return context.Resume();
    }

    public DreamValue GetField(string field) {
        // TODO: Figure out what byond does when these are null
        switch (field) {
            case "name":
                return new DreamValue(VerbName);
            case "category":
                return VerbCategory != null ? new DreamValue(VerbCategory) : DreamValue.Null;
            case "desc":
                return VerbDesc != null ? new DreamValue(VerbDesc) : DreamValue.Null;
            case "invisibility":
                return new DreamValue(Invisibility);
            case "hidden":
                Logger.GetSawmill("opendream.dmproc").Warning("The 'hidden' field on verbs will always return null.");
                return DreamValue.Null;
            default:
                throw new DMException($"Cannot get field \"{field}\" from {OwningType}.{Name}()");
        }
    }

    public override string ToString() {
        string procElement =
            SuperProc == null ? IsVerb ? "verb/" : "proc/" : string.Empty; // Has "proc/" only if it's not an override

        return $"{OwningType.Path}{(OwningType.Path.EndsWith('/') ? string.Empty : "/")}{procElement}{Name}";
    }
}

[Virtual]
internal class DMThrowException : Exception {
    public readonly DreamValue Value;

    public DMThrowException(DreamValue value) : base(GetRuntimeMessage(value)) {
        Value = value;
        Value.IncRef(); // Better hope we're gracefully caught!
    }

    private static string GetRuntimeMessage(DreamValue value) {
        string? name;

        value.TryGetValueAsDreamObject(out DreamObject? dreamObject);
        if (dreamObject?.TryGetVariable("name", out DreamValue nameVar) == true) {
            name = nameVar.TryGetValueAsString(out name) ? name : string.Empty;
            nameVar.Dispose();
        } else {
            name = string.Empty;
        }

        return name;
    }
}

internal sealed class DMCrashRuntime(string message) : Exception(message);

/// <summary>
///     This exception instantly terminates the entire thread of the proc.
/// </summary>
internal sealed class DMError(string message) : Exception(message);

public abstract class ProcState : IDisposable {
    private static int _idCounter;
    public int ArgumentCount;

    /// <summary> This stores our 'src' value. May be null!</summary>
    public DreamObject? Instance;

    [Access(typeof(ProcScheduler))] public DreamValue Result = DreamValue.Null;

    public DreamObject? Usr;

    public int Id { get; private set; }
    public DreamThread Thread { get; set; } = default!;

    public bool WaitFor { get; set; } = true;
    public abstract DreamProc? Proc { get; }
    public ProcState? Caller { get; set; }

    public virtual void Dispose() {
        Thread = null!;
        Result.DecRef();
        Result = DreamValue.Null;
        WaitFor = true;
        Id = -1;
    }

    protected void Initialize(DreamThread thread, bool waitFor) {
        Thread = thread;
        WaitFor = waitFor;
        Id = _idCounter++;
    }

    public abstract ProcStatus Resume();

    /// <summary>
    ///     Returns whether or not the proc is currently in a try catch block.
    /// </summary>
    public virtual bool IsCatching() {
        return false;
    }

    public virtual void CatchException(Exception exception) {
        throw new InvalidOperationException(
            $"Called {nameof(CatchException)} on a {nameof(ProcState)} that isn't catching!");
    }

    public abstract void AppendStackFrame(StringBuilder builder);

    // Most implementations won't require this, so give it a default
    public virtual void ReturnedInto(DreamValue value) {
    }

    public virtual void Cancel() {
    }

    public abstract ReadOnlySpan<DreamValue> GetArguments();

    public abstract void SetArgument(int id, DreamValue value);

#if TOOLS
    public abstract (string SourceFile, int Line) TracyLocationId { get; }
    public ProfilerZone? TracyZoneId { get; set; }
#endif
}

public sealed class DreamThread(string name) {
    private const int MaxStackDepth = 400; // Same as BYOND but /world/loop_checks = 0 raises the limit

    private static readonly ThreadLocal<Stack<DreamThread>> CurrentlyExecuting =
        new(() => new Stack<DreamThread>(), true);

    private static readonly StringBuilder ErrorMessageBuilder = new();

    private static int _idCounter;
    private readonly Stack<ProcState> _stack = new();

    /// <summary>
    ///     Stores the last object that was animated, so that animate() can be called without the object parameter. Does not
    ///     need to be passed to spawn calls, only current execution context.
    /// </summary>
    public DreamValue? LastAnimatedObject = null;

    private ProcState? _current;

    // The amount of stack frames containing `WaitFor = false`
    private int _syncCount;
    public int Id { get; } = ++_idCounter;

    public string Name { get; } = name;

    internal DreamDebugManager.ThreadStepMode? StepMode { get; set; }

    [MustDisposeResource]
    public static DreamValue Run(DreamProc proc, DreamObject src, DreamObject? usr, params DreamValue[] arguments) {
        var context = new DreamThread(proc.ToString());

        if (proc is NativeProc nativeProc)
            // ReSharper disable ExplicitCallerInfoArgument
            using (Profiler.BeginZone(filePath: "Native Proc", lineNumber: 0, memberName: nativeProc.Name)) {
                return nativeProc.Call(context, src, usr, new DreamProcArguments(arguments));
            }
        // ReSharper restore ExplicitCallerInfoArgument

        ProcState state = proc.CreateState(context, src, usr, new DreamProcArguments(arguments));
        context.PushProcState(state);
        return context.Resume();
    }

    [MustDisposeResource]
    public static DreamValue Run(string name,
        Func<AsyncNativeProc.AsyncNativeProcState, Task<DreamValue>> anonymousFunc) {
        var context = new DreamThread(name);
        ProcState state = AsyncNativeProc.CreateAnonymousState(context, anonymousFunc);

        context.PushProcState(state);
        return context.Resume();
    }

    [MustDisposeResource]
    public DreamValue Resume() {
        return ReentrantResume(null, out _);
    }

    /// <summary>
    ///     Resume this thread re-entrantly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This function is suitable for executing from inside a running opcode handler if
    ///         <paramref name="untilState" /> is provided.
    ///     </para>
    ///     Ownership of the return value will be transferred to the caller, so be sure to call
    ///     <see cref="DreamValue.DecRef" />.
    /// </remarks>
    /// <param name="untilState">
    ///     If not null, only continue running until this proc state gets returned into.
    ///     Note that if used, the parent proc will not have its <see cref="ProcState.ReturnedInto" /> called.
    /// </param>
    /// <param name="resultStatus">
    ///     The proc result status that caused this resume to return.
    /// </param>
    /// <returns>The return value of the last proc to return.</returns>
    [MustDisposeResource]
    public DreamValue ReentrantResume(ProcState? untilState, out ProcStatus resultStatus) {
        try {
            CurrentlyExecuting.Value!.Push(this);
            while (_current != null) {
                ProcStatus status;
                try {
#if TOOLS
                    if (_current.TracyZoneId is null && _current.Proc != null) {
                        (string SourceFile, int Line) location = _current.TracyLocationId;
                        string procpath =
                            (_current.Proc.OwningType.Path.Equals("/")
                                ? "/proc/"
                                : _current.Proc.OwningType.Path + "/") + _current.Proc.Name;
                        // ReSharper disable ExplicitCallerInfoArgument
                        _current.TracyZoneId = Profiler.BeginZone(filePath: location.SourceFile,
                            lineNumber: location.Line, memberName: procpath);
                        // ReSharper restore ExplicitCallerInfoArgument
                    }
#endif
                    // _current.Resume may mutate our state!!!
                    status = _current.Resume();
                } catch (DMError dmError) {
                    if (_current == null) {
                        // This happens if a ReentrantResume cancelled, it will have already torn down the stack.
                        // Just bail and do nothing else.
                        resultStatus = ProcStatus.Cancelled;
                        return default;
                    }

                    CancelAll();
                    HandleException(dmError);
                    status = ProcStatus.Cancelled;
                } catch (Exception exception) {
                    if (TryCatchException(exception)) continue;
                    HandleException(exception);
                    status = ProcStatus.Returned;
                }

                switch (status) {
                    // The entire Thread is stopping
                    case ProcStatus.Cancelled:
#if TOOLS
                        if (_current.TracyZoneId is not null) {
                            _current.TracyZoneId.Value.Dispose();
                            _current.TracyZoneId = null;
                        }
#endif

                        ProcState current = _current;
                        _current = null;

#if TOOLS
                        foreach (ProcState s in _stack) {
                            if (s.TracyZoneId is null)
                                continue;
                            s.TracyZoneId.Value.Dispose();
                            s.TracyZoneId = null;
                        }
#endif

                        _stack.Clear();
                        resultStatus = status;
                        return current.Result;

                    // Our top-most proc just returned a value
                    case ProcStatus.Returned:
                        DreamValue returned = _current.Result;
                        returned.IncRef();
                        PopProcState();

                        // If our stack is empty, the context has finished execution
                        // so we can return the result to our native caller
                        if (_current == null || _current == untilState) {
                            resultStatus = status;
                            return returned;
                        }

                        // ... otherwise we just push the return value onto the dm caller's stack
                        _current.ReturnedInto(returned);
                        returned.DecRef();
                        break;

                    // The context is done executing for now
                    case ProcStatus.Deferred:
#if TOOLS
                        if (_current.TracyZoneId is not null) {
                            _current.TracyZoneId.Value.Dispose();
                            _current.TracyZoneId = null;
                        }

                        foreach (ProcState s in _stack) {
                            if (s.TracyZoneId is null)
                                continue;
                            s.TracyZoneId.Value.Dispose();
                            s.TracyZoneId = null;
                        }
#endif
                        // We return the current return value here even though it may not be the final result
                        resultStatus = status;
                        return _current.Result;

                    // Our top-most proc just called a function
                    // This means _current has changed!
                    case ProcStatus.Called:
                        // Nothing to do. The loop will call into _current.Resume for us.
                        break;
                }
            }
        }
        finally {
            if (CurrentlyExecuting.Value!.Pop() != this)
                throw new InvalidOperationException("DreamThread stack corrupted");
        }

        throw new InvalidOperationException();
    }

    public void PushProcState(ProcState state) {
        if (_stack.Count >= MaxStackDepth) throw new DMError("stack depth limit reached");

        if (!state.WaitFor) _syncCount++;

        if (_current != null) _stack.Push(_current);

        state.Caller = _current;
        _current = state;
    }

    public void PopProcState(bool dispose = true) {
#if TOOLS
        if (_current?.TracyZoneId is not null) {
            _current.TracyZoneId.Value.Dispose();
            _current.TracyZoneId = null;
        }
#endif

        if (_current?.WaitFor == false) _syncCount--;

        // Maybe a bit of a hack? If the state got deferred to another thread it shouldn't be disposed.
        if (dispose && _current.Thread == this) _current.Dispose();

        if (!_stack.TryPop(out _current)) _current = null;
    }

    // Used by implementations of DreamProc::InternalContinue to defer execution to be resumed later.
    // This function may mutate `ProcState.Thread` on any of the states within this DreamThread's call stack
    public ProcStatus HandleDefer() {
        // When there are no `WaitFor = false` procs in our stack, just use the current thread
        if (_syncCount <= 0) return ProcStatus.Deferred;

        // Move over all stacks up to and including the first with `WaitFor = false` to a new DreamThread
        Stack<ProcState> newStackReversed = new();

        // `WaitFor = true` frames
        while (_current is not null && _current.WaitFor) {
            newStackReversed.Push(_current);
            PopProcState(false); // Dont dispose; this proc state is just being moved to another thread.
        }

        // `WaitFor = false` frame
        if (_current == null) throw new InvalidOperationException();
        newStackReversed.Push(_current);
        PopProcState(false);

        var newThread = new DreamThread("deferred");
        foreach (ProcState frame in newStackReversed) {
            frame.Thread = newThread;
            newThread.PushProcState(frame);
        }

        // Our returning proc state is expected to be on the stack at this point, so put it back
        // For this small moment, the proc state will be on both threads.
        PushProcState(newStackReversed.Peek());

        return ProcStatus.Returned;
    }

    public void AppendStackTrace(StringBuilder builder) {
        builder.Append("   ");
        if (_current is null)
            builder.Append("(init)...");
        else
            _current.AppendStackFrame(builder);
        builder.AppendLine();

        foreach (ProcState frame in _stack) {
            builder.Append("   ");
            frame.AppendStackFrame(builder);
            builder.AppendLine();
        }
    }

    public void CancelAll() {
        _current?.Cancel();

        foreach (ProcState state in _stack) state.Cancel();
    }

    private void HandleException(Exception exception) {
        _current?.Cancel();

        var dreamMan = IoCManager.Resolve<DreamManager>();

        ErrorMessageBuilder.Clear();
        ErrorMessageBuilder.AppendLine($"Exception occurred: {exception.Message}");

        ErrorMessageBuilder.AppendLine("=DM StackTrace=");
        AppendStackTrace(ErrorMessageBuilder);
        ErrorMessageBuilder.AppendLine();

#if DEBUG
        ErrorMessageBuilder.AppendLine("=C# StackTrace=");
        ErrorMessageBuilder.AppendLine(exception.ToString());
        ErrorMessageBuilder.AppendLine();
#else
                    if (exception is not DMException) {
                        ErrorMessageBuilder.AppendLine("=C# StackTrace=");
                        ErrorMessageBuilder.AppendLine(exception.ToString());
                        ErrorMessageBuilder.AppendLine();
                    }
#endif

        var msg = ErrorMessageBuilder.ToString();

        // Instantiate an /exception and invoke world.Error()
        var file = string.Empty;
        var line = 0;
        if (_current is DMProcState dmProc) { // TODO: Cope with the other ProcStates
            (string, int) source = dmProc.GetCurrentSource();
            file = source.Item1;
            line = source.Item2;
        }

        bool inWorldError = _current?.Proc?.OwningType == dreamMan.WorldInstance.ObjectDefinition.TreeEntry &&
                            _current.Proc.Name == "Error";
        if (!inWorldError && _stack.Count > 0) {
            //if we're not directly in /world.Error, check the stack
            ProcState top = _stack.ElementAt(_stack.Count - 1); //only the top of the stack can be /world/Error
            inWorldError = top.Proc?.OwningType == dreamMan.WorldInstance.ObjectDefinition.TreeEntry &&
                           top.Proc?.Name == "Error";
        }

        dreamMan.HandleException(exception, msg, file, line, inWorldError);
        IoCManager.Resolve<IDreamDebugManager>().HandleException(this, exception);
    }

    public IEnumerable<ProcState> InspectStack() {
        if (_current is not null) yield return _current;

        foreach (ProcState entry in _stack) yield return entry;
    }

    public static IEnumerable<DreamThread> InspectExecutingThreads() {
        return CurrentlyExecuting.Value!.Concat(CurrentlyExecuting.Values.SelectMany(x => x));
    }

    private bool TryCatchException(Exception exception) {
        if (!InspectStack().Any(x => x.IsCatching())) return false;

        while (!_current.IsCatching()) PopProcState();

        _current.CatchException(exception);
        return true;
    }
}
