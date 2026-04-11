using BenchmarkDotNet.Attributes;

namespace MiniArch.Benchmarks;

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
        for (var i = 0; i < _archAddState.Entities.Length; i++)
        {
            _archAddState.World.Add(_archAddState.Entities[i], new Position(i, i));
        }
    }

    [Benchmark(Description = "MiniArch add Position to empty entities")]
    public void MiniArch_Add_Position()
    {
        for (var i = 0; i < _miniAddState.Entities.Length; i++)
        {
            _miniAddState.World.Add(_miniAddState.Entities[i], new Position(i, i));
        }
    }

    [Benchmark(Description = "Arch set Position for existing entities")]
    public void Arch_Set_Position()
    {
        for (var i = 0; i < _archSetState.Entities.Length; i++)
        {
            _archSetState.World.Set(_archSetState.Entities[i], new Position(i + 1, i + 1));
        }
    }

    [Benchmark(Description = "MiniArch set Position for existing entities")]
    public void MiniArch_Set_Position()
    {
        for (var i = 0; i < _miniSetState.Entities.Length; i++)
        {
            _miniSetState.World.Set(_miniSetState.Entities[i], new Position(i + 1, i + 1));
        }
    }

    [Benchmark(Description = "Arch remove Position from entities")]
    public void Arch_Remove_Position()
    {
        for (var i = 0; i < _archRemoveState.Entities.Length; i++)
        {
            _archRemoveState.World.Remove<Position>(_archRemoveState.Entities[i]);
        }
    }

    [Benchmark(Description = "MiniArch remove Position from entities")]
    public void MiniArch_Remove_Position()
    {
        for (var i = 0; i < _miniRemoveState.Entities.Length; i++)
        {
            _miniRemoveState.World.Remove<Position>(_miniRemoveState.Entities[i]);
        }
    }

    [Benchmark(Description = "Arch destroy entities")]
    public void Arch_Destroy_Entity()
    {
        for (var i = 0; i < _archDestroyState.Entities.Length; i++)
        {
            _archDestroyState.World.Destroy(_archDestroyState.Entities[i]);
        }
    }

    [Benchmark(Description = "MiniArch destroy entities")]
    public void MiniArch_Destroy_Entity()
    {
        for (var i = 0; i < _miniDestroyState.Entities.Length; i++)
        {
            _miniDestroyState.World.Destroy(_miniDestroyState.Entities[i]);
        }
    }
}
