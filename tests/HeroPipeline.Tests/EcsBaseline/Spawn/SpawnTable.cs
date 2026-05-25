using System;
using System.Collections.Generic;

namespace Hero.Ecs;

public sealed class SpawnTable
{
    private readonly Dictionary<SpawnKind, SpawnHandler> _handlers = new();

    public SpawnTable Register(SpawnKind kind, SpawnHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[kind] = handler;
        return this;
    }

    public bool TryGet(SpawnKind kind, out SpawnHandler handler) => _handlers.TryGetValue(kind, out handler!);
}


