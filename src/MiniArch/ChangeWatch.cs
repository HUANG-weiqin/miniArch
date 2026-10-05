namespace MiniArch;

/// <summary>
/// A pull-based watch that tracks value changes for component type <typeparamref name="TComponent"/>
/// by comparing the current world state against the last <see cref="Snapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Snapshot"/> to record a baseline. Then call <see cref="Diff"/> to discover
/// entities whose component value has changed since the baseline. Multiple <see cref="Diff"/>
/// calls against the same baseline repeat the same callbacks.
/// </para>
/// <para>
/// Entities without a matching <see cref="Entity.Id"/> and <see cref="Entity.Version"/>
/// baseline are reported with the old value as <c>default</c>. Entities removed/destroyed
/// after <see cref="Snapshot"/> are not reported because the current scan cannot find them.
/// </para>
/// </remarks>
public sealed class ChangeWatch<TComponent, THandler>
    where TComponent : unmanaged, IEquatable<TComponent>
    where THandler : struct, IChangeHandler<TComponent>
{
    private ChangeWatchState<TComponent> _state;
    private THandler _handler;

    internal ChangeWatch(QueryDescription query, THandler handler)
    {
        _state = new ChangeWatchState<TComponent>(query);
        _handler = handler;
    }

    /// <summary>
    /// Gets or sets the handler. Setting replaces the handler for subsequent
    /// <see cref="Diff"/> calls.
    /// </summary>
    public ref THandler Handler => ref _handler;

    /// <summary>
    /// Records a baseline snapshot of all entities matching the watch's query.
    /// Must be called before <see cref="Diff"/>.
    /// </summary>
    /// <remarks>
    /// Calling <see cref="Snapshot"/> again resets the baseline to the current world state.
    /// If snapshot collection fails, the partial baseline is discarded and a successful
    /// <see cref="Snapshot"/> is required before the next <see cref="Diff"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A Snapshot or Diff call is already in progress on this watch.</exception>
    public void Snapshot(World world) =>
        ChangeWatchCore.Snapshot<TComponent, TComponent, THandler, DirectPolicy>(world, ref _state, ref _handler);

    /// <summary>
    /// Scans the current world and calls <see cref="IChangeHandler{TComponent}.OnChange"/>
    /// for each entity whose component value differs from the snapshot baseline.
    /// </summary>
    /// <exception cref="InvalidOperationException">No snapshot exists, or a Snapshot or Diff call is already in progress on this watch.</exception>
    public void Diff(World world) =>
        ChangeWatchCore.Diff<TComponent, TComponent, THandler, DirectPolicy>(world, ref _state, ref _handler);

    private readonly struct DirectPolicy : IChangeWatchPolicy<TComponent, TComponent, THandler>
    {
        public static TComponent Project(ref THandler handler, in TComponent component) => component;

        public static void OnChange(ref THandler handler, World world, Entity entity,
            in TComponent oldValue, in TComponent newValue) =>
            handler.OnChange(world, entity, in oldValue, in newValue);
    }
}
