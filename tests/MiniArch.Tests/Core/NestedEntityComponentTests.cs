using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MiniArch.Core;
using MiniQueryCache = MiniArch.Core.QueryCache;

namespace MiniArchTests.Core;

internal readonly record struct NestedEntityTarget(Entity Target);
internal readonly record struct NestedEntityOuter(int X, NestedEntityTarget Nested);
internal readonly record struct NestedItemLocation(Entity Target);
internal readonly record struct NestedSlotLike<T>(T Value) where T : unmanaged;

[InlineArray(3)]
internal struct EntityReferenceInlineArray
{
    public Entity _element0;
}

[InlineArray(2)]
internal struct RecordEntityInlineArray
{
    public NestedEntityTarget _element0;
}

internal readonly record struct InlineEntityComponent(EntityReferenceInlineArray Values);
internal readonly record struct InlineRecordComponent(RecordEntityInlineArray Values);
internal readonly record struct NestedState(RecordEntityInlineArray Values);
internal readonly record struct DeepNestedEntityComponent(NestedState State);
internal readonly record struct NestedMarker(int Value);

[StructLayout(LayoutKind.Sequential)]
internal struct SequentialNestedLinks
{
    public bool Enabled;
    public byte Kind;
    public NestedEntityTarget First;
    public NestedEntityTarget Second;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct ExplicitNestedLinks
{
    [FieldOffset(0)]
    public bool Enabled;

    [FieldOffset(1)]
    public byte Kind;

    [FieldOffset(8)]
    public NestedEntityTarget First;

    [FieldOffset(16)]
    public NestedEntityTarget Second;
}

internal readonly record struct MultiNestedEntityComponent(
    SequentialNestedLinks Sequential,
    ExplicitNestedLinks Explicit,
    Entity First,
    Entity Second);

[StructLayout(LayoutKind.Auto)]
internal struct AutoLayoutNestedEntity
{
    public Entity Target;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ComponentWithAutoLayoutNestedEntity
{
    public int X;
    public AutoLayoutNestedEntity Nested;
}

public sealed class NestedEntityComponentTests
{
    private static CommandStream MakeStream(World world) =>
        new CommandStream(world) { DeferredEntities = true };

    [Fact]
    public void Nested_record_real_entity_survives_add_set_submit()
    {
        using var world = new World();
        var target = world.CreateEmpty();
        var owner = world.CreateEmpty();
        var stream = new CommandStream(world);

        stream.Add(owner, new NestedEntityOuter(1, new NestedEntityTarget(target)));
        stream.Set(owner, new NestedEntityOuter(2, new NestedEntityTarget(target)));
        Assert.True(stream.Submit());

        Assert.True(world.IsAlive(target));
        Assert.True(world.TryGet(owner, out NestedEntityOuter value));
        Assert.Equal(2, value.X);
        Assert.Equal(target, value.Nested.Target);
    }

    [Fact]
    public void Nested_record_deferred_placeholder_resolves_on_submit()
    {
        using var world = new World();
        var stream = MakeStream(world);
        var target = stream.Create();
        stream.Add(target, new NestedMarker(17));
        var owner = stream.Create();
        stream.Add(owner, new NestedEntityOuter(3, new NestedEntityTarget(target)));
        stream.Add(owner, new NestedMarker(18));

        Assert.True(stream.Submit());

        var realTarget = FindMarkedEntity(world, 17);
        var realOwner = FindMarkedEntity(world, 18);
        Assert.True(world.TryGet(realOwner, out NestedEntityOuter value));
        Assert.Equal(3, value.X);
        Assert.Equal(realTarget, value.Nested.Target);
    }

    [Fact]
    public void SlotLike_nested_entity_resolves_on_submit()
    {
        using var world = new World();
        var stream = MakeStream(world);
        var target = stream.Create();
        stream.Add(target, new NestedMarker(21));
        var owner = stream.Create();
        stream.Add(owner, new NestedSlotLike<NestedItemLocation>(new NestedItemLocation(target)));
        stream.Add(owner, new NestedMarker(22));

        Assert.True(stream.Submit());

        var realTarget = FindMarkedEntity(world, 21);
        var realOwner = FindMarkedEntity(world, 22);
        Assert.True(world.TryGet(realOwner, out NestedSlotLike<NestedItemLocation> value));
        Assert.Equal(realTarget, value.Value.Target);
    }

    [Fact]
    public void InlineArray_entity_resolves_nonzero_placeholder_element()
    {
        using var world = new World();
        var stream = MakeStream(world);
        var target = stream.Create();
        stream.Add(target, new NestedMarker(31));
        var owner = stream.Create();
        var values = new EntityReferenceInlineArray();
        values[1] = target;
        stream.Add(owner, new InlineEntityComponent(values));
        stream.Add(owner, new NestedMarker(32));

        Assert.True(stream.Submit());

        var realTarget = FindMarkedEntity(world, 31);
        var realOwner = FindMarkedEntity(world, 32);
        Assert.True(world.TryGet(realOwner, out InlineEntityComponent value));
        Assert.Equal(realTarget, value.Values[1]);
    }

