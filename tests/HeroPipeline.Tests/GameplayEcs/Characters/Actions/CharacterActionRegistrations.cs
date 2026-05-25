using System;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Movement;

namespace Hero.GameplayEcs.Characters.Actions;

public static class CharacterActionRegistrations
{
    private static readonly CharacterActionTranslationTable TranslationTable = CharacterActionTranslationTable.Create();

    public static RuleTable Register(RuleTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(CharacterActionIds.DispatchRule, DispatchActionRequest);
    }

    private static void DispatchActionRequest(FrameContext context, MiniArch.Entity request)
    {
        FrameView frame = context.Frame;
        RequestTarget requestTarget = frame.Get<RequestTarget>(request);

        if (!frame.TryGet(requestTarget.Target, out ActionEntity _))
        {
            throw new InvalidOperationException($"Request target '{requestTarget.Target}' is not an action entity.");
        }

        ActionKind actionKind = frame.Get<ActionKind>(requestTarget.Target);
        ActionRuleId actionRuleId = frame.Get<ActionRuleId>(requestTarget.Target);

        if (!context.Frame.TryGetParent(requestTarget.Target, out MiniArch.Entity parentCore))
        {
            return;
        }

        MiniArch.Entity parent = parentCore;
        TranslationTable.Translate(actionKind, context, request, parent, actionRuleId);
    }
}


