using System.Numerics;
using System.Runtime.Intrinsics;

namespace Tedd.Defrag.Core;

/// <summary>Application worker activity; neither OS thread count nor device queue depth.</summary>
public sealed record WorkProgress(string Phase, long Completed, long Total, string Unit,
    int WorkerLimit = 1, int ActiveWorkers = 0, int PeakWorkers = 0,
    int InFlightIo = 0, int PeakIo = 0, long BytesProcessed = 0, long ElapsedMilliseconds = 0,
    string Acceleration = "Scalar CPU", string Detail = "");

public sealed record JobDiagnostics(WorkProgress? Scan, WorkProgress? Planning, WorkProgress? Execution,
    int LogicalProcessors, int ProcessThreads, long PrivateBytes, double CpuMilliseconds,
    string MapAcceleration);

public static class WorkerPolicy
{
    public static int CpuWorkers(ResourcePolicy policy)
    {
        int eligible = Environment.ProcessorCount;
        if (policy.AffinityMask != 0) eligible = Math.Min(eligible, BitOperations.PopCount(policy.AffinityMask));
        return Math.Clamp((int)Math.Ceiling(eligible * policy.CpuPercent / 100d), 1, 32);
    }
    public static int ScanWorkers(ResourcePolicy policy)
    {
        int workers = policy.ScanWorkers > 0 ? policy.ScanWorkers : Math.Min(policy.Background ? 2 : 4, CpuWorkers(policy));
        // Retained metadata dominates memory; reserve headroom for concurrent parsed batches too.
        return policy.MemoryMiB == 0 ? workers : Math.Min(workers, Math.Max(1, policy.MemoryMiB / 128));
    }
    public static int PlanningWorkers(ResourcePolicy policy, int files) => Math.Min(
        policy.PlanningWorkers > 0 ? policy.PlanningWorkers : CpuWorkers(policy), Math.Max(1, files / 4096));
    public static string BitmapAcceleration => Vector256.IsHardwareAccelerated ? "SIMD 256-bit uniform bitmap scans" :
        Vector128.IsHardwareAccelerated ? "SIMD 128-bit uniform bitmap scans" : "Scalar 64-bit bitmap scans";
}

public sealed class WorkerActivity
{
    private int active, peak, io, peakIo;
    public int Active => Volatile.Read(ref active);
    public int Peak => Volatile.Read(ref peak);
    public int Io => Volatile.Read(ref io);
    public int PeakIo => Volatile.Read(ref peakIo);
    public void Enter() => RaisePeak(ref peak, Interlocked.Increment(ref active));
    public void Exit() => Interlocked.Decrement(ref active);
    public void EnterIo() => RaisePeak(ref peakIo, Interlocked.Increment(ref io));
    public void ExitIo() => Interlocked.Decrement(ref io);
    private static void RaisePeak(ref int peak, int value)
    {
        int previous;
        while (value > (previous = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, value, previous) != previous) { }
    }
}
