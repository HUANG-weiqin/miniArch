namespace MiniArch;

/// <summary>
/// A pull-based watch that tracks projected value changes for component type
/// <typeparamref name="TComponent"/>, projecting to <typeparamref name="TValue"/>
/// for comparison via <typeparamref name="THandler"/>.
/// </summary>
/// <remarks>
/// Call <see cref="Snapshot"/> to record a baseline. Then call <see cref="Diff"/> to discover
/// entities whose projected value has changed since the baseline. A baseline applies only
/// to the same <see cref="Entity.Id"/> and <see cref="Entity.Version"/> generation.
/// </remarks>
public sealed class ChangeWatch<TComponent, TValue, THandler>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
    where THandler : struct, IChangeHandler<TComponent, TValue>
{
    private ChangeWatchState<TValue> _state;
    private THandler _handler;

    internal ChangeWatch(QueryDescription query, THandler handler)
    {
        _state = new ChangeWatchState<TValue>(query);
        _handler = handler;
    }

    /// <summary>
    /// Gets or sets the handler.
    /// </summary>
    public ref THandler Handler => ref _handler;

    /// <summary>
    /// Records a baseline snapshot of projected values for all matching entities.
    /// </summary>
    /// <remarks>
    /// If snapshot collection fails, the partial baseline is discarded and a successful
    /// Snapshot is required before the next <see cref="Diff"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A Snapshot or Diff call is already in progress on this watch.</exception>
    public void Snapshot(World world) =>
        ChangeWatchCore.Snapshot<TComponent, TValue, THandler, ProjectedPolicy>(world, ref _state, ref _handler);

    /// <summary>
    /// Scans the current world and calls <see cref="IChangeHandler{TComponent, TValue}.OnChange"/>
    /// for each entity whose projected value differs from the snapshot baseline.
    /// </summary>
    /// <exception cref="InvalidOperationException">No snapshot exists, or a Snapshot or Diff call is already in progress on this watch.</exception>
    public void Diff(World world) =>
        ChangeWatchCore.Diff<TComponent, TValue, THandler, ProjectedPolicy>(world, ref _state, ref _handler);

    private readonly struct ProjectedPolicy : IChangeWatchPolicy<TComponent, TValue, THandler>
    {
        public static TValue Project(ref THandler handler, in TComponent component) => handler.Project(in component);

        public static void OnChange(ref THandler handler, World world, Entity entity,
            in TValue oldValue, in TValue newValue) =>
            handler.OnChange(world, entity, oldValue, newValue);
    }
}
