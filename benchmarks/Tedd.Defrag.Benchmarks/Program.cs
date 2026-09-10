using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Tedd.Defrag.Archive;
using Tedd.Defrag.Core;
using Tedd.Defrag.Planning;
using Tedd.Defrag.Visualization;

// BDN 0.15.8's SDK validator predates .NET 11. Its emit toolchain executes the actual
// net11.0 host and preserves MemoryDiagnoser without mislabelling the runtime as .NET 10.
BenchmarkSwitcher.FromAssembly(typeof(BitmapBenchmarks).Assembly).Run(args,
    DefaultConfig.Instance.AddJob(Job.ShortRun.WithId("NET11-InProcess").WithToolchain(InProcessEmitToolchain.Instance)));

[MemoryDiagnoser]
public class BitmapBenchmarks
{
    [Params(4096, 1048576)] public int Bytes { get; set; }
    private byte[] _data = [];
    [GlobalSetup] public void Setup() { _data = new byte[Bytes]; new Random(71).NextBytes(_data); }
    [Benchmark(Baseline = true)] public long ArchiveBitLoop() => BaselinesV1.BitByBitPopcount(_data);
    [Benchmark] public long HardwarePopcount() => BitmapOperations.CountAllocated(_data);
    [Benchmark] public long Avx2NibbleLookup() => BitmapKernel.CountAllocated(_data);
}
[MemoryDiagnoser]
public class FreeSpaceBenchmarks
{
    [Params(1000, 100000)] public int Intervals { get; set; }
    private ClusterRange[] _ranges = [];
    private FreeSpaceIndex _index = null!;
    [GlobalSetup] public void Setup()
    {
        _ranges = Enumerable.Range(0, Intervals).Select(i => new ClusterRange(i * 4096L, i == Intervals - 1 ? 1024 : 16)).ToArray();
        _index = new FreeSpaceIndex(_ranges);
    }
    [Benchmark(Baseline = true)] public long ArchiveLinear() => BaselinesV1.LinearFirstFit(_ranges, 512);
    [Benchmark] public long AugmentedIndex() => _index.FindFirstFit(512);
}
[MemoryDiagnoser]
public class MapAndPlannerBenchmarks
{
    private VolumeLayout _layout = null!;
    private readonly MapCell[] _cells = new MapCell[8192];
    private readonly LayoutPlanner _planner = new();
    private readonly JobRequest _request = new() { Volume = "V:", Operation = Operation.MinimumWrite };
    [GlobalSetup] public void Setup() => _layout = Tedd.Defrag.TestFixtures.SyntheticVolume.Create();
    [Benchmark] public void Aggregate8192Cells() => MapAggregator.Build(_layout, _cells);
    [Benchmark] public MovePlan Plan640Streams() => _planner.Plan(_layout, _request);
    [Benchmark] public MovePlan ArchivePlannerV1() => new Tedd.Defrag.Archive.V1.LayoutPlanner().Plan(_layout, _request);
}

[MemoryDiagnoser]
public class ParallelPlannerBenchmarks
{
    [Params(640, 32768)] public int Files { get; set; }
    private VolumeLayout _layout = null!;
    private readonly LayoutPlanner _planner = new();
    private readonly JobRequest _serial = new() { Volume = "V:\\", Operation = Operation.Alphabetical, MinimumFragments = 2, Resources = new() { PlanningWorkers = 1 } };
    private JobRequest _parallel = null!;
    [GlobalSetup]
    public void Setup()
    {
        long clusters = Files * 8L; byte[] bitmap = new byte[(clusters + 7) / 8];
        var files = Enumerable.Range(0, Files).Select(i => new FileLayout((ulong)i + 32,
            $"V:\\folder{i % 7}\\{(i * 7919L) % Files:000000}.bin", "", 0, 8192, 0, 0,
            [new(0, Files * 4L + i * 4, 1), new(1, Files * 4L + i * 4 + 2, 1)])).ToArray();
        foreach (var e in files.SelectMany(f => f.Extents)) BitmapOperations.SetRange(bitmap, e.Lcn, e.Length, true);
        var volume = new VolumeInfo("fixture", "V:\\", "fixture", "NTFS", clusters * 4096, 0, 4096, true, false, [], "fixture");
        _layout = new(volume, clusters, bitmap, files, DateTimeOffset.UtcNow, Files, 0, true, []);
        _parallel = _serial with { Resources = _serial.Resources with { PlanningWorkers = 4 } };
    }
    [Benchmark(Baseline = true)] public MovePlan Serial() => _planner.Plan(_layout, _serial);
    [Benchmark] public MovePlan FourWorkers() => _planner.Plan(_layout, _parallel);
}

[MemoryDiagnoser]
public class FreeRangeSimdBenchmarks
{
    [Params(false, true)] public bool Mixed { get; set; }
    private byte[] _bitmap = [];
    [GlobalSetup] public void Setup()
    {
        _bitmap = new byte[1024 * 1024];
        if (Mixed) new Random(91).NextBytes(_bitmap);
        else for (int i = 0; i < _bitmap.Length; i += 4096) _bitmap.AsSpan(i, 2048).Fill(255);
    }
    [Benchmark(Baseline = true)] public long ScalarWords() => Count(ScalarRanges(_bitmap, _bitmap.LongLength * 8));
    [Benchmark] public long SimdUniformBlocks() => Count(BitmapOperations.FreeRanges(_bitmap, _bitmap.LongLength * 8));
    private static long Count(IEnumerable<ClusterRange> ranges) { long count = 0; foreach (var range in ranges) count += range.Length; return count; }
    // Frozen pre-SIMD free-range walk for the same-input comparison.
    private static IEnumerable<ClusterRange> ScalarRanges(byte[] bitmap, long clusters)
    {
        long start = -1, i = 0;
        while (i < clusters)
        {
            if ((i & 63) == 0 && i + 64 <= clusters)
            {
                ulong word = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bitmap.AsSpan((int)(i >> 3)));
                if (word == 0) { if (start < 0) start = i; i += 64; continue; }
                if (word == ulong.MaxValue) { if (start >= 0) { yield return new(start, i - start); start = -1; } i += 64; continue; }
            }
            if (!BitmapOperations.IsSet(bitmap, i)) { if (start < 0) start = i; }
            else if (start >= 0) { yield return new(start, i - start); start = -1; }
            i++;
        }
        if (start >= 0) yield return new(start, clusters - start);
    }
}
