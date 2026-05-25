using System;
using System.Collections.Generic;

namespace Hero.Ecs;

public sealed class EffectTable
{
    private readonly Dictionary<EffectId, EffectHandler> _handlers = new();

    public EffectTable Register(EffectId id, EffectHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[id] = handler;
        return this;
    }

    public EffectTable RegisterObservationOnly(EffectId id)
    {
        _handlers[id] = null!;
        return this;
    }

    public bool TryGet(EffectId id, out EffectHandler handler) => _handlers.TryGetValue(id, out handler!);
}