    [Fact]
    public void InlineArray_record_resolves_nonzero_nested_entity_element()
    {
        using var world = new World();
        var stream = MakeStream(world);
        var target = stream.Create();
        stream.Add(target, new NestedMarker(41));
        var owner = stream.Create();
        var values = new RecordEntityInlineArray();
        values[1] = new NestedEntityTarget(target);
        stream.Add(owner, new InlineRecordComponent(values));
        stream.Add(owner, new NestedMarker(42));

        Assert.True(stream.Submit());

        var realTarget = FindMarkedEntity(world, 41);
        var realOwner = FindMarkedEntity(world, 42);
        Assert.True(world.TryGet(realOwner, out InlineRecordComponent value));
        Assert.Equal(realTarget, value.Values[1].Target);
    }

    [Fact]
    public void Deep_nested_inline_array_record_resolves_on_submit()
    {
        using var world = new World();
        var stream = MakeStream(world);
        var target = stream.Create();
        stream.Add(target, new NestedMarker(51));
        var owner = stream.Create();
        var records = new RecordEntityInlineArray();
        records[1] = new NestedEntityTarget(target);
        stream.Add(owner, new DeepNestedEntityComponent(new NestedState(records)));
        stream.Add(owner, new NestedMarker(52));

        Assert.True(stream.Submit());

        var realTarget = FindMarkedEntity(world, 51);
        var realOwner = FindMarkedEntity(world, 52);
        Assert.True(world.TryGet(realOwner, out DeepNestedEntityComponent value));
        Assert.Equal(realTarget, value.State.Values[1].Target);
    }

    [Fact]
    public void Resolver_discovers_sequential_explicit_repeated_and_independent_entity_fields()
    {
        var componentType = Component<MultiNestedEntityComponent>.ComponentType;
        var offsets = EntityFieldResolver.GetOffsets(componentType);
        var size = Unsafe.SizeOf<MultiNestedEntityComponent>();

        Assert.Equal(6, offsets.Length);
        for (var i = 0; i < offsets.Length; i++)
        {
            Assert.InRange(offsets[i], 0, size - Unsafe.SizeOf<Entity>());
            if (i > 0)
                Assert.True(offsets[i - 1] < offsets[i], "Entity offsets must be strictly ascending.");
        }
        Assert.Equal(offsets.Length, offsets.ToArray().Distinct().Count());

        var inlineType = Component<InlineEntityComponent>.ComponentType;
        var inlineOffsets = EntityFieldResolver.GetOffsets(inlineType);
        var inlineSize = ComponentSizeCache.GetSize(typeof(InlineEntityComponent));
        Assert.Equal(Unsafe.SizeOf<InlineEntityComponent>(), inlineSize);
        Assert.Equal(3, inlineOffsets.Length);
        for (var i = 0; i < inlineOffsets.Length; i++)
        {
            Assert.Equal(i * Unsafe.SizeOf<Entity>(), inlineOffsets[i]);
            Assert.InRange(inlineOffsets[i], 0, inlineSize - Unsafe.SizeOf<Entity>());
        }

        var value = new MultiNestedEntityComponent(
            new SequentialNestedLinks
            {
                Enabled = true,
                Kind = 0xA5,
                First = new NestedEntityTarget(new Entity(-1, 0)),
                Second = new NestedEntityTarget(new Entity(-1, 1))
            },
            new ExplicitNestedLinks
            {
                Enabled = false,
                Kind = 0x5A,
                First = new NestedEntityTarget(new Entity(-1, 2)),
                Second = new NestedEntityTarget(new Entity(-1, 3))
            },
            new Entity(-1, 4),
            new Entity(-1, 5));
        var data = new byte[size];
        MemoryMarshal.Write(data, in value);
        var resolveMap = new Entity[6];
        for (var i = 0; i < resolveMap.Length; i++)
            resolveMap[i] = new Entity(i, 1);

        EntityFieldResolver.ResolveInPlace(data, componentType, resolveMap);
        var resolved = MemoryMarshal.Read<MultiNestedEntityComponent>(data);
        Assert.True(resolved.Sequential.Enabled);
        Assert.Equal(0xA5, resolved.Sequential.Kind);
        Assert.Equal(new Entity(0, 1), resolved.Sequential.First.Target);
        Assert.Equal(new Entity(1, 1), resolved.Sequential.Second.Target);
        Assert.False(resolved.Explicit.Enabled);
        Assert.Equal(0x5A, resolved.Explicit.Kind);
        Assert.Equal(new Entity(2, 1), resolved.Explicit.First.Target);
        Assert.Equal(new Entity(3, 1), resolved.Explicit.Second.Target);
        Assert.Equal(new Entity(4, 1), resolved.First);
        Assert.Equal(new Entity(5, 1), resolved.Second);
    }

