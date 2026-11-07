using System.Globalization;
using System.Text;
using System.Text.Json;

using Cilica.AsmIO;

namespace CilicaLab.Compiler;

internal static class ILFormatter
{
    public static string Format(MethodDef method)
    {
        var body = method.ILBody!;
        var text = new StringBuilder();
        text.AppendLine($"// {method}");
        text.AppendLine($".maxstack {body.MaxStack}");
        if (body.Locals.Length > 0) {
            text.AppendLine($".locals{(body.InitLocals ? " init" : "")} (");
            for (int i = 0; i < body.Locals.Length; i++) {
                var local = body.Locals[i];
                text.AppendLine($"    [{i}] {local.Type}{(local.IsPinned ? " pinned" : "")} V_{i}{(i + 1 < body.Locals.Length ? "," : "")}");
            }
            text.AppendLine(")");
        }
        text.AppendLine();
        foreach (var instruction in body.Instructions) {
            string? operand = instruction.OperandType switch {
                ILOperandType.None => null,
                ILOperandType.BrTarget or ILOperandType.ShortBrTarget => $"IL_{instruction.Operand:X4}",
                ILOperandType.Switch => "(" + string.Join(", ", ((int[])instruction.Operand!).Select(offset => $"IL_{offset:X4}")) + ")",
                ILOperandType.String => JsonSerializer.Serialize((string)instruction.Operand!),
                _ => Convert.ToString(instruction.Operand, CultureInfo.InvariantCulture)
            };
            text.AppendLine($"IL_{instruction.Offset:X4}: {instruction.OpCode.GetName()}{(operand == null ? "" : " " + operand)}");
        }
        foreach (var clause in body.ExceptionClauses) {
            text.AppendLine($".try IL_{clause.TryStart:X4} to IL_{clause.TryEnd:X4} {clause.Kind} {clause.CatchType} handler IL_{clause.HandlerStart:X4} to IL_{clause.HandlerEnd:X4}" +
                (clause.Kind == System.Reflection.Metadata.ExceptionRegionKind.Filter ? $" filter IL_{clause.FilterStart:X4}" : ""));
        }
        return text.ToString();
    }
}
