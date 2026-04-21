using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Dense component storage for one signature.
/// </summary>
public sealed class Chunk
{
    private static readonly ConcurrentDictionary<Type, bool> ColumnClearRequirementCache = new();

    private readonly Signature _signature;
    private readonly Entity[] _entities;
    private readonly Array[] _columns;
    private readonly bool[] _columnRequiresClear;
    private readonly ColumnClearMode _columnClearMode;
    private readonly int[] _componentIdToColumnIndex;
    private readonly bool _typedColumns;

    // Caches the last component ID and its resolved column index to avoid repeated lookups
    // in the row-wise hot path where the same component is accessed consecutively.
    private int _lastComponentId = -1;
    private int _lastColumnIndex = -1;

    /// <summary>
    /// Creates a chunk for a signature.
    /// </summary>
    public Chunk(Signature signature, int capacity = 4)
        : this(signature, null, BuildComponentIdToColumnIndex(signature), capacity, false)
    {
    }

    internal Chunk(Signature signature, int[] componentIdToColumnIndex, int capacity = 4)
        : this(signature, null, componentIdToColumnIndex, capacity, false)
    {
    }

    internal Chunk(Signature signature, Type[] componentTypes, int[] componentIdToColumnIndex, int capacity = 4)
        : this(signature, componentTypes, componentIdToColumnIndex, capacity, true)
    {
    }

