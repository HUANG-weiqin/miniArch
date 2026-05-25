namespace MiniArch.Tests.Core;

using MiniArch.Core;
using Xunit;

internal readonly record struct GcTestComponentA(int Value);
internal readonly record struct GcTestComponentB(int Value);

[Collection("GC-sensitive")]
public class CommandBufferGcVerificationTests
{
    [Fact]
    public void Play_SetOperations_SingleFrame_LowGcAllocations()
    {
        var world = new World();
        var entities = CreateEntities(world, 100);

        try
        {
            ForceGarbageCollection();

            var buffer = new CommandBuffer(world);
            for (var i = 0; i < 100; i++)
            {
                var entity = entities[i];
                buffer.Set(entity, new GcTestComponentA { Value = i + 1000 });
                buffer.Set(entity, new GcTestComponentB { Value = (i + 1000) * 2 });
            }

            ForceGarbageCollection();

            var gcInfo = MeasureGc(() =>
            {
                buffer.CompileAndReplay();
            });

            Assert.True(gcInfo.Gen0Collections < 10, $"Expected Gen0 < 10 but got {gcInfo.Gen0Collections}");
            Assert.True(gcInfo.Gen1Collections < 10, $"Expected Gen1 < 10 but got {gcInfo.Gen1Collections}");
            Assert.True(gcInfo.Gen2Collections < 5, $"Expected Gen2 < 5 but got {gcInfo.Gen2Collections}");
        }
        finally
        {
        }
    }

    [Fact]
    public void Play_ManySetCalls_LowGcAllocations()
    {
        var world = new World();
        var entities = CreateEntities(world, 10);

        try
        {
            ForceGarbageCollection();

            var buffers = new CommandBuffer[1000];
            for (var iteration = 0; iteration < 1000; iteration++)
            {
                buffers[iteration] = new CommandBuffer(world);
                for (var i = 0; i < 10; i++)
                {
                    var entity = entities[i];
                    buffers[iteration].Set(entity, new GcTestComponentA { Value = iteration * 100 + i });
                    buffers[iteration].Set(entity, new GcTestComponentB { Value = (iteration * 100 + i) * 2 });
                }
            }

            ForceGarbageCollection();

            var gcInfo = MeasureGc(() =>
            {
                for (var iteration = 0; iteration < 1000; iteration++)
                {
                    buffers[iteration].CompileAndReplay();
                }
            });

            Assert.True(gcInfo.Gen0Collections < 30, $"Expected Gen0 < 30 but got {gcInfo.Gen0Collections}");
            Assert.True(gcInfo.Gen1Collections < 20, $"Expected Gen1 < 20 but got {gcInfo.Gen1Collections}");
            Assert.True(gcInfo.Gen2Collections < 5, $"Expected Gen2 < 5 but got {gcInfo.Gen2Collections}");
        }
        finally
        {
        }
    }

    [Fact]
    public void Play_AddRemoveComponents_SingleFrame_LowGcAllocations()
    {
        var world = new World();
        var entities = CreateEntities(world, 100);

        try
        {
            ForceGarbageCollection();

            var buffer = new CommandBuffer(world);
            for (var i = 0; i < 100; i++)
            {
                var entity = entities[i];
                buffer.Add(entity, new GcTestComponentA { Value = i + 5000 });
                buffer.Remove<GcTestComponentB>(entity);
            }

            ForceGarbageCollection();

            var gcInfo = MeasureGc(() =>
            {
                buffer.CompileAndReplay();
            });

            Assert.True(gcInfo.Gen0Collections < 15, $"Expected Gen0 < 15 but got {gcInfo.Gen0Collections}");
            Assert.True(gcInfo.Gen1Collections < 10, $"Expected Gen1 < 10 but got {gcInfo.Gen1Collections}");
            Assert.True(gcInfo.Gen2Collections < 5, $"Expected Gen2 < 5 but got {gcInfo.Gen2Collections}");
        }
        finally
        {
        }
    }

    [Fact]
    public void Play_CreateWithComponents_SingleFrame_LowGcAllocations()
    {
        var world = new World();
        CreateEntities(world, 100);

        try
        {
            ForceGarbageCollection();

            var buffer = new CommandBuffer(world);
            for (var i = 0; i < 100; i++)
            {
                var entity = buffer.Create();
                buffer.Add(entity, new GcTestComponentA { Value = i });
                buffer.Add(entity, new GcTestComponentB { Value = i * 2 });
            }

            ForceGarbageCollection();

            var gcInfo = MeasureGc(() =>
            {
                buffer.CompileAndReplay();
            });

            Assert.True(gcInfo.Gen0Collections < 10, $"Expected Gen0 < 10 but got {gcInfo.Gen0Collections}");
            Assert.Equal(0, gcInfo.Gen1Collections);
            Assert.Equal(0, gcInfo.Gen2Collections);
        }
        finally
        {
        }
    }

    [Fact]
    public void Play_Destroy_SingleFrame_LowGcAllocations()
    {
        var world = new World();
        var entities = CreateEntities(world, 100);

        try
        {
            ForceGarbageCollection();

            var buffer = new CommandBuffer(world);
            for (var i = 0; i < 50; i++)
            {
                buffer.Destroy(entities[i]);
            }

            ForceGarbageCollection();

            var gcInfo = MeasureGc(() =>
            {
                buffer.CompileAndReplay();
            });

            Assert.True(gcInfo.Gen0Collections < 10, $"Expected Gen0 < 10 but got {gcInfo.Gen0Collections}");
            Assert.Equal(0, gcInfo.Gen1Collections);
            Assert.Equal(0, gcInfo.Gen2Collections);
        }
        finally
        {
        }
    }

    private static Entity[] CreateEntities(World world, int count)
    {
        var entities = new Entity[count];
        for (var i = 0; i < count; i++)
        {
            entities[i] = world.Create(
                new GcTestComponentA { Value = i },
                new GcTestComponentB { Value = i * 2 });
        }
        return entities;
    }

    private static void ForceGarbageCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
    }

    private static GcMeasurement MeasureGc(Action action)
    {
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);

        action();

        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);

        return new GcMeasurement(
            Gen0Collections: gen0After - gen0Before,
            Gen1Collections: gen1After - gen1Before,
            Gen2Collections: gen2After - gen2Before);
    }
}

internal readonly record struct GcMeasurement(int Gen0Collections, int Gen1Collections, int Gen2Collections);

[CollectionDefinition("GC-sensitive", DisableParallelization = true)]
public sealed class GcSensitiveCollection
{
}
