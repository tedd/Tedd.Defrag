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
