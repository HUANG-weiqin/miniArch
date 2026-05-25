using System.Collections.Generic;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Slots;

namespace Hero.GameplayEcs.Characters.Defense;

public static class CharacterArmorSlotPorts
{
    public static IReadOnlyDictionary<SlotKey, IIntSlotPort> Create() =>
        IntSlotPorts.Create(
            (CharacterArmorSlotKeys.CurrentArmor, new ComponentIntSlotPort<CurrentArmorValue>(value => value.Value, static next => new CurrentArmorValue(next))));
}
