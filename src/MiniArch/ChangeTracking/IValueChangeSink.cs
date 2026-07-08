namespace MiniArch;

/// <summary>
/// Receives per-entity value change notifications from
/// <see cref="DenseValueDiff{TComponent,TValue,TProjector}.Drain"/>.
/// </summary>
public interface IValueChangeSink<TValue>
    where TValue : unmanaged, IEquatable<TValue>
{
    /// <summary>
    /// Called for each entity whose projected value has changed.
    /// </summary>
    void OnChanged(Entity entity, TValue oldValue, TValue newValue);
}
