using BenchmarkDotNet.Attributes;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using MiniEntity = MiniArch.Core.Entity;
using MiniWorld = MiniArch.Core.World;

namespace MiniArch.Benchmarks;

// Keep the benchmark focused on create/destroy and structural-change hot paths.
// Memory diagnostics are enabled in the shared BenchmarkDotNet config.
public class StructuralChangeBenchmarks
{
    [Params(1000)]
    public int EntityCount { get; set; }

    private MiniWorldState _miniAddState = null!;
    private ArchWorldState _archAddState = null!;

    private MiniCreateManyWorldState _miniRecycledCreateManyState = null!;
    private ArchCreateManyWorldState _archRecycledCreateManyState = null!;

    private MiniCreateManyWorldState _miniMixedCreateManyState = null!;
    private ArchCreateManyWorldState _archMixedCreateManyState = null!;

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

    [IterationSetup(Target = nameof(Arch_CreateMany_Entity_RecycledIds))]
    public void SetupArchRecycledCreateMany() => _archRecycledCreateManyState = BenchmarkWorldFactory.CreateArchCreateManyRecycledWorld(EntityCount);

    [IterationSetup(Target = nameof(MiniArch_CreateMany_Entity_RecycledIds))]
    public void SetupMiniRecycledCreateMany() => _miniRecycledCreateManyState = BenchmarkWorldFactory.CreateMiniCreateManyRecycledWorld(EntityCount);

    [IterationSetup(Target = nameof(Arch_CreateMany_Entity_MixedIds))]
    public void SetupArchMixedCreateMany() => _archMixedCreateManyState = BenchmarkWorldFactory.CreateArchCreateManyMixedWorld(EntityCount);

    [IterationSetup(Target = nameof(MiniArch_CreateMany_Entity_MixedIds))]
    public void SetupMiniMixedCreateMany() => _miniMixedCreateManyState = BenchmarkWorldFactory.CreateMiniCreateManyMixedWorld(EntityCount);

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

    [IterationCleanup(Target = nameof(Arch_CreateMany_Entity_RecycledIds))]
    public void CleanupArchRecycledCreateMany() => _archRecycledCreateManyState.Dispose();

    [IterationCleanup(Target = nameof(Arch_CreateMany_Entity_MixedIds))]
    public void CleanupArchMixedCreateMany() => _archMixedCreateManyState.Dispose();

    [IterationCleanup(Target = nameof(Arch_Remove_Position))]
    public void CleanupArchRemove() => _archRemoveState.Dispose();

    [IterationCleanup(Target = nameof(Arch_Destroy_Entity))]
    public void CleanupArchDestroy() => _archDestroyState.Dispose();

    [Benchmark(Description = "Arch create empty entities")]
    public void Arch_Create_Entity()
    {
        using var world = Arch.Core.World.Create();
        for (var i = 0; i < EntityCount; i++)
        {
            world.Create();
        }
    }

    [Benchmark(Description = "MiniArch create empty entities")]
    public void MiniArch_Create_Entity()
    {
        var world = new MiniArch.Core.World();
        for (var i = 0; i < EntityCount; i++)
        {
            world.Create();
        }
    }

    [Benchmark(Description = "Arch create empty entities in bulk")]
    public void Arch_CreateMany_Entity()
    {
        using var world = Arch.Core.World.Create();
        var entities = new ArchEntity[EntityCount];
        world.Create(entities, Arch.Core.Signature.Null, EntityCount);
    }

    [Benchmark(Description = "MiniArch create empty entities in bulk")]
    public void MiniArch_CreateMany_Entity()
    {
        var world = new MiniArch.Core.World();
        var entities = new MiniEntity[EntityCount];
        world.CreateMany(entities);
    }

    [Benchmark(Description = "Arch create empty entities in bulk with recycled ids")]
    public void Arch_CreateMany_Entity_RecycledIds()
    {
        var state = _archRecycledCreateManyState;
        state.World.Create(state.Buffer, Arch.Core.Signature.Null, state.Buffer.Length);
    }

    [Benchmark(Description = "MiniArch create empty entities in bulk with recycled ids")]
    public void MiniArch_CreateMany_Entity_RecycledIds()
    {
        var state = _miniRecycledCreateManyState;
        state.World.CreateMany(state.Buffer);
    }

