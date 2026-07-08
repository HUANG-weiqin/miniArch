namespace MiniArch;

/// <summary>
/// Old/new pair for a single component value change.
/// Used by <see cref="SharedValueChanges{T}.Changes"/> for zero-copy access.
/// </summary>
public readonly struct ValueChange<T> where T : unmanaged
{
    /// <summary>The entity whose component changed.</summary>
    public readonly Entity Entity;

    /// <summary>The component value before the change.</summary>
    public readonly T Old;

    /// <summary>The component value after the change.</summary>
    public readonly T New;

    internal ValueChange(Entity entity, T old, T @new)
    {
        Entity = entity;
        Old = old;
        New = @new;
    }
}
