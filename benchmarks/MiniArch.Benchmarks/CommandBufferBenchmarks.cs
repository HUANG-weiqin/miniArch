using BenchmarkDotNet.Attributes;
using MiniArch.Core;

namespace MiniArch.Benchmarks;

public class CommandBufferBenchmarks
{
    [Params(128, 1000, 10000)]
    public int EntityCount { get; set; }

    private World? _recordWorld;
    private CommandBuffer? _recordBuffer;
    private Entity[]? _recordExisting;

    private World? _playbackWorld;
    private CommandBuffer? _playbackBuffer;

    private World? _replayWorld;
    private FrameCommands _replayFrame;

    private World? _playWorld;
    private CommandBuffer? _playBuffer;

    private World? _endToEndWorld;
    private CommandBuffer? _endToEndBuffer;

    [IterationSetup(Target = nameof(MiniArch_CommandBuffer_Record))]
    public void SetupRecord()
    {
        _recordWorld = new World();
        _recordExisting = CreateEntities(_recordWorld, EntityCount);
        _recordBuffer = new CommandBuffer(_recordWorld);
    }

    [IterationCleanup(Target = nameof(MiniArch_CommandBuffer_Record))]
    public void CleanupRecord()
    {
        _recordWorld = null;
        _recordBuffer = null;
        _recordExisting = null;
    }

    [IterationSetup(Target = nameof(MiniArch_CommandBuffer_Playback_Only))]
    public void SetupPlayback()
    {
        (_playbackWorld, _playbackBuffer) = CreateRecordedScenario(EntityCount);
    }

    [IterationCleanup(Target = nameof(MiniArch_CommandBuffer_Playback_Only))]
    public void CleanupPlayback()
    {
        _playbackWorld = null;
        _playbackBuffer = null;
    }

    [IterationSetup(Target = nameof(MiniArch_CommandBuffer_Replay_Only))]
    public void SetupReplay()
    {
        var (_, sourceBuffer) = CreateRecordedScenario(EntityCount);
        _replayFrame = sourceBuffer.Playback();
        _replayWorld = new World();
        CreateEntities(_replayWorld, EntityCount);
    }

    [IterationCleanup(Target = nameof(MiniArch_CommandBuffer_Replay_Only))]
    public void CleanupReplay()
    {
        _replayWorld = null;
        _replayFrame = default;
    }

    [IterationSetup(Target = nameof(MiniArch_CommandBuffer_Play_Only))]
    public void SetupPlay()
    {
        (_playWorld, _playBuffer) = CreateRecordedScenario(EntityCount);
    }

    [IterationCleanup(Target = nameof(MiniArch_CommandBuffer_Play_Only))]
    public void CleanupPlay()
    {
        _playWorld = null;
        _playBuffer = null;
    }

    [IterationSetup(Target = nameof(MiniArch_CommandBuffer_EndToEnd))]
    public void SetupEndToEnd()
    {
        (_endToEndWorld, _endToEndBuffer) = CreateRecordedScenario(EntityCount);
    }

    [IterationCleanup(Target = nameof(MiniArch_CommandBuffer_EndToEnd))]
    public void CleanupEndToEnd()
    {
        _endToEndWorld = null;
        _endToEndBuffer = null;
    }

    [Benchmark(Description = "MiniArch command buffer record")]
    public void MiniArch_CommandBuffer_Record()
    {
        RecordScript(_recordBuffer!, _recordExisting!);
    }

    [Benchmark(Description = "MiniArch command buffer playback only")]
    public FrameCommands MiniArch_CommandBuffer_Playback_Only()
    {
        return _playbackBuffer!.Playback();
    }

    [Benchmark(Description = "MiniArch command buffer replay only")]
    public void MiniArch_CommandBuffer_Replay_Only()
    {
        _replayWorld!.Replay(in _replayFrame);
    }

    [Benchmark(Description = "MiniArch command buffer play only")]
    public void MiniArch_CommandBuffer_Play_Only()
    {
        _playBuffer!.Play();
    }

    [Benchmark(Description = "MiniArch command buffer end-to-end")]
    public void MiniArch_CommandBuffer_EndToEnd()
    {
        var frame = _endToEndBuffer!.Playback();
        _endToEndWorld!.Replay(in frame);
    }

    private static (World World, CommandBuffer Buffer) CreateRecordedScenario(int entityCount)
    {
        var world = new World();
        var existing = CreateEntities(world, entityCount);
        var buffer = new CommandBuffer(world);
        RecordScript(buffer, existing);
        return (world, buffer);
    }

    private static Entity[] CreateEntities(World world, int count)
    {
        var entities = new Entity[count];
        for (var i = 0; i < count; i++)
        {
            entities[i] = world.Create();
        }

        return entities;
    }

    private static void RecordScript(CommandBuffer buffer, Entity[] existing)
    {
        var parent = buffer.Create();
        for (var i = 0; i < existing.Length; i++)
        {
            var entity = existing[i];
            buffer.Add(entity, new Position(i, i + 1));
            buffer.Set(entity, new Position(i + 10, i + 20));
            buffer.Add(entity, new Velocity(i + 30, i + 40));

            if ((i & 1) == 0)
            {
                buffer.Remove<Position>(entity);
            }

            if ((i & 7) == 0)
            {
                buffer.Link(parent, entity);
            }
        }
    }

    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
}
