using System.Collections.Generic;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Slots;

namespace Hero.GameplayEcs.TurnSerial;

public static class TurnSerialSlotPorts
{
    public static IReadOnlyDictionary<SlotKey, IIntSlotPort> Create() =>
        IntSlotPorts.Create(
            (TurnSerialSlotKeys.CurrentAp, new ComponentIntSlotPort<CurrentApValue>(value => value.Value, static next => new CurrentApValue(next))),
            (TurnSerialSlotKeys.MaxAp, new ComponentIntSlotPort<MaxApValue>(value => value.Value, static next => new MaxApValue(next))),
            (TurnSerialSlotKeys.ActionPointCost, new ComponentIntSlotPort<ActionPointCostValue>(value => value.Value, static next => new ActionPointCostValue(next))));
}
