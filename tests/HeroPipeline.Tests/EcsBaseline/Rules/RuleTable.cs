using System;
using System.Collections.Generic;

namespace Hero.Ecs;

public sealed class RuleTable
{
    private readonly Dictionary<RuleId, RuleHandler> _handlers = new();

    public RuleTable Register(RuleId id, RuleHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[id] = handler;
        return this;
    }

    public bool TryGet(RuleId id, out RuleHandler handler) => _handlers.TryGetValue(id, out handler!);
}


