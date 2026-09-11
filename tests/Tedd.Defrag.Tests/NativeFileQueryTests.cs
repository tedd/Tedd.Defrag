using Tedd.Defrag.Windows;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class NativeFileQueryTests
{
    [Fact]
    public void OwnedFileCanBeAnalyzedThroughFilesystemQueries()
    {
        // Exercise the host filesystem (including ReFS when built on a ReFS drive)
        // without opening a privileged volume handle or moving any extents.
        string path = Path.Combine(AppContext.BaseDirectory, $"extent-fixture-{Guid.NewGuid():N}.bin");
        byte[] contents = new byte[256 * 1024]; new Random(42).NextBytes(contents);
        try
        {
            File.WriteAllBytes(path, contents);
            var volume = VolumeDiscovery.Get(Path.GetPathRoot(path)!);
            using var handle = NativeIo.Open(path);
            Assert.Equal(path, FileSystemQueries.FinalPath(handle), ignoreCase: true);
            Assert.Equal(0u, FileSystemQueries.Attributes(handle) & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint));
            var extents = FileSystemQueries.NormalizeExtents(FileSystemQueries.RetrievalPointers(handle), volume.SizeBytes / volume.BytesPerCluster);
            Assert.NotEmpty(extents);
            Assert.Equal(contents.Length, extents.Sum(extent => extent.Length) * volume.BytesPerCluster);
        }
        finally { File.Delete(path); }
    }
}
