using System;
using Hero.Ecs;
using Hero.GameplayEcs.Cards;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Defense;
using Hero.GameplayEcs.Characters.Heal;
using MiniArch.Core;
using MiniArchQueryDescription = MiniArch.QueryDescription;

namespace Hero.GameplayEcs.Trigger;

public static class TriggerRegistrations
{
    private static readonly MiniArchQueryDescription DamageEffectQuery =
        new MiniArchQueryDescription()
            .With<Effect>()
            .With<EffectId>()
            .With<EffectSource>()
            .With<EffectTarget>()
            .With<DamageAmountValue>()
            .With<Validated>();

    private static readonly MiniArchQueryDescription CardDrawnEffectQuery =
        new MiniArchQueryDescription()
            .With<Effect>()
            .With<EffectId>()
            .With<CardDrawnEffectMarker>()
            .With<EffectTarget>()
            .With<Validated>();

    public static TriggerConditionTable Register(TriggerConditionTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table
            .Register(TriggerIds.DamageDealtBySelf, ObserveDamageDealtBySelf)
            .Register(TriggerIds.CardDrawnWithHeal, ObserveCardDrawnWithHeal);
    }

    public static TriggerActionTable Register(TriggerActionTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table
            .Register(TriggerIds.GainArmorFromDamage, ActionGainArmorFromDamage)
            .Register(TriggerIds.HealOnCardDrawn, ActionHealOnCardDrawn);
    }

    private static void ObserveDamageDealtBySelf(FrameView frame, MiniArch.Entity observerEntity, TriggerMatchDelegate onMatch)
    {
        if (!frame.TryGetParent(observerEntity, out MiniArch.Entity owner))
        {
            return;
        }

        foreach (var chunk in frame.ChunkQuery(DamageEffectQuery).GetChunks())
        {
            var effectIds = chunk.GetSpan<EffectId>();
            var sources = chunk.GetSpan<EffectSource>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (effectIds[i] != CharacterAttackIds.DamageEffect)
                {
                    continue;
                }

                if (sources[i].Source == owner)
                {
                    onMatch(entities[i]);
                }
            }
        }
    }

    private static void ObserveCardDrawnWithHeal(FrameView frame, MiniArch.Entity observerEntity, TriggerMatchDelegate onMatch)
    {
        // observerEntity is the card that was drawn (has TriggerCondition marker)
        // We need to find the CardDrawnEffect where target == observerEntity

        foreach (var chunk in frame.ChunkQuery(CardDrawnEffectQuery).GetChunks())
        {
            var effectIds = chunk.GetSpan<EffectId>();
            var targets = chunk.GetSpan<EffectTarget>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (effectIds[i] != CardIds.CardDrawnEffect)
                {
                    continue;
                }

                if (targets[i].Target == observerEntity)
                {
                    onMatch(entities[i]);
                }
            }
        }
    }

    private static void ActionGainArmorFromDamage(FrameContext ctx, MiniArch.Entity observerEntity, TriggerTargets targets)
    {
        if (!ctx.Frame.TryGetParent(observerEntity, out MiniArch.Entity owner))
        {
            return;
        }

        foreach (MiniArch.Entity targetEntity in targets)
        {
            // Defensive: ensure targetEntity actually has DamageAmountValue
            if (!ctx.Frame.TryGet<DamageAmountValue>(targetEntity, out var amountComp))
            {
                continue;
            }

            MiniArch.Entity effect = ctx.Commands.Create();
            ctx.Commands.Add(effect, new Effect());
            ctx.Commands.Add(effect, CharacterArmorIds.GainArmorEffect);
            ctx.Commands.Add(effect, new EffectTarget(owner));
            ctx.Commands.Add(effect, new ArmorAmountValue(amountComp.Value));
        }
    }

    private static void ActionHealOnCardDrawn(FrameContext ctx, MiniArch.Entity observerEntity, TriggerTargets targets)
    {
        // observerEntity is the card that was drawn
        // We need to heal the owner (participant who drew the card)

        if (!ctx.Frame.TryGetParent(observerEntity, out MiniArch.Entity owner))
        {
            return;
        }

        // Read heal amount from the card's HealAmountValue slot
        var healAmount = ctx.Frame.Get<HealAmountValue>(observerEntity);

        foreach (MiniArch.Entity _ in targets)
        {
            MiniArch.Entity effect = ctx.Commands.Create();
            ctx.Commands.Add(effect, new Effect());
            ctx.Commands.Add(effect, CharacterHealIds.HealEffect);
            ctx.Commands.Add(effect, new EffectTarget(owner));
            ctx.Commands.Add(effect, healAmount);
        }
    }

}
