namespace MiniArch;

/// <summary>
/// Typed old/new pair for a single captured component type.
/// Used by <see cref="ChangeQuery.ValueChanges{T}"/> for zero-copy access.
/// </summary>
public readonly struct TypedChange<T> where T : unmanaged
{
    /// <summary>The entity whose component changed.</summary>
    public readonly Entity Entity;

    /// <summary>The component value before the change.</summary>
    public readonly T Old;

    /// <summary>The component value after the change.</summary>
    public readonly T New;

    internal TypedChange(Entity entity, T old, T @new)
    {
        Entity = entity;
        Old = old;
        New = @new;
    }
}
