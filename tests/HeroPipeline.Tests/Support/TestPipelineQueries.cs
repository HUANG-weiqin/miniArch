using Hero.Ecs;
using Hero.GameplayEcs.Characters.Actions;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Components;
using Hero.GameplayEcs.Characters.Slots;
using MiniArch.Core;

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

        foreach (var row in frame.ChunkQuery(ActionDescription).EachSpan<ActionKind>())
        {
            if (row.Get0() != expectedKind)
            {
                continue;
            }

            if (runtime.World.TryGetParent(row.Entity, out MiniArch.Entity candidateParentCore) &&
                candidateParentCore == parent)
            {
                action = row.Entity;
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

        foreach (var row in frame.ChunkQuery(RuleRequestDescription).EachSpan<RequestTarget, RuleId>())
        {
            if (row.Get0().Target != target)
            {
                continue;
            }

            if (row.Get1() != ruleId)
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

        foreach (var row in frame.ChunkQuery(EffectDescription).EachSpan<EffectId, EffectTarget>())
        {
            if (row.Get0() != CharacterAttackIds.DamageEffect)
            {
                continue;
            }

            MiniArch.Entity effectTarget = row.Get1().Target;
            if (effectTarget != firstTarget && effectTarget != secondTarget)
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

        foreach (var row in frame.ChunkQuery(ModifierRequestDescription).EachSpan<RequestTarget, ModifierSlot>())
        {
            if (row.Get0().Target != firstTarget && row.Get0().Target != secondTarget)
            {
                continue;
            }

            if (row.Get1().Slot != CharacterSlotKeys.CurrentHp)
            {
                continue;
            }

            count++;
        }

        return count;
    }
}
