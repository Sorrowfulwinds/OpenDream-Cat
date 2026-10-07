using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using DMCompiler.DM;
using DMCompiler.Json;
using OpenDreamRuntime.Procs;

namespace DMDisassembler;

internal class DMProc(ProcDefinitionJson json) {
    public List<ProcArgumentJson>? Arguments = json.Arguments;
    public byte[] Bytecode = json.Bytecode ?? Array.Empty<byte>();
    public Exception? Exception;
    public sbyte Invisibility = json.Invisibility;
    public bool IsOverride = (json.Attributes & ProcAttributes.IsOverride) != 0;
    public bool IsVerb = json.IsVerb;
    public List<LocalVariableJson>? Locals = json.Locals;
    public int MaxStackSize = json.MaxStackSize;

    public int MaxVariableId = json.MaxVariableId;
    public string Name = json.Name;
    public int OwningTypeId = json.OwningTypeId;
    public string? VerbCategory = json.VerbCategory;
    public string? VerbDesc = json.VerbDesc;
    public string? VerbName = json.VerbName;

    public string Decompile() {
        List<DecompiledOpcode> decompiled = GetDecompiledOpcodes(out HashSet<int> labeledPositions);

        var result = new StringBuilder();

        result.AppendLine($"Max stack size: {MaxStackSize}");
        result.AppendLine($"Max variable ID: {MaxVariableId}");

        if (Arguments is {Count: > 0}) {
            result.AppendLine("Arguments:");
            foreach (ProcArgumentJson argument in Arguments) result.AppendLine($"\t{argument.Name}: {argument.Type}");
        }

        if (Locals is {Count: > 0}) {
            result.AppendLine("Locals:");
            foreach (LocalVariableJson local in Locals)
                result.AppendLine($"\tOffset: {local.Offset}, Remove: {local.Remove}, Add: {local.Add}");
        }

        if (IsVerb) {
            result.AppendLine("Verb:");
            result.AppendLine($"\tName: {VerbName}");
            result.AppendLine($"\tCategory: {VerbCategory}");
            result.AppendLine($"\tDescription: {VerbDesc}");
            result.AppendLine($"\tInvisibility: {Invisibility}");
        }

        result.AppendLine();
        foreach (DecompiledOpcode decompiledOpcode in decompiled) {
            if (labeledPositions.Contains(decompiledOpcode.Position)) {
                result.AppendFormat("0x{0:x}", decompiledOpcode.Position);
                result.AppendLine();
            }

            result.Append('\t');
            result.AppendLine(decompiledOpcode.Text);
        }

        if (labeledPositions.Contains(Bytecode.Length)) {
            // In case of a Jump off the end of the proc.
            result.AppendFormat("0x{0:x}", Bytecode.Length);
            result.AppendLine();
        }

        if (Exception != null) result.Append(Exception);

        return result.ToString();
    }

    public List<DecompiledOpcode> GetDecompiledOpcodes(out HashSet<int> labeledPositions) {
        List<DecompiledOpcode> decompiled = new();
        labeledPositions = new HashSet<int>();

        try {
            foreach ((int position, ITuple instruction) in new ProcDecoder(Program.CompiledJson.Strings, Bytecode)
                         .Disassemble()) {
                decompiled.Add(new DecompiledOpcode(position,
                    ProcDecoder.Format(instruction, type => Program.CompiledJson.Types[type].Path)));
                if (ProcDecoder.GetJumpDestination(instruction) is int jumpPosition) labeledPositions.Add(jumpPosition);
            }
        } catch (Exception ex) {
            Exception = ex;
        }

        return decompiled;
    }

    public string[]? GetArguments() {
        if (json.Arguments is null || json.Arguments.Count == 0) return null;

        var argNames = new string[json.Arguments.Count];
        for (var index = 0; index < json.Arguments.Count; index++) argNames[index] = json.Arguments[index].Name;

        return argNames;
    }

    internal struct DecompiledOpcode(int position, string text) {
        public readonly int Position = position;
        public readonly string Text = text;
    }
}
