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

        foreach (var row in context.Frame.ChunkQuery(EffectQueryDescription).EachSpan<EffectId>())
        {
            EffectId effectId = row.Get0();

            if (!_effectTable.TryGet(effectId, out EffectHandler handler))
            {
                throw new InvalidOperationException(
                    $"No effect handler is registered for effect '{effectId.Value}'.");
            }

            if (handler is not null)
            {
                handler(context, row.Entity);
            }

            commands.Destroy(row.Entity);
        }
    }
}
