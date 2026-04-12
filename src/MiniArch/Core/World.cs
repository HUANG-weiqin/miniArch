using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

public sealed class World
{
    private const int DefaultChunkCapacity = 128;
    private const int EmptyArchetypeChunkCapacity = 1024;
    private const int EmptyArchetypeChunkCapacityThreshold = 128;
    private const int AdaptiveChunkTargetBytes = 16 * 1024;
    private const int AdaptiveMaxChunkCapacity = 1024;
    private const int StackAllocatedBatchRangeLimit = 128;
    private static readonly ConcurrentDictionary<Type, int> ManagedTypeSizeCache = new();
    private static readonly MethodInfo GetManagedTypeSizeMethod = typeof(World)
        .GetMethod(nameof(GetManagedTypeSizeGeneric), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Unable to find managed type size helper.");
    private static readonly int EntitySizeInBytes = GetManagedTypeSizeGeneric<Entity>();

    private readonly ComponentRegistry _components = new();
    private readonly Dictionary<Signature, Archetype> _archetypes = new();
    private readonly HierarchyTable _hierarchy = new();
    private readonly List<int> _versions;
    private readonly List<EntityLocation> _locations;
    private Dictionary<QueryFilter, Query> _queries = new();
    private Archetype[] _archetypeSnapshot = Array.Empty<Archetype>();
    private readonly int _chunkCapacity;
    private readonly bool _adaptiveChunkCapacity;
    private RecycledEntity[] _freeIds;
    private int _freeIdCount;
    private int _archetypeGeneration;
    private int _queryLayoutGeneration;
    private int _queryLayoutSuppressionCount;
    private bool _queryLayoutDirty;

    public World(int chunkCapacity = DefaultChunkCapacity, int entityCapacity = 64)
    {
        if (chunkCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
        }

        if (entityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        }

        _chunkCapacity = chunkCapacity;
        _adaptiveChunkCapacity = chunkCapacity == DefaultChunkCapacity;
        _versions = new List<int>(entityCapacity);
        _locations = new List<EntityLocation>(entityCapacity);
        _freeIds = entityCapacity == 0 ? Array.Empty<RecycledEntity>() : new RecycledEntity[entityCapacity];
    }

    public ComponentRegistry Components => _components;

    public int EntityCapacity => _versions.Capacity;

    internal int ChunkCapacity => _chunkCapacity;

    internal int EntitySlotCount => _versions.Count;

    internal ReadOnlySpan<int> EntityVersions => CollectionsMarshal.AsSpan(_versions);

    internal Archetype[] Archetypes => Volatile.Read(ref _archetypeSnapshot);

    internal HierarchyTable Hierarchy => _hierarchy;

    internal int ArchetypeGeneration => _archetypeGeneration;

    internal int QueryLayoutGeneration => _queryLayoutGeneration;

    internal void ResetSnapshotState(int entitySlotCount)
    {
        if (entitySlotCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entitySlotCount));
        }

        _archetypes.Clear();
        _archetypeSnapshot = Array.Empty<Archetype>();
        _queries = new Dictionary<QueryFilter, Query>();
        _archetypeGeneration = 0;
        _queryLayoutGeneration = 0;
        _freeIdCount = 0;
        _hierarchy.Reset();

        _versions.Clear();
        _locations.Clear();
        EnsureCapacity(entitySlotCount);
        CollectionsMarshal.SetCount(_versions, entitySlotCount);
        CollectionsMarshal.SetCount(_locations, entitySlotCount);
        CollectionsMarshal.AsSpan(_versions).Clear();
        CollectionsMarshal.AsSpan(_locations).Clear();

