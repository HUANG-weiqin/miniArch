using System;
using Hero.Ecs;

namespace Hero.GameplayEcs.Characters.Defense;

public static class CharacterArmorRegistrations
{
    public static RuleTable Register(RuleTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(CharacterArmorIds.GainArmorRule, ApplyGainArmor);
    }

    public static EffectTable Register(EffectTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(CharacterArmorIds.GainArmorEffect, ApplyGainArmorEffect);
    }

    private static void ApplyGainArmor(FrameContext context, MiniArch.Entity request)
    {
        FrameView frame = context.Frame;
        MiniArch.Entity target = frame.Get<RequestTarget>(request).Target;
        int amount = frame.Get<ArmorAmountValue>(request).Value;

        MiniArch.Entity effect = context.Commands.Create();
        context.Commands.Add(effect, new Effect());
        context.Commands.Add(effect, CharacterArmorIds.GainArmorEffect);
        context.Commands.Add(effect, new EffectTarget(target));
        context.Commands.Add(effect, new ArmorAmountValue(amount));
    }

    private static void ApplyGainArmorEffect(FrameContext context, MiniArch.Entity effect)
    {
        FrameView frame = context.Frame;
        MiniArch.Entity target = frame.Get<EffectTarget>(effect).Target;
        int amount = frame.Get<ArmorAmountValue>(effect).Value;

        MiniArch.Entity modifier = context.Commands.Create();
        context.Commands.Add(modifier, new Request());
        context.Commands.Add(modifier, new RequestTarget(target));
        context.Commands.Add(modifier, new ModifierSlot(CharacterArmorSlotKeys.CurrentArmor));
        context.Commands.Add(modifier, new DeltaModifier(amount));
    }
}
