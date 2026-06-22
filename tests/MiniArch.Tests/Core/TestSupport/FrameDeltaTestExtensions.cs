using System.Runtime.CompilerServices;

using MiniArch.Core;

namespace MiniArch.Tests.Core.TestSupport;

/// <summary>
/// Test-only projection of a <see cref="FrameDelta"/> into the 9 typed lists
/// that earlier API versions exposed directly. Parsing is lazy and cached
/// per-instance via <see cref="ConditionalWeakTable{TKey,TValue}"/>, so the
/// production type stays unburdened.
/// </summary>
internal static class FrameDeltaTestExtensions
{
    private sealed class ParsedLists
    {
        public List<Entity> Reserved = new(16);
        public List<RawCreatedEntity> Created = new(16);
        public List<LinkCommand> Link = new(16);
        public List<UnlinkCommand> Unlink = new(16);
        public List<RawComponentCommand> Add = new(16);
        public List<RawComponentCommand> Set = new(16);
        public List<RawRemoveCommand> Remove = new(16);
        public List<Entity> Destroyed = new(16);
        public List<Entity> Released = new(16);
    }

    private static readonly ConditionalWeakTable<FrameDelta, ParsedLists> s_cache = new();

    private static ParsedLists Parse(FrameDelta delta) =>
        s_cache.GetValue(delta, static d => ParseCore(d));

    private static ParsedLists ParseCore(FrameDelta delta)
    {
        var p = new ParsedLists();
        var decoder = delta.GetDecoder();
        var buffer = delta.AsSpan().ToArray();

        while (decoder.MoveNext())
        {
            switch (decoder.Kind)
            {
                case DeltaOpKind.Reserve:
                    p.Reserved.Add(decoder.Entity);
                    break;
                case DeltaOpKind.Release:
                    p.Released.Add(decoder.Entity);
                    break;
                case DeltaOpKind.Create:
                {
                    var compCount = decoder.ReadVarint();
                    var comps = new RawComponentValue[compCount];
                    for (var i = 0; i < compCount; i++)
                    {
                        var type = new ComponentType(decoder.ReadVarint());
                        var dataSize = decoder.ReadVarint();
                        if (dataSize > 0)
                        {
                            var offset = decoder.CurrentPosition;
                            _ = decoder.ReadBytes(dataSize);
                            comps[i] = new RawComponentValue(type, buffer, offset, dataSize);
                        }
                        else
                        {
                            comps[i] = new RawComponentValue(type, Array.Empty<byte>(), 0, 0);
                        }
                    }
                    p.Created.Add(new RawCreatedEntity(decoder.Entity, comps));
                    break;
                }
                case DeltaOpKind.Link:
                {
                    var parent = decoder.ReadExtraEntity();
                    p.Link.Add(new LinkCommand(parent, decoder.Entity));
                    break;
                }
                case DeltaOpKind.Unlink:
                    p.Unlink.Add(new UnlinkCommand(decoder.Entity));
                    break;
                case DeltaOpKind.Add:
                {
                    var comp = decoder.ReadComponentType();
                    var size = decoder.ReadVarint();
                    var offset = decoder.CurrentPosition;
                    _ = decoder.ReadBytes(size);
                    p.Add.Add(new RawComponentCommand(decoder.Entity, comp, offset, size, buffer));
                    break;
                }
                case DeltaOpKind.Set:
                {
                    var comp = decoder.ReadComponentType();
                    var size = decoder.ReadVarint();
                    var offset = decoder.CurrentPosition;
                    _ = decoder.ReadBytes(size);
                    p.Set.Add(new RawComponentCommand(decoder.Entity, comp, offset, size, buffer));
                    break;
                }
                case DeltaOpKind.Remove:
                {
                    var comp = decoder.ReadComponentType();
                    p.Remove.Add(new RawRemoveCommand(decoder.Entity, comp));
                    break;
                }
                case DeltaOpKind.Destroy:
                    p.Destroyed.Add(decoder.Entity);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unhandled DeltaOpKind {decoder.Kind} in ParseCore.");
            }
        }

        return p;
    }

    public static List<Entity> ReservedEntities(this FrameDelta delta) => Parse(delta).Reserved;
    public static List<RawCreatedEntity> CreatedEntities(this FrameDelta delta) => Parse(delta).Created;
    public static List<LinkCommand> LinkCommands(this FrameDelta delta) => Parse(delta).Link;
    public static List<UnlinkCommand> UnlinkCommands(this FrameDelta delta) => Parse(delta).Unlink;
    public static List<RawComponentCommand> AddCommands(this FrameDelta delta) => Parse(delta).Add;
    public static List<RawComponentCommand> SetCommands(this FrameDelta delta) => Parse(delta).Set;
    public static List<RawRemoveCommand> RemoveCommands(this FrameDelta delta) => Parse(delta).Remove;
    public static List<Entity> DestroyedEntities(this FrameDelta delta) => Parse(delta).Destroyed;
    public static List<Entity> ReleasedEntities(this FrameDelta delta) => Parse(delta).Released;
}
