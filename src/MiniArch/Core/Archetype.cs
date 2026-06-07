using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Stores entities for one signature.
/// Archetype is the sole storage unit — there is exactly one data block
/// per archetype (no multi-chunk splitting).
/// </summary>
public sealed class Archetype
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
    /// Gets the maximum logical capacity (growth ceiling).
    /// </summary>
    /// <summary>
    /// Returns a single-element span wrapping this archetype as a <see cref="Chunk"/>.
    /// Maintains compatibility with query iterators that work over chunks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<Chunk> GetChunkSpan() =>
        MemoryMarshal.CreateSpan(ref _chunkView, 1);

    internal ArchetypeEdges Edges { get; } = new();

    // ================================================================
    //  Capacity management
    // ================================================================

    /// <summary>
    /// Grows storage to guarantee capacity for at least <paramref name="requiredCapacity"/> rows.
    /// </summary>
    internal void EnsureCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= _capacity) return;

        // Unbounded growth by doubling, capped at max array element count to avoid overflow.
        var doubleCapacity = Math.Min(_capacity * 2, ArrayMaxLength);
        var newCapacity = Math.Max(requiredCapacity, doubleCapacity);

        var newEntities = new Entity[newCapacity];
        Array.Copy(_entities, newEntities, _count);

        var (newData, newOffsets, _) = CreateStorage(_signature, _componentTypes, newCapacity);

        for (var col = 0; col < _elementSizes.Length; col++)
        {
            var elemSize = _elementSizes[col];
            var columnBytes = _count * elemSize;
            if (columnBytes <= 0) continue;

            ref var srcRef = ref _data[_columnByteOffsets[col]];
            ref var dstRef = ref newData[newOffsets[col]];
            Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, (uint)columnBytes);
        }

        _entities = newEntities;
        _data = newData;
        _columnByteOffsets = newOffsets;
        _capacity = newCapacity;
    }

    // .NET maximum array element count; avoids overflow in _capacity * 2.
    private const int ArrayMaxLength = 0x7FFFFFC7; // Array.MaxLength

    // ================================================================
    //  Entity operations
    // ================================================================

    /// <summary>
    /// Adds an entity and returns its row index.
    /// </summary>
    internal int AddEntity(Entity entity)
    {
        EnsureCapacity(_count + 1);
        var row = _count;
        _entities[row] = entity;
        _count++;
        return row;
    }

    /// <summary>
    /// Reserves rows for batch creation and returns the start row.
    /// </summary>
    internal int ReserveRows(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (count == 0)
            return _count;

        EnsureCapacity(_count + count);
        var row = _count;
        _count += count;
        return row;
    }

    /// <summary>
    /// Removes a row with swap-remove. Returns true if a move occurred.
    /// </summary>
    internal bool RemoveAt(int row, out Entity movedEntity)
    {
        var last = _count - 1;
        if (row != last)
        {
            movedEntity = _entities[last];
            _entities[row] = movedEntity;
            CopyRemovedRow(row, last);
        }
        else
        {
            movedEntity = default;
        }

        _entities[last] = default;
        _count--;
        return row != last;
    }

    // ================================================================
    //  Entity access
    // ================================================================

    /// <summary>
    /// Gets live entities as a span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Entity> GetEntities() => _entities.AsSpan(0, _count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity[] GetEntityStorage() => _entities;

    /// <summary>
    /// Gets the entity at a row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity GetEntity(int row) => _entities[row];

    // ================================================================
    //  Component access (column-based)
    // ================================================================

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRef<T>(int columnIndex)
    {
        return ref Unsafe.As<byte, T>(ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(_data),
            _columnByteOffsets[columnIndex]));
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRefAt<T>(int columnIndex, int row)
    {
        return ref Unsafe.As<byte, T>(ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(_data),
            _columnByteOffsets[columnIndex] + row * _elementSizes[columnIndex]));
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetComponentAtTyped<T>(int columnIndex, int row, in T value)
    {
        ref var target = ref GetComponentRefAt<T>(columnIndex, row);
        target = value;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T GetComponentAt<T>(int columnIndex, int row)
    {
        return GetComponentRefAt<T>(columnIndex, row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T GetComponent<T>(ComponentType component, int row)
    {
        var columnIndex = GetComponentIndex(component);
        return GetComponentAt<T>(columnIndex, row);
    }

    /// <summary>
    /// Gets a span of component <typeparamref name="T"/> at a pre-resolved column index.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetComponentSpanAt<T>(int columnIndex)
    {
        return MemoryMarshal.CreateSpan(ref GetComponentRefAt<T>(columnIndex, 0), _count);
    }

    /// <summary>
    /// Gets a span of component <typeparamref name="T"/> by type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetComponentSpan<T>(ComponentType component)
    {
        var columnIndex = GetComponentIndex(component);
        return GetComponentSpanAt<T>(columnIndex);
    }

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
    internal int GetElementSize(int columnIndex) =>
        ComponentSizeCache.GetSize(_componentTypes[columnIndex]);

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

    internal void CopySharedComponentsFrom(Archetype source, int sourceRow, int destinationRow)
    {
        if (_signature == source._signature)
        {
            CopyAllColumnsFrom(source, sourceRow, destinationRow);
            return;
        }

        var components = _signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (!source.TryGetComponentIndex(component, out var sourceColumnIndex))
                continue;

            CopyComponent(source, sourceColumnIndex, sourceRow, index, destinationRow);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyAllColumnsFrom(Archetype source, int sourceRow, int destinationRow)
    {
        var columnCount = _elementSizes.Length;
        for (var index = 0; index < columnCount; index++)
            CopyComponent(source, index, sourceRow, index, destinationRow);
    }

    [SkipLocalsInit]
    private unsafe void CopyComponent(Archetype source, int sourceColumnIndex, int sourceRow,
        int destinationColumnIndex, int destinationRow)
    {
        var size = _elementSizes[destinationColumnIndex];

        ref var sourceData = ref MemoryMarshal.GetArrayDataReference(source._data);
        ref var destinationData = ref MemoryMarshal.GetArrayDataReference(_data);
        ref var sourceRef = ref Unsafe.Add(ref sourceData,
            source._columnByteOffsets[sourceColumnIndex] + sourceRow * size);
        ref var destinationRef = ref Unsafe.Add(ref destinationData,
            _columnByteOffsets[destinationColumnIndex] + destinationRow * size);
        CopySmall(ref destinationRef, ref sourceRef, size);
    }

    internal unsafe void ReadComponentRaw(int columnIndex, int row, byte* destination)
    {
        ref var source = ref _data[GetByteOffset(columnIndex, row)];
        Unsafe.CopyBlockUnaligned(ref *destination, ref source, (uint)_elementSizes[columnIndex]);
    }

    internal unsafe void WriteComponentRaw(int columnIndex, int row, byte* source)
    {
        ref var target = ref _data[GetByteOffset(columnIndex, row)];
        Unsafe.CopyBlockUnaligned(ref target, ref *source, (uint)_elementSizes[columnIndex]);
    }

    internal void CopyColumnsFrom(Archetype source, int count)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!_signature.Equals(source._signature))
            throw new ArgumentException("Source archetype signature must match.", nameof(source));

        if (count < 0 || count > _count || count > source._count)
            throw new ArgumentOutOfRangeException(nameof(count));

        var componentCount = _signature.Count;
        if (componentCount == 0) return;

        for (var columnIndex = 0; columnIndex < componentCount; columnIndex++)
            CopyColumnFrom(source, columnIndex, count);
    }

    internal void WriteColumnTo<T>(BinaryWriter writer, int columnIndex, int count) where T : unmanaged
    {
        writer.Write(GetColumnBytes(columnIndex, count));
    }

    internal void ReadColumnFrom<T>(BinaryReader reader, int columnIndex, int count) where T : unmanaged
    {
        reader.BaseStream.ReadExactly(GetColumnBytes(columnIndex, count));
    }

    // ================================================================
    //  Row data copying
    // ================================================================

    internal byte[] GetDataArray() => _data;
    internal int[] GetColumnByteOffsets() => _columnByteOffsets;

    internal Span<Entity> GetReservedEntities(int startRow, int count)
    {
        if (startRow < 0 || count < 0 || startRow + count > _count)
            throw new ArgumentOutOfRangeException(nameof(startRow));

        return _entities.AsSpan(startRow, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyRemovedRow(int row, int last)
    {
        ref var dataRef = ref MemoryMarshal.GetArrayDataReference(_data);
        var offsets = _columnByteOffsets;
        var sizes = _elementSizes;
        for (var index = 0; index < sizes.Length; index++)
        {
            var size = sizes[index];
            ref var sourceRef = ref Unsafe.Add(ref dataRef, offsets[index] + last * size);
            ref var destRef = ref Unsafe.Add(ref dataRef, offsets[index] + row * size);
            CopySmall(ref destRef, ref sourceRef, size);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopySmall(ref byte destination, ref byte source, int size)
    {
        switch (size)
        {
            case 1:
                destination = source;
                return;
            case 4:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<int>(ref source));
                return;
            case 8:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<long>(ref source));
                return;
            case 12:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<long>(ref source));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8),
                    Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref source, 8)));
                return;
            case 16:
                Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<long>(ref source));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8),
                    Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref source, 8)));
                return;
            default:
                Unsafe.CopyBlockUnaligned(ref destination, ref source, (uint)size);
                return;
        }
    }

    // ================================================================
    //  Private helpers
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetByteOffset(int columnIndex, int row) =>
        _columnByteOffsets[columnIndex] + row * _elementSizes[columnIndex];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetColumnBytes(int columnIndex, int count) =>
        _data.AsSpan(_columnByteOffsets[columnIndex], count * _elementSizes[columnIndex]);

    private unsafe void CopyColumnFrom(Archetype source, int columnIndex, int count)
    {
        var byteCount = checked((uint)(count * _elementSizes[columnIndex]));
        ref var sourceRef = ref source._data[source._columnByteOffsets[columnIndex]];
        ref var destinationRef = ref _data[_columnByteOffsets[columnIndex]];
        Unsafe.CopyBlockUnaligned(ref destinationRef, ref sourceRef, byteCount);
    }

    private static (byte[] Data, int[] ColumnByteOffsets, int[] ElementSizes) CreateStorage(
        Signature signature, Type[] componentTypes, int capacity)
    {
        var componentCount = signature.Count;
        var columnByteOffsets = new int[componentCount];
        var elementSizes = new int[componentCount];

        if (componentCount == 0)
            return (Array.Empty<byte>(), columnByteOffsets, elementSizes);

        if (componentTypes.Length != componentCount)
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));

        var totalBytes = 0;
        for (var index = 0; index < componentCount; index++)
        {
            ThrowIfManagedComponent(componentTypes[index]);
            var elementSize = ComponentSizeCache.GetSize(componentTypes[index]);
            totalBytes = AlignUp(totalBytes, Math.Min(elementSize, 8));
            columnByteOffsets[index] = totalBytes;
            elementSizes[index] = elementSize;
            totalBytes += elementSize * capacity;
        }

        return (new byte[totalBytes], columnByteOffsets, elementSizes);
    }

    private static void ThrowIfManagedComponent(Type type)
    {
        if (!ManagedReferenceCheck.IsManaged(type))
            return;

        throw new NotSupportedException(
            $"Component {type.FullName ?? type.Name} contains managed references " +
            "and cannot be stored in flat byte chunks.");
    }

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 1) return value;
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }
}
