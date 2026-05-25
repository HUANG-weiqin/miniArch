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
    private readonly int[] _componentIdToColumnIndex;

    internal Chunk(Signature signature, Type[] componentTypes, int[] componentIdToColumnIndex, int capacity = 4)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(componentTypes);
        ArgumentNullException.ThrowIfNull(componentIdToColumnIndex);

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _signature = signature;
        _componentIdToColumnIndex = componentIdToColumnIndex;
        _entities = new Entity[capacity];
        _columns = CreateColumns(signature, componentTypes, capacity);
        _columnRequiresClear = CreateColumnClearMap(componentTypes);
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
    internal int Add(Entity entity, IReadOnlyDictionary<ComponentType, object?> components)
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
    internal int Add(Entity entity)
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
    internal object? GetComponent(ComponentType component, int row)
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
        return ((T[])_columns[columnIndex])[row];
    }

    /// <summary>
    /// Gets a typed component span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> GetComponentSpan<T>(ComponentType component)
    {
        var columnIndex = GetComponentIndex(component);
        return ((T[])_columns[columnIndex]).AsSpan(0, Count);
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
    internal void SetComponent(ComponentType component, int row, object? value)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        _columns[columnIndex].SetValue(value, row);
    }

    /// <summary>
    /// Sets a typed component value.
    /// </summary>
    internal void SetComponent<T>(ComponentType component, int row, in T value)
    {
        ValidateRow(row);
        var columnIndex = GetComponentIndex(component);
        SetComponentAtTyped(columnIndex, row, in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetComponentAtTyped<T>(int columnIndex, int row, in T value)
    {
        ((T[])_columns[columnIndex])[row] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T GetComponentAt<T>(int columnIndex, int row)
    {
        return ((T[])_columns[columnIndex])[row];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<T> GetComponentSpanAt<T>(int columnIndex)
    {
        return ((T[])_columns[columnIndex]).AsSpan(0, Count);
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
        if (TryGetComponentIndex(component, out var columnIndex))
        {
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
    /// Removes a row with swap-remove.
    /// </summary>
    internal bool RemoveAt(int row, out Entity movedEntity)
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

    private static Array[] CreateColumns(Signature signature, Type[] componentTypes, int capacity)
    {
        var componentCount = signature.Count;
        var columns = new Array[componentCount];

        if (componentCount == 0)
        {
            return columns;
        }

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

    private static bool[] CreateColumnClearMap(Type[] componentTypes)
    {
        var clearMap = new bool[componentTypes.Length];
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyRemovedRow(int row, int last)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearRemovedTail(int last)
    {
        for (var index = 0; index < _columns.Length; index++)
        {
            if (_columnRequiresClear[index])
            {
                Array.Clear(_columns[index], last, 1);
            }
        }
    }

    private void ValidateRow(int row)
    {
        if (row < 0 || row >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }
    }
}
