using Cilica;

namespace CilicaLab.Compiler;

public sealed record LabOptions(
    bool Release = true,
    bool EnableInlining = true,
    bool AllowCrossAssemblyIPO = true,
    PassPipeline? Pipeline = null,
    bool VerifyEachPass = true,
    bool AssumeLittleEndian = true,
    bool PreserveTrimmingDependencies = false,
    bool NativeAotCompatibility = false,
    int MaxInlinedMethodILSize = 32 * 1024)
{
    internal CompilationSettings ToCompilationSettings()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxInlinedMethodILSize);
        return new CompilationSettings {
            EnableInlining = EnableInlining,
            AllowCrossAssemblyIPO = AllowCrossAssemblyIPO,
            VerifyEachPass = VerifyEachPass,
            AssumeLittleEndian = AssumeLittleEndian,
            PreserveTrimmingDependencies = PreserveTrimmingDependencies,
            NativeAotCompatibility = NativeAotCompatibility,
            MaxInlinedMethodILSize = MaxInlinedMethodILSize
        };
    }
}
