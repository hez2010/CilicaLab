using System.Reflection.PortableExecutable;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

namespace CilicaLab.Compiler;

internal sealed class AssemblyDecompiler(string referenceDirectory) : IAssemblyResolver, IDisposable
{
    private readonly Dictionary<string, MetadataFile> _assemblies = new(StringComparer.OrdinalIgnoreCase);

    public string Decompile(byte[] assembly)
    {
        using var file = new PEFile("CilicaLab.Snippet.dll", new MemoryStream(assembly, writable: false), PEStreamOptions.PrefetchEntireImage);
        var decompiler = new CSharpDecompiler(file, this, new DecompilerSettings());
        return decompiler.DecompileWholeModuleAsString();
    }

    public MetadataFile? Resolve(IAssemblyReference reference) => ResolveFile(reference.Name + ".dll");
    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) => ResolveFile(moduleName);

    // Browser reference files are already local, so resolution completes without scheduling worker threads.
    public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));
    public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));
    public IDisposable? BeginSnapshot() => null;

    private MetadataFile? ResolveFile(string name)
    {
        if (_assemblies.TryGetValue(name, out var assembly)) return assembly;
        string path = Path.Combine(referenceDirectory, name);
        if (!File.Exists(path)) return null;
        assembly = new PEFile(path, PEStreamOptions.PrefetchEntireImage);
        _assemblies.Add(name, assembly);
        return assembly;
    }

    public void Dispose()
    {
        foreach (var assembly in _assemblies.Values) assembly.Dispose();
    }
}
