using System;

namespace Hero.Ecs;

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
        foreach (var chunk in context.Frame.ChunkQuery(TriggerQueryDescription).GetChunks())
        {
            ReadOnlySpan<MiniArch.Entity> entities = chunk.GetEntities();
            ReadOnlySpan<TriggerCondition> conditions = chunk.GetSpan<TriggerCondition>();
            ReadOnlySpan<TriggerAction> actions = chunk.GetSpan<TriggerAction>();

            bool hasGuard = chunk.TryGetComponentIndex<TriggerGuard>(out int guardColumn);
            ReadOnlySpan<TriggerGuard> guards = hasGuard
                ? chunk.GetComponentSpanAt<TriggerGuard>(guardColumn)
                : default;

            bool hasPostAction = chunk.TryGetComponentIndex<TriggerPostAction>(out int postActionColumn);
            ReadOnlySpan<TriggerPostAction> postActions = hasPostAction
                ? chunk.GetComponentSpanAt<TriggerPostAction>(postActionColumn)
                : default;

            for (int i = 0; i < chunk.Count; i++)
            {
                TriggerCondition condition = conditions[i];
                TriggerAction action = actions[i];

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

                if (hasGuard)
                {
                    if (!guards[i].Condition(context.Frame, entities[i]))
                    {
                        continue;
                    }
                }

                _matchCount = 0;
                conditionHandler(context.Frame, entities[i], _cachedOnMatch);

                if (_matchCount == 0)
                {
                    continue;
                }

                TriggerTargets targets = new(_matchBuffer, _matchCount);
                actionHandler(context, entities[i], targets);

                if (hasPostAction)
                {
                    postActions[i].Handler(context, entities[i], targets);
                }
            }
        }
    }
}
