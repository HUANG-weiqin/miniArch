using BenchmarkDotNet.Attributes;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using MiniEntity = MiniArch.Core.Entity;
using MiniWorld = MiniArch.Core.World;

namespace MiniArch.Benchmarks;

public class StructuralChangeBenchmarks
{
    private const int DefaultSeed = 0x4D694D78;
    private const int OperationsPerCycle = 5;

    private const byte CreateKind = 0;
    private const byte AddKind = 1;
    private const byte SetKind = 2;
    private const byte RemoveKind = 3;
    private const byte DestroyKind = 4;

    [Params(100, 1000, 10000)]
    public int EntityCount { get; set; }

    private byte[] _operationKinds = null!;
    private int[] _operationSlots = null!;

    private MiniWorld _miniWorld = null!;
    private MiniEntity[] _miniEntities = null!;

    private ArchWorld _archWorld = null!;
    private ArchEntity[] _archEntities = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        (_operationKinds, _operationSlots) = CreateOperations(EntityCount);
        _miniWorld = new MiniWorld();
        _miniEntities = CreateMiniEntities(_miniWorld, EntityCount);
        _archWorld = ArchWorld.Create();
        _archEntities = CreateArchEntities(_archWorld, EntityCount);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _archWorld.Dispose();
        _miniWorld = null!;
        _archWorld = null!;
        _miniEntities = Array.Empty<MiniEntity>();
        _archEntities = Array.Empty<ArchEntity>();
    }

    [Benchmark(Description = "Arch mixed create/add/set/remove/destroy")]
    public void Arch_Mixed_CreateAddSetRemoveDestroy()
    {
        RunArchScript();
    }

    [Benchmark(Description = "MiniArch mixed create/add/set/remove/destroy")]
    public void MiniArch_Mixed_CreateAddSetRemoveDestroy()
    {
        RunMiniScript();
    }

    private void RunArchScript()
    {
        for (var i = 0; i < _operationKinds.Length; i++)
        {
            ApplyArchOperation(_operationKinds[i], _operationSlots[i], i);
        }
    }

    private void RunMiniScript()
    {
        for (var i = 0; i < _operationKinds.Length; i++)
        {
            ApplyMiniOperation(_operationKinds[i], _operationSlots[i], i);
        }
    }

    private void ApplyArchOperation(byte kind, int slot, int stepIndex)
    {
        switch (kind)
        {
            case CreateKind:
                _archEntities[slot] = _archWorld.Create();
                return;
            case AddKind:
                _archWorld.Add(_archEntities[slot], new Position(stepIndex, stepIndex));
                return;
            case SetKind:
                _archWorld.Set(_archEntities[slot], new Position(stepIndex + 1, stepIndex + 1));
                return;
            case RemoveKind:
                _archWorld.Remove<Position>(_archEntities[slot]);
                return;
            case DestroyKind:
                _archWorld.Destroy(_archEntities[slot]);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private void ApplyMiniOperation(byte kind, int slot, int stepIndex)
    {
        switch (kind)
        {
            case CreateKind:
                _miniEntities[slot] = _miniWorld.Create();
                return;
            case AddKind:
                _miniWorld.Add(_miniEntities[slot], new Position(stepIndex, stepIndex));
                return;
            case SetKind:
                _miniWorld.Set(_miniEntities[slot], new Position(stepIndex + 1, stepIndex + 1));
                return;
            case RemoveKind:
                _miniWorld.Remove<Position>(_miniEntities[slot]);
                return;
            case DestroyKind:
                _miniWorld.Destroy(_miniEntities[slot]);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static (byte[] Kinds, int[] Slots) CreateOperations(int entityCount)
    {
        var operationCount = entityCount * OperationsPerCycle;
        var kinds = new byte[operationCount];
        var slots = new int[operationCount];
        var positionedSlots = new List<int>(entityCount);
        var emptySlots = new List<int>(entityCount);
        var rng = new Random(DefaultSeed);
        var writeIndex = 0;
        var nextSlot = entityCount;

        for (var slot = 0; slot < entityCount; slot++)
        {
            positionedSlots.Add(slot);
        }

        for (var cycle = 0; cycle < entityCount; cycle++)
        {
            var createSlot = nextSlot++;
            kinds[writeIndex] = CreateKind;
            slots[writeIndex++] = createSlot;
            emptySlots.Add(createSlot);

            var addSlot = PopAt(emptySlots, rng.Next(emptySlots.Count));
            kinds[writeIndex] = AddKind;
            slots[writeIndex++] = addSlot;
            positionedSlots.Add(addSlot);

            var setSlot = positionedSlots[rng.Next(positionedSlots.Count)];
            kinds[writeIndex] = SetKind;
            slots[writeIndex++] = setSlot;

            var removeSlot = PopAt(positionedSlots, rng.Next(positionedSlots.Count));
            kinds[writeIndex] = RemoveKind;
            slots[writeIndex++] = removeSlot;
            emptySlots.Add(removeSlot);

            var destroySlot = PopAt(emptySlots, rng.Next(emptySlots.Count));
            kinds[writeIndex] = DestroyKind;
            slots[writeIndex++] = destroySlot;
        }

        return (kinds, slots);
    }

    private static int PopAt(List<int> slots, int index)
    {
        var lastIndex = slots.Count - 1;
        var value = slots[index];
        slots[index] = slots[lastIndex];
        slots.RemoveAt(lastIndex);
        return value;
    }

    private static ArchEntity[] CreateArchEntities(ArchWorld world, int entityCount)
    {
        var entities = new ArchEntity[entityCount * 2];
        for (var i = 0; i < entityCount; i++)
        {
            var entity = world.Create();
            world.Add(entity, new Position(i, i));
            entities[i] = entity;
        }

        return entities;
    }

    private static MiniEntity[] CreateMiniEntities(MiniWorld world, int entityCount)
    {
        var entities = new MiniEntity[entityCount * 2];
        for (var i = 0; i < entityCount; i++)
        {
            var entity = world.Create();
            world.Add(entity, new Position(i, i));
            entities[i] = entity;
        }

        return entities;
    }
}