    private Chunk(Signature signature, Type[]? componentTypes, int[] componentIdToColumnIndex, int capacity, bool hasTypedColumns)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(componentIdToColumnIndex);

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _signature = signature;
        _typedColumns = hasTypedColumns;
        _componentIdToColumnIndex = componentIdToColumnIndex;
        _entities = new Entity[capacity];
        _columns = CreateColumns(signature, componentTypes, capacity, hasTypedColumns);
        _columnRequiresClear = CreateColumnClearMap(_columns, componentTypes, hasTypedColumns);
        _columnClearMode = GetColumnClearMode(_columnRequiresClear);
    }

    /// <summary>
    /// Gets the chunk signature.
    /// </summary>
    public Signature Signature => _signature;

    /// <summary>
    /// Gets the chunk capacity.
    /// </summary>
    public int Capacity => _entities.Length;

    /// <summary>
    /// Gets the live row count.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Gets live entities as a span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Entity> GetEntities()
    {
        return _entities.AsSpan(0, Count);
    }

    internal Array[] Columns => _columns;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity[] GetEntityStorage() => _entities;

    /// <summary>
    /// Gets the entity at a row.
    /// </summary>
    public Entity GetEntity(int row)
    {
        ValidateRow(row);
        return _entities[row];
    }

    /// <summary>
    /// Adds an entity and writes its components.
    /// </summary>
    public int Add(Entity entity, IReadOnlyDictionary<ComponentType, object?> components)
    {
        var row = Add(entity);
        var signature = _signature.AsSpan();
        for (var index = 0; index < signature.Length; index++)
        {
            var component = signature[index];
            if (!components.TryGetValue(component, out var value))
            {
                throw new InvalidOperationException($"Missing component {component.Value}.");
            }

            var columnIndex = GetComponentIndex(component);
            _columns[columnIndex].SetValue(value, row);
        }

        return row;
    }

    /// <summary>
    /// Adds an entity.
    /// </summary>
    public int Add(Entity entity)
    {
        if (Count == Capacity)
        {
            throw new InvalidOperationException("Chunk is full.");
        }

        var row = Count;
        _entities[row] = entity;
        Count++;
        return row;
    }

    internal int ReserveRows(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (Count + count > Capacity)
        {
            throw new InvalidOperationException("Chunk is full.");
        }

        var row = Count;
        Count += count;
        return row;
    }

    internal Span<Entity> GetReservedEntities(int startRow, int count)
    {
        if (startRow < 0 || count < 0 || startRow + count > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startRow));
        }

        return _entities.AsSpan(startRow, count);
    }

    /// <summary>
    /// Gets a boxed component value.
    /// </summary>
    public object? GetComponent(ComponentType component, int row)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        return _columns[columnIndex].GetValue(row);
    }

    /// <summary>
    /// Gets a component value by type.
    /// </summary>
    public T GetComponent<T>(ComponentType component, int row)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        return GetComponentAt<T>(columnIndex, row);
    }

    /// <summary>
    /// Gets a typed component span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> GetComponentSpan<T>(ComponentType component)
    {
        var columnIndex = GetComponentIndex(component);
        return GetComponentSpanAt<T>(columnIndex);
    }

    public void GetComponentSpans<T1, T2>(
        ComponentType component1, ComponentType component2,
        out ReadOnlySpan<T1> span1, out ReadOnlySpan<T2> span2)
    {
        var id1 = component1.Value;
        var id2 = component2.Value;
        var map = _componentIdToColumnIndex;
        span1 = ((T1[])_columns[map[id1]]).AsSpan(0, Count);
        span2 = ((T2[])_columns[map[id2]]).AsSpan(0, Count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRef<T>(int columnIndex)
    {
        return ref Unsafe.As<T[]>(_columns[columnIndex])[0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int[] GetComponentIdToColumnMap() => _componentIdToColumnIndex;

    /// <summary>
    /// Sets a boxed component value.
    /// </summary>
    public void SetComponent(ComponentType component, int row, object? value)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        _columns[columnIndex].SetValue(value, row);
    }

    /// <summary>
    /// Sets a typed component value.
    /// </summary>
    public void SetComponent<T>(ComponentType component, int row, in T value)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        SetComponentAt(columnIndex, row, in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetComponentAtTyped<T>(int columnIndex, int row, in T value)
    {
        ((T[])_columns[columnIndex])[row] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetComponentAt<T>(int columnIndex, int row, in T value)
    {
        if (!_typedColumns)
        {
            _columns[columnIndex].SetValue(value, row);
            return;
        }

        SetComponentAtTyped(columnIndex, row, in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T GetComponentAt<T>(int columnIndex, int row)
    {
        if (!_typedColumns)
        {
            return (T)_columns[columnIndex].GetValue(row)!;
        }

        return ((T[])_columns[columnIndex])[row];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<T> GetComponentSpanAt<T>(int columnIndex)
    {
        if (!_typedColumns)
        {
            throw new InvalidOperationException("Component spans require typed columns.");
        }

        return ((T[])_columns[columnIndex]).AsSpan(0, Count);
    }

    internal T[] GetTypedColumnStorageAt<T>(int columnIndex)
    {
        if (!_typedColumns)
        {
            throw new InvalidOperationException("Typed column storage requires typed columns.");
        }

        if (_columns[columnIndex] is not T[] typedColumn)
        {
            throw new InvalidOperationException($"Component column {columnIndex} cannot be read as {typeof(T).Name}.");
        }

        return typedColumn;
    }

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

    /// <summary>
    /// Resolves column indices for a batch of component types in a single call.
    /// Hoists per-row column lookups out of the row loop.
    /// </summary>
    internal bool TryGetColumnIndices(ReadOnlySpan<ComponentType> components, Span<int> outIndices)
    {
        if (components.Length != outIndices.Length)
        {
            throw new ArgumentException("Output span length must match component count.", nameof(outIndices));
        }

        for (var i = 0; i < components.Length; i++)
        {
            if (!TryGetComponentIndex(components[i], out var columnIndex))
            {
                return false;
            }

            outIndices[i] = columnIndex;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetComponentIndex(ComponentType component)
    {
        var componentId = component.Value;

        // Fast path: check if this is the same component as the last lookup
        if (componentId == _lastComponentId)
        {
            return _lastColumnIndex;
        }

        if (TryGetComponentIndex(component, out var columnIndex))
        {
            _lastComponentId = componentId;
            _lastColumnIndex = columnIndex;
            return columnIndex;
        }

        throw new ArgumentException($"Chunk does not contain component {component.Value}.", nameof(component));
    }

    internal void CopySharedComponentsFrom(Chunk source, int sourceRow, int destinationRow)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.ValidateRow(sourceRow);
        ValidateRow(destinationRow);

        var components = _signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (!source.TryGetComponentIndex(component, out var sourceColumnIndex))
            {
                continue;
            }

            Array.Copy(source._columns[sourceColumnIndex], sourceRow, _columns[index], destinationRow, 1);
        }
    }

    /// <summary>
    /// Captures a row into a dictionary.
    /// </summary>
    public IReadOnlyDictionary<ComponentType, object?> CaptureRow(int row)
    {
        ValidateRow(row);

        var values = new Dictionary<ComponentType, object?>(_signature.Count);
        var components = _signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            values[components[index]] = _columns[index].GetValue(row);
        }

        return values;
    }

    /// <summary>
    /// Removes a row with swap-remove.
    /// </summary>
    public bool RemoveAt(int row, out Entity movedEntity)
    {
        ValidateRow(row);

        var last = Count - 1;
        if (row != last)
        {
            movedEntity = _entities[last];
            _entities[row] = movedEntity;
            CopyRemovedRow(row, last);
        }
        else
        {
            movedEntity = default;
            ClearRemovedTail(last);
        }

        _entities[last] = default;
        Count--;
        return row != last;
    }

    private static Array[] CreateColumns(Signature signature, Type[]? componentTypes, int capacity, bool typedColumns)
    {
        var componentCount = signature.Count;
        var columns = new Array[componentCount];

        if (componentCount == 0)
        {
            return columns;
        }

        if (!typedColumns)
        {
            for (var index = 0; index < componentCount; index++)
            {
                columns[index] = new object?[capacity];
            }

            return columns;
        }

        ArgumentNullException.ThrowIfNull(componentTypes);
        if (componentTypes.Length != componentCount)
        {
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));
        }

        for (var index = 0; index < componentCount; index++)
        {
            columns[index] = Array.CreateInstance(componentTypes[index], capacity);
        }

        return columns;
    }

    private static bool[] CreateColumnClearMap(Array[] columns, Type[]? componentTypes, bool typedColumns)
    {
        var clearMap = new bool[columns.Length];
        if (columns.Length == 0)
        {
            return clearMap;
        }

        if (!typedColumns)
        {
            Array.Fill(clearMap, true);
            return clearMap;
        }

        ArgumentNullException.ThrowIfNull(componentTypes);
        for (var index = 0; index < componentTypes.Length; index++)
        {
            clearMap[index] = RequiresClear(componentTypes[index]);
        }

        return clearMap;
    }

    private static bool RequiresClear(Type type)
    {
        return ColumnClearRequirementCache.GetOrAdd(type, static componentType =>
        {
            return (bool)typeof(Chunk)
                .GetMethod(nameof(RequiresClearGeneric), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .MakeGenericMethod(componentType)
                .Invoke(null, null)!;
        });
    }

    private static bool RequiresClearGeneric<T>()
    {
        return RuntimeHelpers.IsReferenceOrContainsReferences<T>();
    }

    private static ColumnClearMode GetColumnClearMode(bool[] clearMap)
    {
        if (clearMap.Length == 0)
        {
            return ColumnClearMode.None;
        }

        var requiresClear = 0;
        for (var index = 0; index < clearMap.Length; index++)
        {
            if (clearMap[index])
            {
                requiresClear++;
            }
        }

        if (requiresClear == 0)
        {
            return ColumnClearMode.None;
        }

        if (requiresClear == clearMap.Length)
        {
            return ColumnClearMode.All;
        }

        return ColumnClearMode.Mixed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyRemovedRow(int row, int last)
    {
        switch (_columnClearMode)
        {
            case ColumnClearMode.None:
                CopyColumnsWithoutClearing(row, last);
                break;
            case ColumnClearMode.All:
                CopyColumnsAndClearAll(row, last);
                break;
            default:
                CopyColumnsAndClearMixed(row, last);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearRemovedTail(int last)
    {
        switch (_columnClearMode)
        {
            case ColumnClearMode.All:
                ClearAllColumns(last);
                break;
            case ColumnClearMode.Mixed:
                ClearMixedColumns(last);
                break;
        }
    }

    private void CopyColumnsWithoutClearing(int row, int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            var column = _columns[index];
            Array.Copy(column, last, column, row, 1);
        }
    }

    private void CopyColumnsAndClearAll(int row, int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            var column = _columns[index];
            Array.Copy(column, last, column, row, 1);
            Array.Clear(column, last, 1);
        }
    }

    private void CopyColumnsAndClearMixed(int row, int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            var column = _columns[index];
            Array.Copy(column, last, column, row, 1);
            if (_columnRequiresClear[index])
            {
                Array.Clear(column, last, 1);
            }
        }
    }

    private void ClearAllColumns(int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            Array.Clear(_columns[index], last, 1);
        }
    }

    private void ClearMixedColumns(int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            if (_columnRequiresClear[index])
            {
                Array.Clear(_columns[index], last, 1);
            }
        }
    }

    private static int[] BuildComponentIdToColumnIndex(Signature signature)
    {
        var maxComponentId = -1;
        var components = signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            var componentId = components[index].Value;
            if (componentId > maxComponentId)
            {
                maxComponentId = componentId;
            }
        }

        if (maxComponentId < 0)
        {
            return Array.Empty<int>();
        }

        var lookup = new int[maxComponentId + 1];
        Array.Fill(lookup, -1);

        for (var index = 0; index < components.Length; index++)
        {
            lookup[components[index].Value] = index;
        }

        return lookup;
    }

    private void ValidateRow(int row)
    {
        if (row < 0 || row >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }
    }

    private enum ColumnClearMode
    {
        None,
        All,
        Mixed,
    }
}
