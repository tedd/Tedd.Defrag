using Tedd.Defrag.Core;

namespace Tedd.Defrag.Scheduling;

/// <summary>Atomic acquisition avoids deadlocks. Older conflicting requests reserve priority; disjoint disks may bypass.</summary>
public sealed class ResourceScheduler
{
    private sealed record Pending(Guid Id, string Volume, string[] Resources);
    private readonly object _sync = new();
    private readonly List<Pending> _waiting = [];
    private readonly Dictionary<Guid, Pending> _active = [];
    public bool TryAcquire(Guid id, string volume, string[] resources, SchedulerSettings settings)
    {
        lock (_sync)
        {
            if (_active.ContainsKey(id)) return true;
            var request = _waiting.FirstOrDefault(p => p.Id == id);
            if (request == null) { request = new(id, volume, resources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()); _waiting.Add(request); }
            if (_active.Count >= settings.MaxConcurrentVolumes || _active.Values.Any(p => p.Volume.Equals(volume, StringComparison.OrdinalIgnoreCase))) return false;
            foreach (var older in _waiting)
            {
                if (older.Id == id) break;
                if (older.Volume.Equals(volume, StringComparison.OrdinalIgnoreCase) || older.Resources.Intersect(request.Resources, StringComparer.OrdinalIgnoreCase).Any()) return false;
            }
            int limit = settings.AllowParallelOnSharedStorage ? settings.MaxConcurrentJobsPerSharedResource : 1;
            foreach (var resource in request.Resources)
                if (_active.Values.Count(p => p.Resources.Contains(resource, StringComparer.OrdinalIgnoreCase)) >= limit) return false;
            _waiting.Remove(request); _active.Add(id, request); return true;
        }
    }
    public void Release(Guid id) { lock (_sync) { _active.Remove(id); _waiting.RemoveAll(p => p.Id == id); } }
}