    [Fact]
    public void Snapshot_validate_replay_converges_for_nested_slot_and_deep_inline_components()
    {
        using var source = new World();
        var stream = MakeStream(source);
        var target = stream.Create();
        stream.Add(target, new NestedMarker(61));
        var owner = stream.Create();
        stream.Add(owner, new NestedMarker(62));
        stream.Add(owner, new NestedEntityOuter(7, new NestedEntityTarget(target)));
        stream.Add(owner, new NestedSlotLike<NestedItemLocation>(new NestedItemLocation(target)));

        var entityValues = new EntityReferenceInlineArray();
        entityValues[1] = target;
        stream.Add(owner, new InlineEntityComponent(entityValues));

        var recordValues = new RecordEntityInlineArray();
        recordValues[1] = new NestedEntityTarget(target);
        stream.Add(owner, new DeepNestedEntityComponent(new NestedState(recordValues)));

        var delta = stream.Snapshot();
        delta.Validate();
        Assert.True(stream.Submit());

        using var replica = new World();
        new CommandStream(replica).Replay(delta);

        Assert.Equal(source.CanonicalChecksum(), replica.CanonicalChecksum());
        var sourceTarget = FindMarkedEntity(source, 61);
        var sourceOwner = FindMarkedEntity(source, 62);
        var replicaTarget = FindMarkedEntity(replica, 61);
        var replicaOwner = FindMarkedEntity(replica, 62);
        Assert.Equal(sourceTarget, source.Get<NestedEntityOuter>(sourceOwner).Nested.Target);
        Assert.Equal(replicaTarget, replica.Get<NestedEntityOuter>(replicaOwner).Nested.Target);
        Assert.Equal(sourceTarget, source.Get<NestedSlotLike<NestedItemLocation>>(sourceOwner).Value.Target);
        Assert.Equal(replicaTarget, replica.Get<NestedSlotLike<NestedItemLocation>>(replicaOwner).Value.Target);
        Assert.Equal(sourceTarget, source.Get<InlineEntityComponent>(sourceOwner).Values[1]);
        Assert.Equal(replicaTarget, replica.Get<InlineEntityComponent>(replicaOwner).Values[1]);
        Assert.Equal(sourceTarget, source.Get<DeepNestedEntityComponent>(sourceOwner).State.Values[1].Target);
        Assert.Equal(replicaTarget, replica.Get<DeepNestedEntityComponent>(replicaOwner).State.Values[1].Target);
    }

    [Fact]
    public void ParallelCommandStream_resolves_deep_inline_nonzero_placeholder()
    {
        using var world = new World();
        var stream = new ParallelCommandStream(world) { DeferredEntities = true };
        var target = stream.Create();
        stream.Add(target, new NestedMarker(71));
        var owner = stream.Create();
        var values = new RecordEntityInlineArray();
        values[1] = new NestedEntityTarget(target);
        stream.Add(owner, new DeepNestedEntityComponent(new NestedState(values)));
        stream.Add(owner, new NestedMarker(72));

        Assert.True(stream.Submit());

        var realTarget = FindMarkedEntity(world, 71);
        var realOwner = FindMarkedEntity(world, 72);
        Assert.Equal(realTarget, world.Get<DeepNestedEntityComponent>(realOwner).State.Values[1].Target);
    }

    [Fact]
    public void Unknown_placeholder_in_deep_inline_array_is_rejected_before_mutation()
    {
        using var world = new World();
        var before = world.CanonicalChecksum();
        var stream = MakeStream(world);
        var owner = stream.Create();
        var values = new RecordEntityInlineArray();
        values[1] = new NestedEntityTarget(new Entity(-1, 999));
        stream.Add(owner, new DeepNestedEntityComponent(new NestedState(values)));

        var ex = Assert.Throws<InvalidOperationException>(() => stream.Submit());
        Assert.Contains("unknown placeholder", ex.Message);
        Assert.Equal(before, world.CanonicalChecksum());
        Assert.Equal(new Entity(0, 1), world.CreateEmpty());
    }

    [Fact]
    public void Cancelled_placeholder_in_nested_record_is_rejected_before_mutation()
    {
        using var world = new World();
        var before = world.CanonicalChecksum();
        var stream = MakeStream(world);
        var target = stream.Create();
        stream.Destroy(target);
        var owner = stream.Create();
        stream.Add(owner, new NestedEntityOuter(9, new NestedEntityTarget(target)));

        var ex = Assert.Throws<InvalidOperationException>(() => stream.Submit());
        Assert.Contains("cancelled or unknown placeholder", ex.Message);
        Assert.Equal(before, world.CanonicalChecksum());
        Assert.Equal(new Entity(0, 1), world.CreateEmpty());
    }

    [Fact]
    public void Resolver_rejects_auto_layout_nested_entity_component()
    {
        var componentType = Component<ComponentWithAutoLayoutNestedEntity>.ComponentType;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntityFieldResolver.GetOffsets(componentType));
        Assert.Contains("LayoutKind.Auto", ex.Message);
    }

    private static Entity FindMarkedEntity(World world, int marker)
    {
        var description = new QueryDescription().With<NestedMarker>();
        foreach (var chunk in world.Query(in description).GetChunks())
        {
            var entities = chunk.GetEntities();
            var markers = chunk.GetSpan<NestedMarker>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (markers[i].Value == marker)
                    return entities[i];
            }
        }

        Assert.Fail($"No entity with NestedMarker({marker}) was found.");
        return default;
    }
}