    [Benchmark(Description = "Arch create empty entities in bulk with mixed ids")]
    public void Arch_CreateMany_Entity_MixedIds()
    {
        var state = _archMixedCreateManyState;
        state.World.Create(state.Buffer, Arch.Core.Signature.Null, state.Buffer.Length);
    }

    [Benchmark(Description = "MiniArch create empty entities in bulk with mixed ids")]
    public void MiniArch_CreateMany_Entity_MixedIds()
    {
        var state = _miniMixedCreateManyState;
        state.World.CreateMany(state.Buffer);
    }

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

// The mixed benchmark uses one deterministic script for both engines so the
// result compares hot-path behavior instead of setup noise or random input drift.
public class MixedStructuralChangeBenchmarks
{
    private const int DefaultSeed = 0x4D694D78;

    [Params(100, 1000, 10000)]
    public int EntityCount { get; set; }

    private MixedMiniWorldState _miniState = null!;
    private MixedArchWorldState _archState = null!;

    [IterationSetup(Target = nameof(Arch_Mixed_CreateAddSetRemoveDestroy))]
    public void SetupArch() => _archState = MixedArchWorldState.Create(EntityCount);

    [IterationSetup(Target = nameof(MiniArch_Mixed_CreateAddSetRemoveDestroy))]
    public void SetupMini() => _miniState = MixedMiniWorldState.Create(EntityCount);

    [IterationCleanup(Target = nameof(Arch_Mixed_CreateAddSetRemoveDestroy))]
    public void CleanupArch() => _archState.Dispose();

    [Benchmark(Description = "Arch mixed create/add/set/remove/destroy")]
    public void Arch_Mixed_CreateAddSetRemoveDestroy()
    {
        var state = _archState;
        var operations = state.Operations;
        for (var i = 0; i < operations.Length; i++)
        {
            state.Apply(operations[i], i);
        }
    }

    [Benchmark(Description = "MiniArch mixed create/add/set/remove/destroy")]
    public void MiniArch_Mixed_CreateAddSetRemoveDestroy()
    {
        var state = _miniState;
        var operations = state.Operations;
        for (var i = 0; i < operations.Length; i++)
        {
            state.Apply(operations[i], i);
        }
    }

    private enum MixedOperationKind
    {
        Create = 0,
        Add = 1,
        Set = 2,
        Remove = 3,
        Destroy = 4
    }

    private readonly record struct MixedOperation(MixedOperationKind Kind, int Selector);

    private static MixedOperation[] CreateOperations(int operationCount)
    {
        var operations = new MixedOperation[operationCount];
        var rng = new Random(DefaultSeed);
        var quotas = BuildQuotas(operationCount);
        var writeIndex = 0;

        for (var kindIndex = 0; kindIndex < quotas.Length; kindIndex++)
        {
            var kind = (MixedOperationKind)kindIndex;
            for (var i = 0; i < quotas[kindIndex]; i++)
            {
                operations[writeIndex++] = new MixedOperation(kind, rng.Next());
            }
        }

        Shuffle(operations, rng);
        return operations;
    }

    private static int[] BuildQuotas(int operationCount)
    {
        var quotas = new int[5];
        var baseQuota = operationCount / quotas.Length;
        var remainder = operationCount % quotas.Length;

        for (var i = 0; i < quotas.Length; i++)
        {
            quotas[i] = baseQuota + (i < remainder ? 1 : 0);
        }

        return quotas;
    }

    private static void Shuffle(MixedOperation[] operations, Random rng)
    {
        for (var i = operations.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (operations[i], operations[j]) = (operations[j], operations[i]);
        }
    }

