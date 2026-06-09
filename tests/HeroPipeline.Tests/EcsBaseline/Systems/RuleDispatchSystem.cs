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

        foreach (var chunk in context.Frame.ChunkQuery(RuleQueryDescription).GetChunks())
        {
            ReadOnlySpan<MiniArch.Entity> entities = chunk.GetEntities();
            ReadOnlySpan<RuleId> ruleIds = chunk.GetSpan<RuleId>();
            ReadOnlySpan<RuleTier> tiers = chunk.GetSpan<RuleTier>();
            bool hasRequest = chunk.TryGetComponentIndex<Request>(out int _);

            for (int i = 0; i < chunk.Count; i++)
            {
                if (tiers[i] != tier)
                {
                    continue;
                }

                if (!_ruleTable.TryGet(ruleIds[i], out RuleHandler handler))
                {
                    throw new InvalidOperationException(
                        $"No rule handler is registered for rule '{ruleIds[i].Value}'.");
                }

                MiniArch.Entity entity = entities[i];
                handler(context, entity);

                if (hasRequest)
                {
                    commands.Destroy(entity);
                }
            }
        }
    }
}
