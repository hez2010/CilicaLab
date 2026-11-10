using System.Runtime.CompilerServices;

using Cilica;
using Cilica.Passes;

namespace CilicaLab.Compiler;

internal static class DefaultPipelineReader
{
    internal static (PassPipeline Pipeline, IReadOnlyList<IMethodPass> Passes) Read(PassManager manager)
    {
        var segments = manager.Pipeline.ToList();
        for (int i = 0; i < segments.Count; i++) {
            if (GetContinuation(segments[i]) is { } child && !segments.Contains(child)) segments.Add(child);
        }
        var passes = new List<IMethodPass>();
        var templates = new Dictionary<IMethodPass, int>(ReferenceEqualityComparer.Instance);
        var stages = segments.Select((segment, index) => new PipelineStage(
            Id: $"stage-{index + 1}",
            Steps: GetPasses(segment).Select(pass => {
                if (!templates.TryGetValue(pass, out int template)) {
                    templates.Add(pass, template = passes.Count);
                    passes.Add(pass);
                }
                return new PipelineStep(pass.GetType().FullName!, DefaultPass: template);
            }).ToArray(),
            MaxIterations: GetMaxIterations(segment),
            RunInSequence: manager.Pipeline.Contains(segment),
            ApplyIndependently: GetApplyIndependently(segment),
            IfChanged: GetContinuation(segment) is { } child ? $"stage-{segments.IndexOf(child) + 1}" : null
        )).ToArray();
        return (new PassPipeline(stages), passes);
    }

    // Cilica exposes construction but not inspection. Keep access to the pinned package's internals here;
    // scheduling, shared continuations, and configured pass instances come from its actual default plan.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_passes")]
    private static extern ref List<IMethodPass> GetPasses(PassManager.PipelineSegment segment);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_contIfChanged")]
    private static extern ref PassManager.PipelineSegment? GetContinuation(PassManager.PipelineSegment segment);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_maxIters")]
    private static extern ref int GetMaxIterations(PassManager.PipelineSegment segment);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_applyIndependently")]
    private static extern ref bool GetApplyIndependently(PassManager.PipelineSegment segment);
}
