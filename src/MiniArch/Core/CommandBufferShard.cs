namespace MiniArch.Core;

internal sealed class CommandBufferShard
{
    public CommandBufferShard(int order)
    {
        Order = order;
    }

    public int Order { get; }

    public List<Entity> Creates { get; } = [];

    public List<RecordedHierarchyCommand> HierarchyCommands { get; } = [];

    public List<RecordedComponentCommand> Adds { get; } = [];

    public List<RecordedComponentCommand> Sets { get; } = [];

    public List<RecordedRemoveCommand> Removes { get; } = [];

    public List<Entity> Destroys { get; } = [];
}
