using System;
using MiniArch.Core;
using CoreCommandBuffer = MiniArch.Core.CommandStream;

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

        foreach (var chunk in context.Frame.ChunkQuery(SpawnQueryDescription).GetChunks())
        {
            var kinds = chunk.GetSpan<SpawnKind>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (!_spawnTable.TryGet(kinds[i], out SpawnHandler handler))
                {
                    throw new InvalidOperationException(
                        $"No spawn handler is registered for spawn kind '{kinds[i].Value}'.");
                }

                handler(context, entities[i]);
                commands.Remove<SpawnPending>(entities[i]);
                commands.Remove<Validated>(entities[i]);
            }
        }
    }
}
