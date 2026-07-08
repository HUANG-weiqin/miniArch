namespace MiniArch;

/// <summary>
/// Projects a <typeparamref name="TComponent"/> value into a
/// <typeparamref name="TValue"/> for change comparison.
/// </summary>
public interface IValueProjector<TComponent, TValue>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
{
    /// <summary>
    /// Extracts the tracked value from <paramref name="component"/>.
    /// </summary>
    TValue Project(in TComponent component);
}
