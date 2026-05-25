using Hero.Ecs;

namespace Hero.GameplayEcs.Trigger;

public static class TriggerIds
{
    public static TriggerConditionId DamageDealtBySelf { get; } = new(4001);
    public static TriggerActionId GainArmorFromDamage { get; } = new(4101);
    public static TriggerConditionId CardDrawnWithHeal { get; } = new(4002);
    public static TriggerActionId HealOnCardDrawn { get; } = new(4102);
}
