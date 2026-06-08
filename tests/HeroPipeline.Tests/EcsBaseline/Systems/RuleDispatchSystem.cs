using System;
using CoreCommandBuffer = MiniArch.Core.CommandBuffer;

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

    private static readonly MiniArch.Core.ComponentType RequestComponentType = MiniArch.Core.Component<Request>.ComponentType;

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
        MiniArch.Core.Query coreQuery = context.Frame.ChunkQuery(RuleQueryDescription);

        foreach (MiniArch.Core.Chunk chunk in coreQuery.Chunks)
        {
            ReadOnlySpan<MiniArch.Entity> entities = chunk.GetEntities();
            ReadOnlySpan<RuleId> ruleIds = chunk.GetComponentSpan<RuleId>(MiniArch.Core.Component<RuleId>.ComponentType);
            ReadOnlySpan<RuleTier> tiers = chunk.GetComponentSpan<RuleTier>(MiniArch.Core.Component<RuleTier>.ComponentType);
            bool hasRequest = chunk.TryGetComponentIndex(RequestComponentType, out int requestColumn);

            for (int i = 0; i < entities.Length; i++)
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
