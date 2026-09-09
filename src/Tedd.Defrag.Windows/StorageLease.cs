using System.Security.Cryptography;
using System.Text;

namespace Tedd.Defrag.Windows;

/// <summary>Cross-process storage locks survive broker failure. Leases are released by Windows if the worker exits.</summary>
public sealed class StorageLease : IDisposable
{
    private readonly List<Mutex> _held = [];
    public StorageLease(string volumeIdentity, string[] resources, bool sharedOverride, CancellationToken token, Func<bool>? cancelled = null)
    {
        // A mutex remains mandatory per volume even when physical-device concurrency is explicitly enabled.
        var names = new[] { "volume:" + volumeIdentity }.Concat(sharedOverride ? [] : resources.Select(s => "resource:" + s))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string name in names)
            {
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())));
                var mutex = new Mutex(false, "Global\\Tedd.Defrag.Storage." + key);
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (cancelled?.Invoke() == true) throw new OperationCanceledException("Cancelled while waiting for storage access.");
                        try { if (mutex.WaitOne(250)) break; }
                        catch (AbandonedMutexException) { break; } // The next scan still re-observes everything.
                    }
                    _held.Add(mutex);
                }
                catch { mutex.Dispose(); throw; }
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        for (int i = _held.Count - 1; i >= 0; i--) { _held[i].ReleaseMutex(); _held[i].Dispose(); }
        _held.Clear();
    }
}
