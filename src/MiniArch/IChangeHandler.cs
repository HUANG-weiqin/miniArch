namespace MiniArch;

/// <summary>
/// Handles value changes for a component type <typeparamref name="TComponent"/>.
/// Used by <see cref="ChangeWatch{TComponent, THandler}"/>.
/// </summary>
public interface IChangeHandler<TComponent>
    where TComponent : unmanaged, IEquatable<TComponent>
{
    /// <summary>
    /// Called for each entity whose component value differs from the snapshot baseline.
    /// </summary>
    void OnChange(World world, Entity entity, in TComponent oldValue, in TComponent newValue);
}

/// <summary>
/// Handles projected value changes for a component type <typeparamref name="TComponent"/>,
/// projecting to <typeparamref name="TValue"/> for comparison.
/// Used by <see cref="ChangeWatch{TComponent, TValue, THandler}"/>.
/// </summary>
public interface IChangeHandler<TComponent, TValue>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
{
    /// <summary>
    /// Projects a component value into the tracked value.
    /// </summary>
    TValue Project(in TComponent component);

    /// <summary>
    /// Called for each entity whose projected value differs from the snapshot baseline.
    /// </summary>
    void OnChange(World world, Entity entity, TValue oldValue, TValue newValue);
}
