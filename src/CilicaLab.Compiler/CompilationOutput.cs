using Cilica;

using Microsoft.CodeAnalysis;

namespace CilicaLab.Compiler;

public sealed record LabOptions(bool Release = true, bool EnableInlining = true, bool AllowCrossAssemblyIPO = true, PassPipeline? Pipeline = null);

public sealed record SourceDiagnostic(string Id, string Severity, string Message, int Line, int Column, int EndLine, int EndColumn)
{
    internal static SourceDiagnostic FromDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new SourceDiagnostic(diagnostic.Id, diagnostic.Severity.ToString(), diagnostic.GetMessage(),
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
    }
}

public sealed record MethodSnapshot(string IR, string Graphviz);

/// <summary> A message logged during a pass. Scope entries name a nested operation, such as a size probe or a nested pass. </summary>
public sealed record TraceEntry(LogLevel Level, int Depth, string Message, bool IsScope);

public sealed record PassSnapshot(
    int Number,
    string Name,
    MethodSnapshot Before,
    MethodSnapshot After,
    bool Changed,
    double Milliseconds,
    string? Error,
    IReadOnlyList<TraceEntry> Trace)
{
    public int TraceMessages { get; } = Trace.Count(entry => !entry.IsScope);
}

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
    byte[]? OriginalAssembly,
    byte[]? Assembly,
    double Milliseconds)
{
    public bool Succeeded => Assembly != null && Methods.All(method => method.Status is MethodCompilationStatus.Optimized or MethodCompilationStatus.Unchanged);
}
