using Hero.Ecs;
using Hero.GameplayEcs.Characters.Actions;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Components;
using Hero.GameplayEcs.Characters.Slots;

namespace Hero.Tests;

internal static class TestPipelineQueries
{
    internal static readonly MiniArch.QueryDescription ActionDescription = new MiniArch.QueryDescription()
        .With<ActionEntity>()
        .With<ActionKind>();

    internal static readonly MiniArch.QueryDescription RuleRequestDescription = new MiniArch.QueryDescription()
        .With<Request>()
        .With<RequestTarget>()
        .With<RuleId>();

    internal static readonly MiniArch.QueryDescription EffectDescription = new MiniArch.QueryDescription()
        .With<Effect>()
        .With<EffectId>()
        .With<EffectTarget>();

    internal static readonly MiniArch.QueryDescription ModifierRequestDescription = new MiniArch.QueryDescription()
        .With<Request>()
        .With<RequestTarget>()
        .With<ModifierSlot>()
        .With<DeltaModifier>();

    internal static void StepUntilStable(MiniArchRuntime runtime, int maxIterations = 8)
    {
        for (int i = 0; i < maxIterations; i++)
        {
            runtime.Tick();
            if (runtime.IsStable)
            {
                return;
            }
        }

        throw new InvalidOperationException("Runtime did not stabilize within the iteration limit.");
    }

    internal static MiniArch.Entity FindActionChild(
        MiniArchRuntime runtime, FrameView frame, MiniArch.Entity parent,
        ActionKind expectedKind, string debugName)
    {
        MiniArch.Entity action = default;

        foreach (MiniArch.Entity candidate in frame.Each(ActionDescription))
        {
            ActionKind actionKind = frame.Get<ActionKind>(candidate);

            if (actionKind != expectedKind)
            {
                continue;
            }

            if (runtime.World.TryGetParent(candidate, out MiniArch.Entity candidateParentCore) &&
                candidateParentCore == parent)
            {
                action = candidate;
            }
        }

        if (action == default)
        {
            throw new Xunit.Sdk.XunitException($"Missing {debugName} action child.");
        }

        return action;
    }

    internal static int CountRuleRequests(FrameView frame, MiniArch.Entity target, RuleId ruleId)
    {
        int count = 0;

        foreach (MiniArch.Entity entity in frame.Each(RuleRequestDescription))
        {
            RequestTarget requestTarget = frame.Get<RequestTarget>(entity);

            if (requestTarget.Target != target)
            {
                continue;
            }

            if (frame.Get<RuleId>(entity) != ruleId)
            {
                continue;
            }

            count++;
        }

        return count;
    }

    internal static int CountDamageEffects(FrameView frame, MiniArch.Entity firstTarget, MiniArch.Entity secondTarget)
    {
        int count = 0;

        foreach (MiniArch.Entity entity in frame.Each(EffectDescription))
        {
            EffectId effectId = frame.Get<EffectId>(entity);

            if (effectId != CharacterAttackIds.DamageEffect)
            {
                continue;
            }

            MiniArch.Entity target = frame.Get<EffectTarget>(entity).Target;
            if (target != firstTarget && target != secondTarget)
            {
                continue;
            }

            count++;
        }

        return count;
    }

    internal static int CountHpModifierRequests(FrameView frame, MiniArch.Entity firstTarget, MiniArch.Entity secondTarget)
    {
        int count = 0;

        foreach (MiniArch.Entity entity in frame.Each(ModifierRequestDescription))
        {
            RequestTarget requestTarget = frame.Get<RequestTarget>(entity);

            if (requestTarget.Target != firstTarget && requestTarget.Target != secondTarget)
            {
                continue;
            }

            if (frame.Get<ModifierSlot>(entity).Slot != CharacterSlotKeys.CurrentHp)
            {
                continue;
            }

            count++;
        }

        return count;
    }
}
