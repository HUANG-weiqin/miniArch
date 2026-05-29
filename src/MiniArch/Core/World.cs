using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Runtime.CompilerServices;

using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Owns entity storage and queries.
/// </summary>
public sealed class World : IDisposable
{
    private const int DefaultChunkCapacity = 128;
    private const int EmptyArchetypeChunkCapacity = 1024;
    private const int EmptyArchetypeChunkCapacityThreshold = 128;
    private const int AdaptiveChunkTargetBytes = 16 * 1024;
    private const int AdaptiveMaxChunkCapacity = 1024;
    private const int StackAllocatedBatchRangeLimit = 128;
    private static readonly int EntitySizeInBytes = Unsafe.SizeOf<Entity>();

    private readonly Dictionary<Signature, Archetype> _archetypes = new();
    private readonly Dictionary<CreateArchetypeKey, Archetype> _createArchetypeCache = new();
    private readonly HierarchyTable _hierarchy = new();
    private int[] _versions;
    private EntityLocation[] _locations;
    private int _entitySlotCount;
    private Dictionary<QueryDescription, QueryFilter> _queryFiltersByDescription = new();
    private Dictionary<QueryFilter, MiniArch.Core.Query> _queries = new();
    private Archetype[] _archetypeSnapshot = Array.Empty<Archetype>();
    private readonly int _chunkCapacity;
    private readonly bool _adaptiveChunkCapacity;
    private RecycledEntity[] _freeIds;
    private int _freeIdCount;
    private int _queryLayoutSuppressionCount;
    private bool _queryLayoutDirty;
    private int _queryGeneration;
    private int _createArchetypeCacheGeneration;
    private readonly List<Entity> _destroyOrderScratch = new(8);
    private int[] _destroyVisitedGen = [];
    private int _destroyCurrentGen;
    private readonly Dictionary<Type, ComponentType> _replayComponentTypeScratch = new(16);

    private bool _disposed;

    /// <summary>
    /// Creates a world.
    /// </summary>
    public World(int chunkCapacity = DefaultChunkCapacity, int entityCapacity = 64)
    {
        if (chunkCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCapacity), "Chunk capacity must be positive.");
        }

        if (entityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCapacity), "Entity capacity must be non-negative.");
        }

        _chunkCapacity = chunkCapacity;
        _adaptiveChunkCapacity = chunkCapacity == DefaultChunkCapacity;
        _versions = new int[entityCapacity];
        _locations = new EntityLocation[entityCapacity];
        _entitySlotCount = 0;
        _freeIds = entityCapacity == 0 ? Array.Empty<RecycledEntity>() : new RecycledEntity[entityCapacity];
        _destroyVisitedGen = entityCapacity == 0 ? [] : new int[entityCapacity];
        _destroyOrderScratch = new List<Entity>(entityCapacity);
    }

    /// <summary>
    /// Releases owned runtime state.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _archetypes.Clear();
        _createArchetypeCache.Clear();
        _queryFiltersByDescription.Clear();
        _queries.Clear();
        _archetypeSnapshot = Array.Empty<Archetype>();
        _entitySlotCount = 0;
        _versions = Array.Empty<int>();
        _locations = Array.Empty<EntityLocation>();
        _freeIdCount = 0;
        _destroyVisitedGen = [];
        _destroyCurrentGen = 0;
        _hierarchy.Reset();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
#if DEBUG
        if (_disposed) throw new ObjectDisposedException(nameof(World));
