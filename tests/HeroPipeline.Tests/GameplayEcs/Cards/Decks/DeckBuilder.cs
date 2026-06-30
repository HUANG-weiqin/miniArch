using Hero.Ecs;
using Hero.GameplayEcs.Characters.Actions;
using Hero.GameplayEcs.Characters.Defense;
using Hero.GameplayEcs.Characters.Heal;
using Hero.GameplayEcs.TurnSerial;

namespace Hero.GameplayEcs.Cards.Decks;

public static class DeckBuilder
{
    public static void Build(in FrameContext context, MiniArch.Entity owner, CombatDeckLoadout loadout)
    {
        for (int order = 0; order < loadout.Cards.Count; order++)
        {
            CombatDeckCardSpec spec = loadout.Cards[order];
            CardTemplate template = CardTemplateCatalog.Get(spec.TemplateId);
            MiniArch.Entity card = context.Commands.Create();

            context.Commands.Add(card, new ActionEntity());
            context.Commands.Add(card, template.ActionKind);
            context.Commands.Add(card, new ActionRuleId(template.RuleId));
            context.Commands.Add(card, new ActionPointCostValue(template.ActionPointCost));
            context.Commands.Add(card, new CardZone(CardZoneKind.Discard));
            context.Commands.Add(card, new CardOrderValue(order));

            if (template.ArmorAmount is { } armor)
            {
                context.Commands.Add(card, new ArmorAmountValue(armor));
            }

            if (template.HealAmount is { } heal)
            {
                context.Commands.Add(card, new HealAmountValue(heal));
            }

            if (template.Delayed)
            {
                context.Commands.Add(card, new CardTraitDelayed());
            }

            if (template.Finale)
            {
                context.Commands.Add(card, new CardTraitFinale());
            }

            context.Commands.AddChild(owner, card);
        }
    }
}
