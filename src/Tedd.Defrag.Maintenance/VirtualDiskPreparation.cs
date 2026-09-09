using Tedd.Defrag.Core;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Maintenance;

public static class VirtualDiskPreparation
{
    public const string Warning = "Use pre-zeroing only when a virtual-disk compaction workflow requires it. It writes substantial data and may expand thin storage. Verify host capacity. Zeroing does not itself compact the virtual-disk file.";
    public static long Zero(JobRequest request, VolumeInfo volume, Action<long> progress, Action checkpoint, CancellationToken token)
    {
        if (!request.ConfirmVirtualDiskZeroing) throw new ArgumentException(Warning);
        string path = Path.Combine(volume.Root, ".tedd-defrag-zero-" + request.Id.ToString("N") + ".tmp");
        byte[] zero = new byte[1024 * 1024]; long written = 0;
        // CreateNew + DeleteOnClose makes the filesystem own allocation and cleanup, including worker termination.
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.WriteThrough | FileOptions.DeleteOnClose | FileOptions.SequentialScan))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.Encrypted)) != 0)
                throw new IOException("Pre-zeroing requires an ordinary uncompressed, unencrypted, non-sparse temporary file.");
            long writeLimit = request.MaxMoveBytes == 0 ? long.MaxValue : request.MaxMoveBytes;
            while (written < writeLimit)
            {
                token.ThrowIfCancellationRequested(); checkpoint();
                long available = new DriveInfo(volume.Root).AvailableFreeSpace - request.FreeSpaceReserveBytes;
                int count = (int)Math.Min(zero.Length, Math.Min(available, writeLimit - written));
                if (count <= 0) break;
                stream.Write(zero.AsSpan(0, count)); written += count; progress(written);
            }
            stream.Flush(true);
        }
        // Deallocation is submitted after deleting the zero file, when the storage stack supports it.
        if (volume.TrimEnabled == true)
            WindowsMaintenance.Run(request with { Operation = Operation.ReTrim, SelectedPaths = [] }, volume, _ => { }, checkpoint, token);
        return written;
    }
}
