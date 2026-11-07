using System.Collections.Immutable;
using System.IO.Compression;

using Microsoft.CodeAnalysis;

namespace CilicaLab.Compiler;

public sealed class FrameworkReferences : IDisposable
{
    public string DirectoryPath { get; }
    public ImmutableArray<MetadataReference> Metadata { get; }

    private FrameworkReferences(string directoryPath)
    {
        DirectoryPath = directoryPath;
        Metadata = Directory.GetFiles(directoryPath, "*.dll")
            .Order(StringComparer.Ordinal)
            .Select(path => (MetadataReference)MetadataReference.CreateFromImage(File.ReadAllBytes(path), filePath: path))
            .ToImmutableArray();
    }

    public static FrameworkReferences FromArchive(Stream archive)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cilicalab-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(archive, directory);
        return new FrameworkReferences(directory);
    }

    public void Dispose()
    {
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
