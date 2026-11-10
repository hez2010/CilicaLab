using System.ComponentModel;
using System.Reflection;

using Cilica;
using Cilica.Passes;

namespace CilicaLab.Compiler;

public sealed record PipelineStep(string Pass, bool Enabled = true, int? DefaultPass = null);

/// <summary> A sequence of passes with bounded repetition and an optional continuation when it changes a method. </summary>
public sealed record PipelineStage(string Id, IReadOnlyList<PipelineStep> Steps, int MaxIterations = 1,
    bool RunInSequence = true, bool ApplyIndependently = false, string? IfChanged = null);

public sealed record PassPipeline(IReadOnlyList<PipelineStage> Stages);

public sealed class OptimizationPass
{
    public string Id { get; }
    public string Name { get; }
    public string? Description { get; }
    internal Action<PassManager.PipelineSegment> Apply { get; }

    private OptimizationPass(string id, string name, string? description, Action<PassManager.PipelineSegment> apply)
        => (Id, Name, Description, Apply) = (id, name, description, apply);

    internal static OptimizationPass FromType(Type type)
    {
        var method = typeof(PassManager.PipelineSegment).GetMethods()
            .Single(method => method.Name == nameof(PassManager.PipelineSegment.Apply) && method.IsGenericMethodDefinition);
        var apply = method.MakeGenericMethod(type).CreateDelegate<Func<PassManager.PipelineSegment, PassManager.PipelineSegment>>();
        return new(type.FullName!, type.Name, type.GetCustomAttribute<DescriptionAttribute>()?.Description, segment => apply(segment));
    }
}

public static class OptimizationPipeline
{
    public const int MaxSteps = 64;
    public const int MaxStages = 32;
    public const int MaxIterations = 10;

    public static IReadOnlyList<OptimizationPass> Passes { get; } = Array.AsReadOnly(typeof(IMethodPass).Assembly.GetTypes()
        .Where(type => type.IsVisible && type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters && typeof(IMethodPass).IsAssignableFrom(type))
        .OrderBy(type => type.Name, StringComparer.Ordinal).Select(OptimizationPass.FromType).ToArray());

    private static readonly IReadOnlyDictionary<string, OptimizationPass> PassesById = Passes.ToDictionary(pass => pass.Id, StringComparer.Ordinal);

    /// <summary> Builds the built-in pipeline or an edited pipeline, preserving the configuration of passes copied from the default. </summary>
    public static PassManager Build(Cilica.Compilation compilation, PassPipeline? pipeline)
    {
        var manager = CompilationPipeline.CreateDefault(compilation);
        if (pipeline == null) return manager;

        var defaults = DefaultPipelineReader.Read(manager);
        Validate(pipeline, defaults.Passes);
        manager.Pipeline.Clear();
        var segments = pipeline.Stages.ToDictionary(stage => stage.Id, stage => manager.AddPasses(stage.ApplyIndependently), StringComparer.Ordinal);
        foreach (var stage in pipeline.Stages) {
            var segment = segments[stage.Id].RepeatUntilFixedPoint(stage.MaxIterations);
            foreach (var step in stage.Steps.Where(step => step.Enabled)) {
                if (step.DefaultPass is int template) segment.Apply(defaults.Passes[template]);
                else PassesById[step.Pass].Apply(segment);
            }
            if (stage.IfChanged != null) segment.IfChanged(segments[stage.IfChanged]);
        }
        manager.Pipeline.Clear();
        manager.Pipeline.AddRange(pipeline.Stages.Where(stage => stage.RunInSequence).Select(stage => segments[stage.Id]));
        return manager;
    }

    private static void Validate(PassPipeline pipeline, IReadOnlyList<IMethodPass> defaults)
    {
        if (pipeline.Stages == null || pipeline.Stages.Count is < 1 or > MaxStages) {
            throw new ArgumentException($"A custom pipeline must contain between 1 and {MaxStages} stages.", nameof(pipeline));
        }
        var stages = new Dictionary<string, PipelineStage>(StringComparer.Ordinal);
        int steps = 0;
        foreach (var stage in pipeline.Stages) {
            if (stage == null || string.IsNullOrEmpty(stage.Id) || !stages.TryAdd(stage.Id, stage) || stage.Steps == null ||
                stage.MaxIterations is < 1 or > MaxIterations || (steps += stage.Steps.Count) > MaxSteps) {
                throw new ArgumentException("Invalid pipeline stage, step count, or iteration limit.", nameof(pipeline));
            }
            foreach (var step in stage.Steps) {
                if (step?.Pass == null || (step.DefaultPass is int template
                    ? template < 0 || template >= defaults.Count || defaults[template].GetType().FullName != step.Pass
                    : !PassesById.ContainsKey(step.Pass))) {
                    throw new ArgumentException($"Unknown optimization pass or default configuration '{step?.Pass}'.", nameof(pipeline));
                }
            }
        }
        foreach (var stage in pipeline.Stages) {
            var visited = new HashSet<string>(StringComparer.Ordinal) { stage.Id };
            string? next = stage.IfChanged;
            while (next != null) {
                if (!visited.Add(next) || !stages.TryGetValue(next, out var child)) {
                    throw new ArgumentException("Conditional stages must exist and cannot form a cycle.", nameof(pipeline));
                }
                next = child.IfChanged;
            }
        }
    }
}

