using Hero.Ecs;

namespace Hero.GameplayEcs.Characters.Slots;

public static class CharacterSlotKeys
{
    public static SlotKey PositionQ { get; } = new(1001);
    public static SlotKey PositionR { get; } = new(1002);
    public static SlotKey CurrentHp { get; } = new(1003);
    public static SlotKey MaxHp { get; } = new(1004);
    public static SlotKey AttackPower { get; } = new(1005);
}


