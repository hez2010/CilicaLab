using System.Reflection;

using CilicaLab.Compiler;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CilicaLab.Components;

public partial class Lab
{
    private static readonly string CilicaVersion = typeof(Cilica.Compilation).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private HttpClient Http { get; set; } = null!;

    private ElementReference _sourceElement, _diffElement, _workspaceElement, _splitterElement, _beforeGraphElement, _afterGraphElement;
    private ElementReference _diffPanelElement, _traceSplitterElement;
    private ElementReference _shareDialogElement, _shareInputElement, _pipelineDialogElement;
    private IJSObjectReference? _editor;
    private DotNetObjectReference<Lab>? _callback;
    private FrameworkReferences? _references;
    private LabCompiler? _compiler;
    private EditorLanguageService? _languageService;
    private CompilationOutput? _output;
    private DecompiledOutput? _decompiled;
    private string _sampleId = Samples.All[0].Id;
    private string _status = "Loading compiler…";
    private string? _error;
    private string? _viewError;
    private string? _shareError;
    private string _shareUrl = "", _shareMessage = "";
    private bool _sharing;
    private int _methodIndex = -1, _passIndex = -1;
    private bool _busy = true, _dirty, _changedOnly;
    private bool _splitView = true;
    private bool _traceOpen = true;
    private LabOptions _options = new();
    private PassPipeline? _defaultPipeline;
    private OutputView _view;
    private PassDisplay _passDisplay;
    private string _theme = "system";

    private enum OutputView { Passes, IL, CSharp }
    private enum PassDisplay { IR, Graphviz }
    private sealed record EditorState(string Source, LabOptions Options, string? ShareError);
    private sealed record ShareLinkResult(string Url, string? Error);
    private bool IsPassView => _view == OutputView.Passes;
    private bool IsILView => _view == OutputView.IL;
    private bool IsCSharpView => _view == OutputView.CSharp;
    private bool ShowGraphs => IsPassView && _passDisplay == PassDisplay.Graphviz;
    private bool OutputMissing => IsCSharpView ? _decompiled == null : SelectedMethod == null || IsPassView && SelectedPass == null;

    private MethodOutput? SelectedMethod => _output?.Methods.ElementAtOrDefault(_methodIndex);
    private PassSnapshot? SelectedPass => SelectedMethod?.Passes.ElementAtOrDefault(_passIndex);
    private IEnumerable<PassSnapshot> VisiblePasses => SelectedMethod?.Passes.Where(pass => !_changedOnly || pass.Changed || pass.Error != null) ?? [];
    private string RunSummary => _busy ? _status : _output == null ? "Ready when you are." :
        $"{_output.Methods.Count} methods · {_output.Methods.Sum(method => method.ChangedPasses)} changed passes · {_output.Milliseconds:0} ms";

    protected override void OnInitialized()
    {
        if (JS is IJSInProcessRuntime runtime) _theme = runtime.Invoke<string>("cilicaLabTheme.getPreference");
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try {
            _status = "Loading .NET assemblies…";
            StateHasChanged();
            using var archive = new MemoryStream(await Http.GetByteArrayAsync("references/framework.zip"), writable: false);
            _references = FrameworkReferences.FromArchive(archive);
            _compiler = new LabCompiler(_references);
            _defaultPipeline = _compiler.GetDefaultPipeline();
            _editor = await JS.InvokeAsync<IJSObjectReference>("import", "./js/lab.mjs");
            _callback = DotNetObjectReference.Create(this);
            var session = await _editor.InvokeAsync<EditorState>("initialize", _sourceElement, _diffElement, _workspaceElement, _splitterElement,
                Samples.All[0].Source, _options, new {
                    PassIds = OptimizationPipeline.Passes.Select(pass => pass.Id),
                    DefaultPasses = _defaultPipeline.Stages.SelectMany(stage => stage.Steps).OrderBy(step => step.DefaultPass)
                        .DistinctBy(step => step.DefaultPass).Select(step => step.Pass),
                    OptimizationPipeline.MaxSteps, OptimizationPipeline.MaxStages, OptimizationPipeline.MaxIterations
                }, _callback);
            await _editor.InvokeVoidAsync("initializeTraceSplitter", _diffPanelElement, _traceSplitterElement);
            if (session.Source != Samples.All[0].Source) _sampleId = "custom";
            _options = session.Options;
            _shareError = session.ShareError;
            _languageService = new EditorLanguageService(_references);
            await _editor.InvokeVoidAsync("enableLanguageServices", _callback);
            _busy = false;
            await CompileAsync();
        } catch (Exception error) {
            _error = error.Message;
            _busy = false;
            StateHasChanged();
        }
    }

