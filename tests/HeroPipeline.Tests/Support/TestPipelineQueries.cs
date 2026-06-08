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

        foreach (var chunk in frame.ChunkQuery(ActionDescription).GetChunks())
        {
            var kinds = chunk.GetSpan<ActionKind>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (kinds[i] != expectedKind)
                {
                    continue;
                }

                if (runtime.World.TryGetParent(entities[i], out MiniArch.Entity candidateParentCore) &&
                    candidateParentCore == parent)
                {
                    action = entities[i];
                }
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

        foreach (var chunk in frame.ChunkQuery(RuleRequestDescription).GetChunks())
        {
            var targets = chunk.GetSpan<RequestTarget>();
            var ruleIds = chunk.GetSpan<RuleId>();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (targets[i].Target != target)
                {
                    continue;
                }

                if (ruleIds[i] != ruleId)
                {
                    continue;
                }

                count++;
            }
        }

        return count;
    }

    internal static int CountDamageEffects(FrameView frame, MiniArch.Entity firstTarget, MiniArch.Entity secondTarget)
    {
        int count = 0;

        foreach (var chunk in frame.ChunkQuery(EffectDescription).GetChunks())
        {
            var effectIds = chunk.GetSpan<EffectId>();
            var effectTargets = chunk.GetSpan<EffectTarget>();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (effectIds[i] != CharacterAttackIds.DamageEffect)
                {
                    continue;
                }

                MiniArch.Entity effectTarget = effectTargets[i].Target;
                if (effectTarget != firstTarget && effectTarget != secondTarget)
                {
                    continue;
                }

                count++;
            }
        }

        return count;
    }

    internal static int CountHpModifierRequests(FrameView frame, MiniArch.Entity firstTarget, MiniArch.Entity secondTarget)
    {
        int count = 0;

        foreach (var chunk in frame.ChunkQuery(ModifierRequestDescription).GetChunks())
        {
            var targets = chunk.GetSpan<RequestTarget>();
            var slots = chunk.GetSpan<ModifierSlot>();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (targets[i].Target != firstTarget && targets[i].Target != secondTarget)
                {
                    continue;
                }

                if (slots[i].Slot != CharacterSlotKeys.CurrentHp)
                {
                    continue;
                }

                count++;
            }
        }

        return count;
    }
}
