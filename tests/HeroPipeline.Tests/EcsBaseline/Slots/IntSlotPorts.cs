using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Hero.Ecs;

public static class IntSlotPorts
{
    public static IReadOnlyDictionary<SlotKey, IIntSlotPort> Empty { get; } =
        new ReadOnlyDictionary<SlotKey, IIntSlotPort>(new Dictionary<SlotKey, IIntSlotPort>());

    public static IReadOnlyDictionary<SlotKey, IIntSlotPort> Create(params (SlotKey Slot, IIntSlotPort Port)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Dictionary<SlotKey, IIntSlotPort> ports = new(entries.Length);
        foreach ((SlotKey slot, IIntSlotPort port) in entries)
        {
            ArgumentNullException.ThrowIfNull(port);
            ports[slot] = port;
        }

        return new ReadOnlyDictionary<SlotKey, IIntSlotPort>(ports);
    }
}


