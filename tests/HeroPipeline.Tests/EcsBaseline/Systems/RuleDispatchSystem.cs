using System;
using CoreCommandBuffer = MiniArch.Core.ICommandRecorder;

namespace Hero.Ecs;

public sealed class RuleDispatchSystem : ISystem
{
    private readonly RuleTable _ruleTable;
    private static readonly MiniArch.QueryDescription RuleQueryDescription = new MiniArch.QueryDescription()
        .With<RuleId>()
        .With<RuleTier>()
        .WithAny<Resident>()
        .WithAny<Validated>()
        .Without<PendingRequest>()
        .Without<Rejected>();

    public RuleDispatchSystem(RuleTable ruleTable)
    {
        _ruleTable = ruleTable ?? throw new ArgumentNullException(nameof(ruleTable));
    }

    public void Execute(in FrameContext context)
    {
        DispatchTier(context, RuleTier.High);
        DispatchTier(context, RuleTier.Normal);
        DispatchTier(context, RuleTier.Low);
    }

    private void DispatchTier(FrameContext context, RuleTier tier)
    {
        CoreCommandBuffer commands = context.Commands;

        foreach (MiniArch.Entity entity in context.Frame.Each(RuleQueryDescription))
        {
            RuleId ruleId = context.Frame.Get<RuleId>(entity);
            RuleTier rowTier = context.Frame.Get<RuleTier>(entity);

            if (rowTier != tier)
            {
                continue;
            }

            if (!_ruleTable.TryGet(ruleId, out RuleHandler handler))
            {
                throw new InvalidOperationException(
                    $"No rule handler is registered for rule '{ruleId.Value}'.");
            }

            handler(context, entity);

            if (context.Frame.TryGet(entity, out Request _))
            {
                commands.Destroy(entity);
            }
        }
    }
}


