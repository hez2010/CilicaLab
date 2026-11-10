using System.Diagnostics;

using Cilica;
using Cilica.AsmIO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CilicaLab.Compiler;

public sealed class LabCompiler(FrameworkReferences references)
{
    public PassPipeline GetDefaultPipeline()
    {
        using var resolver = new ModuleResolver();
        resolver.AddSearchPaths([references.DirectoryPath]);
        var compilation = new Cilica.Compilation(resolver.Create("CilicaLab.Pipeline"), new PassTraceLogger(), new CompilationSettings());
        return DefaultPipelineReader.Read(CompilationPipeline.CreateDefault(compilation)).Pipeline;
    }

    public CompilationOutput Compile(string source, LabOptions options)
    {
        var settings = options.ToCompilationSettings();
        long started = Stopwatch.GetTimestamp();
        var tree = CSharpSyntaxTree.ParseText(source, SnippetCompilation.ParseOptions, path: "Snippet.cs");
        var compilation = CSharpCompilation.Create("CilicaLab.Snippet", [tree], references.Metadata,
            SnippetCompilation.Options(tree.GetRoot(), options.Release));

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        var diagnostics = emitted.Diagnostics
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(SourceDiagnostic.FromDiagnostic).ToArray();
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
        var optimizer = new Cilica.Compilation(module, logger, settings);
        var manager = OptimizationPipeline.Build(optimizer, options.Pipeline);
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
}
