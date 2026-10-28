using Tedd.Defrag.Core;

namespace Tedd.Defrag.Engine;

internal static class JobProgressEstimate
{
    // Placement constraints and repeated relocation can change the actual work.
    // Keep the initial eligible allocation as a stable, batch-independent estimate.
    internal static long MoveBytes(VolumeLayout layout, JobRequest request, PathRules rules, Action? checkpoint = null)
    {
        bool ordered = request.Operation is Operation.Alphabetical or Operation.Size or Operation.Created
            or Operation.Modified or Operation.Extension or Operation.DirectoryLocality;
        long clusters = 0;
        long boundary = request.ShrinkBoundaryBytes / layout.Volume.BytesPerCluster;
        for (int index = 0; index < layout.Files.Length; index++)
        {
            if ((index & 4095) == 0) checkpoint?.Invoke();
            var file = layout.Files[index];
            if (!file.Movable || !rules.IsSelected(file.Path) || rules.IsExcluded(file.Path)) continue;
            bool metadata = (file.Flags & StreamFlags.Metadata) != 0;
            bool directory = (file.Flags & StreamFlags.Directory) != 0;
            if (request.Operation == Operation.OptimizeMft ? (file.FileId & 0xFFFFFFFFFFFF) != 0 : metadata) continue;
            if (request.Operation == Operation.DirectoryIndexes ? !directory : directory && request.Operation != Operation.DirectoryLocality) continue;
            if (file.Size < request.MinimumFileBytes || request.MaximumFileBytes > 0 && file.Size > request.MaximumFileBytes) continue;
            if (request.Operation is Operation.MinimumWrite or Operation.FilesOnly && file.Extents.Length < request.MinimumFragments) continue;
            if (!ordered && request.Operation is not (Operation.Pack or Operation.PackAndDefrag or Operation.PrepareShrink) && !file.Fragmented) continue;
            foreach (var extent in file.Extents)
            {
                if (extent.IsSparse) continue;
                clusters = checked(clusters + (request.Operation == Operation.PrepareShrink
                    ? Math.Max(0, extent.End - Math.Max(extent.Lcn, boundary)) : extent.Length));
            }
        }
        long bytes = checked(clusters * layout.Volume.BytesPerCluster);
        return request.MaxMoveBytes > 0 ? Math.Min(bytes, request.MaxMoveBytes) : bytes;
    }
}
