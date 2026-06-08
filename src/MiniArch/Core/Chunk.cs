using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Lightweight read-only view of an <see cref="Archetype"/>'s storage.
/// Provides public access to entity rows and component spans.
/// Every archetype has exactly one chunk — this struct wraps it.
/// </summary>
internal readonly struct Chunk
{
    private readonly Archetype _archetype;

    internal Chunk(Archetype archetype) => _archetype = archetype;

    internal Archetype Archetype => _archetype;

    // ================================================================
    //  Public API
    // ================================================================

    /// <summary>Gets the number of live rows.</summary>
    internal int Count => _archetype.EntityCount;

    /// <summary>Gets live entities as a span.</summary>
    internal ReadOnlySpan<Entity> GetEntities() => _archetype.GetEntities();

    /// <summary>
    /// Gets a span of component <typeparamref name="T"/> for all rows.
    /// </summary>
    internal Span<T> GetComponentSpan<T>() where T : struct =>
        _archetype.GetComponentSpan<T>(Component<T>.ComponentType);

    internal Span<T> GetComponentSpan<T>(ComponentType component) =>
        _archetype.GetComponentSpan<T>(component);

    /// <summary>
    /// Gets a span of component <typeparamref name="T"/> at a pre-resolved column index.
    /// </summary>
    [SkipLocalsInit]
    internal Span<T> GetComponentSpanAt<T>(int columnIndex) =>
        _archetype.GetComponentSpanAt<T>(columnIndex);

    /// <summary>
    /// Tries to get the column index for a component type.
    /// </summary>
    internal bool TryGetComponentIndex(ComponentType component, out int columnIndex) =>
        _archetype.TryGetComponentIndexPublic(component, out columnIndex);

    // ================================================================
    //  Internal API
    // ================================================================

    internal int Capacity => _archetype.Capacity;
    internal Entity[] GetEntityStorage() => _archetype.GetEntityStorage();
    internal int Add(Entity entity) => _archetype.AddEntity(entity);
    internal int ReserveRows(int count) => _archetype.ReserveRows(count);
    internal Span<Entity> GetReservedEntities(int startRow, int count) =>
        _archetype.GetReservedEntities(startRow, count);
    internal bool RemoveAt(int row, out Entity movedEntity) =>
        _archetype.RemoveAt(row, out movedEntity);
    internal void EnsureCapacity(int requiredCapacity) =>
        _archetype.EnsureCapacity(requiredCapacity);
    internal Entity GetEntity(int row) => _archetype.GetEntity(row);
    internal T GetComponent<T>(ComponentType component, int row) =>
        _archetype.GetComponent<T>(component, row);
    internal ref T GetComponentRef<T>(ComponentType component) =>
        ref _archetype.GetComponentRef<T>(GetComponentIndexFast(component));
    internal ref T GetComponentRef<T>(int columnIndex) =>
        ref _archetype.GetComponentRef<T>(columnIndex);
    internal ref T GetComponentRefAt<T>(int columnIndex, int row) =>
        ref _archetype.GetComponentRefAt<T>(columnIndex, row);
    internal void SetComponentAtTyped<T>(int columnIndex, int row, in T value) =>
        _archetype.SetComponentAtTyped(columnIndex, row, value);
    internal T GetComponentAt<T>(int columnIndex, int row) =>
        _archetype.GetComponentAt<T>(columnIndex, row);
    internal int GetComponentIndex(ComponentType component) =>
        _archetype.GetComponentIndex(component);
    internal int GetComponentIndexFast(ComponentType component) =>
        _archetype.GetComponentIndexFast(component);
    internal bool TryGetColumnIndices(ReadOnlySpan<ComponentType> components, Span<int> outIndices) =>
        _archetype.TryGetColumnIndices(components, outIndices);
    internal void CopySharedComponentsFrom(Chunk source, int sourceRow, int destinationRow) =>
        _archetype.CopySharedComponentsFrom(source._archetype, sourceRow, destinationRow);
    internal unsafe void ReadComponentRaw(int columnIndex, int row, byte* destination) =>
        _archetype.ReadComponentRaw(columnIndex, row, destination);
    internal unsafe void WriteComponentRaw(int columnIndex, int row, byte* source) =>
        _archetype.WriteComponentRaw(columnIndex, row, source);
    internal void CopyColumnsFrom(Chunk source, int count) =>
        _archetype.CopyColumnsFrom(source._archetype, count);
}
