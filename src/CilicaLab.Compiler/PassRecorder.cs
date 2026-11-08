using System.Diagnostics;

using Cilica;
using Cilica.AsmIO;
using Cilica.IR.Utils;
using Cilica.Passes;

namespace CilicaLab.Compiler;

internal sealed class PassRecorder(PassTraceLogger logger) : IPassInspector
{
    public Dictionary<MethodDef, List<PassSnapshot>> Timelines { get; } = [];

    private MethodSnapshot _before = null!;
    private long _started;

    public void OnBeforePass(IMethodPass pass, MethodTransformContext context)
    {
        _before = Print(context);
        logger.BeginCapture();
        _started = Stopwatch.GetTimestamp();
    }

    public void OnAfterPass(IMethodPass pass, MethodTransformContext context, MethodPassResult result)
    {
        double milliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        var trace = logger.EndCapture();
        var after = result.Changes == MethodInvalidations.None ? _before : Print(context);
        Add(pass, context, after, result.Changes != MethodInvalidations.None, milliseconds, null, trace);
    }

    public void OnPassFailed(IMethodPass pass, MethodTransformContext context, Exception error)
    {
        double milliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        // Failed IR is not a committed result; the pass manager retains the input IL.
        Add(pass, context, _before, false, milliseconds, error.Message, logger.EndCapture());
    }

    private void Add(IMethodPass pass, MethodTransformContext context, MethodSnapshot after, bool changed, double milliseconds, string? error,
        IReadOnlyList<TraceEntry> trace)
    {
        if (!Timelines.TryGetValue(context.Definition, out var timeline)) {
            Timelines.Add(context.Definition, timeline = []);
        }
        timeline.Add(new PassSnapshot(timeline.Count + 1, pass.GetType().Name, _before, after, changed, milliseconds, error, trace));
    }

    private static MethodSnapshot Print(MethodTransformContext context)
    {
        using var writer = new StringWriter();
        IRPrinter.ExportPlain(context.Method, writer);
        string ir = writer.ToString();
        writer.GetStringBuilder().Clear();
        IRPrinter.ExportDot(context.Method, writer);
        return new MethodSnapshot(ir, writer.ToString());
    }
}
