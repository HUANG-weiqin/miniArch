using System;
using System.Collections.Generic;

namespace Hero.Ecs;

public delegate void TriggerMatchDelegate(MiniArch.Entity targetEntity);

// Contract: condition handlers must synchronously invoke onMatch for each unique match
// discovered in the current frame and must not retain the callback after returning.
public delegate void ConditionDelegate(FrameView frame, MiniArch.Entity observerEntity, TriggerMatchDelegate onMatch);

public sealed class TriggerConditionTable
{
    private readonly Dictionary<TriggerConditionId, ConditionDelegate> _delegates = new();

    public TriggerConditionTable Register(TriggerConditionId id, ConditionDelegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _delegates[id] = handler;
        return this;
    }

    public bool TryGet(TriggerConditionId id, out ConditionDelegate handler) =>
        _delegates.TryGetValue(id, out handler!);
}
