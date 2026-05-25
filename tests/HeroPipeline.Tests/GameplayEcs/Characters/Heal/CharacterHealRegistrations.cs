using System;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Slots;

namespace Hero.GameplayEcs.Characters.Heal;

public static class CharacterHealRegistrations
{
    public static RuleTable Register(RuleTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(CharacterHealIds.HealRule, ApplyHealRule);
    }

    public static EffectTable Register(EffectTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(CharacterHealIds.HealEffect, ApplyHealEffect);
    }

    private static void ApplyHealRule(FrameContext context, MiniArch.Entity request)
    {
        FrameView frame = context.Frame;
        MiniArch.Entity target = frame.Get<RequestTarget>(request).Target;
        int amount = frame.Get<HealAmountValue>(request).Value;

        // Create a heal effect
        MiniArch.Entity effect = context.Commands.Create();
        context.Commands.Add(effect, new Effect());
        context.Commands.Add(effect, CharacterHealIds.HealEffect);
        context.Commands.Add(effect, new EffectTarget(target));
        context.Commands.Add(effect, new HealAmountValue(amount));
    }

    private static void ApplyHealEffect(FrameContext context, MiniArch.Entity effect)
    {
        FrameView frame = context.Frame;
        MiniArch.Entity target = frame.Get<EffectTarget>(effect).Target;
        int amount = frame.Get<HealAmountValue>(effect).Value;

        MiniArch.Entity modifier = context.Commands.Create();
        context.Commands.Add(modifier, new Request());
        context.Commands.Add(modifier, new RequestTarget(target));
        context.Commands.Add(modifier, new ModifierSlot(CharacterSlotKeys.CurrentHp));
        context.Commands.Add(modifier, new DeltaModifier(amount));
    }
}
