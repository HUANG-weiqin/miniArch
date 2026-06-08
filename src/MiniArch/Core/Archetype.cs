using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Stores entities for one signature.
/// Archetype is the sole storage unit — there is exactly one data block
/// per archetype (no multi-chunk splitting).
/// </summary>
internal sealed partial class Archetype
{
    // --- Storage (was in Chunk) ---
    private Entity[] _entities;
    private byte[] _data;
    private int[] _columnByteOffsets;
    private int[] _elementSizes;
    private int _count;
    private int _capacity;
    private Chunk _chunkView;

    // --- Archetype metadata ---
    private readonly Signature _signature;
    private readonly Type[] _componentTypes;
    private readonly int[] _componentIdToColumnIndex;
    private Archetype?[] _addDestinationCache = Array.Empty<Archetype?>();
    private Archetype?[] _removeDestinationCache = Array.Empty<Archetype?>();
    internal Archetype(Signature signature, Type[] componentTypes, int capacity = 4)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        if (componentTypes.Length != signature.Count)
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));

        _signature = signature;
        _capacity = capacity;
        _componentTypes = componentTypes;
        _componentIdToColumnIndex = ComponentColumnMap.Build(signature);
        _entities = new Entity[capacity];
        (_data, _columnByteOffsets, _elementSizes) = CreateStorage(signature, componentTypes, capacity);
        _chunkView = new Chunk(this);
    }

    /// <summary>
    /// Gets the archetype signature.
    /// </summary>
    internal Signature Signature => _signature;

    /// <summary>
    /// Gets the entity count.
    /// </summary>
    internal int EntityCount => _count;

    /// <summary>
    /// Gets the current physical capacity (maximum entities before resize).
    /// </summary>
    internal int Capacity => _capacity;

    /// <summary>
    /// Gets the component types that define this archetype's signature.
    /// </summary>
    internal IReadOnlyList<Type> ComponentTypes => _componentTypes;

    /// <summary>
    /// Returns a single-element span wrapping this archetype as a <see cref="Chunk"/>.
    /// Maintains compatibility with query iterators that work over chunks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<Chunk> GetChunkSpan() =>
        MemoryMarshal.CreateSpan(ref _chunkView, 1);

    internal bool TryGetAddDestination(ComponentType component, out Archetype? destination) =>
        TryGetCached(_addDestinationCache, component, out destination);
    internal bool TryGetRemoveDestination(ComponentType component, out Archetype? destination) =>
        TryGetCached(_removeDestinationCache, component, out destination);
    internal void CacheAddDestination(ComponentType component, Archetype destination) =>
        CacheWrite(ref _addDestinationCache, component, destination);
    internal void CacheRemoveDestination(ComponentType component, Archetype destination) =>
        CacheWrite(ref _removeDestinationCache, component, destination);

    private static bool TryGetCached(Archetype?[] cache, ComponentType component, out Archetype? destination)
    {
        var id = component.Value;
        if ((uint)id >= (uint)cache.Length) { destination = null; return false; }
        destination = cache[id];
        return destination is not null;
    }

    private static void CacheWrite(ref Archetype?[] cache, ComponentType component, Archetype destination)
    {
        var id = component.Value;
        if ((uint)id >= (uint)cache.Length) Array.Resize(ref cache, id + 1);
        cache[id] = destination;
    }

    // ================================================================
    //  Capacity management
    // ================================================================


    // .NET maximum array element count; avoids overflow in _capacity * 2.
    private const int ArrayMaxLength = 0x7FFFFFC7; // Array.MaxLength

    // ================================================================
    //  Entity operations
    // ================================================================




    // ================================================================
    //  Entity access
    // ================================================================




    // ================================================================
    //  Component access (column-based)
    // ================================================================








    // ================================================================
    //  Component index resolution
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetComponentIndex(ComponentType component, out int columnIndex)
    {
        var componentId = component.Value;
        if ((uint)componentId >= (uint)_componentIdToColumnIndex.Length)
        {
            columnIndex = -1;
            return false;
        }

        columnIndex = _componentIdToColumnIndex[componentId];
        return columnIndex >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetComponentIndexPublic(ComponentType component, out int columnIndex)
    {
        return TryGetComponentIndex(component, out columnIndex);
    }

    internal int GetComponentIndex(ComponentType component)
    {
        if (TryGetComponentIndex(component, out var columnIndex))
            return columnIndex;

        throw new ArgumentException($"Archetype does not contain component {component.Value}.", nameof(component));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetComponentIndexFast(ComponentType component) =>
        _componentIdToColumnIndex[component.Value];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetColumnIndices(ReadOnlySpan<ComponentType> components, Span<int> outIndices)
    {
        if (components.Length != outIndices.Length)
            throw new ArgumentException("Output span length must match component count.", nameof(outIndices));

        for (var i = 0; i < components.Length; i++)
        {
            if (!TryGetComponentIndex(components[i], out var columnIndex))
                return false;

            outIndices[i] = columnIndex;
        }

        return true;
    }

    // ================================================================
    //  Cross-chunk (cross-archetype) copies
    // ================================================================









    // ================================================================
    //  Row data copying
    // ================================================================




    // ================================================================
    //  Private helpers
    // ================================================================






}
