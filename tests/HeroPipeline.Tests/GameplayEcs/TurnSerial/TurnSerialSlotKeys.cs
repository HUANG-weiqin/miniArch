using Hero.Ecs;

namespace Hero.GameplayEcs.TurnSerial;

public static class TurnSerialSlotKeys
{
    public static SlotKey CurrentAp { get; } = new(5001);
    public static SlotKey MaxAp { get; } = new(5002);
    public static SlotKey ActionPointCost { get; } = new(5003);
}
