using System;
using Hero.Ecs;
using CoreCommandBuffer = MiniArch.Core.ICommandRecorder;

namespace Hero.GameplayEcs.Characters.Slots;

/// <summary>
/// Bridges an ECS component of type <typeparamref name="TValue"/> to the <see cref="IIntSlotPort"/> interface.
/// Reads the component's int value via <paramref name="read"/> and writes back via <paramref name="create"/>.
/// </summary>
public sealed class ComponentIntSlotPort<TValue> : IIntSlotPort
    where TValue : unmanaged
{
    private readonly Func<TValue, int> _read;
    private readonly Func<int, TValue> _create;

    public ComponentIntSlotPort(Func<TValue, int> read, Func<int, TValue> create)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _create = create ?? throw new ArgumentNullException(nameof(create));
    }

    public bool TryRead(FrameView frame, MiniArch.Entity target, out int current)
    {
        if (!frame.TryGet(target, out TValue value))
        {
            current = default;
            return false;
        }

        current = _read(value);
        return true;
    }

    public void Write(CoreCommandBuffer cb, MiniArch.Entity target, int next) => cb.Set(target, _create(next));
}
