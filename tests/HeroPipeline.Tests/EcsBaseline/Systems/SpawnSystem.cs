using System;
using MiniArch.Core;
using CoreCommandBuffer = MiniArch.Core.CommandBuffer;

namespace Hero.Ecs;

public sealed class SpawnSystem : ISystem
{
    private readonly SpawnTable _spawnTable;
    private static readonly MiniArch.QueryDescription SpawnQueryDescription = new MiniArch.QueryDescription()
        .With<SpawnPending>()
        .With<SpawnKind>()
        .With<Validated>()
        .Without<Rejected>();

    public SpawnSystem(SpawnTable spawnTable)
    {
        _spawnTable = spawnTable ?? throw new ArgumentNullException(nameof(spawnTable));
    }

    public void Execute(in FrameContext context)
    {
        CoreCommandBuffer commands = context.Commands;

        foreach (var row in context.Frame.ChunkQuery(SpawnQueryDescription).EachSpan<SpawnKind>())
        {
            if (!_spawnTable.TryGet(row.Get0(), out SpawnHandler handler))
            {
                throw new InvalidOperationException(
                    $"No spawn handler is registered for spawn kind '{row.Get0().Value}'.");
            }

            handler(context, row.Entity);
            commands.Remove<SpawnPending>(row.Entity);
            commands.Remove<Validated>(row.Entity);
        }
    }
}
