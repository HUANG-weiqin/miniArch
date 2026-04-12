namespace MiniArch.Core;

/// <summary>
/// Compiled frame commands.
/// </summary>
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

    /// <summary>
    /// Gets created entities.
    /// </summary>
    public IReadOnlyList<FrameCreatedEntity> CreatedEntities => _state?.CreatedEntities ?? EmptyCreates;

    /// <summary>
    /// Gets link commands.
    /// </summary>
    public IReadOnlyList<FrameLinkCommand> LinkCommands => _state?.LinkCommands ?? EmptyLinks;

    /// <summary>
    /// Gets unlink commands.
    /// </summary>
    public IReadOnlyList<FrameUnlinkCommand> UnlinkCommands => _state?.UnlinkCommands ?? EmptyUnlinks;

    /// <summary>
    /// Gets add commands.
    /// </summary>
    public IReadOnlyList<FrameEntityComponentCommand> AddCommands => _state?.AddCommands ?? EmptyComponentCommands;

    /// <summary>
    /// Gets set commands.
    /// </summary>
    public IReadOnlyList<FrameEntityComponentCommand> SetCommands => _state?.SetCommands ?? EmptyComponentCommands;

    /// <summary>
    /// Gets remove commands.
    /// </summary>
    public IReadOnlyList<FrameEntityRemoveCommand> RemoveCommands => _state?.RemoveCommands ?? EmptyRemoves;

    /// <summary>
    /// Gets destroyed entities.
    /// </summary>
    public IReadOnlyList<Entity> DestroyedEntities => _state?.DestroyedEntities ?? EmptyEntities;

    /// <summary>
    /// Gets released entities.
    /// </summary>
    public IReadOnlyList<Entity> ReleasedEntities => _state?.ReleasedEntities ?? EmptyEntities;

    internal IReadOnlyList<Entity> ReservedEntities => _state?.ReservedEntities ?? EmptyEntities;

    internal FrameCommandsState State => _state ?? throw new InvalidOperationException("Frame commands are not initialized.");
}

    /// <summary>
    /// Compiled reverse frame commands.
    /// </summary>
    public readonly struct ReverseFrameCommands
{
    private static readonly IReadOnlyList<ReverseFrameEntity> EmptyRestoredEntities = Array.Empty<ReverseFrameEntity>();
    private static readonly IReadOnlyList<Entity> EmptyEntities = Array.Empty<Entity>();
    private static readonly IReadOnlyList<FrameLinkCommand> EmptyLinks = Array.Empty<FrameLinkCommand>();
    private static readonly IReadOnlyList<FrameUnlinkCommand> EmptyUnlinks = Array.Empty<FrameUnlinkCommand>();
    private static readonly IReadOnlyList<FrameEntityComponentCommand> EmptyComponentCommands = Array.Empty<FrameEntityComponentCommand>();
    private static readonly IReadOnlyList<FrameEntityRemoveCommand> EmptyRemoves = Array.Empty<FrameEntityRemoveCommand>();

    private readonly ReverseFrameCommandsState? _state;

    internal ReverseFrameCommands(ReverseFrameCommandsState state)
    {
        _state = state;
    }

    /// <summary>
    /// Gets restored entities.
    /// </summary>
    public IReadOnlyList<ReverseFrameEntity> RestoredEntities => _state?.RestoredEntities ?? EmptyRestoredEntities;

    /// <summary>
    /// Gets destroyed entities.
    /// </summary>
    public IReadOnlyList<Entity> DestroyedEntities => _state?.DestroyedEntities ?? EmptyEntities;

    /// <summary>
    /// Gets link commands.
    /// </summary>
    public IReadOnlyList<FrameLinkCommand> LinkCommands => _state?.LinkCommands ?? EmptyLinks;

    /// <summary>
    /// Gets unlink commands.
    /// </summary>
    public IReadOnlyList<FrameUnlinkCommand> UnlinkCommands => _state?.UnlinkCommands ?? EmptyUnlinks;

    /// <summary>
    /// Gets add commands.
    /// </summary>
    public IReadOnlyList<FrameEntityComponentCommand> AddCommands => _state?.AddCommands ?? EmptyComponentCommands;

    /// <summary>
    /// Gets set commands.
    /// </summary>
    public IReadOnlyList<FrameEntityComponentCommand> SetCommands => _state?.SetCommands ?? EmptyComponentCommands;

    /// <summary>
    /// Gets remove commands.
    /// </summary>
    public IReadOnlyList<FrameEntityRemoveCommand> RemoveCommands => _state?.RemoveCommands ?? EmptyRemoves;

    internal IReadOnlyList<Entity> ReservedEntities => _state?.ReservedEntities ?? EmptyEntities;

    internal ReverseFrameCommandsState State => _state ?? throw new InvalidOperationException("Reverse frame commands are not initialized.");
}

