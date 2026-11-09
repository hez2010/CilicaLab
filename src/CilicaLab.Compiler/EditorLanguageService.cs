using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Reflection;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace CilicaLab.Compiler;

public sealed class EditorLanguageService : IDisposable
{
    private readonly CompositionHost _composition;
    private readonly AdhocWorkspace _workspace;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, CompletionSession> _completions = [];
    private Document _document;
    private string _source = "";
    private int _nextSession;

    private sealed record CompletionSession(Document Document, CompletionService Service, IReadOnlyList<CompletionItem> Items);

    public EditorLanguageService(FrameworkReferences references)
    {
        Assembly[] assemblies = [
            typeof(AdhocWorkspace).Assembly,
            typeof(Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions).Assembly,
            typeof(CompletionService).Assembly,
            Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")
        ];
        // These providers support in-memory snippets without desktop indexing or semantic snippet generation.
        var parts = assemblies.SelectMany(assembly => assembly.GetTypes()).Where(type =>
            type.GetCustomAttribute<ExportCompletionProviderAttribute>() is not { } provider ||
                provider.Name is "KeywordCompletionProvider" or "SymbolCompletionProvider" or
                    "ObjectCreationCompletionProvider" or "NamedParameterCompletionProvider");
        _composition = new ContainerConfiguration().WithParts(parts).CreateContainer();
        var host = MefHostServices.Create(_composition);
        _workspace = new AdhocWorkspace(host);
        var project = _workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
            "CilicaLab.Snippet", "CilicaLab.Snippet", LanguageNames.CSharp,
            parseOptions: SnippetCompilation.ParseOptions,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: references.Metadata));
        _document = _workspace.AddDocument(project.Id, "Snippet.cs", SourceText.From(""));
    }

    private async Task<Document> GetDocumentAsync(string source)
    {
        if (source != _source) {
            _document = _document.WithText(SourceText.From(source));
            _source = source;
        }
        var document = _document;
        var root = (await document.GetSyntaxRootAsync(_lifetime.Token))!;
        return document.Project.Solution.WithProjectCompilationOptions(document.Project.Id, SnippetCompilation.Options(root))
            .GetDocument(document.Id)!;
    }

    public async Task<EditorCompletions?> GetCompletionsAsync(string source, int position)
    {
        var document = await GetDocumentAsync(source);
        var service = CompletionService.GetService(document)!;
        var list = await service.GetCompletionsAsync(document, position, cancellationToken: _lifetime.Token);
        if (list == null || list.ItemsList.Count == 0) return null;

        var items = new List<EditorCompletion>(list.ItemsList.Count);
        foreach (var item in list.ItemsList) {
            var change = await service.GetChangeAsync(document, item, cancellationToken: _lifetime.Token);
            var changes = change.TextChanges.IsDefaultOrEmpty ? [change.TextChange] : change.TextChanges;
            var edits = changes.Select(edit => new EditorTextEdit(edit.Span.Start, edit.Span.Length, edit.NewText!)).ToArray();
            items.Add(new EditorCompletion(item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix,
                item.FilterText, item.SortText, item.Tags, edits, change.NewPosition));
        }
        int session = ++_nextSession;
        _completions.Add(session, new CompletionSession(document, service, list.ItemsList));
        return new EditorCompletions(session, items);
    }

    public async Task<string?> DescribeCompletionAsync(int sessionId, int index)
    {
        if (!_completions.TryGetValue(sessionId, out var session)) return null;
        var description = await session.Service.GetDescriptionAsync(session.Document, session.Items[index], _lifetime.Token);
        return description?.Text;
    }

    public void ReleaseCompletions(int sessionId) => _completions.Remove(sessionId);

    public async Task<EditorHover?> GetHoverAsync(string source, int position)
    {
        var document = await GetDocumentAsync(source);
        var info = await QuickInfoService.GetService(document)!.GetQuickInfoAsync(document, position, _lifetime.Token);
        if (info == null) return null;
        return new EditorHover(info.Span.Start, info.Span.Length,
            info.Sections.Select(section => new EditorHoverSection(section.Kind,
                string.Concat(section.TaggedParts.Select(part => part.Text)))).ToArray());
    }

    public async Task<IReadOnlyList<SourceDiagnostic>> GetDiagnosticsAsync(string source)
    {
        var document = await GetDocumentAsync(source);
        var compilation = (await document.Project.GetCompilationAsync(_lifetime.Token))!;
        return compilation.GetDiagnostics(_lifetime.Token)
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(SourceDiagnostic.FromDiagnostic).ToArray();
    }

    public async Task<IReadOnlyList<EditorSemanticToken>> GetSemanticTokensAsync(string source)
    {
        var document = await GetDocumentAsync(source);
        var text = await document.GetTextAsync(_lifetime.Token);
        var spans = await Classifier.GetClassifiedSpansAsync(document, new TextSpan(0, text.Length), _lifetime.Token);
        var tokens = new List<EditorSemanticToken>();
        foreach (var group in spans.GroupBy(span => span.TextSpan).OrderBy(group => group.Key.Start)) {
            var symbol = group.Select(span => (span.ClassificationType, Type: SemanticTokenType(span.ClassificationType)))
                .FirstOrDefault(span => span.Type != null);
            if (symbol.Type == null) continue;
            var position = text.Lines.GetLinePosition(group.Key.Start);
            int modifiers = group.Any(span => span.ClassificationType == ClassificationTypeNames.StaticSymbol) ? 1 : 0;
            if (symbol.ClassificationType == ClassificationTypeNames.ConstantName) modifiers |= 2;
            tokens.Add(new EditorSemanticToken(position.Line, position.Character, group.Key.Length, symbol.Type, modifiers));
        }
        return tokens;
    }

    private static string? SemanticTokenType(string classification) => classification switch {
        ClassificationTypeNames.NamespaceName => "namespace",
        ClassificationTypeNames.ClassName or ClassificationTypeNames.RecordClassName => "class",
        ClassificationTypeNames.StructName or ClassificationTypeNames.RecordStructName => "struct",
        ClassificationTypeNames.InterfaceName => "interface",
        ClassificationTypeNames.EnumName => "enum",
        ClassificationTypeNames.DelegateName => "type",
        ClassificationTypeNames.TypeParameterName => "typeParameter",
        ClassificationTypeNames.ParameterName => "parameter",
        ClassificationTypeNames.LocalName => "variable",
        ClassificationTypeNames.FieldName => "field",
        ClassificationTypeNames.ConstantName => "constant",
        ClassificationTypeNames.PropertyName => "property",
        ClassificationTypeNames.EnumMemberName => "enumMember",
        ClassificationTypeNames.EventName => "event",
        ClassificationTypeNames.MethodName or ClassificationTypeNames.ExtensionMethodName => "method",
        ClassificationTypeNames.LabelName => "label",
        _ => null
    };

    public async Task<EditorSignatureHelp?> GetSignatureHelpAsync(string source, int position)
    {
        var document = await GetDocumentAsync(source);
        var root = (await document.GetSyntaxRootAsync(_lifetime.Token))!;
        var arguments = root.FindToken(Math.Max(0, position - 1)).Parent?.AncestorsAndSelf()
            .OfType<ArgumentListSyntax>().FirstOrDefault(list => list.OpenParenToken.Span.End <= position &&
                (list.CloseParenToken.IsMissing || position <= list.CloseParenToken.SpanStart));
        if (arguments == null) return null;

        var semantic = (await document.GetSemanticModelAsync(_lifetime.Token))!;
        var call = arguments.Parent!;
        var symbolInfo = semantic.GetSymbolInfo(call, _lifetime.Token);
        IEnumerable<IMethodSymbol> methods = call switch {
            InvocationExpressionSyntax invocation => semantic.GetMemberGroup(invocation.Expression, _lifetime.Token).OfType<IMethodSymbol>(),
            ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax =>
                (semantic.GetTypeInfo(call, _lifetime.Token).Type as INamedTypeSymbol)?.InstanceConstructors
                    .Where(method => semantic.IsAccessible(position, method)) ?? [],
            ConstructorInitializerSyntax initializer =>
                (initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword)
                    ? semantic.GetEnclosingSymbol(position, _lifetime.Token)?.ContainingType
                    : semantic.GetEnclosingSymbol(position, _lifetime.Token)?.ContainingType.BaseType)?.InstanceConstructors
                    .Where(method => semantic.IsAccessible(position, method)) ?? [],
            _ => []
        };
        var overloads = methods.Concat(symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
            .Concat(symbolInfo.Symbol is IMethodSymbol bound ? [bound] : [])
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToArray();
        if (overloads.Length == 0) return null;

        int argumentIndex = arguments.Arguments.GetSeparators().Count(separator => separator.SpanStart < position);
        string? argumentName = arguments.Arguments.ElementAtOrDefault(argumentIndex)?.NameColon?.Name.Identifier.ValueText;
        var signatures = overloads.Select(method => Signature(method, semantic, position, argumentIndex, argumentName)).ToArray();
        int activeSignature = Array.FindIndex(overloads, method => SymbolEqualityComparer.Default.Equals(method, symbolInfo.Symbol));
        if (activeSignature < 0) {
            activeSignature = Array.FindIndex(overloads, method => method.Parameters.Length > argumentIndex || method.Parameters.LastOrDefault()?.IsParams == true);
        }
        return new EditorSignatureHelp(signatures, Math.Max(0, activeSignature));
    }

    private static EditorSignature Signature(IMethodSymbol method, SemanticModel semantic, int position, int argumentIndex, string? argumentName)
    {
        var format = SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeRef);
        var label = new StringBuilder(method.ToMinimalDisplayString(semantic, position, format)).Append('(');
        var parameters = new List<EditorParameter>();
        foreach (var parameter in method.Parameters) {
            if (parameters.Count > 0) label.Append(", ");
            int start = label.Length;
            label.Append(parameter.ToMinimalDisplayString(semantic, position, SymbolDisplayFormat.MinimallyQualifiedFormat));
            parameters.Add(new EditorParameter(start, label.Length));
        }
        label.Append(')');
        int activeParameter = argumentName == null ? argumentIndex : method.Parameters.IndexOf(method.Parameters.FirstOrDefault(parameter => parameter.Name == argumentName)!);
        return new EditorSignature(label.ToString(), parameters, Math.Clamp(activeParameter, 0, Math.Max(0, parameters.Count - 1)));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _completions.Clear();
        _workspace.Dispose();
        _composition.Dispose();
    }
}

public sealed record EditorTextEdit(int Start, int Length, string Text);
public sealed record EditorCompletion(string Label, string FilterText, string SortText, ImmutableArray<string> Tags, IReadOnlyList<EditorTextEdit> Edits, int? NewPosition);
public sealed record EditorCompletions(int SessionId, IReadOnlyList<EditorCompletion> Items);
public sealed record EditorHoverSection(string Kind, string Text);
public sealed record EditorHover(int Start, int Length, IReadOnlyList<EditorHoverSection> Sections);
public sealed record EditorSemanticToken(int Line, int Column, int Length, string Type, int Modifiers);
public sealed record EditorParameter(int Start, int End);
public sealed record EditorSignature(string Label, IReadOnlyList<EditorParameter> Parameters, int ActiveParameter);
public sealed record EditorSignatureHelp(IReadOnlyList<EditorSignature> Signatures, int ActiveSignature);
