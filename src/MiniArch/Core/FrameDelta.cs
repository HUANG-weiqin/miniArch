namespace MiniArch.Core;

public sealed class FrameDelta
{
    public List<Entity> ReservedEntities { get; } = new(4);
    public List<RawCreatedEntity> CreatedEntities { get; } = new(4);
    public List<FrameLinkCommand> LinkCommands { get; } = new(4);
    public List<FrameUnlinkCommand> UnlinkCommands { get; } = new(4);
    public List<RawComponentCommand> AddCommands { get; } = new(4);
    public List<RawComponentCommand> SetCommands { get; } = new(4);
    public List<RawRemoveCommand> RemoveCommands { get; } = new(4);
    public List<Entity> DestroyedEntities { get; } = new(4);
    public List<Entity> ReleasedEntities { get; } = new(4);

    public void Clear()
    {
        ReservedEntities.Clear();
        CreatedEntities.Clear();
        LinkCommands.Clear();
        UnlinkCommands.Clear();
        AddCommands.Clear();
        SetCommands.Clear();
        RemoveCommands.Clear();
        DestroyedEntities.Clear();
        ReleasedEntities.Clear();
    }

    public bool IsEmpty =>
        ReservedEntities.Count == 0 &&
        CreatedEntities.Count == 0 &&
        LinkCommands.Count == 0 &&
        UnlinkCommands.Count == 0 &&
        AddCommands.Count == 0 &&
        SetCommands.Count == 0 &&
        RemoveCommands.Count == 0 &&
        DestroyedEntities.Count == 0 &&
        ReleasedEntities.Count == 0;
}

public readonly record struct RawComponentValue(
    int ComponentTypeId,
    Type RuntimeType,
    ComponentType ComponentType,
    int ComponentSize,
    byte[] Data,
    int DataOffset,
    int DataSize);

public readonly record struct RawCreatedEntity(Entity Entity, Signature? Signature, RawComponentValue[] Components);

public readonly record struct RawComponentCommand(
    Entity Entity,
    int ComponentTypeId,
    Type RuntimeType,
    ComponentType ComponentType,
    int DataOffset,
    int DataSize,
    ComponentWriterCache.ColumnWriterDelegate? ColumnWriter,
    byte[] Data);

public readonly record struct RawRemoveCommand(Entity Entity, int ComponentTypeId, Type RuntimeType, ComponentType ComponentType);

public readonly record struct FrameLinkCommand(Entity Parent, Entity Child);

public readonly record struct FrameUnlinkCommand(Entity Child);

internal readonly record struct RecordedHierarchyCommand(Entity Child, Entity Parent, bool IsLink);

internal readonly record struct RecordedRawCommand(Entity Entity, int ComponentTypeId, int DataOffset, int DataSize);

internal readonly record struct RecordedRemoveCommand(Entity Entity, int ComponentTypeId);
