using System;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Actions;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Defense;
using Hero.GameplayEcs.Characters.Heal;
using Hero.GameplayEcs.Characters.Movement;

namespace Hero.GameplayEcs.Cards.Decks;

public readonly record struct CardTemplateId(int Value);

public static class CardTemplateIds
{
    public static CardTemplateId Move { get; } = new(1);
    public static CardTemplateId Attack { get; } = new(2);
    public static CardTemplateId DelayedFinaleAttack { get; } = new(3);
    public static CardTemplateId Defend { get; } = new(4);
    public static CardTemplateId Heal { get; } = new(5);
}

public readonly record struct CardTemplate(
    CardTemplateId Id,
    ActionKind ActionKind,
    RuleId RuleId,
    int ActionPointCost,
    int? ArmorAmount = null,
    int? HealAmount = null,
    bool Delayed = false,
    bool Finale = false);

public static class CardTemplateCatalog
{
    public static CardTemplate Get(CardTemplateId id)
    {
        if (id == CardTemplateIds.Move)
        {
            return new CardTemplate(id, CardActionKinds.Move, CharacterMovementIds.MoveRule, 1);
        }

        if (id == CardTemplateIds.Attack)
        {
            return new CardTemplate(id, CardActionKinds.Attack, CharacterAttackIds.AttackRule, 1);
        }

        if (id == CardTemplateIds.DelayedFinaleAttack)
        {
            return new CardTemplate(id, CardActionKinds.Attack, CharacterAttackIds.AttackRule, 1, Delayed: true, Finale: true);
        }

        if (id == CardTemplateIds.Defend)
        {
            return new CardTemplate(id, CardActionKinds.Defend, CharacterArmorIds.GainArmorRule, 1, ArmorAmount: 5);
        }

        if (id == CardTemplateIds.Heal)
        {
            return new CardTemplate(id, CardActionKinds.Heal, CharacterHealIds.HealRule, 1, HealAmount: 3);
        }

        throw new InvalidOperationException($"Unknown card template id: {id.Value}");
    }
}
