using System;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Slots;

namespace Hero.GameplayEcs.Characters.Movement;

public static class CharacterMovementRegistrations
{
    public static RuleTable Register(RuleTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(CharacterMovementIds.MoveRule, ApplyMoveRule);
    }

    public static EffectTable Register(EffectTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(CharacterMovementIds.MoveEffect, ApplyMoveEffect);
    }

    private static void ApplyMoveRule(FrameContext context, MiniArch.Entity request)
    {
        FrameView frame = context.Frame;
        RequestTarget requestTarget = frame.Get<RequestTarget>(request);
        MoveDqValue dq = frame.Get<MoveDqValue>(request);
        MoveDrValue dr = frame.Get<MoveDrValue>(request);

        MiniArch.Entity effect = context.Commands.Create();
        context.Commands.Add(effect, new Effect());
        context.Commands.Add(effect, CharacterMovementIds.MoveEffect);
        context.Commands.Add(effect, new EffectTarget(requestTarget.Target));
        context.Commands.Add(effect, dq);
        context.Commands.Add(effect, dr);
    }

    private static void ApplyMoveEffect(FrameContext context, MiniArch.Entity effectEntity)
    {
        FrameView frame = context.Frame;
        EffectTarget effectTarget = frame.Get<EffectTarget>(effectEntity);
        MoveDqValue dq = frame.Get<MoveDqValue>(effectEntity);
        MoveDrValue dr = frame.Get<MoveDrValue>(effectEntity);

        SpawnModifierRequest(context, effectTarget.Target, CharacterSlotKeys.PositionQ, dq.Value);
        SpawnModifierRequest(context, effectTarget.Target, CharacterSlotKeys.PositionR, dr.Value);
    }

    private static void SpawnModifierRequest(FrameContext context, MiniArch.Entity target, SlotKey slot, int delta)
    {
        MiniArch.Entity request = context.Commands.Create();
        context.Commands.Add(request, new Request());
        context.Commands.Add(request, new RequestTarget(target));
        context.Commands.Add(request, new ModifierSlot(slot));
        context.Commands.Add(request, new DeltaModifier(delta));
    }
}


