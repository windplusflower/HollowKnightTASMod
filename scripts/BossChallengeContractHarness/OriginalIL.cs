using System.Reflection;
using System.Reflection.Emit;

// Copies the actual pinned original IL, changing only specified external calls.
// Branches, field accesses, locals, cache and delegate allocation stay original.
internal static class OriginalIL
{
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields()
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(c => c.Value);
    private sealed record Instruction(int Offset, OpCode Code, object? Operand);

    internal static DynamicMethod Copy(MethodInfo original, Func<MethodInfo, MethodInfo?> replace)
    {
        var body = original.GetMethodBody() ?? throw new InvalidDataException("Original method has no body.");
        if (body.ExceptionHandlingClauses.Count != 0) throw new InvalidDataException("Fixture does not interpret exception regions.");
        var parameters = original.GetParameters().Select(p => p.ParameterType).ToList();
        if (!original.IsStatic) parameters.Insert(0, original.DeclaringType!);
        var result = new DynamicMethod("Original_" + original.MetadataToken, original.ReturnType,
            parameters.ToArray(), typeof(OriginalIL).Module, true);
        var output = result.GetILGenerator();
        foreach (var local in body.LocalVariables) output.DeclareLocal(local.LocalType, local.IsPinned);
        var bytes = body.GetILAsByteArray()!;
        var instructions = new List<Instruction>();
        for (int cursor = 0; cursor < bytes.Length;) {
            int offset = cursor;
            var first = bytes[cursor++];
            var code = Codes[first == 0xfe ? (short)(0xfe00 | bytes[cursor++]) : first];
            object? operand = null;
            int I32() { var n = BitConverter.ToInt32(bytes, cursor); cursor += 4; return n; }
            switch (code.OperandType) {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI: operand = (sbyte)bytes[cursor++]; break;
                case OperandType.InlineI: operand = I32(); break;
                case OperandType.ShortInlineVar: operand = bytes[cursor++]; break;
                case OperandType.InlineVar: operand = BitConverter.ToInt16(bytes, cursor); cursor += 2; break;
                case OperandType.ShortInlineBrTarget: { var delta = (sbyte)bytes[cursor++]; operand = cursor + delta; break; }
                case OperandType.InlineBrTarget: { var delta = I32(); operand = cursor + delta; break; }
                case OperandType.InlineSwitch: {
                    int count = I32(), end = cursor + 4 * count;
                    operand = Enumerable.Range(0, count).Select(_ => end + I32()).ToArray(); break;
                }
                case OperandType.InlineField: operand = original.Module.ResolveField(I32()); break;
                case OperandType.InlineMethod: operand = original.Module.ResolveMethod(I32()); break;
                case OperandType.InlineType: operand = original.Module.ResolveType(I32()); break;
                case OperandType.InlineString: operand = original.Module.ResolveString(I32()); break;
                default: throw new InvalidDataException("Fixture unsupported operand: " + code);
            }
            instructions.Add(new Instruction(offset, code, operand));
        }
        var labels = instructions.ToDictionary(i => i.Offset, _ => output.DefineLabel());
        foreach (var instruction in instructions) {
            output.MarkLabel(labels[instruction.Offset]);
            var code = instruction.Code; var operand = instruction.Operand;
            if ((code == OpCodes.Call || code == OpCodes.Callvirt) && operand is MethodInfo call && replace(call) is MethodInfo replacement) {
                output.Emit(replacement.IsStatic ? OpCodes.Call : code, replacement); continue;
            }
            switch (code.OperandType) {
                case OperandType.InlineNone: output.Emit(code); break;
                case OperandType.ShortInlineI: output.Emit(code, (sbyte)operand!); break;
                case OperandType.InlineI: output.Emit(code, (int)operand!); break;
                case OperandType.ShortInlineVar: output.Emit(code, (byte)operand!); break;
                case OperandType.InlineVar: output.Emit(code, (short)operand!); break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.InlineBrTarget: output.Emit(code, labels[(int)operand!]); break;
                case OperandType.InlineSwitch: output.Emit(code, ((int[])operand!).Select(x => labels[x]).ToArray()); break;
                case OperandType.InlineField: output.Emit(code, (FieldInfo)operand!); break;
                case OperandType.InlineType: output.Emit(code, (Type)operand!); break;
                case OperandType.InlineString: output.Emit(code, (string)operand!); break;
                case OperandType.InlineMethod:
                    if (operand is ConstructorInfo constructor) output.Emit(code, constructor);
                    else output.Emit(code, (MethodInfo)operand!);
                    break;
                default: throw new InvalidDataException("Fixture unsupported emission.");
            }
        }
        return result;
    }
}
