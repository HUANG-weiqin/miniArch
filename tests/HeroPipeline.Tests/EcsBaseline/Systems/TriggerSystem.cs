using System;

namespace Hero.Ecs;

/// <summary>
/// Scans all entities with <see cref="TriggerCondition"/> + <see cref="TriggerAction"/>
/// and invokes the matching action once with all targets discovered by the condition.
/// </summary>
/// <remarks>
/// Runs in parallel with other systems against the same frame snapshot.
/// Condition scans read the current <see cref="FrameView"/>; action writes go to
/// the shared command buffer and are not visible to other systems until the next tick.
///
/// <para>Buffer reuse semantics:</para>
/// A single pre-allocated array (<c>_matchBuffer</c>) is reused across all observers
/// in the same frame. <c>_matchCount</c> is reset to 0 for each observer, so
/// <see cref="TriggerTargets"/> only ever sees the targets written for the current
/// observer. There is no cross-observer or cross-frame contamination.
///
/// <para>Overflow behavior:</para>
/// The buffer capacity is fixed at 64. If an observer matches more than 64 targets,
/// an <see cref="InvalidOperationException"/> is thrown with the current capacity
/// and overflow reason. This is a fail-fast defense; the current game scale
/// (&lt;20 entities) makes overflow practically impossible.
/// </remarks>
public sealed class TriggerSystem : ISystem
{
    private readonly TriggerConditionTable _conditionTable;
    private readonly TriggerActionTable _actionTable;
    private readonly MiniArch.Entity[] _matchBuffer = new MiniArch.Entity[64];
    private int _matchCount;
    private readonly TriggerMatchDelegate _cachedOnMatch;

    private static readonly MiniArch.QueryDescription TriggerQueryDescription =
        new MiniArch.QueryDescription()
            .With<TriggerCondition>()
            .With<TriggerAction>();

    public TriggerSystem(TriggerConditionTable conditionTable, TriggerActionTable actionTable)
    {
        _conditionTable = conditionTable ?? throw new ArgumentNullException(nameof(conditionTable));
        _actionTable = actionTable ?? throw new ArgumentNullException(nameof(actionTable));
        _cachedOnMatch = OnMatch;
    }

    private void OnMatch(MiniArch.Entity target)
    {
        if (_matchCount >= _matchBuffer.Length)
        {
            throw new InvalidOperationException(
                $"Trigger match buffer overflow: observer matched {_matchBuffer.Length}+ targets in a single frame. " +
                $"Current capacity is {_matchBuffer.Length}. Increase buffer size or reduce match count.");
        }
        _matchBuffer[_matchCount++] = target;
    }

    public void Execute(in FrameContext context)
    {
        foreach (MiniArch.Entity observerEntity in context.Frame.Each(TriggerQueryDescription))
        {
            TriggerCondition condition = context.Frame.Get<TriggerCondition>(observerEntity);
            TriggerAction action = context.Frame.Get<TriggerAction>(observerEntity);

            if (!_conditionTable.TryGet(condition.Id, out ConditionDelegate conditionHandler))
            {
                throw new InvalidOperationException(
                    $"No trigger condition handler is registered for condition '{condition.Id.Value}'.");
            }

            if (!_actionTable.TryGet(action.Id, out ActionDelegate actionHandler))
            {
                throw new InvalidOperationException(
                    $"No trigger action handler is registered for action '{action.Id.Value}'.");
            }

            if (context.Frame.TryGet<TriggerGuard>(observerEntity, out TriggerGuard guard))
            {
                if (!guard.Condition(context.Frame, observerEntity))
                {
                    continue;
                }
            }

            _matchCount = 0;
            conditionHandler(context.Frame, observerEntity, _cachedOnMatch);

            if (_matchCount == 0)
            {
                continue;
            }

            TriggerTargets targets = new(_matchBuffer, _matchCount);
            actionHandler(context, observerEntity, targets);

            if (context.Frame.TryGet<TriggerPostAction>(observerEntity, out TriggerPostAction postAction))
            {
                postAction.Handler(context, observerEntity, targets);
            }
        }
    }
}