/// <summary>
/// Created entity payload.
/// </summary>
/// <param name="Entity">The entity handle.</param>
/// <param name="Components">The created components.</param>
public readonly record struct FrameCreatedEntity(Entity Entity, IReadOnlyList<FrameComponentValue> Components);

/// <summary>
/// Component payload value.
/// </summary>
/// <param name="ComponentType">The component type.</param>
/// <param name="Value">The component value.</param>
public readonly record struct FrameComponentValue(Type ComponentType, object? Value);

/// <summary>
/// Restored entity payload.
/// </summary>
/// <param name="Entity">The entity handle.</param>
/// <param name="Components">The restored components.</param>
/// <param name="Parent">The restored parent.</param>
public readonly record struct ReverseFrameEntity(Entity Entity, IReadOnlyList<FrameComponentValue> Components, Entity Parent);

/// <summary>
/// Link command.
/// </summary>
/// <param name="Parent">The parent entity.</param>
/// <param name="Child">The child entity.</param>
public readonly record struct FrameLinkCommand(Entity Parent, Entity Child);

/// <summary>
/// Unlink command.
/// </summary>
/// <param name="Child">The child entity.</param>
public readonly record struct FrameUnlinkCommand(Entity Child);

/// <summary>
/// Entity component command.
/// </summary>
/// <param name="Entity">The entity handle.</param>
/// <param name="ComponentType">The component type.</param>
/// <param name="Value">The component value.</param>
public readonly record struct FrameEntityComponentCommand(Entity Entity, Type ComponentType, object? Value);

/// <summary>
/// Entity remove command.
/// </summary>
/// <param name="Entity">The entity handle.</param>
/// <param name="ComponentType">The component type.</param>
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

internal sealed class ReverseFrameCommandsState
{
    public ReverseFrameCommandsState(
        ReverseFrameEntity[] restoredEntities,
        Entity[] destroyedEntities,
        FrameLinkCommand[] linkCommands,
        FrameUnlinkCommand[] unlinkCommands,
        FrameEntityComponentCommand[] addCommands,
        FrameEntityComponentCommand[] setCommands,
        FrameEntityRemoveCommand[] removeCommands,
        Entity[] reservedEntities)
    {
        RestoredEntities = restoredEntities;
        DestroyedEntities = destroyedEntities;
        LinkCommands = linkCommands;
        UnlinkCommands = unlinkCommands;
        AddCommands = addCommands;
        SetCommands = setCommands;
        RemoveCommands = removeCommands;
        ReservedEntities = reservedEntities;
    }

    public ReverseFrameEntity[] RestoredEntities { get; }

    public Entity[] DestroyedEntities { get; }

    public FrameLinkCommand[] LinkCommands { get; }

    public FrameUnlinkCommand[] UnlinkCommands { get; }

    public FrameEntityComponentCommand[] AddCommands { get; }

    public FrameEntityComponentCommand[] SetCommands { get; }

    public FrameEntityRemoveCommand[] RemoveCommands { get; }

    public Entity[] ReservedEntities { get; }
}

internal readonly record struct RecordedHierarchyCommand(Entity Child, Entity Parent, bool IsLink);

internal readonly record struct RecordedComponentCommand(Entity Entity, Type ComponentType, object? Value);

internal readonly record struct RecordedRemoveCommand(Entity Entity, Type ComponentType);
