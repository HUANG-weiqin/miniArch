using System;
using MiniArch.Core;
using CoreCommandBuffer = MiniArch.Core.CommandBuffer;

namespace Hero.Ecs;

public sealed class EffectDispatchSystem : ISystem
{
    private readonly EffectTable _effectTable;
    private static readonly MiniArch.QueryDescription EffectQueryDescription = new MiniArch.QueryDescription()
        .With<Effect>()
        .With<EffectId>()
        .With<Validated>()
        .Without<Rejected>();

    public EffectDispatchSystem(EffectTable effectTable)
    {
        _effectTable = effectTable ?? throw new ArgumentNullException(nameof(effectTable));
    }

    public void Execute(in FrameContext context)
    {
        CoreCommandBuffer commands = context.Commands;

        foreach (var chunk in context.Frame.ChunkQuery(EffectQueryDescription).GetChunks())
        {
            var effectIds = chunk.GetSpan<EffectId>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                EffectId effectId = effectIds[i];

                if (!_effectTable.TryGet(effectId, out EffectHandler handler))
                {
                    throw new InvalidOperationException(
                        $"No effect handler is registered for effect '{effectId.Value}'.");
                }

                if (handler is not null)
                {
                    handler(context, entities[i]);
                }

                commands.Destroy(entities[i]);
            }
        }
    }
}
