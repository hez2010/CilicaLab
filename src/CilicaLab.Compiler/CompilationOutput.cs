using Cilica;

namespace CilicaLab.Compiler;

public sealed record LabOptions(bool Release = true, bool EnableInlining = true, bool AllowCrossAssemblyIPO = true);

public sealed record SourceDiagnostic(string Id, string Severity, string Message, int Line, int Column, int EndLine, int EndColumn);

public sealed record MethodSnapshot(string IR, string Graphviz);

public sealed record PassSnapshot(int Number, string Name, MethodSnapshot Before, MethodSnapshot After, bool Changed, double Milliseconds, string? Error);

public sealed record DecompiledOutput(string Original, string Optimized);

public sealed record MethodOutput(
    string Signature,
    MethodCompilationStatus Status,
    string OriginalIL,
    string GeneratedIL,
    IReadOnlyList<PassSnapshot> Passes,
    string? Error)
{
    public int ChangedPasses => Passes.Count(pass => pass.Changed);
}

public sealed record CompilationOutput(
    IReadOnlyList<SourceDiagnostic> Diagnostics,
    IReadOnlyList<MethodOutput> Methods,
    IReadOnlyList<string> Messages,
    byte[]? OriginalAssembly,
    byte[]? Assembly,
    double Milliseconds)
{
    public bool Succeeded => Assembly != null && Methods.All(method => method.Status is MethodCompilationStatus.Optimized or MethodCompilationStatus.Unchanged);
}
