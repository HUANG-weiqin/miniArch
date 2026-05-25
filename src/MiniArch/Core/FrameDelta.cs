using System.Runtime.InteropServices;

namespace MiniArch.Core;

public sealed class FrameDelta
{
    public List<Entity> ReservedEntities { get; } = new(4);
    public List<RawCreatedEntity> CreatedEntities { get; } = new(4);
    public List<LinkCommand> LinkCommands { get; } = new(4);
    public List<UnlinkCommand> UnlinkCommands { get; } = new(4);
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

    public int DeltaCount =>
        ReservedEntities.Count +
        CreatedEntities.Count +
        LinkCommands.Count +
        UnlinkCommands.Count +
        AddCommands.Count +
        SetCommands.Count +
        RemoveCommands.Count +
        DestroyedEntities.Count +
        ReleasedEntities.Count;

    public bool IsEmpty => DeltaCount == 0;

    public bool HasEntity(Entity entity)
    {
        for (var i = 0; i < ReservedEntities.Count; i++)
            if (ReservedEntities[i].Equals(entity)) return true;
        for (var i = 0; i < CreatedEntities.Count; i++)
            if (CreatedEntities[i].Entity.Equals(entity)) return true;
        for (var i = 0; i < AddCommands.Count; i++)
            if (AddCommands[i].Entity.Equals(entity)) return true;
        for (var i = 0; i < SetCommands.Count; i++)
            if (SetCommands[i].Entity.Equals(entity)) return true;
        for (var i = 0; i < RemoveCommands.Count; i++)
            if (RemoveCommands[i].Entity.Equals(entity)) return true;
        for (var i = 0; i < DestroyedEntities.Count; i++)
            if (DestroyedEntities[i].Equals(entity)) return true;
        for (var i = 0; i < ReleasedEntities.Count; i++)
            if (ReleasedEntities[i].Equals(entity)) return true;
        for (var i = 0; i < LinkCommands.Count; i++)
            if (LinkCommands[i].Child.Equals(entity)) return true;
        for (var i = 0; i < UnlinkCommands.Count; i++)
            if (UnlinkCommands[i].Child.Equals(entity)) return true;
        return false;
    }

    public static FrameDelta Merge(FrameDelta a, FrameDelta b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var entityStates = new Dictionary<Entity, SquashEntityState>();
        var appearanceOrder = new List<Entity>();

        ProcessCommandsInto(entityStates, appearanceOrder, a);
        ProcessCommandsInto(entityStates, appearanceOrder, b);

        var result = new FrameDelta();
        RebuildFromState(result, entityStates, appearanceOrder);
        return result;
    }

    private static void ProcessCommandsInto(
        Dictionary<Entity, SquashEntityState> entityStates,
        List<Entity> appearanceOrder,
        FrameDelta source)
    {
        void Ensure(Entity e)
        {
            ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(entityStates, e, out bool exists);
            if (!exists)
            {
                state = new SquashEntityState();
                appearanceOrder.Add(e);
            }
        }

        foreach (var e in source.ReservedEntities)
        {
            Ensure(e);
            entityStates[e].IsReserved = true;
        }

        foreach (var e in source.ReleasedEntities)
        {
            Ensure(e);
            entityStates[e].IsReleased = true;
        }

        foreach (var ce in source.CreatedEntities)
        {
            Ensure(ce.Entity);
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(entityStates, ce.Entity);
            state.IsCreated = true;
            state.CreatedEntity = ce;
            if (ce.Components is { Length: > 0 })
            {
                state.CreatedComponents ??= new Dictionary<int, RawComponentValue>(ce.Components.Length);
                foreach (var c in ce.Components)
                    state.CreatedComponents[c.ComponentTypeId] = c;
            }
        }

        foreach (var lc in source.LinkCommands)
        {
            Ensure(lc.Child);
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(entityStates, lc.Child);
            state.HasHierarchyChange = true;
            state.NetIsLinked = true;
            state.NetLinkParent = lc.Parent;
        }

        foreach (var uc in source.UnlinkCommands)
        {
            Ensure(uc.Child);
            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(entityStates, uc.Child);
            state.HasHierarchyChange = true;
            state.NetIsLinked = false;
        }

        foreach (var cmd in source.AddCommands)
        {
            Ensure(cmd.Entity);
            FoldComponent(ref CollectionsMarshal.GetValueRefOrNullRef(entityStates, cmd.Entity), ComponentNetKind.Add, cmd, default);
        }

        foreach (var cmd in source.SetCommands)
        {
            Ensure(cmd.Entity);
            FoldComponent(ref CollectionsMarshal.GetValueRefOrNullRef(entityStates, cmd.Entity), ComponentNetKind.Set, cmd, default);
        }

        foreach (var cmd in source.RemoveCommands)
        {
            Ensure(cmd.Entity);
            FoldComponent(ref CollectionsMarshal.GetValueRefOrNullRef(entityStates, cmd.Entity), ComponentNetKind.Remove, default, cmd);
        }

        foreach (var e in source.DestroyedEntities)
        {
            Ensure(e);
            entityStates[e].IsDestroyed = true;
        }
    }