    private abstract class MixedWorldState<TEntity, TWorld>
        where TEntity : struct
    {
        private readonly List<TEntity> _positionedEntities = new();
        private readonly Dictionary<int, int> _positionedEntityIndex = new();
        private readonly List<TEntity> _emptyEntities = new();
        private readonly Dictionary<int, int> _emptyEntityIndex = new();
        private readonly MixedOperation[] _operations;

        protected MixedWorldState(TWorld world, MixedOperation[] operations, List<TEntity> positionedEntities, List<TEntity> emptyEntities)
            : base()
        {
            World = world;
            _operations = operations;
            _positionedEntities = positionedEntities;
            _emptyEntities = emptyEntities;
            RebuildIndexes();
        }

        protected TWorld World { get; }

        public MixedOperation[] Operations => _operations;

        public void Apply(MixedOperation operation, int stepIndex)
        {
            switch (operation.Kind)
            {
                case MixedOperationKind.Create:
                    ApplyCreate(stepIndex);
                    return;
                case MixedOperationKind.Add:
                    ApplyAdd(stepIndex, operation.Selector);
                    return;
                case MixedOperationKind.Set:
                    ApplySet(stepIndex, operation.Selector);
                    return;
                case MixedOperationKind.Remove:
                    ApplyRemove(stepIndex, operation.Selector);
                    return;
                case MixedOperationKind.Destroy:
                    ApplyDestroy(operation.Selector);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        protected abstract TEntity CreateEntity();

        protected abstract void AddPosition(TEntity entity, int stepIndex);

        protected abstract void SetPosition(TEntity entity, int stepIndex);

        protected abstract void RemovePosition(TEntity entity);

        protected abstract void DestroyEntity(TEntity entity);

        protected abstract int GetEntityId(TEntity entity);

        private void ApplyCreate(int stepIndex)
        {
            var entity = CreateEntity();
            AddToPool(_emptyEntities, _emptyEntityIndex, entity);
        }

        private void ApplyAdd(int stepIndex, int selector)
        {
            var entity = RemoveFromPool(_emptyEntities, _emptyEntityIndex, selector);
            AddPosition(entity, stepIndex);
            AddToPool(_positionedEntities, _positionedEntityIndex, entity);
        }

        private void ApplySet(int stepIndex, int selector)
        {
            var entity = SelectFromPool(_positionedEntities, selector);
            SetPosition(entity, stepIndex);
        }

        private void ApplyRemove(int stepIndex, int selector)
        {
            var entity = RemoveFromPool(_positionedEntities, _positionedEntityIndex, selector);
            RemovePosition(entity);
            AddToPool(_emptyEntities, _emptyEntityIndex, entity);
        }

        private void ApplyDestroy(int selector)
        {
            var positionedCount = _positionedEntities.Count;
            var emptyCount = _emptyEntities.Count;
            if (positionedCount == 0 && emptyCount == 0)
            {
                throw new InvalidOperationException("Mixed benchmark exhausted all entities.");
            }

            if (positionedCount == 0)
            {
                DestroyFromPool(_emptyEntities, _emptyEntityIndex, selector);
                return;
            }

            if (emptyCount == 0)
            {
                DestroyFromPool(_positionedEntities, _positionedEntityIndex, selector);
                return;
            }

            if (positionedCount >= emptyCount)
            {
                if (positionedCount == emptyCount && (selector & 1) != 0)
                {
                    DestroyFromPool(_emptyEntities, _emptyEntityIndex, selector);
                    return;
                }

                DestroyFromPool(_positionedEntities, _positionedEntityIndex, selector);
                return;
            }

            DestroyFromPool(_emptyEntities, _emptyEntityIndex, selector);
        }

        private TEntity RemoveFromPool(List<TEntity> pool, Dictionary<int, int> indexes, int selector)
        {
            var index = selector % pool.Count;
            return RemoveFromPoolAt(pool, indexes, index);
        }

        private TEntity RemoveFromPoolAt(List<TEntity> pool, Dictionary<int, int> indexes, int index)
        {
            var lastIndex = pool.Count - 1;
            var entity = pool[index];
            var entityId = GetEntityId(entity);
            if (index != lastIndex)
            {
                var lastEntity = pool[lastIndex];
                pool[index] = lastEntity;
                indexes[GetEntityId(lastEntity)] = index;
            }

            pool.RemoveAt(lastIndex);
            indexes.Remove(entityId);
            return entity;
        }

        private void DestroyFromPool(List<TEntity> pool, Dictionary<int, int> indexes, int selector)
        {
            var entity = RemoveFromPool(pool, indexes, selector);
            DestroyEntity(entity);
        }

        private TEntity SelectFromPool(List<TEntity> pool, int selector)
        {
            return pool[selector % pool.Count];
        }

        private void AddToPool(List<TEntity> pool, Dictionary<int, int> indexes, TEntity entity)
        {
            indexes[GetEntityId(entity)] = pool.Count;
            pool.Add(entity);
        }

        private void RebuildIndexes()
        {
            _positionedEntityIndex.Clear();
            _emptyEntityIndex.Clear();

            for (var i = 0; i < _positionedEntities.Count; i++)
            {
                _positionedEntityIndex[GetEntityId(_positionedEntities[i])] = i;
            }

            for (var i = 0; i < _emptyEntities.Count; i++)
            {
                _emptyEntityIndex[GetEntityId(_emptyEntities[i])] = i;
            }
        }
    }

    private sealed class MixedMiniWorldState : MixedWorldState<MiniEntity, MiniWorld>
    {
        private MixedMiniWorldState(MiniWorld world, MixedOperation[] operations, List<MiniEntity> positionedEntities, List<MiniEntity> emptyEntities)
            : base(world, operations, positionedEntities, emptyEntities)
        {
        }

        public static MixedMiniWorldState Create(int entityCount)
        {
            var world = new MiniWorld();
            var positionedEntities = new List<MiniEntity>(entityCount / 2);
            var emptyEntities = new List<MiniEntity>(entityCount);

            for (var i = 0; i < entityCount; i++)
            {
                var entity = world.Create();
                emptyEntities.Add(entity);
            }

            for (var i = 0; i < entityCount / 2; i++)
            {
                var entity = emptyEntities[^1];
                emptyEntities.RemoveAt(emptyEntities.Count - 1);
                world.Add(entity, new Position(i, i));
                positionedEntities.Add(entity);
            }

            return new MixedMiniWorldState(world, CreateOperations(entityCount), positionedEntities, emptyEntities);
        }

        protected override MiniEntity CreateEntity() => World.Create();

        protected override void AddPosition(MiniEntity entity, int stepIndex) => World.Add(entity, new Position(stepIndex, stepIndex));

        protected override void SetPosition(MiniEntity entity, int stepIndex) => World.Set(entity, new Position(stepIndex + 1, stepIndex + 1));

        protected override void RemovePosition(MiniEntity entity) => World.Remove<Position>(entity);

        protected override void DestroyEntity(MiniEntity entity) => World.Destroy(entity);

        protected override int GetEntityId(MiniEntity entity) => entity.Id;
    }

    private sealed class MixedArchWorldState : MixedWorldState<ArchEntity, ArchWorld>, IDisposable
    {
        private MixedArchWorldState(ArchWorld world, MixedOperation[] operations, List<ArchEntity> positionedEntities, List<ArchEntity> emptyEntities)
            : base(world, operations, positionedEntities, emptyEntities)
        {
        }

        public static MixedArchWorldState Create(int entityCount)
        {
            var world = ArchWorld.Create();
            var positionedEntities = new List<ArchEntity>(entityCount / 2);
            var emptyEntities = new List<ArchEntity>(entityCount);

            for (var i = 0; i < entityCount; i++)
            {
                var entity = world.Create();
                emptyEntities.Add(entity);
            }

            for (var i = 0; i < entityCount / 2; i++)
            {
                var entity = emptyEntities[^1];
                emptyEntities.RemoveAt(emptyEntities.Count - 1);
                world.Add(entity, new Position(i, i));
                positionedEntities.Add(entity);
            }

            return new MixedArchWorldState(world, CreateOperations(entityCount), positionedEntities, emptyEntities);
        }

        protected override ArchEntity CreateEntity() => World.Create();

        protected override void AddPosition(ArchEntity entity, int stepIndex) => World.Add(entity, new Position(stepIndex, stepIndex));

        protected override void SetPosition(ArchEntity entity, int stepIndex) => World.Set(entity, new Position(stepIndex + 1, stepIndex + 1));

        protected override void RemovePosition(ArchEntity entity) => World.Remove<Position>(entity);

        protected override void DestroyEntity(ArchEntity entity) => World.Destroy(entity);

        protected override int GetEntityId(ArchEntity entity) => entity.Id;

        public void Dispose() => World.Dispose();
    }
}