    [JSInvokable]
    public async Task CompileAsync()
    {
        if (_busy || _compiler == null) return;
        _busy = true;
        _error = null;
        _status = "Compiling & optimizing…";
        string? previousMethod = SelectedMethod?.Signature;
        _output = null;
        _decompiled = null;
        _viewError = null;
        StateHasChanged();
        try {
            var editor = _editor!;
            string source = await editor.InvokeAsync<string>("getSource");
            await editor.InvokeVoidAsync("nextFrame");
            _output = _compiler.Compile(source, _options);
            _dirty = false;
            _methodIndex = Array.FindIndex(_output.Methods.ToArray(), method => method.Signature == previousMethod);
            if (_methodIndex < 0 && _output.Methods.Count > 0) {
                _methodIndex = _output.Methods.Select((method, index) => (method, index)).MaxBy(item => item.method.ChangedPasses).index;
            }
            SelectFirstPass();
            await editor.InvokeVoidAsync("setDiagnostics", _output.Diagnostics, source);
            await UpdateViewAsync();
        } catch (Exception error) {
            _error = error.Message;
        } finally {
            _busy = false;
            StateHasChanged();
        }
    }

    [JSInvokable]
    public Task<EditorCompletions?> GetCompletionsAsync(string source, int position)
        => _languageService!.GetCompletionsAsync(source, position);

    [JSInvokable]
    public Task<string?> DescribeCompletionAsync(int sessionId, int index)
        => _languageService!.DescribeCompletionAsync(sessionId, index);

    [JSInvokable]
    public void ReleaseCompletions(int sessionId) => _languageService!.ReleaseCompletions(sessionId);

    [JSInvokable]
    public Task<EditorHover?> GetHoverAsync(string source, int position)
        => _languageService!.GetHoverAsync(source, position);

    [JSInvokable]
    public Task<EditorSignatureHelp?> GetSignatureHelpAsync(string source, int position)
        => _languageService!.GetSignatureHelpAsync(source, position);

    [JSInvokable]
    public Task<IReadOnlyList<SourceDiagnostic>> GetEditorDiagnosticsAsync(string source)
        => _languageService!.GetDiagnosticsAsync(source);

    [JSInvokable]
    public Task<IReadOnlyList<EditorSemanticToken>> GetSemanticTokensAsync(string source)
        => _languageService!.GetSemanticTokensAsync(source);

    [JSInvokable]
    public void SourceChanged()
    {
        _dirty = true;
        _sampleId = "custom";
        StateHasChanged();
    }

    private async Task OptionsChangedAsync(LabOptions options)
    {
        _options = options;
        _dirty = true;
        await _editor!.InvokeVoidAsync("saveDraft", _options);
    }

    private Task PipelineChangedAsync(PassPipeline? pipeline) => OptionsChangedAsync(_options with { Pipeline = pipeline });

    private async Task OpenPipelineAsync() => await _editor!.InvokeVoidAsync("openDialog", _pipelineDialogElement);
    private async Task ClosePipelineAsync() => await _editor!.InvokeVoidAsync("closeDialog", _pipelineDialogElement);

    private async Task ShareAsync()
    {
        _sharing = true;
        _shareError = null;
        _shareMessage = "";
        try {
            var editor = _editor!;
            var result = await editor.InvokeAsync<ShareLinkResult>("createShareLink", _options);
            if (result.Error != null) {
                _shareError = result.Error;
                return;
            }
            _shareUrl = result.Url;
            StateHasChanged();
            await editor.InvokeVoidAsync("nextFrame");
            await editor.InvokeVoidAsync("openShareDialog", _shareDialogElement, _shareInputElement);
        } finally {
            _sharing = false;
        }
    }

    private async Task CopyShareLinkAsync()
    {
        bool copied = await _editor!.InvokeAsync<bool>("copyShareLink", _shareUrl, _shareInputElement);
        _shareMessage = copied ? "Link copied." : "Select and copy the link above.";
    }

