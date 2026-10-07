using System.Runtime.InteropServices;
using JetBrains.Annotations;
using OpenDreamRuntime.ByondApi;
using Api = OpenDreamRuntime.ByondApi.ByondApi;

namespace OpenDreamRuntime.Procs;

internal static partial class DMOpcodeHandlers {
    private static ProcStatus CallExt(
        DMProcState state,
        DreamValue source,
        DMProcState.DMStackArgumentInfo argumentsInfo) {
        if (!source.TryGetValueAsString(out string? dllName))
            throw new DMException($"{source} is not a valid DLL");

        using DreamValue popProc = state.Pop();
        if (!popProc.TryGetValueAsString(out string? procName))
            throw new DMException($"{popProc} is not a valid proc name");

        DreamProcArguments arguments = state.PopProcArguments(null, argumentsInfo);

        // If we're on linux, we use a .so instead of a .dll
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && dllName.EndsWith(".dll"))
            dllName = dllName[..^"dll".Length] + "so";

        if (procName.StartsWith("byond:")) return CallExtByond(state, dllName, procName, arguments);

        return CallExtString(state, dllName, procName, arguments);
    }

    private static unsafe ProcStatus CallExtByond(
        DMProcState state,
        string dllName,
        string procName,
        [HandlesResourceDisposal] DreamProcArguments arguments) {
        // TODO: Don't allocate string copy
        // TODO: Handle stdcall (do we care?)
        var entryPoint = (delegate* unmanaged[Cdecl]<uint, CByondValue*, CByondValue>)
            DllHelper.ResolveDllTarget(state.Proc.DreamResourceManager, dllName, procName["byond:".Length..]);

        Span<CByondValue> args = stackalloc CByondValue[arguments.Count];
        args.Clear();

        for (var i = 0; i < args.Length; i++) {
            DreamValue arg = arguments.GetArgument(i);
            args[i] = Api.ValueToByondApi(arg);
        }

        using DreamValue result = Api.ValueFromDreamApi(Api.DoCall(entryPoint, args));
        state.Push(result);
        arguments.Dispose();
        return ProcStatus.Continue;
    }

    private static unsafe ProcStatus CallExtString(
        DMProcState state,
        string dllName,
        string procName,
        [HandlesResourceDisposal] DreamProcArguments arguments) {
        delegate* unmanaged[Cdecl]<int, byte**, byte*> entryPoint =
            DllHelper.ResolveDllTarget(state.Proc.DreamResourceManager, dllName, procName);

        Span<nint> argV = stackalloc nint[arguments.Count];
        argV.Fill(0);
        try {
            for (var i = 0; i < argV.Length; i++) {
                string arg = arguments.GetArgument(i).Stringify();

                argV[i] = Marshal.StringToCoTaskMemUTF8(arg);
            }

            byte* ret;
            if (arguments.Count > 0)
                fixed (nint* ptr = &argV[0]) {
                    ret = entryPoint(arguments.Count, (byte**)ptr);
                }
            else
                ret = entryPoint(0, (byte**)0);

            if (ret == null) {
                state.Push(DreamValue.Null);
                return ProcStatus.Continue;
            }

            string? retString = Marshal.PtrToStringUTF8((nint)ret);
            state.Push(new DreamValue(retString));
            return ProcStatus.Continue;
        }
        finally {
            arguments.Dispose();
            foreach (IntPtr arg in argV)
                if (arg != 0)
                    Marshal.ZeroFreeCoTaskMemUTF8(arg);
        }
    }
}
