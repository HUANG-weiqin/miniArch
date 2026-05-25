using System;
using System.Collections.Generic;

namespace Hero.Ecs;

public delegate void ActionDelegate(FrameContext ctx, MiniArch.Entity observerEntity, TriggerTargets targets);

public sealed class TriggerActionTable
{
    private readonly Dictionary<TriggerActionId, ActionDelegate> _delegates = new();

    public TriggerActionTable Register(TriggerActionId id, ActionDelegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _delegates[id] = handler;
        return this;
    }

    public bool TryGet(TriggerActionId id, out ActionDelegate handler) =>
        _delegates.TryGetValue(id, out handler!);
}
