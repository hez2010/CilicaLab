using System.Diagnostics;

using Cilica;
using Cilica.AsmIO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CilicaLab.Compiler;

public sealed class LabCompiler(FrameworkReferences references)
{
    public CompilationOutput Compile(string source, LabOptions options)
    {
        long started = Stopwatch.GetTimestamp();
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14), path: "Snippet.cs");
        bool hasTopLevelStatements = tree.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>().Any();
        var compilation = CSharpCompilation.Create("CilicaLab.Snippet", [tree], references.Metadata,
            new CSharpCompilationOptions(hasTopLevelStatements ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: options.Release ? OptimizationLevel.Release : OptimizationLevel.Debug,
                allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable, deterministic: true, concurrentBuild: false));

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        var diagnostics = emitted.Diagnostics
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(ToDiagnostic).ToArray();
        if (!emitted.Success) {
            return new CompilationOutput(diagnostics, [], null, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        string inputPath = Path.Combine(references.DirectoryPath, "CilicaLab.Snippet.dll");
        byte[] originalAssembly = image.ToArray();
        File.WriteAllBytes(inputPath, originalAssembly);

        var logger = new PassTraceLogger();
        using var resolver = new ModuleResolver(logger);
        resolver.AddSearchPaths([references.DirectoryPath]);
        var module = resolver.Load(inputPath);
        var candidates = PassManager.GetCandidateMethodsFromIL(module, (_, method) => method.Module == module);
        var originalIL = candidates.ToDictionary(method => method, ILFormatter.Format);
        var optimizer = new Cilica.Compilation(module, logger, new CompilationSettings {
            EnableInlining = options.EnableInlining,
            AllowCrossAssemblyIPO = options.AllowCrossAssemblyIPO,
            VerifyEachPass = true
        });
        var manager = CompilationPipeline.CreateDefault(optimizer);
        var recorder = new PassRecorder(logger);
        manager.Inspectors.Add(recorder);
        var result = manager.Run(candidates);

        var methods = result.Methods.Select(result => new MethodOutput(
            result.Method.ToString(), result.Status, originalIL[result.Method], ILFormatter.Format(result.Method),
            recorder.Timelines.GetValueOrDefault(result.Method) ?? [],
            result.Error == null ? null : $"{result.FailedPhase}: {result.Error.Message}"))
            .OrderBy(method => method.Signature, StringComparer.Ordinal).ToArray();

        using var output = new MemoryStream();
        module.Save(output, null, "CilicaLab.Snippet.dll");
        return new CompilationOutput(diagnostics, methods, originalAssembly, output.ToArray(), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public DecompiledOutput Decompile(byte[] originalAssembly, byte[] optimizedAssembly)
    {
        using var decompiler = new AssemblyDecompiler(references.DirectoryPath);
        return new DecompiledOutput(decompiler.Decompile(originalAssembly), decompiler.Decompile(optimizedAssembly));
    }

    private static SourceDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new SourceDiagnostic(diagnostic.Id, diagnostic.Severity.ToString(), diagnostic.GetMessage(),
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
    }

}
