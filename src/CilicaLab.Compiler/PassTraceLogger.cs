using Cilica;

namespace CilicaLab.Compiler;

/// <summary> Records the messages Cilica logs while an inspected pass runs. Logging is disabled outside a capture. </summary>
internal sealed class PassTraceLogger : ICompilationLogger
{
    private readonly List<Scope> _scopes = [];
    private List<TraceEntry>? _entries;
    private int _baseDepth, _lastScopeId;

    public LogLevel MinLevel => LogLevel.Trace;

    public bool IsEnabled(LogLevel level) => _entries != null && level >= (_scopes.Count == 0 ? LogLevel.Trace : _scopes[^1].MinLevel);

    public void BeginCapture()
    {
        _entries = [];
        _baseDepth = _scopes.Count;
    }

    public IReadOnlyList<TraceEntry> EndCapture()
    {
        var entries = _entries ?? [];
        _entries = null;
        return entries;
    }

    public void Log(LogLevel level, ReadOnlySpan<char> msg, Exception? exception = null)
    {
        if (!IsEnabled(level)) return;

        // Scopes are written lazily so nested passes and probes that log nothing stay out of the trace.
        for (int i = _baseDepth; i < _scopes.Count; i++) {
            var scope = _scopes[i];
            if (scope.Written) continue;
            scope.Written = true;
            Add(new TraceEntry(LogLevel.Trace, i - _baseDepth, scope.Message ?? scope.Name, true));
        }
        string message = exception == null ? msg.ToString() : $"{msg}{Environment.NewLine}{exception}";
        Add(new TraceEntry(level, _scopes.Count - _baseDepth, message, false));
    }

    public LoggerScopeHandle Push(in LoggerScopeInfo info, string? msg = null)
    {
        var minLevel = _scopes.Count == 0 || info.MinLevel > _scopes[^1].MinLevel ? info.MinLevel : _scopes[^1].MinLevel;
        _scopes.Add(new Scope(++_lastScopeId, info.Name, msg, minLevel));
        return new LoggerScopeHandle(this, _lastScopeId);
    }

    public void Pop(in LoggerScopeHandle handle)
    {
        int id = handle.SyncId;
        int index = _scopes.FindLastIndex(scope => scope.Id == id);
        if (index < 0) return;
        _scopes.RemoveRange(index, _scopes.Count - index);
        _baseDepth = Math.Min(_baseDepth, _scopes.Count);
    }

    private void Add(TraceEntry entry)
    {
        _entries!.Add(entry);
    }

    private sealed class Scope(int id, string name, string? message, LogLevel minLevel)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public string? Message { get; } = message;
        public LogLevel MinLevel { get; } = minLevel;
        public bool Written { get; set; }
    }
}
