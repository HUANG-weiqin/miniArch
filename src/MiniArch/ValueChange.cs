namespace MiniArch;

/// <summary>
/// Records a component value change: the entity whose component was set via
/// <see cref="World"/>.<c>Set&lt;T&gt;</c> along with the value before and after the operation.
/// Obtain via <see cref="ChangeQuery{T}.Changes"/> when tracking was configured with
/// <see cref="ChangeQuery{T}.WithPreviousValues"/>.
/// </summary>
/// <remarks>
/// Captures <c>World.Set&lt;T&gt;</c>, CommandStream.Set, and replay (FrameDelta) Set
/// operations. <see cref="EntityAccessor.Set{T}"/> does not produce records.
/// </remarks>
/// <example>
/// <code>
/// foreach (var c in hp.Changes())
///     ShowDamageNumber(c.Entity, c.OldValue.Value - c.NewValue.Value);
/// </code>
/// </example>
public readonly struct ValueChange<T> where T : unmanaged
{
    /// <summary>The entity whose component was written.</summary>
    public readonly Entity Entity;

    /// <summary>The component value before the <see cref="World.Set{T}"/> call.</summary>
    public readonly T OldValue;

    /// <summary>The component value after the <see cref="World.Set{T}"/> call.</summary>
    public readonly T NewValue;

    /// <summary>Creates a value-change record.</summary>
    public ValueChange(Entity entity, in T oldValue, in T newValue)
    {
        Entity = entity;
        OldValue = oldValue;
        NewValue = newValue;
    }
}
