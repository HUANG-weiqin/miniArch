using BenchmarkDotNet.Attributes;

namespace MiniArch.Benchmarks;

// Keep the benchmark focused on the structural-change hot path.
// Memory diagnostics are enabled in the shared BenchmarkDotNet config.
public class StructuralChangeBenchmarks
{
    [Params(1000)]
    public int EntityCount { get; set; }

    private MiniWorldState _miniAddState = null!;
    private ArchWorldState _archAddState = null!;

    private MiniWorldState _miniSetState = null!;
    private ArchWorldState _archSetState = null!;

    private MiniWorldState _miniRemoveState = null!;
    private ArchWorldState _archRemoveState = null!;

    private MiniWorldState _miniDestroyState = null!;
    private ArchWorldState _archDestroyState = null!;

    [IterationSetup(Target = nameof(Arch_Add_Position))]
    public void SetupArchAdd() => _archAddState = BenchmarkWorldFactory.CreateArchEmptyWorld(EntityCount);

    [IterationSetup(Target = nameof(MiniArch_Add_Position))]
    public void SetupMiniAdd() => _miniAddState = BenchmarkWorldFactory.CreateMiniEmptyWorld(EntityCount);

    [IterationSetup(Target = nameof(Arch_Set_Position))]
    public void SetupArchSet() => _archSetState = BenchmarkWorldFactory.CreateArchWorldWithPosition(EntityCount);

    [IterationSetup(Target = nameof(MiniArch_Set_Position))]
    public void SetupMiniSet() => _miniSetState = BenchmarkWorldFactory.CreateMiniWorldWithPosition(EntityCount);

    [IterationSetup(Target = nameof(Arch_Remove_Position))]
    public void SetupArchRemove() => _archRemoveState = BenchmarkWorldFactory.CreateArchWorldWithPosition(EntityCount);

    [IterationSetup(Target = nameof(MiniArch_Remove_Position))]
    public void SetupMiniRemove() => _miniRemoveState = BenchmarkWorldFactory.CreateMiniWorldWithPosition(EntityCount);

    [IterationSetup(Target = nameof(Arch_Destroy_Entity))]
    public void SetupArchDestroy() => _archDestroyState = BenchmarkWorldFactory.CreateArchEmptyWorld(EntityCount);

    [IterationSetup(Target = nameof(MiniArch_Destroy_Entity))]
    public void SetupMiniDestroy() => _miniDestroyState = BenchmarkWorldFactory.CreateMiniEmptyWorld(EntityCount);

    [IterationCleanup(Target = nameof(Arch_Add_Position))]
    public void CleanupArchAdd() => _archAddState.Dispose();

    [IterationCleanup(Target = nameof(Arch_Set_Position))]
    public void CleanupArchSet() => _archSetState.Dispose();

    [IterationCleanup(Target = nameof(Arch_Remove_Position))]
    public void CleanupArchRemove() => _archRemoveState.Dispose();

    [IterationCleanup(Target = nameof(Arch_Destroy_Entity))]
    public void CleanupArchDestroy() => _archDestroyState.Dispose();

    [Benchmark(Description = "Arch add Position to empty entities")]
    public void Arch_Add_Position()
    {
        var world = _archAddState.World;
        var entities = _archAddState.Entities;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Add(entities[i], new Position(i, i));
        }
    }

    [Benchmark(Description = "MiniArch add Position to empty entities")]
    public void MiniArch_Add_Position()
    {
        var world = _miniAddState.World;
        var entities = _miniAddState.Entities;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Add(entities[i], new Position(i, i));
        }
    }

    [Benchmark(Description = "Arch set Position for existing entities")]
    public void Arch_Set_Position()
    {
        var world = _archSetState.World;
        var entities = _archSetState.Entities;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Set(entities[i], new Position(i + 1, i + 1));
        }
    }

    [Benchmark(Description = "MiniArch set Position for existing entities")]
    public void MiniArch_Set_Position()
    {
        var world = _miniSetState.World;
        var entities = _miniSetState.Entities;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Set(entities[i], new Position(i + 1, i + 1));
        }
    }

    [Benchmark(Description = "Arch remove Position from entities")]
    public void Arch_Remove_Position()
    {
        var world = _archRemoveState.World;
        var entities = _archRemoveState.Entities;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Remove<Position>(entities[i]);
        }
    }

    [Benchmark(Description = "MiniArch remove Position from entities")]
    public void MiniArch_Remove_Position()
    {
        var world = _miniRemoveState.World;
        var entities = _miniRemoveState.Entities;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Remove<Position>(entities[i]);
        }
    }

    [Benchmark(Description = "Arch destroy entities")]
    public void Arch_Destroy_Entity()
    {
        var world = _archDestroyState.World;
        var entities = _archDestroyState.Entities;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Destroy(entities[i]);
        }
    }

    [Benchmark(Description = "MiniArch destroy entities")]
    public void MiniArch_Destroy_Entity()
    {
        var world = _miniDestroyState.World;
        var entities = _miniDestroyState.Entities;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Destroy(entities[i]);
        }
    }
}
