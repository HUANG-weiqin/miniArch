using System.Collections.Generic;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Components;
using Hero.GameplayEcs.Characters.Defense;

namespace Hero.GameplayEcs.Characters.Slots;

public static class CharacterSlotPorts
{
    public static IReadOnlyDictionary<SlotKey, IIntSlotPort> Create()
    {
        Dictionary<SlotKey, IIntSlotPort> ports = new(IntSlotPorts.Create(
            (CharacterSlotKeys.PositionQ, new ComponentIntSlotPort<PositionQValue>(value => value.Value, static next => new PositionQValue(next))),
            (CharacterSlotKeys.PositionR, new ComponentIntSlotPort<PositionRValue>(value => value.Value, static next => new PositionRValue(next))),
            (CharacterSlotKeys.CurrentHp, new ComponentIntSlotPort<CurrentHpValue>(value => value.Value, static next => new CurrentHpValue(next))),
            (CharacterSlotKeys.MaxHp, new ComponentIntSlotPort<MaxHpValue>(value => value.Value, static next => new MaxHpValue(next))),
            (CharacterSlotKeys.AttackPower, new ComponentIntSlotPort<AttackPowerValue>(value => value.Value, static next => new AttackPowerValue(next)))));

        foreach (KeyValuePair<SlotKey, IIntSlotPort> pair in CharacterArmorSlotPorts.Create())
        {
            ports[pair.Key] = pair.Value;
        }

        return ports;
    }
}