#endif
    }

    /// <summary>
    /// Gets the component registry.
    /// </summary>
    public ComponentRegistry Components
    {
        get
        {
            ThrowIfDisposed();
            return ComponentRegistry.Shared;
        }
    }

    /// <summary>
    /// Gets the entity metadata capacity.
    /// </summary>
    public int EntityCapacity => _versions.Length;

    internal int ChunkCapacity => _chunkCapacity;

    internal int EntitySlotCount => _entitySlotCount;

    internal ReadOnlySpan<int> EntityVersions => _versions.AsSpan(0, _entitySlotCount);

    internal Archetype[] Archetypes => Volatile.Read(ref _archetypeSnapshot);

    internal HierarchyTable Hierarchy => _hierarchy;

    internal int QueryGeneration => _queryGeneration;

    internal void Reset(int entitySlotCount)
    {
        if (entitySlotCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entitySlotCount));
        }

        _archetypes.Clear();
        _createArchetypeCache.Clear();
        _archetypeSnapshot = Array.Empty<Archetype>();
        _queryFiltersByDescription = new Dictionary<QueryDescription, QueryFilter>();
        _queries = new Dictionary<QueryFilter, MiniArch.Core.Query>();
        _queryGeneration = 0;
        _createArchetypeCacheGeneration++;
        _freeIdCount = 0;
        _destroyVisitedGen = [];
        _destroyCurrentGen = 0;
        _hierarchy.Reset();

        _entitySlotCount = 0;
        EnsureCapacity(entitySlotCount);
        _entitySlotCount = entitySlotCount;
        _versions.AsSpan(0, entitySlotCount).Clear();
        _locations.AsSpan(0, entitySlotCount).Clear();

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
        if (_freeIds.Length < _entitySlotCount)
        {
            Array.Resize(ref _freeIds, _entitySlotCount);
        }

        _freeIdCount = 0;
        for (var id = _entitySlotCount - 1; id >= 0; id--)
        {
            if (_locations[id].Archetype is null)
            {
                _freeIds[_freeIdCount++] = new RecycledEntity(id, _versions[id]);
            }
        }
    }

    /// <summary>
    /// Creates an empty entity.
    /// </summary>
    public Entity Create()
    {
        ThrowIfDisposed();
        var archetype = GetOrCreateArchetype(Signature.Empty);
        return CreateInArchetype(archetype, out _, out _);
    }

    /// <summary>
    /// Creates an entity with one component.
    /// </summary>
    public Entity Create<T1>(T1 component1)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var archetype = GetOrCreateCreateArchetype<T1>(componentType1);
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        chunk.SetComponentAtTyped(archetype.GetComponentIndex(componentType1), rowIndex, in component1);
        return entity;
    }

    /// <summary>
    /// Creates an entity with two components.
    /// </summary>
    public Entity Create<T1, T2>(T1 component1, T2 component2)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var archetype = GetOrCreateCreateArchetype<T1, T2>(componentType1, componentType2);
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        chunk.SetComponentAtTyped(archetype.GetComponentIndex(componentType1), rowIndex, in component1);
        chunk.SetComponentAtTyped(archetype.GetComponentIndex(componentType2), rowIndex, in component2);
        return entity;
    }

    /// <summary>
    /// Creates an entity with three components.
    /// </summary>
    public Entity Create<T1, T2, T3>(T1 component1, T2 component2, T3 component3)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        Span<ComponentType> components = stackalloc ComponentType[3] { componentType1, componentType2, componentType3 };
        var archetype = GetOrCreateCreateArchetype(components);
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        return entity;
    }

    /// <summary>
    /// Creates an entity with four components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4>(T1 component1, T2 component2, T3 component3, T4 component4)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        Span<ComponentType> components = stackalloc ComponentType[4] { componentType1, componentType2, componentType3, componentType4 };
        var archetype = GetOrCreateCreateArchetype(components);
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        return entity;
    }

    /// <summary>
    /// Creates an entity with five components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        Span<ComponentType> components = stackalloc ComponentType[5] { componentType1, componentType2, componentType3, componentType4, componentType5 };
        var archetype = GetOrCreateCreateArchetype(components);
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        return entity;
    }

    /// <summary>
    /// Creates an entity with six components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        Span<ComponentType> components = stackalloc ComponentType[6] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6 };
        var archetype = GetOrCreateCreateArchetype(components);
        var entity = CreateInArchetype(archetype, out var chunk, out var rowIndex);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, chunk, rowIndex, componentType6, in component6);
        return entity;
    }

    /// <summary>
    /// Creates an entity with seven components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        Span<ComponentType> components = stackalloc ComponentType[7] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7 };
        var archetype = GetOrCreateCreateArchetype(components);
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

    /// <summary>
    /// Creates an entity with eight components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        Span<ComponentType> components = stackalloc ComponentType[8] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8 };
        var archetype = GetOrCreateCreateArchetype(components);
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

    /// <summary>
    /// Creates an entity with nine components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9)
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        Span<ComponentType> components = stackalloc ComponentType[9] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9 };
        var archetype = GetOrCreateCreateArchetype(components);
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

    /// <summary>
    /// Creates an entity with ten components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10)
    {
        ThrowIfDisposed();
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
        Span<ComponentType> components = stackalloc ComponentType[10] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10 };
        var archetype = GetOrCreateCreateArchetype(components);
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

    /// <summary>
    /// Creates an entity with eleven components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11)
    {
        ThrowIfDisposed();
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
        Span<ComponentType> components = stackalloc ComponentType[11] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11 };
        var archetype = GetOrCreateCreateArchetype(components);
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

    /// <summary>
    /// Creates an entity with twelve components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12)
    {
        ThrowIfDisposed();
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
        Span<ComponentType> components = stackalloc ComponentType[12] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12 };
        var archetype = GetOrCreateCreateArchetype(components);
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

    /// <summary>
    /// Creates an entity with thirteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13)
    {
        ThrowIfDisposed();
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
        Span<ComponentType> components = stackalloc ComponentType[13] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13 };
        var archetype = GetOrCreateCreateArchetype(components);
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

    /// <summary>
    /// Creates an entity with fourteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14)
    {
        ThrowIfDisposed();
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
        Span<ComponentType> components = stackalloc ComponentType[14] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14 };
        var archetype = GetOrCreateCreateArchetype(components);
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

    /// <summary>
    /// Creates an entity with fifteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15)
    {
        ThrowIfDisposed();
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
        Span<ComponentType> components = stackalloc ComponentType[15] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14, componentType15 };
        var archetype = GetOrCreateCreateArchetype(components);
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

    /// <summary>
    /// Creates an entity with sixteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15, T16 component16)
    {
        ThrowIfDisposed();
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
        Span<ComponentType> components = stackalloc ComponentType[16] { componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14, componentType15, componentType16 };
        var archetype = GetOrCreateCreateArchetype(components);
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

    /// <summary>
    /// Creates many empty entities.
    /// </summary>
    public void CreateMany(Span<Entity> entities)
    {
        ThrowIfDisposed();
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

    /// <summary>
    /// Ensures entity metadata capacity.
    /// </summary>
    public void EnsureCapacity(int entityCapacity)
    {
        ThrowIfDisposed();
        if (entityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        }

        if (_versions.Length < entityCapacity)
        {
            Array.Resize(ref _versions, entityCapacity);
        }

        if (_locations.Length < entityCapacity)
        {
            Array.Resize(ref _locations, entityCapacity);
        }

        EnsureDestroyScratchCapacity(entityCapacity);
    }


    private void EnsureEntityCapacity(int requiredCount)
    {
        if (requiredCount <= _versions.Length) return;
        var newLength = Math.Max(requiredCount, _versions.Length * 2);
        Array.Resize(ref _versions, newLength);
        Array.Resize(ref _locations, newLength);
    }

    /// <summary>
    /// Destroys an entity.
    /// </summary>
    public void Destroy(Entity entity)
    {
        ThrowIfDisposed();

        if (!_hierarchy.HasChildren(entity))
        {
            DestroySingle(entity);
            TouchQueryLayout();
            return;
        }

        _destroyOrderScratch.Clear();
        if (++_destroyCurrentGen == 0)
        {
            Array.Clear(_destroyVisitedGen);
            _destroyCurrentGen = 1;
        }

        try
        {
            _hierarchy.CollectDestroySubtree(this, entity, _destroyVisitedGen, _destroyCurrentGen, _destroyOrderScratch);
            if (_destroyOrderScratch.Count == 0)
            {
                throw new InvalidOperationException($"Cannot destroy entity {entity}: it is no longer alive. The entity may have already been destroyed.");
            }

            for (var index = 0; index < _destroyOrderScratch.Count; index++)
            {
                DestroySingle(_destroyOrderScratch[index]);
            }

            TouchQueryLayout();
        }
        finally
        {
            _destroyOrderScratch.Clear();
        }
    }

    /// <summary>
    /// Links a child to a parent.
    /// </summary>
    public void Link(Entity parent, Entity child)
    {
        ThrowIfDisposed();
        _hierarchy.Link(this, parent, child);
    }

    /// <summary>
    /// Unlinks a child.
    /// </summary>
    public void Unlink(Entity child)
    {
        ThrowIfDisposed();
        GetRequiredLocation(child);
        _hierarchy.Unlink(child);
    }

    /// <summary>
    /// Tries to get a parent entity.
    /// </summary>
    public bool TryGetParent(Entity child, out Entity parent)
    {
        ThrowIfDisposed();
        return _hierarchy.TryGetParent(this, child, out parent);
    }

    /// <summary>
    /// Gets the direct children of an entity.
    /// </summary>
    public List<Entity> GetChildren(Entity parent)
    {
        ThrowIfDisposed();
        return _hierarchy.GetChildren(this, parent);
    }

    /// <summary>
    /// Adds a component to an entity.
    /// </summary>
    public void Add<T>(Entity entity, T component)
    {
        ThrowIfDisposed();
        ApplyTypedAddOrSet(entity, GetComponentType<T>(), in component);
    }

    /// <summary>
    /// Sets a component on an entity.
    /// </summary>
    public void Set<T>(Entity entity, T component)
    {
        ThrowIfDisposed();
        ApplyTypedAddOrSet(entity, GetComponentType<T>(), in component);
    }

    /// <summary>
    /// Removes a component from an entity.
    /// </summary>
    public void Remove<T>(Entity entity)
    {
        ThrowIfDisposed();
        var componentType = GetComponentType<T>();
        RemoveBoxed(entity, componentType);
    }

    /// <summary>
    /// Tries to read a component directly from an entity.
    /// </summary>
    public bool TryGet<T>(Entity entity, out T component)
    {
        ThrowIfDisposed();
        if (!TryGetLocation(entity, out var info))
        {
            component = default!;
            return false;
        }

        var componentType = GetComponentType<T>();
        if (!info.Archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            component = default!;
            return false;
        }

        component = info.Archetype
            .GetChunk(info.ChunkIndex)
            .GetComponentAt<T>(componentIndex, info.RowIndex);
        return true;
    }

    /// <summary>
    /// Gets a component directly without version or bounds checks.
    /// Use only when the entity is known to be alive and the component is known to exist.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Get<T>(Entity entity)
    {
        var stored = _locations[entity.Id];
        var componentType = GetComponentType<T>();
        return stored.Archetype
            .GetChunk(stored.ChunkIndex)
            .GetComponentAt<T>(stored.Archetype.GetComponentIndexFast(componentType), stored.RowIndex);
    }

    /// <summary>
    /// Gets an entity-only query from a description.
    /// </summary>
    public Query Query(in QueryDescription description)
    {
        ThrowIfDisposed();
        return new Query(GetAdvancedQuery(in description));
    }

    /// <summary>
    /// Tries to get an entity location.
    /// </summary>
    public bool TryGetLocation(Entity entity, out EntityInfo info)
    {
        ThrowIfDisposed();
        if (entity.Id < 0 || entity.Id >= _entitySlotCount)
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

    /// <summary>
    /// Returns whether an entity is alive.
    /// </summary>
    public bool IsAlive(Entity entity)
    {
        ThrowIfDisposed();
        return TryGetLocation(entity, out _);
    }

    /// <summary>
    /// Creates a snapshot-equivalent clone of this world.
    /// </summary>
    public World Clone()
    {
        return WorldClone.Clone(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveEntityCore(
        Entity entity,
        EntityLocation sourceInfo,
        Archetype destination,
        out Chunk destinationChunk,
        out int destinationChunkIndex,
        out int destinationRowIndex)
    {
        var sourceChunk = sourceInfo.Archetype.GetChunk(sourceInfo.ChunkIndex);
        destinationChunk = destination.ReserveEntity(entity, out destinationChunkIndex, out destinationRowIndex);
        destinationChunk.CopySharedComponentsFrom(sourceChunk, sourceInfo.RowIndex, destinationRowIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FinishMoveEntity(
        Entity entity,
        EntityLocation sourceInfo,
        Archetype destination,
        int destinationChunkIndex,
        int destinationRowIndex)
    {
        sourceInfo.Archetype.RemoveEntity(sourceInfo.ChunkIndex, sourceInfo.RowIndex, out var movedEntity);

        if (movedEntity.IsValid)
        {
            _locations[movedEntity.Id] = sourceInfo;
        }

        _locations[entity.Id] = new EntityLocation(destination, destinationChunkIndex, destinationRowIndex);
        TouchQueryLayout();
    }

    private void MoveEntity(Entity entity, EntityLocation sourceInfo, Archetype destination)
    {
        MoveEntityCore(entity, sourceInfo, destination, out _, out var chunkIdx, out var rowIdx);
        FinishMoveEntity(entity, sourceInfo, destination, chunkIdx, rowIdx);
    }

    private void MoveEntity<T>(Entity entity, EntityLocation sourceInfo, Archetype destination, ComponentType componentType, in T componentValue)
    {
        MoveEntityCore(entity, sourceInfo, destination, out var destChunk, out var chunkIdx, out var rowIdx);
        destChunk.SetComponentAtTyped(destination.GetComponentIndex(componentType), rowIdx, in componentValue);
        FinishMoveEntity(entity, sourceInfo, destination, chunkIdx, rowIdx);
    }

    private void MoveEntityBoxed(Entity entity, EntityLocation sourceInfo, Archetype destination, ComponentType componentType, object? componentValue)
    {
        MoveEntityCore(entity, sourceInfo, destination, out var destChunk, out var chunkIdx, out var rowIdx);
        destChunk.SetComponent(componentType, rowIdx, componentValue);
        FinishMoveEntity(entity, sourceInfo, destination, chunkIdx, rowIdx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyTypedAddOrSet<T>(Entity entity, ComponentType componentType, in T component)
    {
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            archetype.GetChunk(info.ChunkIndex).SetComponentAtTyped(componentIndex, info.RowIndex, in component);
            return;
        }

        var destination = GetOrCreateAddDestinationArchetype(archetype, componentType);
        MoveEntity(entity, info, destination, componentType, in component);
    }

    private void ApplyBoxedAddOrSet(Entity entity, ComponentType componentType, object? component)
    {
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (archetype.TryGetComponentIndex(componentType, out _))
        {
            archetype.GetChunk(info.ChunkIndex).SetComponent(componentType, info.RowIndex, component);
            return;
        }

        var destination = GetOrCreateAddDestinationArchetype(archetype, componentType);
        MoveEntityBoxed(entity, info, destination, componentType, component);
    }

    private unsafe void ApplyRawAddOrSet(Entity entity, ComponentType componentType, Type runtimeType, byte[] data, int offset, int size)
    {
        fixed (byte* ptr = data)
        {
            ApplyRawAddOrSet(entity, componentType, runtimeType, ptr + offset, null);
        }
    }

    internal unsafe void ApplyRawAddOrSet(Entity entity, ComponentType componentType, Type runtimeType, byte[] data, int offset, ComponentWriterCache.ColumnWriterDelegate? columnWriter)
    {
        fixed (byte* ptr = data)
        {
            ApplyRawAddOrSet(entity, componentType, runtimeType, ptr + offset, columnWriter);
        }
    }

    private unsafe void ApplyRawAddOrSet(Entity entity, ComponentType componentType, Type runtimeType, byte* source, ComponentWriterCache.ColumnWriterDelegate? columnWriter)
    {
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            var chunk = archetype.GetChunk(info.ChunkIndex);
            WriteComponentFromBytes(chunk, componentType, info.RowIndex, source, columnWriter);
            return;
        }

        var destination = GetOrCreateAddDestinationArchetype(archetype, componentType);
        MoveEntityFromBytes(entity, info, destination, componentType, runtimeType, source, columnWriter);
    }

    private static unsafe void WriteComponentFromBytes(Chunk chunk, ComponentType componentType, int row, byte* source, ComponentWriterCache.ColumnWriterDelegate? columnWriter)
    {
        var columnIndex = chunk.GetComponentIndex(componentType);
        if (columnWriter is not null)
        {
            columnWriter(chunk, columnIndex, row, source);
        }
        else
        {
            chunk.WriteComponentRaw(columnIndex, row, source);
        }
    }

    private unsafe void MoveEntityFromBytes(
        Entity entity,
        EntityLocation sourceInfo,
        Archetype destination,
        ComponentType componentType,
        Type runtimeType,
        byte* source,
        ComponentWriterCache.ColumnWriterDelegate? columnWriter)
    {
        MoveEntityCore(entity, sourceInfo, destination, out var destChunk, out var chunkIdx, out var rowIdx);
        WriteComponentFromBytes(destChunk, componentType, rowIdx, source, columnWriter);
        FinishMoveEntity(entity, sourceInfo, destination, chunkIdx, rowIdx);
    }

    private Archetype GetOrCreateAddDestinationArchetype(Archetype source, ComponentType componentType)
    {
        if (source.Edges.TryGetAdd(componentType, out var cached) && cached is not null)
        {
            return cached;
        }

        var destinationSignature = source.Signature.Add(componentType);
        return GetOrCreateDestinationArchetype(source, componentType, destinationSignature, isAdd: true);
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
        AdvanceQueryGeneration();
        return archetype;
    }

    internal Archetype GetOrCreateArchetype(CreateArchetypeKey key)
    {
        if (_createArchetypeCache.TryGetValue(key, out var archetype))
        {
            return archetype;
        }

        archetype = GetOrCreateArchetype(Signature.CreateNormalized(key.ToComponentArray()));
        _createArchetypeCache.TryAdd(key, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype(Span<ComponentType> components)
    {
        if (components.Length == 0)
        {
            return GetOrCreateArchetype(Signature.Empty);
        }

        var uniqueCount = SpanHelper.SortAndDeduplicate(components);

        var normalized = components[..uniqueCount];
        var key = new CreateArchetypeKey(normalized);
        if (_createArchetypeCache.TryGetValue(key, out var archetype))
        {
            return archetype;
        }

        archetype = GetOrCreateArchetype(Signature.CreateNormalized(key.ToComponentArray()));
        _createArchetypeCache.Add(key, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1>(ComponentType componentType1)
    {
        var entry = CreateArchetypeCache<T1>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cachedArchetype))
        {
            return cachedArchetype;
        }

        var archetype = GetOrCreateArchetype(new Signature(componentType1));
        CreateArchetypeCache<T1>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2>(ComponentType componentType1, ComponentType componentType2)
    {
        var entry = CreateArchetypeCache<T1, T2>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cachedArchetype))
        {
            return cachedArchetype;
        }

        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2));
        CreateArchetypeCache<T1, T2>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
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
            bytesPerEntity += ComponentSizeCache.GetSize(ComponentRegistry.Shared.GetType(components[index]));
        }

        return bytesPerEntity;
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
            types[index] = ComponentRegistry.Shared.GetType(components[index]);
        }

        return types;
    }

    internal MiniArch.Core.Query GetAdvancedQuery(in QueryDescription description)
    {
        return GetOrCreateQuery(GetOrCreateQueryFilter(description));
    }

    internal MiniArch.Core.Query GetOrCreateQuery(QueryFilter filter)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _queries);
            if (snapshot.TryGetValue(filter, out var query))
            {
                return query;
            }

            var candidate = new MiniArch.Core.Query(this, filter);
            var updated = new Dictionary<QueryFilter, MiniArch.Core.Query>(snapshot)
            {
                [filter] = candidate
            };

            if (ReferenceEquals(Interlocked.CompareExchange(ref _queries, updated, snapshot), snapshot))
            {
                return candidate;
            }
        }
    }

    private QueryFilter GetOrCreateQueryFilter(in QueryDescription description)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _queryFiltersByDescription);
            if (snapshot.TryGetValue(description, out var filter))
            {
                return filter;
            }

            var candidate = new QueryFilter(
                CreateQueryComponentSet(description.GetRequiredTypes()),
                CreateQueryComponentSet(description.GetExcludedTypes()),
                CreateQueryComponentSet(description.GetAnyTypes()));
            var updated = new Dictionary<QueryDescription, QueryFilter>(snapshot)
            {
                [description] = candidate
            };

            if (ReferenceEquals(Interlocked.CompareExchange(ref _queryFiltersByDescription, updated, snapshot), snapshot))
            {
                return candidate;
            }
        }
    }

    private QueryComponentSet CreateQueryComponentSet(ReadOnlySpan<Type> types)
    {
        if (types.Length == 0)
        {
            return QueryComponentSet.Empty;
        }

        var componentTypes = new ComponentType[types.Length];
        for (var i = 0; i < types.Length; i++)
        {
            componentTypes[i] = ComponentRegistry.Shared.GetOrCreate(types[i]);
        }

        return QueryComponentSet.CreateFrom(componentTypes);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EntityLocation GetRequiredLocation(Entity entity)
    {
        var id = entity.Id;
#if DEBUG
        if ((uint)id >= (uint)_entitySlotCount)
        {
            ThrowInvalidEntity(entity);
        }
#endif

        var info = _locations[id];
        if (info.Archetype is null || _versions[id] != entity.Version)
        {
            ThrowStaleEntity(entity);
        }

        return info;
    }

    [DoesNotReturn]
    private void ThrowInvalidEntity(Entity entity)
    {
        throw new InvalidOperationException($"Entity {entity} does not exist. The entity may have never been created, or its id is invalid.");
    }

    [DoesNotReturn]
    private void ThrowStaleEntity(Entity entity)
    {
        throw new InvalidOperationException($"Entity {entity} is no longer alive. It may have been destroyed in a previous frame or the handle is stale.");
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
        var startId = _entitySlotCount;
        if (newEntityCount == 0)
        {
            return startId;
        }

        var requiredCount = startId + newEntityCount;
        EnsureBatchCapacity(requiredCount, newEntityCount);
        EnsureEntityCapacity(requiredCount);
        _entitySlotCount = requiredCount;
        _versions.AsSpan(startId..requiredCount).Fill(1);
        return startId;
    }

    private void EnsureBatchCapacity(int requiredCount, int batchCount)
    {
        if (_versions.Length >= requiredCount && _locations.Length >= requiredCount)
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
        var locations = _locations.AsSpan(0, _entitySlotCount);
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

    private readonly object _entityIdLock = new();

    private int AcquireEntityId(out int version)
    {
        lock (_entityIdLock)
        {
            if (_freeIdCount > 0)
            {
                var recycled = PopFreeId();
                version = recycled.Version;
                return recycled.Id;
            }

            var id = _entitySlotCount;
            EnsureEntityCapacity(_entitySlotCount + 1);
            _versions[_entitySlotCount] = 1;
            _locations[_entitySlotCount] = default;
            _entitySlotCount++;
            EnsureDestroyScratchCapacity(_entitySlotCount);
            version = 1;
            return id;
        }
    }

    private void PushFreeId(int id, int version)
    {
        lock (_entityIdLock)
        {
            if (_freeIdCount == _freeIds.Length)
            {
                var newCapacity = _freeIds.Length == 0 ? 4 : _freeIds.Length * 2;
                Array.Resize(ref _freeIds, newCapacity);
            }

            _freeIds[_freeIdCount++] = new RecycledEntity(id, version);
        }
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

        AdvanceQueryGeneration();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceQueryGeneration()
    {
        _queryGeneration++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ComponentType GetComponentType<T>()
    {
        return Component<T>.ComponentType;
    }

    private void ValidateSnapshotEntitySlot(int entityId)
    {
        if (entityId < 0 || entityId >= _entitySlotCount)
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
        if (entity.Id < 0 || entity.Id >= _entitySlotCount)
        {
            throw new InvalidOperationException($"Entity {entity} is not a valid deferred entity. The entity handle may be invalid or already materialized.");
        }

        if (_locations[entity.Id].Archetype is not null || _versions[entity.Id] != entity.Version)
        {
            throw new InvalidOperationException($"Entity {entity} is not a deferred reserved entity. It may have already been materialized or was never reserved via CommandBuffer.");
        }

        var nextVersion = entity.Version + 1;
        _versions[entity.Id] = nextVersion;
        PushFreeId(entity.Id, nextVersion);
    }

    public void Replay(FrameDelta delta) => ReplayCore(delta, trusted: false);

    internal void ReplayTrusted(FrameDelta delta) => ReplayCore(delta, trusted: true);

    private void ReplayCore(FrameDelta delta, bool trusted)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(delta);

        Dictionary<Type, ComponentType>? componentTypeCache = null;
        if (!trusted)
        {
            componentTypeCache = _replayComponentTypeScratch;
            componentTypeCache.Clear();
        }

        BeginDeferredLayoutUpdates();
        try
        {
            if (!trusted)
            {
                foreach (var reserved in delta.ReservedEntities)
                    EnsureReplayReservation(reserved);
            }

            foreach (var released in delta.ReleasedEntities)
                ReleaseReservedEntity(released);

            foreach (var created in delta.CreatedEntities)
            {
                if (trusted)
                    MaterializeReservedEntityTrusted(created.Entity, created.Signature, created.Components);
                else
                    MaterializeReservedEntity(created.Entity, created.Components, componentTypeCache, reservationChecked: true);
            }

            foreach (var link in delta.LinkCommands)
                Link(link.Parent, link.Child);

            foreach (var unlink in delta.UnlinkCommands)
                Unlink(unlink.Child);

            unsafe
            {
                foreach (var add in delta.AddCommands)
                {
                    var componentType = trusted
                        ? add.ComponentType
                        : ResolveCompiledComponentType(add.RuntimeType, add.ComponentType, componentTypeCache);
                    ApplyRawAddOrSet(add.Entity, componentType, add.RuntimeType, add.Data, add.DataOffset, add.ColumnWriter);
                }

                foreach (var set in delta.SetCommands)
                {
                    var componentType = trusted
                        ? set.ComponentType
                        : ResolveCompiledComponentType(set.RuntimeType, set.ComponentType, componentTypeCache);
                    ApplyRawAddOrSet(set.Entity, componentType, set.RuntimeType, set.Data, set.DataOffset, set.ColumnWriter);
                }
            }

            foreach (var remove in delta.RemoveCommands)
            {
                var componentType = trusted
                    ? remove.ComponentType
                    : ResolveCompiledComponentType(remove.RuntimeType, remove.ComponentType, componentTypeCache);
                RemoveBoxed(remove.Entity, componentType);
            }

            foreach (var entity in delta.DestroyedEntities)
                if (IsAlive(entity)) Destroy(entity);
        }
        finally
        {
            componentTypeCache?.Clear();
            EndDeferredLayoutUpdates();
        }
    }

    internal void MaterializeReservedEntity(
        Entity entity,
        IReadOnlyList<RawComponentValue> components,
        Dictionary<Type, ComponentType>? componentTypeCache = null,
        bool reservationChecked = false)
    {
        if (!reservationChecked)
        {
            EnsureReplayReservation(entity);
        }

        var signature = BuildReplaySignature(components, componentTypeCache);
        MaterializeReservedEntityCore(entity, signature, components, componentTypeCache, trustCompiledComponentTypes: false);
    }

    internal void MaterializeReservedEntityTrusted(Entity entity, Signature signature, IReadOnlyList<RawComponentValue> components)
    {
        MaterializeReservedEntityCore(entity, signature, components, componentTypeCache: null, trustCompiledComponentTypes: true);
    }

    internal void MaterializeReservedEntityDirect(Entity entity, Archetype archetype, ReadOnlySpan<RawComponentValue> components)
    {
        var chunk = archetype.ReserveEntity(entity, out var chunkIndex, out var rowIndex);
        _locations[entity.Id] = new EntityLocation(archetype, chunkIndex, rowIndex);

        unsafe
        {
            for (var index = 0; index < components.Length; index++)
            {
                ref readonly var component = ref components[index];
                var writer = ComponentWriterCache.GetColumnWriter(component.RuntimeType);
                fixed (byte* ptr = component.Data)
                {
                    WriteComponentFromBytes(chunk, component.ComponentType, rowIndex, ptr + component.DataOffset, writer);
                }
            }
        }

        TouchQueryLayout();
    }

    private void MaterializeReservedEntityCore(
        Entity entity,
        Signature signature,
        IReadOnlyList<RawComponentValue> components,
        Dictionary<Type, ComponentType>? componentTypeCache,
        bool trustCompiledComponentTypes)
    {
        var archetype = GetOrCreateArchetype(signature);
        var chunk = archetype.ReserveEntity(entity, out var chunkIndex, out var rowIndex);
        _locations[entity.Id] = new EntityLocation(archetype, chunkIndex, rowIndex);

        unsafe
        {
            for (var index = 0; index < components.Count; index++)
            {
                var component = components[index];
                var resolvedType = trustCompiledComponentTypes
                    ? component.ComponentType
                    : ResolveCompiledComponentType(component.RuntimeType, component.ComponentType, componentTypeCache);
                var writer = ComponentWriterCache.GetColumnWriter(component.RuntimeType);
                fixed (byte* ptr = component.Data)
                {
                    WriteComponentFromBytes(chunk, resolvedType, rowIndex, ptr + component.DataOffset, writer);
                }
            }
        }

        TouchQueryLayout();
    }

    internal void AddBoxed(Entity entity, ComponentType componentType, object? component)
    {
        ApplyBoxedAddOrSet(entity, componentType, component);
    }

    internal void SetBoxed(Entity entity, ComponentType componentType, object? component)
    {
        ApplyBoxedAddOrSet(entity, componentType, component);
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

    internal void BeginDeferredLayoutUpdates()
    {
        _queryLayoutSuppressionCount++;
    }

    private void EnsureDestroyScratchCapacity(int entityCount)
    {
        if (entityCount <= 0)
        {
            return;
        }

        _destroyOrderScratch.EnsureCapacity(entityCount);
        if (_destroyVisitedGen.Length < entityCount)
        {
            Array.Resize(ref _destroyVisitedGen, Math.Max(entityCount, _destroyVisitedGen.Length * 2));
        }
    }

    internal void EndDeferredLayoutUpdates()
    {
        if (_queryLayoutSuppressionCount == 0)
        {
            throw new InvalidOperationException("Unbalanced DeferQueryLayoutUpdates/Dispose call.");
        }

        _queryLayoutSuppressionCount--;
        if (_queryLayoutSuppressionCount == 0 && _queryLayoutDirty)
        {
            _queryLayoutDirty = false;
            AdvanceQueryGeneration();
        }
    }

    private void EnsureReplayReservation(Entity entity)
    {
        if (entity.Id < _entitySlotCount &&
            _locations[entity.Id].Archetype is null &&
            _versions[entity.Id] == entity.Version &&
            !IsEntityAvailableInFreeList(entity))
        {
            return;
        }

        var reserved = ReserveDeferredEntity();
        if (reserved != entity)
        {
            throw new InvalidOperationException($"Replay failed: expected to reserve entity {entity} but got {reserved} instead. The source and target worlds may be out of sync.");
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
        IReadOnlyList<RawComponentValue> components,
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

    private ComponentType ResolveCompiledComponentType(
        Type runtimeType,
        ComponentType componentType,
        Dictionary<Type, ComponentType>? cache)
    {
        if (componentType.IsValid && ComponentRegistry.Shared.TryGetType(componentType, out var existing) && existing == runtimeType)
        {
            return componentType;
        }

        if (cache is not null && cache.TryGetValue(runtimeType, out var resolved))
        {
            return resolved;
        }

        resolved = GetComponentType(runtimeType);
        cache?.Add(runtimeType, resolved);
        return resolved;
    }

    private ComponentType GetComponentType(Type componentType)
    {
        return ComponentRegistry.Shared.GetOrCreate(componentType);
    }

    private static class CreateArchetypeCache<T1>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2>
    {
        public static CachedCreateArchetype? Entry;
    }

    private sealed class CachedCreateArchetype
    {
        private readonly WeakReference<World> _world;
        private readonly WeakReference<Archetype> _archetype;

        public CachedCreateArchetype(World world, int generation, Archetype archetype)
        {
            _world = new WeakReference<World>(world);
            _archetype = new WeakReference<Archetype>(archetype);
            Generation = generation;
        }

        public int Generation { get; }

        public bool TryGetArchetype(World world, int generation, [NotNullWhen(true)] out Archetype? archetype)
        {
            archetype = null;
            if (generation != Generation ||
                !_world.TryGetTarget(out var cachedWorld) ||
                !ReferenceEquals(cachedWorld, world))
            {
                return false;
            }

            return _archetype.TryGetTarget(out archetype);
        }
    }

    internal readonly struct CreateArchetypeKey : IEquatable<CreateArchetypeKey>
    {
        private readonly int _count;
        private readonly int _c1;
        private readonly int _c2;
        private readonly int _c3;
        private readonly int _c4;
        private readonly int _c5;
        private readonly int _c6;
        private readonly int _c7;
        private readonly int _c8;
        private readonly int _c9;
        private readonly int _c10;
        private readonly int _c11;
        private readonly int _c12;
        private readonly int _c13;
        private readonly int _c14;
        private readonly int _c15;
        private readonly int _c16;

        public CreateArchetypeKey(ReadOnlySpan<ComponentType> components)
        {
            if (components.Length is < 1 or > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(components));
            }

            _count = components.Length;
            _c1 = GetComponentValue(components, 0);
            _c2 = GetComponentValue(components, 1);
            _c3 = GetComponentValue(components, 2);
            _c4 = GetComponentValue(components, 3);
            _c5 = GetComponentValue(components, 4);
            _c6 = GetComponentValue(components, 5);
            _c7 = GetComponentValue(components, 6);
            _c8 = GetComponentValue(components, 7);
            _c9 = GetComponentValue(components, 8);
            _c10 = GetComponentValue(components, 9);
            _c11 = GetComponentValue(components, 10);
            _c12 = GetComponentValue(components, 11);
            _c13 = GetComponentValue(components, 12);
            _c14 = GetComponentValue(components, 13);
            _c15 = GetComponentValue(components, 14);
            _c16 = GetComponentValue(components, 15);
        }

        public ComponentType[] ToComponentArray()
        {
            var components = new ComponentType[_count];
            for (var index = 0; index < components.Length; index++)
            {
                components[index] = new ComponentType(GetComponentValue(index));
            }

            return components;
        }

        public bool Equals(CreateArchetypeKey other)
        {
            if (_count != other._count)
            {
                return false;
            }

            for (var index = 0; index < _count; index++)
            {
                if (GetComponentValue(index) != other.GetComponentValue(index))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is CreateArchetypeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hash = _count;
            for (var index = 0; index < _count; index++)
            {
                hash = unchecked((hash * 31) + GetComponentValue(index));
            }

            return hash;
        }

        private int GetComponentValue(int index)
        {
            return index switch
            {
                0 => _c1,
                1 => _c2,
                2 => _c3,
                3 => _c4,
                4 => _c5,
                5 => _c6,
                6 => _c7,
                7 => _c8,
                8 => _c9,
                9 => _c10,
                10 => _c11,
                11 => _c12,
                12 => _c13,
                13 => _c14,
                14 => _c15,
                15 => _c16,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
        }

        private static int GetComponentValue(ReadOnlySpan<ComponentType> components, int index)
        {
            return index < components.Length ? components[index].Value : -1;
        }
    }

    private readonly record struct RecycledEntity(int Id, int Version);
}

