using BenchmarkDotNet.Attributes;
using MiniArch.Core;

namespace MiniArch.Benchmarks;

public class CommandBufferBenchmarks
{
    [Params(1000)]
    public int EntityCount { get; set; }

    [Benchmark(Description = "MiniArch command buffer record")]
    public void MiniArch_CommandBuffer_Record()
    {
        var world = new World();
        var existing = CreateEntities(world, EntityCount);
        var buffer = new CommandBuffer(world);
        RecordScript(buffer, existing);
    }

    [Benchmark(Description = "MiniArch command buffer playback")]
    public FrameCommands MiniArch_CommandBuffer_Playback()
    {
        var world = new World();
        var existing = CreateEntities(world, EntityCount);
        var buffer = new CommandBuffer(world);
        RecordScript(buffer, existing);
        return buffer.Playback();
    }

    [Benchmark(Description = "MiniArch command buffer replay")]
    public void MiniArch_CommandBuffer_Replay()
    {
        var world = new World();
        var existing = CreateEntities(world, EntityCount);
        var buffer = new CommandBuffer(world);
        RecordScript(buffer, existing);
        var frame = buffer.Playback();
        world.Replay(in frame);
    }

    [Benchmark(Description = "MiniArch command buffer play")]
    public void MiniArch_CommandBuffer_Play()
    {
        var world = new World();
        var existing = CreateEntities(world, EntityCount);
        var buffer = new CommandBuffer(world);
        RecordScript(buffer, existing);
        buffer.Play();
    }

    [Benchmark(Description = "MiniArch command buffer record+playback+replay")]
    public void MiniArch_CommandBuffer_EndToEnd()
    {
        var world = new World();
        var existing = CreateEntities(world, EntityCount);
        var buffer = new CommandBuffer(world);
        RecordScript(buffer, existing);
        var frame = buffer.Playback();
        world.Replay(in frame);
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
