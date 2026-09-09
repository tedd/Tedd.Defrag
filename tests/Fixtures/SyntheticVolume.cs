using Tedd.Defrag.Core;

namespace Tedd.Defrag.TestFixtures;

/// <summary>Deterministic filesystem fixture shared by tests and benchmarks; never included in application assemblies.</summary>
public static class SyntheticVolume
{
    public static VolumeLayout Create(int seed = 731)
    {
        const int clusters = 262144, bytesPerCluster = 4096;
        byte[] bitmap = new byte[clusters / 8];
        var random = new Random(seed);
        var files = new List<FileLayout>();
        string[] folders = ["Projects", "Media", "Documents", "Windows", "Archives", "Games"];
        long cursor = 0;
        for (int i = 0; i < 640 && cursor < clusters - 1200; i++)
        {
            cursor += random.Next(2, 100);
            int fragments = random.Next(100) < 30 ? random.Next(2, 6) : 1;
            var extents = new List<Extent>(); long vcn = 0;
            for (int j = 0; j < fragments; j++)
            {
                long n = random.Next(25, 180);
                extents.Add(new(vcn, cursor, n)); BitmapOperations.SetRange(bitmap, cursor, n, true);
                cursor += n + (j + 1 < fragments ? random.Next(20, 100) : 0); vcn += n;
            }
            string path = $"V:\\{folders[i % folders.Length]}\\{(i % 3 == 0 ? "workspace" : "asset")}-{i:0000}.{(i % 4 == 0 ? "bin" : "dat")}";
            files.Add(new((ulong)(i + 32), path, "", i % 37 == 0 ? StreamFlags.Metadata : StreamFlags.None, vcn * bytesPerCluster,
                DateTime.UtcNow.AddDays(-random.Next(365)).Ticks, DateTime.UtcNow.AddDays(-random.Next(30)).Ticks, extents.ToArray()));
        }
        long free = clusters - BitmapOperations.CountAllocated(bitmap);
        var volume = new VolumeInfo("TEST_FIXTURE", "V:\\", "Test fixture", "NTFS", (long)clusters * bytesPerCluster,
            free * bytesPerCluster, bytesPerCluster, true, false, ["fixture-device"], "Synthetic fixture");
        return new(volume, clusters, bitmap, files.ToArray(), DateTimeOffset.UtcNow, files.Count, 0, true, []);
    }
}
