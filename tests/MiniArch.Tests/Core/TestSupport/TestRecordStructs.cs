namespace MiniArch.Core;

// ── Test-only record structs (moved from production FrameDelta.cs) ────

internal readonly record struct RawCreatedEntity(Entity Entity, RawComponentValue[] Components);

internal readonly record struct RawComponentCommand(
    Entity Entity,
    ComponentType ComponentType,
    int DataOffset,
    int DataSize,
    byte[] Data);

internal readonly record struct RawRemoveCommand(Entity Entity, ComponentType ComponentType);

internal readonly record struct LinkCommand(Entity Parent, Entity Child);

internal readonly record struct UnlinkCommand(Entity Child);
