using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CilicaLab.Compiler;

internal static class SnippetCompilation
{
    public static CSharpParseOptions ParseOptions { get; } = new(LanguageVersion.CSharp14);

    public static CSharpCompilationOptions Options(SyntaxNode root, bool release = true)
        => new(root.ChildNodes().OfType<GlobalStatementSyntax>().Any() ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: release ? OptimizationLevel.Release : OptimizationLevel.Debug,
            allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable, deterministic: true, concurrentBuild: false);
}