        if (_freeIds.Length < entitySlotCount)
        {
            Array.Resize(ref _freeIds, entitySlotCount);
        }
    }

    internal void SetSnapshotEntityVersion(int entityId, int version)
    {
        ValidateSnapshotEntitySlot(entityId);
        _versions[entityId] = version;
    }

    internal int GetEntityVersion(int entityId)
    {
        ValidateSnapshotEntitySlot(entityId);
        return _versions[entityId];
    }

    internal void SetSnapshotLocation(Entity entity, Archetype archetype, int chunkIndex, int rowIndex)
    {
        ValidateSnapshotEntitySlot(entity.Id);
        _locations[entity.Id] = new EntityLocation(archetype, chunkIndex, rowIndex);
    }

    internal void RebuildFreeIdStack()
    {
        if (_freeIds.Length < _locations.Count)
        {
            Array.Resize(ref _freeIds, _locations.Count);
        }

        _freeIdCount = 0;
        for (var id = _locations.Count - 1; id >= 0; id--)
        {
            if (_locations[id].Archetype is null)
            {
                _freeIds[_freeIdCount++] = new RecycledEntity(id, _versions[id]);
            }
        }
    }

    public Entity Create()
    {
        var archetype = GetOrCreateArchetype(Signature.Empty);
        return CreateInArchetype(archetype, out _, out _);
    }

    public Entity Create<T1>(T1 component1)
    {
        var componentType1 = GetComponentType<T1>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        chunk.SetComponentAtTyped(archetype.GetComponentIndex(componentType1), rowIndex, in component1);
        return entity;
    }

    public Entity Create<T1, T2>(T1 component1, T2 component2)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        chunk.SetComponentAtTyped(archetype.GetComponentIndex(componentType1), rowIndex, in component1);
        chunk.SetComponentAtTyped(archetype.GetComponentIndex(componentType2), rowIndex, in component2);
        return entity;
    }

    public Entity Create<T1, T2, T3>(T1 component1, T2 component2, T3 component3)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4>(T1 component1, T2 component2, T3 component3, T4 component4)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6, T7>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType7, in component7);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType8, in component8);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType9, in component9);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType10, in component10);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType11, in component11);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType12, in component12);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType13, in component13);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var componentType14 = GetComponentType<T14>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType13, in component13);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType14, in component14);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var componentType14 = GetComponentType<T14>();
        var componentType15 = GetComponentType<T15>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14, componentType15));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType13, in component13);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType14, in component14);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType15, in component15);
        return entity;
    }

    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15, T16 component16)
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var componentType14 = GetComponentType<T14>();
        var componentType15 = GetComponentType<T15>();
        var componentType16 = GetComponentType<T16>();
        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14, componentType15, componentType16));
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType13, in component13);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType14, in component14);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType15, in component15);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType16, in component16);
        return entity;
    }

    public void CreateMany(Span<Entity> entities)
    {
        if (entities.Length == 0)
        {
            return;
        }

        if (_freeIdCount == 0)
        {
            CreateManyFresh(entities);
            TouchQueryLayout();
            return;
        }

        var reusedCount = Math.Min(entities.Length, _freeIdCount);
        var startId = AppendEntitySlots(entities.Length - reusedCount);

        var archetype = GetOrCreateArchetype(Signature.Empty);
        var maxRangeCount = Math.Min(entities.Length, archetype.Chunks.Count + ((entities.Length + _chunkCapacity - 1) / _chunkCapacity));

        if (maxRangeCount <= StackAllocatedBatchRangeLimit)
        {
            Span<EntityBatchRange> ranges = stackalloc EntityBatchRange[StackAllocatedBatchRangeLimit];
            var rangeCount = archetype.ReserveEntityRanges(entities.Length, ranges);
            WriteCreatedEntitiesAndLocations(archetype, entities, ranges[..rangeCount], reusedCount, startId);
            TouchQueryLayout();
            return;
        }

        var rentedRanges = ArrayPool<EntityBatchRange>.Shared.Rent(maxRangeCount);
        try
        {
            var ranges = rentedRanges.AsSpan(0, maxRangeCount);
            var rangeCount = archetype.ReserveEntityRanges(entities.Length, ranges);
            WriteCreatedEntitiesAndLocations(archetype, entities, ranges[..rangeCount], reusedCount, startId);
        }
        finally
        {
            ArrayPool<EntityBatchRange>.Shared.Return(rentedRanges);
        }

        TouchQueryLayout();
    }

    public void EnsureCapacity(int entityCapacity)
    {
        if (entityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        }

        if (_versions.Capacity < entityCapacity)
        {
            _versions.Capacity = entityCapacity;
        }

        if (_locations.Capacity < entityCapacity)
        {
            _locations.Capacity = entityCapacity;
        }
    }

    public void Destroy(Entity entity)
    {
        var destroyOrder = new List<Entity>();
        _hierarchy.CollectDestroySubtree(this, entity, new HashSet<Entity>(), destroyOrder);
        if (destroyOrder.Count == 0)
        {
            throw new InvalidOperationException($"Entity {entity} is stale or unknown.");
        }

        foreach (var target in destroyOrder)
        {
            DestroySingle(target);
        }

        TouchQueryLayout();
    }

    public void Link(Entity parent, Entity child)
    {
        _hierarchy.Link(this, parent, child);
    }

    public void Unlink(Entity child)
    {
        GetRequiredLocation(child);
        _hierarchy.Unlink(child);
    }

    public bool TryGetParent(Entity child, out Entity parent)
    {
        return _hierarchy.TryGetParent(this, child, out parent);
    }

    public List<Entity> GetChildren(Entity parent)
    {
        return _hierarchy.GetChildren(this, parent);
    }

    public void Add<T>(Entity entity, T component)
    {
        var componentType = GetComponentType<T>();
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            archetype.GetChunk(info.ChunkIndex).SetComponentAtTyped(componentIndex, info.RowIndex, in component);
            return;
        }

        if (archetype.Edges.TryGetAdd(componentType, out var cached) && cached is not null)
        {
            MoveEntity(entity, info, cached, componentType, in component);
            return;
        }

        var destinationSignature = archetype.Signature.Add(componentType);
        var destination = GetOrCreateDestinationArchetype(archetype, componentType, destinationSignature, isAdd: true);
        MoveEntity(entity, info, destination, componentType, in component);
    }

    public void Set<T>(Entity entity, T component)
    {
        var componentType = GetComponentType<T>();
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            archetype.GetChunk(info.ChunkIndex).SetComponentAtTyped(componentIndex, info.RowIndex, in component);
            return;
        }

        var destinationSignature = archetype.Signature.Add(componentType);
        var destination = GetOrCreateDestinationArchetype(archetype, componentType, destinationSignature, isAdd: true);
        MoveEntity(entity, info, destination, componentType, in component);
    }

    public void Remove<T>(Entity entity)
    {
        var componentType = GetComponentType<T>();
        RemoveBoxed(entity, componentType);
    }

    public QueryBuilder Query()
    {
        return new QueryBuilder(this, QueryFilter.Empty);
    }

    public Query Query<T1>()
    {
        var componentType = GetComponentType<T1>();
        return GetOrCreateQuery(QueryFilter.CreateRequired(componentType));
    }

    public Query Query<T1, T2>()
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        return GetOrCreateQuery(QueryFilter.CreateRequired(componentType1, componentType2));
    }

    public Query Query<T1, T2, T3>()
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        return GetOrCreateQuery(QueryFilter.CreateRequired(componentType1, componentType2, componentType3));
    }

    public bool TryGetLocation(Entity entity, out EntityInfo info)
    {
        if (entity.Id < 0 || entity.Id >= _locations.Count)
        {
            info = default;
            return false;
        }

        var stored = _locations[entity.Id];
        if (stored.Archetype is null || _versions[entity.Id] != entity.Version)
        {
            info = default;
            return false;
        }

        info = new EntityInfo(entity.Version, stored.Archetype, stored.ChunkIndex, stored.RowIndex);
        return true;
    }

    public bool IsAlive(Entity entity)
    {
        return TryGetLocation(entity, out _);
    }

    private void MoveEntity(Entity entity, EntityLocation sourceInfo, Archetype destination)
    {
        var sourceChunk = sourceInfo.Archetype.GetChunk(sourceInfo.ChunkIndex);
        var destinationChunk = destination.ReserveEntity(entity, out var destinationChunkIndex, out var destinationRowIndex);
        destinationChunk.CopySharedComponentsFrom(sourceChunk, sourceInfo.RowIndex, destinationRowIndex);

        sourceInfo.Archetype.RemoveEntity(sourceInfo.ChunkIndex, sourceInfo.RowIndex, out var movedEntity);

        if (movedEntity.IsValid)
        {
            _locations[movedEntity.Id] = sourceInfo;
        }

        _locations[entity.Id] = new EntityLocation(destination, destinationChunkIndex, destinationRowIndex);
        TouchQueryLayout();
    }

    private void MoveEntity<T>(Entity entity, EntityLocation sourceInfo, Archetype destination, ComponentType componentType, in T componentValue)
    {
        var sourceChunk = sourceInfo.Archetype.GetChunk(sourceInfo.ChunkIndex);
        var destinationChunk = destination.ReserveEntity(entity, out var destinationChunkIndex, out var destinationRowIndex);
        destinationChunk.CopySharedComponentsFrom(sourceChunk, sourceInfo.RowIndex, destinationRowIndex);

        var destinationColumnIndex = destination.GetComponentIndex(componentType);
        destinationChunk.SetComponentAtTyped(destinationColumnIndex, destinationRowIndex, in componentValue);

        sourceInfo.Archetype.RemoveEntity(sourceInfo.ChunkIndex, sourceInfo.RowIndex, out var movedEntity);

        if (movedEntity.IsValid)
        {
            _locations[movedEntity.Id] = sourceInfo;
        }

        _locations[entity.Id] = new EntityLocation(destination, destinationChunkIndex, destinationRowIndex);
        TouchQueryLayout();
    }

    private void MoveEntityBoxed(Entity entity, EntityLocation sourceInfo, Archetype destination, ComponentType componentType, object? componentValue)
    {
        var sourceChunk = sourceInfo.Archetype.GetChunk(sourceInfo.ChunkIndex);
        var destinationChunk = destination.ReserveEntity(entity, out var destinationChunkIndex, out var destinationRowIndex);
        destinationChunk.CopySharedComponentsFrom(sourceChunk, sourceInfo.RowIndex, destinationRowIndex);

        var destinationColumnIndex = destination.GetComponentIndex(componentType);
        destinationChunk.SetComponent(componentType, destinationRowIndex, componentValue);

        sourceInfo.Archetype.RemoveEntity(sourceInfo.ChunkIndex, sourceInfo.RowIndex, out var movedEntity);

        if (movedEntity.IsValid)
        {
            _locations[movedEntity.Id] = sourceInfo;
        }

        _locations[entity.Id] = new EntityLocation(destination, destinationChunkIndex, destinationRowIndex);
        TouchQueryLayout();
    }

    private Archetype GetOrCreateDestinationArchetype(Archetype source, ComponentType componentType, Signature destinationSignature, bool isAdd)
    {
        var destination = GetOrCreateArchetype(destinationSignature);
        if (isAdd)
        {
            source.Edges.CacheAdd(componentType, destination);
            destination.Edges.CacheRemove(componentType, source);
        }
        else
        {
            source.Edges.CacheRemove(componentType, destination);
            destination.Edges.CacheAdd(componentType, source);
        }

        return destination;
    }

    internal Archetype GetOrCreateArchetype(Signature signature)
    {
        if (_archetypes.TryGetValue(signature, out var archetype))
        {
            return archetype;
        }

        var chunkCapacity = GetArchetypeChunkCapacity(signature);
        archetype = new Archetype(signature, ResolveComponentTypes(signature), chunkCapacity);
        _archetypes.Add(signature, archetype);
        PublishArchetypeSnapshot(archetype);
        _archetypeGeneration++;
        return archetype;
    }

    private int GetArchetypeChunkCapacity(Signature signature)
    {
        if (signature.Count == 0 && _chunkCapacity >= EmptyArchetypeChunkCapacityThreshold)
        {
            return Math.Max(_chunkCapacity, EmptyArchetypeChunkCapacity);
        }

        if (!_adaptiveChunkCapacity)
        {
            return _chunkCapacity;
        }

        var approximateBytesPerEntity = GetApproximateBytesPerEntity(signature);
        if (approximateBytesPerEntity <= 0)
        {
            return _chunkCapacity;
        }

        var adaptiveChunkCapacity = AdaptiveChunkTargetBytes / approximateBytesPerEntity;
        if (adaptiveChunkCapacity <= _chunkCapacity)
        {
            return _chunkCapacity;
        }

        return Math.Min(adaptiveChunkCapacity, AdaptiveMaxChunkCapacity);
    }

    private int GetApproximateBytesPerEntity(Signature signature)
    {
        var bytesPerEntity = EntitySizeInBytes;
        var components = signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            bytesPerEntity += GetManagedTypeSize(_components.GetType(components[index]));
        }

        return bytesPerEntity;
    }

    private static int GetManagedTypeSize(Type componentType)
    {
        return ManagedTypeSizeCache.GetOrAdd(componentType, static type =>
        {
            return (int)GetManagedTypeSizeMethod
                .MakeGenericMethod(type)
                .Invoke(null, null)!;
        });
    }

    private static int GetManagedTypeSizeGeneric<T>()
    {
        return Unsafe.SizeOf<T>();
    }

    private Type[] ResolveComponentTypes(Signature signature)
    {
        var componentCount = signature.Count;
        if (componentCount == 0)
        {
            return Array.Empty<Type>();
        }

        var types = new Type[componentCount];
        var components = signature.AsSpan();
        for (var index = 0; index < componentCount; index++)
        {
            types[index] = _components.GetType(components[index]);
        }

        return types;
    }

    internal Query GetOrCreateQuery(QueryFilter filter)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _queries);
            if (snapshot.TryGetValue(filter, out var query))
            {
                return query;
            }

            var candidate = new Query(this, filter);
            var updated = new Dictionary<QueryFilter, Query>(snapshot)
            {
                [filter] = candidate
            };

            if (ReferenceEquals(Interlocked.CompareExchange(ref _queries, updated, snapshot), snapshot))
            {
                return candidate;
            }
        }
    }

    private void PublishArchetypeSnapshot(Archetype archetype)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _archetypeSnapshot);
            var updated = new Archetype[snapshot.Length + 1];
            Array.Copy(snapshot, updated, snapshot.Length);
            updated[^1] = archetype;

            if (ReferenceEquals(Interlocked.CompareExchange(ref _archetypeSnapshot, updated, snapshot), snapshot))
            {
                return;
            }
        }
    }

    private EntityLocation GetRequiredLocation(Entity entity)
    {
        if (entity.Id < 0 || entity.Id >= _locations.Count)
        {
            throw new InvalidOperationException($"Entity {entity} is stale or unknown.");
        }

        var info = _locations[entity.Id];
        if (info.Archetype is null || _versions[entity.Id] != entity.Version)
        {
            throw new InvalidOperationException($"Entity {entity} is stale or unknown.");
        }

        return info;
    }

    private void DestroySingle(Entity entity)
    {
        var info = GetRequiredLocation(entity);
        info.Archetype.RemoveEntity(info.ChunkIndex, info.RowIndex, out var movedEntity);
        _hierarchy.RemoveDestroyed(entity);
        _locations[entity.Id] = default;
        _versions[entity.Id] = entity.Version + 1;
        PushFreeId(entity.Id, entity.Version + 1);

        if (movedEntity.IsValid)
        {
            _locations[movedEntity.Id] = info;
        }
    }

    private Entity CreateInArchetype(Archetype archetype, out Chunk chunk, out int rowIndex)
    {
        var id = AcquireEntityId(out var version);
        var entity = new Entity(id, version);
        chunk = archetype.ReserveEntity(entity, out var chunkIndex, out rowIndex);
        _locations[id] = new EntityLocation(archetype, chunkIndex, rowIndex);
        TouchQueryLayout();
        return entity;
    }

    private static void SetCreatedComponent<T>(Archetype archetype, Chunk chunk, int rowIndex, ComponentType componentType, in T component)
    {
        chunk.SetComponentAtTyped(archetype.GetComponentIndex(componentType), rowIndex, in component);
    }

    private int AppendEntitySlots(int newEntityCount)
    {
        var startId = _versions.Count;
        if (newEntityCount == 0)
        {
            return startId;
        }

        var requiredCount = startId + newEntityCount;
        EnsureBatchCapacity(requiredCount, newEntityCount);
        CollectionsMarshal.SetCount(_versions, requiredCount);
        CollectionsMarshal.SetCount(_locations, requiredCount);
        CollectionsMarshal.AsSpan(_versions)[startId..requiredCount].Fill(1);
        return startId;
    }

    private void EnsureBatchCapacity(int requiredCount, int batchCount)
    {
        if (_versions.Capacity >= requiredCount && _locations.Capacity >= requiredCount)
        {
            return;
        }

        var targetCapacity = requiredCount + (batchCount / 2);
        if (targetCapacity < requiredCount)
        {
            targetCapacity = requiredCount;
        }

        EnsureCapacity(targetCapacity);
    }

    private void CreateManyFresh(Span<Entity> entities)
    {
        var startId = AppendEntitySlots(entities.Length);

        var archetype = GetOrCreateArchetype(Signature.Empty);
        var maxRangeCount = Math.Min(entities.Length, archetype.Chunks.Count + ((entities.Length + _chunkCapacity - 1) / _chunkCapacity));

        if (maxRangeCount <= StackAllocatedBatchRangeLimit)
        {
            Span<EntityBatchRange> ranges = stackalloc EntityBatchRange[StackAllocatedBatchRangeLimit];
            var rangeCount = archetype.ReserveEntityRanges(entities.Length, ranges);
            WriteCreatedEntitiesAndLocations(archetype, entities, ranges[..rangeCount], 0, startId);
            return;
        }

        var rentedRanges = ArrayPool<EntityBatchRange>.Shared.Rent(maxRangeCount);
        try
        {
            var ranges = rentedRanges.AsSpan(0, maxRangeCount);
            var rangeCount = archetype.ReserveEntityRanges(entities.Length, ranges);
            WriteCreatedEntitiesAndLocations(archetype, entities, ranges[..rangeCount], 0, startId);
        }
        finally
        {
            ArrayPool<EntityBatchRange>.Shared.Return(rentedRanges);
        }
    }

    private void WriteCreatedEntitiesAndLocations(Archetype archetype, Span<Entity> entities, ReadOnlySpan<EntityBatchRange> ranges, int reusedCount, int startId)
    {
        var locations = CollectionsMarshal.AsSpan(_locations);
        var freeIds = _freeIds;
        var freeIndex = _freeIdCount;
        var entityIndex = 0;
        var nextId = startId;

        foreach (var range in ranges)
        {
            var chunkEntities = archetype.GetChunk(range.ChunkIndex).GetReservedEntities(range.StartRow, range.Count);
            var rowOffset = 0;

            for (; rowOffset < range.Count && entityIndex < reusedCount; rowOffset++)
            {
                var recycled = freeIds[--freeIndex];
                var entity = new Entity(recycled.Id, recycled.Version);
                entities[entityIndex++] = entity;
                chunkEntities[rowOffset] = entity;
                locations[entity.Id] = new EntityLocation(archetype, range.ChunkIndex, range.StartRow + rowOffset);
            }

            for (; rowOffset < range.Count; rowOffset++)
            {
                var entity = new Entity(nextId++, 1);
                entities[entityIndex++] = entity;
                chunkEntities[rowOffset] = entity;
                locations[entity.Id] = new EntityLocation(archetype, range.ChunkIndex, range.StartRow + rowOffset);
            }
        }

        _freeIdCount = freeIndex;
    }

    private int AcquireEntityId(out int version)
    {
        if (_freeIdCount > 0)
        {
            var recycled = PopFreeId();
            version = recycled.Version;
            return recycled.Id;
        }

        var id = _versions.Count;
        _versions.Add(1);
        _locations.Add(default);
        version = 1;
        return id;
    }

    private void PushFreeId(int id, int version)
    {
        if (_freeIdCount == _freeIds.Length)
        {
            var newCapacity = _freeIds.Length == 0 ? 4 : _freeIds.Length * 2;
            Array.Resize(ref _freeIds, newCapacity);
        }

        _freeIds[_freeIdCount++] = new RecycledEntity(id, version);
    }

    private RecycledEntity PopFreeId()
    {
        return _freeIds[--_freeIdCount];
    }

    private void TouchQueryLayout()
    {
        if (_queryLayoutSuppressionCount > 0)
        {
            _queryLayoutDirty = true;
            return;
        }

        _queryLayoutGeneration++;
    }

    private ComponentType GetComponentType<T>()
    {
        if (ComponentTypeCache<T>.Registry != _components)
        {
            ComponentTypeCache<T>.Registry = _components;
            ComponentTypeCache<T>.ComponentType = _components.GetOrCreate<T>();
        }

        return ComponentTypeCache<T>.ComponentType;
    }

    private void ValidateSnapshotEntitySlot(int entityId)
    {
        if (entityId < 0 || entityId >= _versions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(entityId));
        }
    }

    internal void LinkSnapshot(Entity parent, Entity child)
    {
        _hierarchy.LinkRestored(parent, child);
    }

    internal Entity ReserveDeferredEntity()
    {
        var id = AcquireEntityId(out var version);
        return new Entity(id, version);
    }

    internal void ReleaseReservedEntity(Entity entity)
    {
        if (entity.Id < 0 || entity.Id >= _locations.Count)
        {
            throw new InvalidOperationException($"Entity {entity} is stale or unknown.");
        }

        if (_locations[entity.Id].Archetype is not null || _versions[entity.Id] != entity.Version)
        {
            throw new InvalidOperationException($"Entity {entity} is not a deferred reserved entity.");
        }

        var nextVersion = entity.Version + 1;
        _versions[entity.Id] = nextVersion;
        PushFreeId(entity.Id, nextVersion);
    }

    public void Replay(in FrameCommands frameCommands)
    {
        var state = frameCommands.State;
        var componentTypeCache = new Dictionary<Type, ComponentType>();
        BeginDeferredLayoutUpdates();
        try
        {
            foreach (var reserved in state.ReservedEntities)
            {
                EnsureReplayReservation(reserved);
            }

            foreach (var released in state.ReleasedEntities)
            {
                ReleaseReservedEntity(released);
            }

            foreach (var created in state.CreatedEntities)
            {
                MaterializeReservedEntity(created.Entity, created.Components, componentTypeCache, reservationChecked: true);
            }

            foreach (var link in state.LinkCommands)
            {
                Link(link.Parent, link.Child);
            }

            foreach (var unlink in state.UnlinkCommands)
            {
                Unlink(unlink.Child);
            }

            foreach (var add in state.AddCommands)
            {
                AddBoxed(add.Entity, ResolveReplayComponentType(add.ComponentType, componentTypeCache), add.Value);
            }

            foreach (var set in state.SetCommands)
            {
                SetBoxed(set.Entity, ResolveReplayComponentType(set.ComponentType, componentTypeCache), set.Value);
            }

            foreach (var remove in state.RemoveCommands)
            {
                RemoveBoxed(remove.Entity, ResolveReplayComponentType(remove.ComponentType, componentTypeCache));
            }

            foreach (var entity in state.DestroyedEntities)
            {
                if (IsAlive(entity))
                {
                    Destroy(entity);
                }
            }
        }
        finally
        {
            EndDeferredLayoutUpdates();
        }
    }

    internal void Replay(CommandBuffer.CompiledCommandBatch compiledCommands)
    {
        ArgumentNullException.ThrowIfNull(compiledCommands);

        var componentTypeCache = new Dictionary<Type, ComponentType>();
        BeginDeferredLayoutUpdates();
        try
        {
            foreach (var reserved in compiledCommands.ReservedEntities)
            {
                EnsureReplayReservation(reserved);
            }

            foreach (var released in compiledCommands.ReleasedEntities)
            {
                ReleaseReservedEntity(released);
            }

            foreach (var created in compiledCommands.CreatedEntities)
            {
                MaterializeReservedEntity(created.Entity, created.Signature, created.Components, componentTypeCache, reservationChecked: true);
            }

            foreach (var link in compiledCommands.LinkCommands)
            {
                Link(link.Parent, link.Child);
            }

            foreach (var unlink in compiledCommands.UnlinkCommands)
            {
                Unlink(unlink.Child);
            }

            foreach (var add in compiledCommands.AddCommands)
            {
                AddBoxed(add.Entity, ResolveCompiledComponentType(add.RuntimeType, add.ComponentType, componentTypeCache), add.Value);
            }

            foreach (var set in compiledCommands.SetCommands)
            {
                SetBoxed(set.Entity, ResolveCompiledComponentType(set.RuntimeType, set.ComponentType, componentTypeCache), set.Value);
            }

            foreach (var remove in compiledCommands.RemoveCommands)
            {
                RemoveBoxed(remove.Entity, ResolveCompiledComponentType(remove.RuntimeType, remove.ComponentType, componentTypeCache));
            }

            foreach (var entity in compiledCommands.DestroyedEntities)
            {
                if (IsAlive(entity))
                {
                    Destroy(entity);
                }
            }
        }
        finally
        {
            EndDeferredLayoutUpdates();
        }
    }

    internal void MaterializeReservedEntity(
        Entity entity,
        IReadOnlyList<FrameComponentValue> components,
        Dictionary<Type, ComponentType>? componentTypeCache = null,
        bool reservationChecked = false)
    {
        if (!reservationChecked)
        {
            EnsureReplayReservation(entity);
        }

        var signature = BuildReplaySignature(components, componentTypeCache);
        var archetype = GetOrCreateArchetype(signature);
        var chunk = archetype.ReserveEntity(entity, out var chunkIndex, out var rowIndex);
        _locations[entity.Id] = new EntityLocation(archetype, chunkIndex, rowIndex);

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            chunk.SetComponent(ResolveReplayComponentType(component.ComponentType, componentTypeCache), rowIndex, component.Value);
        }

        TouchQueryLayout();
    }

    internal void MaterializeReservedEntity(
        Entity entity,
        Signature? signature,
        IReadOnlyList<CommandBuffer.CompiledComponentValue> components,
        Dictionary<Type, ComponentType>? componentTypeCache = null,
        bool reservationChecked = false)
    {
        if (!reservationChecked)
        {
            EnsureReplayReservation(entity);
        }

        signature ??= BuildReplaySignature(components, componentTypeCache);
        var archetype = GetOrCreateArchetype(signature);
        var chunk = archetype.ReserveEntity(entity, out var chunkIndex, out var rowIndex);
        _locations[entity.Id] = new EntityLocation(archetype, chunkIndex, rowIndex);

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            chunk.SetComponent(ResolveCompiledComponentType(component.RuntimeType, component.ComponentType, componentTypeCache), rowIndex, component.Value);
        }

        TouchQueryLayout();
    }

    internal void AddBoxed(Entity entity, Type componentType, object? component)
    {
        AddBoxed(entity, GetComponentType(componentType), component);
    }

    internal void AddBoxed(Entity entity, ComponentType componentType, object? component)
    {
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (archetype.TryGetComponentIndex(componentType, out _))
        {
            archetype.GetChunk(info.ChunkIndex).SetComponent(componentType, info.RowIndex, component);
            return;
        }

        if (archetype.Edges.TryGetAdd(componentType, out var cached) && cached is not null)
        {
            MoveEntityBoxed(entity, info, cached, componentType, component);
            return;
        }

        var destinationSignature = archetype.Signature.Add(componentType);
        var destination = GetOrCreateDestinationArchetype(archetype, componentType, destinationSignature, isAdd: true);
        MoveEntityBoxed(entity, info, destination, componentType, component);
    }

    internal void SetBoxed(Entity entity, Type componentType, object? component)
    {
        SetBoxed(entity, GetComponentType(componentType), component);
    }

    internal void SetBoxed(Entity entity, ComponentType componentType, object? component)
    {
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (archetype.TryGetComponentIndex(componentType, out _))
        {
            archetype.GetChunk(info.ChunkIndex).SetComponent(componentType, info.RowIndex, component);
            return;
        }

        var destinationSignature = archetype.Signature.Add(componentType);
        var destination = GetOrCreateDestinationArchetype(archetype, componentType, destinationSignature, isAdd: true);
        MoveEntityBoxed(entity, info, destination, componentType, component);
    }

    internal void RemoveBoxed(Entity entity, Type componentType)
    {
        RemoveBoxed(entity, GetComponentType(componentType));
    }

    internal void RemoveBoxed(Entity entity, ComponentType componentType)
    {
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (!archetype.TryGetComponentIndex(componentType, out _))
        {
            return;
        }

        if (archetype.Edges.TryGetRemove(componentType, out var cached) && cached is not null)
        {
            MoveEntity(entity, info, cached);
            return;
        }

        var destinationSignature = archetype.Signature.Remove(componentType);
        var destination = GetOrCreateDestinationArchetype(archetype, componentType, destinationSignature, isAdd: false);
        MoveEntity(entity, info, destination);
    }

    private void BeginDeferredLayoutUpdates()
    {
        _queryLayoutSuppressionCount++;
    }

    private void EndDeferredLayoutUpdates()
    {
        _queryLayoutSuppressionCount--;
        if (_queryLayoutSuppressionCount == 0 && _queryLayoutDirty)
        {
            _queryLayoutDirty = false;
            _queryLayoutGeneration++;
        }
    }

    private void EnsureReplayReservation(Entity entity)
    {
        if (entity.Id < _locations.Count &&
            _locations[entity.Id].Archetype is null &&
            _versions[entity.Id] == entity.Version &&
            !IsEntityAvailableInFreeList(entity))
        {
            return;
        }

        var reserved = ReserveDeferredEntity();
        if (reserved != entity)
        {
            throw new InvalidOperationException($"Replay target diverged while reserving {entity}; got {reserved} instead.");
        }
    }

    private bool IsEntityAvailableInFreeList(Entity entity)
    {
        for (var index = 0; index < _freeIdCount; index++)
        {
            var recycled = _freeIds[index];
            if (recycled.Id == entity.Id && recycled.Version == entity.Version)
            {
                return true;
            }
        }

        return false;
    }

    private Signature BuildReplaySignature(
        IReadOnlyList<FrameComponentValue> components,
        Dictionary<Type, ComponentType>? componentTypeCache = null)
    {
        if (components.Count == 0)
        {
            return Signature.Empty;
        }

        var componentTypes = new ComponentType[components.Count];
        for (var index = 0; index < components.Count; index++)
        {
            componentTypes[index] = ResolveReplayComponentType(components[index].ComponentType, componentTypeCache);
        }

        return new Signature(componentTypes);
    }

    private Signature BuildReplaySignature(
        IReadOnlyList<CommandBuffer.CompiledComponentValue> components,
        Dictionary<Type, ComponentType>? componentTypeCache = null)
    {
        if (components.Count == 0)
        {
            return Signature.Empty;
        }

        var componentTypes = new ComponentType[components.Count];
        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            componentTypes[index] = ResolveCompiledComponentType(component.RuntimeType, component.ComponentType, componentTypeCache);
        }

        return new Signature(componentTypes);
    }

    private ComponentType ResolveReplayComponentType(Type componentType, Dictionary<Type, ComponentType>? cache)
    {
        if (cache is not null && cache.TryGetValue(componentType, out var resolved))
        {
            return resolved;
        }

        resolved = GetComponentType(componentType);
        cache?.Add(componentType, resolved);
        return resolved;
    }

    private ComponentType ResolveCompiledComponentType(
        Type runtimeType,
        ComponentType componentType,
        Dictionary<Type, ComponentType>? cache)
    {
        if (componentType.IsValid)
        {
            return componentType;
        }

        return ResolveReplayComponentType(runtimeType, cache);
    }

    private ComponentType GetComponentType(Type componentType)
    {
        return _components.GetOrCreate(componentType);
    }

    private static class ComponentTypeCache<T>
    {
        public static ComponentRegistry? Registry;
        public static ComponentType ComponentType;
    }

    private readonly record struct RecycledEntity(int Id, int Version);
}
