using System.Collections.Concurrent;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using MiniArch.Core;

namespace MiniArchBenchmarks;

public class SnapshotBenchmarks
{
    [Params(100, 500, 1000, 2000)]
    public int EntityCount { get; set; }

    private SnapshotSaveState _saveState = null!;
    private SnapshotLoadState _loadState = null!;

    [IterationSetup(Target = nameof(MiniArch_Snapshot_Save))]
    public void SetupSave() => _saveState = SnapshotSaveState.Create(EntityCount);

    [IterationSetup(Target = nameof(MiniArch_Snapshot_Load))]
    public void SetupLoad() => _loadState = SnapshotLoadState.Create(EntityCount);

    [IterationCleanup(Target = nameof(MiniArch_Snapshot_Save))]
    public void CleanupSave() => _saveState.Dispose();

    [IterationCleanup(Target = nameof(MiniArch_Snapshot_Load))]
    public void CleanupLoad() => _loadState.Dispose();

    [Benchmark(Description = "MiniArch snapshot save")]
    public void MiniArch_Snapshot_Save()
    {
        var state = _saveState;
        state.Stream.Position = 0;
        state.Stream.SetLength(0);

        WorldSnapshot.Save(state.Stream, state.World);
    }

    [Benchmark(Description = "MiniArch snapshot load")]
    public void MiniArch_Snapshot_Load()
    {
        var state = _loadState;
        state.Stream.Position = 0;

        state.LoadedWorld = WorldSnapshot.Load(state.Stream);
    }

    private static World CreateWorld(int entityCount)
    {
        var world = new World();
        var entities = new Entity[entityCount];

        for (var index = 0; index < entityCount; index++)
        {
            entities[index] = world.CreateEmpty();
        }

        for (var index = 0; index < entityCount; index++)
        {
            var entity = entities[index];
            world.Add(entity, new SnapshotComponent01(index));
            world.Add(entity, new SnapshotComponent02(index + 1));
            world.Add(entity, new SnapshotComponent03(index + 2));
            world.Add(entity, new SnapshotComponent04(index + 3));
            world.Add(entity, new SnapshotComponent05(index + 4));
            world.Add(entity, new SnapshotComponent06(index + 5));
            world.Add(entity, new SnapshotComponent07(index + 6));
            world.Add(entity, new SnapshotComponent08(index + 7));
            world.Add(entity, new SnapshotComponent09(index + 8));
            world.Add(entity, new SnapshotComponent10(index + 9));
        }

        return world;
    }

    private sealed class SnapshotSaveState : IDisposable
    {
        private SnapshotSaveState(World world, MemoryStream stream)
        {
            World = world;
            Stream = stream;
        }

        public World World { get; }

        public MemoryStream Stream { get; }

        public static SnapshotSaveState Create(int entityCount)
        {
            var world = CreateWorld(entityCount);

            using var sizingStream = new MemoryStream();
            WorldSnapshot.Save(sizingStream, world);

            var snapshotSizeBytes = sizingStream.Length;
            SnapshotBenchmarkMetrics.RecordSnapshotSize(entityCount, snapshotSizeBytes);

            var stream = new MemoryStream(checked((int)snapshotSizeBytes));
            return new SnapshotSaveState(world, stream);
        }

        public void Dispose()
        {
            Stream.Dispose();
        }
    }

    private sealed class SnapshotLoadState : IDisposable
    {
        private SnapshotLoadState(MemoryStream stream)
        {
            Stream = stream;
        }

        public MemoryStream Stream { get; }

        public World? LoadedWorld { get; set; }

        public static SnapshotLoadState Create(int entityCount)
        {
            var world = CreateWorld(entityCount);
            using var sizingStream = new MemoryStream();
            WorldSnapshot.Save(sizingStream, world);

            var bytes = sizingStream.ToArray();
            SnapshotBenchmarkMetrics.RecordSnapshotSize(entityCount, bytes.Length);

            return new SnapshotLoadState(new MemoryStream(bytes, writable: false));
        }

        public void Dispose()
        {
            Stream.Dispose();
        }
    }
}

internal static class SnapshotBenchmarkMetrics
{
    private static readonly ConcurrentDictionary<int, long> SnapshotSizes = new();

    public static void RecordSnapshotSize(int entityCount, long snapshotSizeBytes)
    {
        SnapshotSizes[entityCount] = snapshotSizeBytes;
        var path = GetSnapshotSizePath(entityCount);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, snapshotSizeBytes.ToString(CultureInfo.InvariantCulture));
    }

    public static bool TryGetSnapshotSize(int entityCount, out long snapshotSizeBytes)
    {
        if (SnapshotSizes.TryGetValue(entityCount, out snapshotSizeBytes))
        {
            return true;
        }

        var path = GetSnapshotSizePath(entityCount);
        if (!File.Exists(path))
        {
            snapshotSizeBytes = 0;
            return false;
        }

        return long.TryParse(File.ReadAllText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out snapshotSizeBytes);
    }

    public static string GetSnapshotSizePath(int entityCount)
    {
        return Path.Combine(
            Path.GetTempPath(),
            "MiniArch.Benchmarks",
            "SnapshotBenchmarks",
            $"{entityCount}.txt");
    }
}

internal readonly struct SnapshotComponent01
{
    public SnapshotComponent01(int value) => Value = value;
    public int Value { get; }
}

internal readonly struct SnapshotComponent02
{
    public SnapshotComponent02(int value) => Value = value;
    public int Value { get; }
}

internal readonly struct SnapshotComponent03
{
    public SnapshotComponent03(int value) => Value = value;
    public int Value { get; }
}

internal readonly struct SnapshotComponent04
{
    public SnapshotComponent04(int value) => Value = value;
    public int Value { get; }
}

internal readonly struct SnapshotComponent05
{
    public SnapshotComponent05(int value) => Value = value;
    public int Value { get; }
}

internal readonly struct SnapshotComponent06
{
    public SnapshotComponent06(int value) => Value = value;
    public int Value { get; }
}

internal readonly struct SnapshotComponent07
{
    public SnapshotComponent07(int value) => Value = value;
    public int Value { get; }
}

internal readonly struct SnapshotComponent08
{
    public SnapshotComponent08(int value) => Value = value;
    public int Value { get; }
}

internal readonly struct SnapshotComponent09
{
    public SnapshotComponent09(int value) => Value = value;
    public int Value { get; }
}

internal readonly struct SnapshotComponent10
{
    public SnapshotComponent10(int value) => Value = value;
    public int Value { get; }
}