    private async Task CloseShareDialogAsync() => await _editor!.InvokeVoidAsync("closeDialog", _shareDialogElement);

    private async Task ChangeSampleAsync(ChangeEventArgs change)
    {
        var sample = Samples.All.Single(sample => sample.Id == (string)change.Value!);
        _sampleId = sample.Id;
        await _editor!.InvokeVoidAsync("setSource", sample.Source);
        _dirty = true;
        await CompileAsync();
    }

    private async Task ChangeMethodAsync(ChangeEventArgs change)
    {
        _methodIndex = int.Parse((string)change.Value!);
        SelectFirstPass();
        await RenderViewAsync();
    }

    private void SelectFirstPass()
    {
        var visible = VisiblePasses.ToArray();
        _passIndex = (visible.FirstOrDefault(pass => pass.Changed || pass.Error != null) ?? visible.FirstOrDefault())?.Number - 1 ?? -1;
    }

    private async Task SelectPassAsync(int index)
    {
        _passIndex = index;
        await RenderViewAsync();
    }

    private async Task FilterPassesAsync()
    {
        if (!VisiblePasses.Any(pass => pass.Number - 1 == _passIndex)) SelectFirstPass();
        await RenderViewAsync();
    }

    private async Task ChangeViewAsync(OutputView view)
    {
        _view = view;
        await RenderViewAsync();
    }

    private async Task ChangePassDisplayAsync(PassDisplay display)
    {
        _passDisplay = display;
        await RenderViewAsync();
    }

    private void ToggleTrace() => _traceOpen = !_traceOpen;

    private async Task ChangeThemeAsync(string theme)
    {
        _theme = theme;
        await JS.InvokeVoidAsync("cilicaLabTheme.setPreference", theme);
    }

    private async Task RenderViewAsync()
    {
        _busy = true;
        _status = IsCSharpView ? "Decompiling IL…" : ShowGraphs ? "Rendering control flow…" : "Updating view…";
        StateHasChanged();
        try {
            await _editor!.InvokeVoidAsync("nextFrame");
            await UpdateViewAsync();
        } finally {
            _busy = false;
            StateHasChanged();
        }
    }

    private async Task UpdateViewAsync()
    {
        _viewError = null;
        try {
            if (IsCSharpView && _output?.Assembly != null && _decompiled == null) {
                await _editor!.InvokeVoidAsync("nextFrame");
                _decompiled = _compiler!.Decompile(_output.OriginalAssembly!, _output.Assembly);
            }
            if (ShowGraphs) {
                await _editor!.InvokeVoidAsync("showGraphs", _beforeGraphElement, _afterGraphElement, SelectedPass?.Before.Graphviz, SelectedPass?.After.Graphviz);
            } else {
                string before = IsCSharpView ? _decompiled?.Original ?? "" : IsILView ? SelectedMethod?.OriginalIL ?? "" : SelectedPass?.Before.IR ?? "";
                string after = IsCSharpView ? _decompiled?.Optimized ?? "" : IsILView ? SelectedMethod?.GeneratedIL ?? "" : SelectedPass?.After.IR ?? "";
                await _editor!.InvokeVoidAsync("showDiff", before, after, IsCSharpView ? "csharp" : IsILView ? "cil" : "cilica-ir", _splitView);
            }
        } catch (Exception error) {
            _viewError = error.Message;
        }
    }

    private async Task ZoomGraphAsync(bool before, double factor)
        => await _editor!.InvokeVoidAsync("zoomGraph", before ? _beforeGraphElement : _afterGraphElement, factor);

    private async Task FitGraphAsync(bool before)
        => await _editor!.InvokeVoidAsync("fitGraph", before ? _beforeGraphElement : _afterGraphElement);

    private async Task DownloadAssemblyAsync()
        => await _editor!.InvokeVoidAsync("download", "CilicaLab.Snippet.dll", _output!.Assembly);

    public async ValueTask DisposeAsync()
    {
        if (_editor != null) {
            await _editor.InvokeVoidAsync("dispose");
            await _editor.DisposeAsync();
        }
        _callback?.Dispose();
        _languageService?.Dispose();
        _references?.Dispose();
    }
}
