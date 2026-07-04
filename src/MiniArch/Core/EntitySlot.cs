namespace MiniArch.Core;

/// <summary>
/// A tracked entity handle that auto-updates when a deferred placeholder is resolved.
/// </summary>
/// <remarks>
/// <para>
/// Obtain an EntitySlot via <see cref="CommandStream.Track"/>. The <see cref="Value"/>
/// property returns the placeholder entity before resolution and the real entity after
/// <see cref="CommandStream.Submit"/> or <see cref="CommandStream.Replay(FrameDelta)"/>.
/// </para>
/// <para>
/// <b>EntitySlot cannot be stored in ECS components</b> (it contains reference types and
/// is not <c>unmanaged</c>). Store <see cref="Entity"/> (via <c>slot.Value</c>) in
/// component fields instead —the existing <c>EntityFieldResolver</c> handles auto-resolution
/// of component fields independently.
/// </para>
/// </remarks>
public readonly struct EntitySlot
{
    private readonly Entity _entity;
    private readonly Slot? _slot;

    /// <summary>Creates an EntitySlot wrapping an inline real entity (non-deferred mode).</summary>
    internal EntitySlot(Entity entity)
    {
        _entity = entity;
        _slot = null;
    }

    /// <summary>Creates an EntitySlot wrapping a mutable Slot (deferred mode).</summary>
    internal EntitySlot(Slot slot)
    {
        _entity = default;
        _slot = slot;
    }

    /// <summary>
    /// The current entity. Returns the placeholder before resolution,
    /// the real entity after Submit/Replay.
    /// </summary>
    public Entity Value => _slot is not null ? _slot.Entity : _entity;

    /// <summary>Whether this slot holds a non-default entity handle.</summary>
    public bool HasValue => Value != default;

    /// <summary>
    /// Implicit conversion to <see cref="Entity"/>. Returns the current value
    /// of <see cref="Value"/> —placeholder before resolution, real entity after.
    /// </summary>
    /// <remarks>
    /// This allows passing an <see cref="EntitySlot"/> directly to any method
    /// that accepts <see cref="Entity"/> (e.g. <see cref="CommandStream.Add{T}"/>,
    /// <see cref="CommandStream.Set{T}"/>, <see cref="CommandStream.Destroy"/>).
    /// The conversion always returns the best-known entity at call time.
    /// </remarks>
    public static implicit operator Entity(EntitySlot slot) => slot.Value;
}
