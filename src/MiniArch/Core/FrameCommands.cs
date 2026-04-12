namespace MiniArch.Core;

public readonly struct FrameCommands
{
    private static readonly IReadOnlyList<FrameCreatedEntity> EmptyCreates = Array.Empty<FrameCreatedEntity>();
    private static readonly IReadOnlyList<FrameLinkCommand> EmptyLinks = Array.Empty<FrameLinkCommand>();
    private static readonly IReadOnlyList<FrameUnlinkCommand> EmptyUnlinks = Array.Empty<FrameUnlinkCommand>();
    private static readonly IReadOnlyList<FrameEntityComponentCommand> EmptyComponentCommands = Array.Empty<FrameEntityComponentCommand>();
    private static readonly IReadOnlyList<FrameEntityRemoveCommand> EmptyRemoves = Array.Empty<FrameEntityRemoveCommand>();
    private static readonly IReadOnlyList<Entity> EmptyEntities = Array.Empty<Entity>();

    private readonly FrameCommandsState? _state;

    internal FrameCommands(FrameCommandsState state)
    {
        _state = state;
    }

    public IReadOnlyList<FrameCreatedEntity> CreatedEntities => _state?.CreatedEntities ?? EmptyCreates;

    public IReadOnlyList<FrameLinkCommand> LinkCommands => _state?.LinkCommands ?? EmptyLinks;

    public IReadOnlyList<FrameUnlinkCommand> UnlinkCommands => _state?.UnlinkCommands ?? EmptyUnlinks;

    public IReadOnlyList<FrameEntityComponentCommand> AddCommands => _state?.AddCommands ?? EmptyComponentCommands;

    public IReadOnlyList<FrameEntityComponentCommand> SetCommands => _state?.SetCommands ?? EmptyComponentCommands;

    public IReadOnlyList<FrameEntityRemoveCommand> RemoveCommands => _state?.RemoveCommands ?? EmptyRemoves;

    public IReadOnlyList<Entity> DestroyedEntities => _state?.DestroyedEntities ?? EmptyEntities;

    public IReadOnlyList<Entity> ReleasedEntities => _state?.ReleasedEntities ?? EmptyEntities;

    internal IReadOnlyList<Entity> ReservedEntities => _state?.ReservedEntities ?? EmptyEntities;

    internal FrameCommandsState State => _state ?? throw new InvalidOperationException("Frame commands are not initialized.");
}

public readonly record struct FrameCreatedEntity(Entity Entity, IReadOnlyList<FrameComponentValue> Components);

public readonly record struct FrameComponentValue(Type ComponentType, object? Value);

public readonly record struct FrameLinkCommand(Entity Parent, Entity Child);

public readonly record struct FrameUnlinkCommand(Entity Child);

public readonly record struct FrameEntityComponentCommand(Entity Entity, Type ComponentType, object? Value);

public readonly record struct FrameEntityRemoveCommand(Entity Entity, Type ComponentType);

internal sealed class FrameCommandsState
{
    public FrameCommandsState(
        Entity[] reservedEntities,
        FrameCreatedEntity[] createdEntities,
        FrameLinkCommand[] linkCommands,
        FrameUnlinkCommand[] unlinkCommands,
        FrameEntityComponentCommand[] addCommands,
        FrameEntityComponentCommand[] setCommands,
        FrameEntityRemoveCommand[] removeCommands,
        Entity[] destroyedEntities,
        Entity[] releasedEntities)
    {
        ReservedEntities = reservedEntities;
        CreatedEntities = createdEntities;
        LinkCommands = linkCommands;
        UnlinkCommands = unlinkCommands;
        AddCommands = addCommands;
        SetCommands = setCommands;
        RemoveCommands = removeCommands;
        DestroyedEntities = destroyedEntities;
        ReleasedEntities = releasedEntities;
    }

    public Entity[] ReservedEntities { get; }

    public FrameCreatedEntity[] CreatedEntities { get; }

    public FrameLinkCommand[] LinkCommands { get; }

    public FrameUnlinkCommand[] UnlinkCommands { get; }

    public FrameEntityComponentCommand[] AddCommands { get; }

    public FrameEntityComponentCommand[] SetCommands { get; }

    public FrameEntityRemoveCommand[] RemoveCommands { get; }

    public Entity[] DestroyedEntities { get; }

    public Entity[] ReleasedEntities { get; }
}

internal readonly record struct RecordedHierarchyCommand(Entity Child, Entity Parent, bool IsLink);

internal readonly record struct RecordedComponentCommand(Entity Entity, Type ComponentType, object? Value);

internal readonly record struct RecordedRemoveCommand(Entity Entity, Type ComponentType);
