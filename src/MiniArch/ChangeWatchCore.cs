using System.Runtime.CompilerServices;

namespace MiniArch;

internal interface IChangeWatchPolicy<TComponent, TValue, THandler>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
    where THandler : struct
{
    static abstract TValue Project(ref THandler handler, in TComponent component);
    static abstract void OnChange(ref THandler handler, World world, Entity entity, in TValue oldValue, in TValue newValue);
}

internal struct ChangeWatchState<TValue> where TValue : unmanaged, IEquatable<TValue>
{
    internal readonly QueryDescription Query;
    internal TValue[] OldValues;
    internal int[] BaselineVersions;
    internal int[] TouchedIds;
    internal int TouchedCount;
    internal Entry[] Buffer;
    internal bool HasSnapshot;
    internal bool OperationInProgress;

    internal struct Entry
    {
        internal Entity Entity;
        internal TValue OldValue;
        internal TValue NewValue;
    }

    internal ChangeWatchState(QueryDescription query)
    {
        Query = query;
        OldValues = [];
        BaselineVersions = [];
        TouchedIds = [];
        Buffer = [];
    }
}

internal static class ChangeWatchCore
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Snapshot<TComponent, TValue, THandler, TPolicy>(
        World world, ref ChangeWatchState<TValue> state, ref THandler handler)
        where TComponent : unmanaged
        where TValue : unmanaged, IEquatable<TValue>
        where THandler : struct
        where TPolicy : struct, IChangeWatchPolicy<TComponent, TValue, THandler>
    {
        ArgumentNullException.ThrowIfNull(world);
        BeginOperation(ref state);
        state.HasSnapshot = false;

        try
        {
            for (var i = 0; i < state.TouchedCount; i++)
            {
                var id = state.TouchedIds[i];
                if ((uint)id < (uint)state.OldValues.Length)
                {
                    state.OldValues[id] = default;
                    state.BaselineVersions[id] = 0;
                }
            }
            state.TouchedCount = 0;

            foreach (var chunk in world.Query(in state.Query).GetChunks())
            {
                var components = chunk.GetSpan<TComponent>();
                var entities = chunk.GetEntities();
                for (var i = 0; i < chunk.Count; i++)
                {
                    var entityId = entities[i].Id;
                    if ((uint)entityId >= (uint)state.OldValues.Length)
                    {
                        var newLength = Math.Max(entityId + 1, state.OldValues.Length * 2);
                        Array.Resize(ref state.OldValues, newLength);
                        Array.Resize(ref state.BaselineVersions, newLength);
                    }

                    state.OldValues[entityId] = TPolicy.Project(ref handler, in components[i]);
                    state.BaselineVersions[entityId] = entities[i].Version;

                    if (state.TouchedCount >= state.TouchedIds.Length)
                        Array.Resize(ref state.TouchedIds, Math.Max(state.TouchedCount + 1, state.TouchedIds.Length * 2));
                    state.TouchedIds[state.TouchedCount++] = entityId;
                }
            }

            state.HasSnapshot = true;
        }
        finally
        {
            state.OperationInProgress = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Diff<TComponent, TValue, THandler, TPolicy>(
        World world, ref ChangeWatchState<TValue> state, ref THandler handler)
        where TComponent : unmanaged
        where TValue : unmanaged, IEquatable<TValue>
        where THandler : struct
        where TPolicy : struct, IChangeWatchPolicy<TComponent, TValue, THandler>
    {
        ArgumentNullException.ThrowIfNull(world);
        BeginOperation(ref state);

        try
        {
            if (!state.HasSnapshot)
                throw new InvalidOperationException(
                    "ChangeWatch.Diff requires a prior Snapshot call. Call Snapshot(World) before Diff.");

            // Collect before callbacks so handlers can mutate the world without invalidating the scan.
            var bufferCount = 0;
            foreach (var chunk in world.Query(in state.Query).GetChunks())
            {
                var components = chunk.GetSpan<TComponent>();
                var entities = chunk.GetEntities();
                for (var i = 0; i < chunk.Count; i++)
                {
                    var entity = entities[i];
                    var entityId = entity.Id;
                    var hasBaseline = (uint)entityId < (uint)state.OldValues.Length &&
                        state.BaselineVersions[entityId] == entity.Version;
                    var oldValue = hasBaseline ? state.OldValues[entityId] : default;
                    var newValue = TPolicy.Project(ref handler, in components[i]);

                    if (oldValue.Equals(newValue))
                        continue;

                    if (bufferCount >= state.Buffer.Length)
                        Array.Resize(ref state.Buffer, Math.Max(bufferCount + 1, state.Buffer.Length * 2));

                    state.Buffer[bufferCount] = new ChangeWatchState<TValue>.Entry
                    {
                        Entity = entity,
                        OldValue = oldValue,
                        NewValue = newValue
                    };
                    bufferCount++;
                }
            }

            for (var i = 0; i < bufferCount; i++)
            {
                ref var entry = ref state.Buffer[i];
                TPolicy.OnChange(ref handler, world, entry.Entity, in entry.OldValue, in entry.NewValue);
            }
        }
        finally
        {
            state.OperationInProgress = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BeginOperation<TValue>(ref ChangeWatchState<TValue> state)
        where TValue : unmanaged, IEquatable<TValue>
    {
        if (state.OperationInProgress)
            throw new InvalidOperationException(
                "ChangeWatch does not support nested Snapshot or Diff calls on the same watch.");

        state.OperationInProgress = true;
    }
}
