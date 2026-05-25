using System;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Components;
using Hero.GameplayEcs.Collision;
using Hero.GameplayEcs.Characters.Slots;

namespace Hero.GameplayEcs.Characters.Attack;

public static class CharacterAttackRegistrations
{
    public static RuleTable Register(RuleTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(CharacterAttackIds.HitConsequenceRule, ApplyAttackHitConsequence);
    }

    public static EffectTable Register(EffectTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table
            .Register(CharacterAttackIds.DamageEffect, ApplyDamageEffect)
            .RegisterObservationOnly(CharacterAttackIds.SwingEffect);
    }

    private static void ApplyAttackHitConsequence(FrameContext context, MiniArch.Entity request)
    {
        FrameView frame = context.Frame;
        CollisionSource source = frame.Get<CollisionSource>(request);
        CollisionTarget target = frame.Get<CollisionTarget>(request);
        int amount = frame.Get<AttackPowerValue>(source.Entity).Value;

        MiniArch.Entity effect = context.Commands.Create();
        context.Commands.Add(effect, new Effect());
        context.Commands.Add(effect, CharacterAttackIds.DamageEffect);
        context.Commands.Add(effect, new EffectSource(source.Entity));
        context.Commands.Add(effect, new EffectTarget(target.Entity));
        context.Commands.Add(effect, new DamageAmountValue(amount));
    }

    private static void ApplyDamageEffect(FrameContext context, MiniArch.Entity effect)
    {
        FrameView frame = context.Frame;
        MiniArch.Entity target = frame.Get<EffectTarget>(effect).Target;
        int amount = frame.Get<DamageAmountValue>(effect).Value;

        MiniArch.Entity request = context.Commands.Create();
        context.Commands.Add(request, new Request());
        context.Commands.Add(request, new RequestTarget(target));
        context.Commands.Add(request, new ModifierSlot(CharacterSlotKeys.CurrentHp));
        context.Commands.Add(request, new DeltaModifier(-amount));
    }
}