    private static void RebuildFromState(FrameDelta target, Dictionary<Entity, SquashEntityState> entityStates, List<Entity> appearanceOrder)
    {
        target.Clear();

        foreach (var entity in appearanceOrder)
        {
            ref readonly var st = ref CollectionsMarshal.GetValueRefOrNullRef(entityStates, entity);

            if (st.IsCreated && st.IsDestroyed)
            {
                target.ReservedEntities.Add(entity);
                target.ReleasedEntities.Add(entity);
                continue;
            }

            if (st.IsReserved && st.IsReleased && !st.IsCreated && !st.IsDestroyed)
                continue;

            if (st.IsCreated)
            {
                if (st.ComponentActions is not null)
                {
                    foreach (var (typeId, action) in st.ComponentActions)
                    {
                        ref var comps = ref st.CreatedComponents;
                        switch (action.Kind)
                        {
                            case ComponentNetKind.Add or ComponentNetKind.Set:
                                comps ??= new Dictionary<int, RawComponentValue>();
                                comps[typeId] = new RawComponentValue(
                                    action.Command.ComponentTypeId,
                                    action.Command.RuntimeType,
                                    action.Command.ComponentType,
                                    action.Command.Data,
                                    action.Command.DataOffset,
                                    action.Command.DataSize);
                                break;
                            case ComponentNetKind.Remove:
                                comps?.Remove(typeId);
                                break;
                        }
                    }
                }

                var compArray = st.CreatedComponents is { Count: > 0 }
                    ? System.Linq.Enumerable.ToArray(st.CreatedComponents.Values)
                    : [];

                target.CreatedEntities.Add(new RawCreatedEntity(entity, compArray));
            }
            else
            {
                if (st.IsReserved)
                    target.ReservedEntities.Add(entity);

                if (st.ComponentActions is not null)
                {
                    foreach (var (typeId, action) in st.ComponentActions)
                    {
                        switch (action.Kind)
                        {
                            case ComponentNetKind.Add:
                                target.AddCommands.Add(action.Command);
                                break;
                            case ComponentNetKind.Set:
                                target.SetCommands.Add(action.Command);
                                break;
                            case ComponentNetKind.Remove:
                                target.RemoveCommands.Add(action.RemoveCmd);
                                break;
                        }
                    }
                }
            }

            if (st.HasHierarchyChange)
            {
                if (st.NetIsLinked)
                    target.LinkCommands.Add(new LinkCommand(st.NetLinkParent, entity));
                else
                    target.UnlinkCommands.Add(new UnlinkCommand(entity));
            }

            if (st.IsDestroyed)
                target.DestroyedEntities.Add(entity);
            if (st.IsReleased)
                target.ReleasedEntities.Add(entity);
        }
    }

    private static void FoldComponent(ref SquashEntityState state, ComponentNetKind newKind, RawComponentCommand cmd, RawRemoveCommand removeCmd)
    {
        int typeId = newKind == ComponentNetKind.Remove ? removeCmd.ComponentTypeId : cmd.ComponentTypeId;
        state.ComponentActions ??= new Dictionary<int, ComponentNetAction>();

        ref var action = ref CollectionsMarshal.GetValueRefOrAddDefault(state.ComponentActions, typeId, out bool exists);
        if (!exists)
        {
            action.Kind = newKind;
            if (newKind == ComponentNetKind.Remove)
                action.RemoveCmd = removeCmd;
            else
                action.Command = cmd;
            return;
        }

        switch (action.Kind, newKind)
        {
            case (ComponentNetKind.Add, ComponentNetKind.Set):
                action.Command = cmd;
                action.Kind = ComponentNetKind.Add;
                break;
            case (ComponentNetKind.Add, ComponentNetKind.Remove):
                state.ComponentActions!.Remove(typeId);
                break;
            case (ComponentNetKind.Set, ComponentNetKind.Set):
                action.Command = cmd;
                break;
            case (ComponentNetKind.Set, ComponentNetKind.Remove):
                action.Kind = ComponentNetKind.Remove;
                action.RemoveCmd = removeCmd;
                break;
            case (ComponentNetKind.Remove, ComponentNetKind.Add):
                action.Kind = ComponentNetKind.Set;
                action.Command = cmd;
                break;
            case (ComponentNetKind.Remove, ComponentNetKind.Set):
                action.Kind = ComponentNetKind.Set;
                action.Command = cmd;
                break;
            default:
                action.Kind = newKind;
                if (newKind == ComponentNetKind.Remove)
                    action.RemoveCmd = removeCmd;
                else
                    action.Command = cmd;
                break;
        }
    }

    private enum ComponentNetKind : byte { None = 0, Add, Set, Remove }

    private struct ComponentNetAction
    {
        public ComponentNetKind Kind;
        public RawComponentCommand Command;
        public RawRemoveCommand RemoveCmd;
    }

    private class SquashEntityState
    {
        public bool IsReserved;
        public bool IsReleased;
        public bool IsCreated;
        public bool IsDestroyed;
        public bool HasHierarchyChange;
        public bool NetIsLinked;
        public Entity NetLinkParent;
        public RawCreatedEntity CreatedEntity;
        public Dictionary<int, RawComponentValue>? CreatedComponents;
        public Dictionary<int, ComponentNetAction>? ComponentActions;
    }
}

public readonly record struct RawComponentValue(
    int ComponentTypeId,
    Type RuntimeType,
    ComponentType ComponentType,
    byte[] Data,
    int DataOffset,
    int DataSize);

public readonly record struct RawCreatedEntity(Entity Entity, RawComponentValue[] Components);

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

public readonly record struct LinkCommand(Entity Parent, Entity Child);

public readonly record struct UnlinkCommand(Entity Child);

internal readonly record struct RecordedHierarchyCommand(Entity Child, Entity Parent, bool IsLink);

internal readonly record struct RecordedRawCommand(Entity Entity, int ComponentTypeId, int DataOffset, int DataSize);

internal readonly record struct RecordedRemoveCommand(Entity Entity, int ComponentTypeId);
