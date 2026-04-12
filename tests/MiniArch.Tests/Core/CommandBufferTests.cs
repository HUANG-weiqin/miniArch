using System.Runtime.ExceptionServices;
using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class CommandBufferTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Playback_returns_frame_without_mutating_world_and_frame_can_be_replayed_into_another_world()
    {
        var world = new World();
        var replica = new World();
        var buffer = new CommandBuffer(world);

        var entity = buffer.Create();
        buffer.Add(entity, new Position(1, 2));

        var frame = buffer.Playback();

        Assert.False(world.IsAlive(entity));
        Assert.Equal(0, world.Query<Position>().GetChunkSpan().Length);
        Assert.Single(frame.CreatedEntities);

        world.Replay(in frame);

        Assert.True(world.IsAlive(entity));
        Assert.Equal(1, world.Query<Position>().GetChunkSpan().Length);

        replica.Replay(in frame);
        Assert.True(replica.IsAlive(entity));
        Assert.Equal(1, replica.Query<Position>().GetChunkSpan().Length);
    }

    [Fact]
    public void Create_returns_real_entities_and_same_frame_linking_of_created_entities_replays()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var parent = buffer.Create();
        var child = buffer.Create();
        buffer.Add(child, new Position(4, 5));
        buffer.Link(parent, child);

        var frame = buffer.Playback();
        world.Replay(in frame);

        Assert.True(world.IsAlive(parent));
        Assert.True(world.IsAlive(child));
        Assert.True(world.TryGetParent(child, out var resolvedParent));
        Assert.Equal(parent, resolvedParent);
        Assert.Equal([child], world.GetChildren(parent));
    }

    [Fact]
    public void Fixed_bucket_order_overrides_recording_order_for_existing_entities()
    {
        var world = new World();
        var parent = world.Create();
        var existing = world.Create();
        var doomed = world.Create();
        var buffer = new CommandBuffer(world);

        buffer.Destroy(doomed);
        buffer.Set(existing, new Position(9, 9));
        buffer.Link(parent, existing);
        buffer.Add(existing, new Velocity(3, 4));
        buffer.Remove<Position>(existing);

        var frame = buffer.Playback();
        world.Replay(in frame);

        Assert.False(world.IsAlive(doomed));
        Assert.True(world.TryGetParent(existing, out var resolvedParent));
        Assert.Equal(parent, resolvedParent);

        Assert.True(world.TryGetLocation(existing, out var info));
        var positionId = world.Components.GetOrCreate<Position>();
        var velocityId = world.Components.GetOrCreate<Velocity>();
        Assert.DoesNotContain(positionId, info.Archetype.Signature);
        Assert.Contains(velocityId, info.Archetype.Signature);

        var chunk = info.Archetype.GetChunk(info.ChunkIndex);
        Assert.Equal(new Velocity(3, 4), chunk.GetComponent<Velocity>(velocityId, info.RowIndex));
    }

    [Fact]
    public void Created_entities_land_in_their_final_form_and_create_destroy_pairs_are_eliminated()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var survivor = buffer.Create();
        buffer.Add(survivor, new Position(1, 1));
        buffer.Set(survivor, new Position(7, 8));
        buffer.Remove<Position>(survivor);
        buffer.Set(survivor, new Health(10));

        var transient = buffer.Create();
        buffer.Add(transient, new Position(3, 3));
        buffer.Destroy(transient);

        var frame = buffer.Playback();

        Assert.Single(frame.CreatedEntities);
        Assert.Single(frame.ReleasedEntities);
        Assert.Equal(transient, frame.ReleasedEntities[0]);

        world.Replay(in frame);

        Assert.True(world.IsAlive(survivor));
        Assert.False(world.IsAlive(transient));
        Assert.True(world.TryGetLocation(survivor, out var info));
        Assert.DoesNotContain(world.Components.GetOrCreate<Position>(), info.Archetype.Signature);
        Assert.Contains(world.Components.GetOrCreate<Health>(), info.Archetype.Signature);
    }

    [Fact]
    public void Recycled_ids_are_reused_across_frames_but_not_inside_the_same_frame()
    {
        var world = new World();
        var existing = world.Create();

        var first = new CommandBuffer(world);
        first.Destroy(existing);
        var sameFrameCreated = first.Create();

        Assert.NotEqual(existing.Id, sameFrameCreated.Id);

        var firstFrame = first.Playback();
        world.Replay(in firstFrame);

        var second = new CommandBuffer(world);
        var recycled = second.Create();

        Assert.Equal(existing.Id, recycled.Id);
        Assert.NotEqual(existing.Version, recycled.Version);
    }

    [Fact]
    public void Concurrent_recording_matches_single_thread_reference_for_existing_entities_and_hierarchy()
    {
        const int entityCount = 32;
        var referenceWorld = new World();
        var concurrentWorld = new World();
        var referenceParents = CreateEntities(referenceWorld, entityCount);
        var concurrentParents = CreateEntities(concurrentWorld, entityCount);
        var referenceChildren = CreateEntities(referenceWorld, entityCount);
        var concurrentChildren = CreateEntities(concurrentWorld, entityCount);

        referenceWorld.Components.GetOrCreate<Position>();
        referenceWorld.Components.GetOrCreate<Velocity>();
        concurrentWorld.Components.GetOrCreate<Position>();
        concurrentWorld.Components.GetOrCreate<Velocity>();

        var reference = new CommandBuffer(referenceWorld);
        var concurrent = new CommandBuffer(concurrentWorld);

        for (var i = 0; i < entityCount; i++)
        {
            RecordReference(reference, referenceParents[i], referenceChildren[i], i);
        }

        Parallel.For(0, entityCount, i =>
        {
            RecordReference(concurrent, concurrentParents[i], concurrentChildren[i], i);
        });

        var referenceFrame = reference.Playback();
        var concurrentFrame = concurrent.Playback();
        referenceWorld.Replay(in referenceFrame);
        concurrentWorld.Replay(in concurrentFrame);

        var positionId = referenceWorld.Components.GetOrCreate<Position>();
        var velocityId = referenceWorld.Components.GetOrCreate<Velocity>();

        for (var i = 0; i < entityCount; i++)
        {
            Assert.True(referenceWorld.TryGetLocation(referenceChildren[i], out var referenceInfo));
            Assert.True(concurrentWorld.TryGetLocation(concurrentChildren[i], out var concurrentInfo));
            Assert.Equal(referenceInfo.Archetype.Signature.ToString(), concurrentInfo.Archetype.Signature.ToString());

            if (referenceInfo.Archetype.Signature.Contains(positionId))
            {
                var expected = referenceInfo.Archetype.GetChunk(referenceInfo.ChunkIndex).GetComponent<Position>(positionId, referenceInfo.RowIndex);
                var actual = concurrentInfo.Archetype.GetChunk(concurrentInfo.ChunkIndex).GetComponent<Position>(positionId, concurrentInfo.RowIndex);
                Assert.Equal(expected, actual);
            }

            if (referenceInfo.Archetype.Signature.Contains(velocityId))
            {
                var expected = referenceInfo.Archetype.GetChunk(referenceInfo.ChunkIndex).GetComponent<Velocity>(velocityId, referenceInfo.RowIndex);
                var actual = concurrentInfo.Archetype.GetChunk(concurrentInfo.ChunkIndex).GetComponent<Velocity>(velocityId, concurrentInfo.RowIndex);
                Assert.Equal(expected, actual);
            }

            var hasReferenceParent = referenceWorld.TryGetParent(referenceChildren[i], out var referenceParent);
            var hasConcurrentParent = concurrentWorld.TryGetParent(concurrentChildren[i], out var concurrentParent);
            Assert.Equal(hasReferenceParent, hasConcurrentParent);
            if (hasReferenceParent)
            {
                Assert.Equal(referenceParents[i].Id, referenceParent.Id);
                Assert.Equal(concurrentParents[i].Id, concurrentParent.Id);
            }
        }
    }

    [Fact]
    public void Frames_from_one_empty_world_can_be_replayed_into_another_empty_world_tick_by_tick()
    {
        var source = new World();
        var replica = new World();

        var tick1 = new CommandBuffer(source);
        var parent = tick1.Create();
        var child = tick1.Create();
        tick1.Add(parent, new Health(10));
        tick1.Add(child, new Position(1, 2));
        tick1.Link(parent, child);
        var frame1 = tick1.Playback();

        source.Replay(in frame1);
        replica.Replay(in frame1);
        AssertWorldStatesMatch(source, replica, parent, child);

        var tick2 = new CommandBuffer(source);
        tick2.Set(parent, new Health(20));
        tick2.Set(child, new Position(7, 8));
        tick2.Add(child, new Velocity(3, 4));
        var frame2 = tick2.Playback();

        source.Replay(in frame2);
        replica.Replay(in frame2);
        AssertWorldStatesMatch(source, replica, parent, child);

        var tick3 = new CommandBuffer(source);
        tick3.Destroy(child);
        var replacement = tick3.Create();
        tick3.Add(replacement, new Position(9, 9));
        var frame3 = tick3.Playback();

        source.Replay(in frame3);
        replica.Replay(in frame3);
        AssertWorldStatesMatch(source, replica, parent, child, replacement);
    }

    [Fact]
    public void Complex_frames_from_one_empty_world_can_be_replayed_into_another_empty_world_tick_by_tick()
    {
        var source = new World();
        var replica = new World();

        var tick1 = new CommandBuffer(source);
        var root = tick1.Create();
        var childA = tick1.Create();
        var childB = tick1.Create();
        var transient = tick1.Create();
        tick1.Add(root, new Health(100));
        tick1.Add(root, new Position(0, 0));
        tick1.Add(childA, new Position(1, 1));
        tick1.Add(childA, new Velocity(10, 11));
        tick1.Add(childB, new Position(2, 2));
        tick1.Add(childB, new Health(50));
        tick1.Add(transient, new Position(99, 99));
        tick1.Link(root, childA);
        tick1.Link(root, childB);
        tick1.Destroy(transient);
        var frame1 = tick1.Playback();

        source.Replay(in frame1);
        replica.Replay(in frame1);
        AssertWorldStatesMatch(source, replica, root, childA, childB, transient);

        var tick2 = new CommandBuffer(source);
        var sibling = tick2.Create();
        var branch = tick2.Create();
        tick2.Add(sibling, new Position(5, 5));
        tick2.Add(sibling, new Velocity(6, 6));
        tick2.Add(branch, new Health(33));
        tick2.Link(root, sibling);
        tick2.Link(childA, branch);
        tick2.Unlink(childB);
        tick2.Link(sibling, childB);
        tick2.Set(root, new Health(125));
        tick2.Remove<Position>(root);
        tick2.Set(childA, new Position(7, 8));
        tick2.Remove<Velocity>(childA);
        tick2.Set(childB, new Position(20, 30));
        tick2.Add(childB, new Velocity(40, 50));
        var frame2 = tick2.Playback();

        source.Replay(in frame2);
        replica.Replay(in frame2);
        AssertWorldStatesMatch(source, replica, root, childA, childB, sibling, branch, transient);

        var tick3 = new CommandBuffer(source);
        var recycledA = tick3.Create();
        var ephemeralParent = tick3.Create();
        var ephemeralChild = tick3.Create();
        tick3.Add(recycledA, new Position(100, 100));
        tick3.Add(recycledA, new Health(1));
        tick3.Link(root, recycledA);
        tick3.Add(ephemeralParent, new Position(200, 200));
        tick3.Add(ephemeralChild, new Position(300, 300));
        tick3.Link(ephemeralParent, ephemeralChild);
        tick3.Destroy(ephemeralChild);
        tick3.Destroy(ephemeralParent);
        tick3.Destroy(childA);
        tick3.Unlink(branch);
        tick3.Link(root, branch);
        var frame3 = tick3.Playback();

        source.Replay(in frame3);
        replica.Replay(in frame3);
        AssertWorldStatesMatch(source, replica, root, childA, childB, sibling, branch, recycledA, ephemeralParent, ephemeralChild, transient);

        var tick4 = new CommandBuffer(source);
        var recycledB = tick4.Create();
        tick4.Add(recycledB, new Position(400, 401));
        tick4.Add(recycledB, new Velocity(402, 403));
        tick4.Link(sibling, recycledB);
        tick4.Destroy(childB);
        tick4.Set(branch, new Health(88));
        tick4.Add(branch, new Position(55, 56));
        tick4.Set(recycledA, new Health(99));
        tick4.Remove<Position>(recycledA);
        tick4.Unlink(recycledA);
        tick4.Link(branch, recycledA);
        var frame4 = tick4.Playback();

        source.Replay(in frame4);
        replica.Replay(in frame4);
        AssertWorldStatesMatch(source, replica, root, childA, childB, sibling, branch, recycledA, recycledB, ephemeralParent, ephemeralChild, transient);

        var tick5 = new CommandBuffer(source);
        var createdThenDestroyed = tick5.Create();
        tick5.Add(createdThenDestroyed, new Position(500, 500));
        tick5.Add(createdThenDestroyed, new Velocity(501, 501));
        tick5.Destroy(createdThenDestroyed);
        tick5.Set(root, new Health(150));
        tick5.Add(root, new Velocity(1, 2));
        tick5.Set(sibling, new Position(60, 61));
        tick5.Remove<Velocity>(sibling);
        tick5.Destroy(branch);
        tick5.Destroy(recycledA);
        tick5.Unlink(recycledB);
        tick5.Link(root, recycledB);
        var frame5 = tick5.Playback();

        source.Replay(in frame5);
        replica.Replay(in frame5);
        AssertWorldStatesMatch(source, replica, root, childA, childB, sibling, branch, recycledA, recycledB, createdThenDestroyed, ephemeralParent, ephemeralChild, transient);
    }

    [Fact]
    public void Randomized_frames_can_be_replayed_into_another_world_and_match_final_state()
    {
        const int frameCount = 300;
        const int maxCreatesPerFrame = 3;
        const int seed = 0x5A17;

        var source = new World();
        var replica = new World();
        var rng = new Random(seed);
        var knownEntities = new List<Entity>();

        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var buffer = new CommandBuffer(source);
            RecordRandomizedFrame(source, buffer, knownEntities, rng, frameIndex, maxCreatesPerFrame);

            var frame = buffer.Playback();
            source.Replay(in frame);
            replica.Replay(in frame);
        }

        AssertWorldStatesMatch(source, replica, knownEntities.ToArray());
        Assert.Equal(GetLiveLinks(source), GetLiveLinks(replica));
    }

    [Fact]
    public void Play_matches_playback_and_replay_for_randomized_frames()
    {
        RunOnDedicatedThread(() =>
        {
            const int frameCount = 300;
            const int maxCreatesPerFrame = 3;
            const int seed = 0x5A17;

            var playbackWorld = new World();
            var playWorld = new World();
            var playbackRng = new Random(seed);
            var playRng = new Random(seed);
            var playbackKnownEntities = new List<Entity>();
            var playKnownEntities = new List<Entity>();

            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                var playbackBuffer = new CommandBuffer(playbackWorld);
                RecordRandomizedFrame(playbackWorld, playbackBuffer, playbackKnownEntities, playbackRng, frameIndex, maxCreatesPerFrame);

                var playBuffer = new CommandBuffer(playWorld);
                RecordRandomizedFrame(playWorld, playBuffer, playKnownEntities, playRng, frameIndex, maxCreatesPerFrame);

                var frame = playbackBuffer.Playback();
                playbackWorld.Replay(in frame);
                playBuffer.Play();
            }

            AssertWorldStatesMatch(
                playbackWorld,
                playWorld,
                playbackKnownEntities.Concat(playKnownEntities).Distinct().ToArray());
            Assert.Equal(GetLiveLinks(playbackWorld), GetLiveLinks(playWorld));
        });
    }

    [Fact]
    public void Play_allocates_less_than_playback_plus_replay_for_the_same_script()
    {
        RunOnDedicatedThread(() =>
        {
            const int batchSize = 32;

            WarmupPlayAllocations();

            var playbackWorlds = new World[batchSize];
            var playbackBuffers = new CommandBuffer[batchSize];
            var playWorlds = new World[batchSize];
            var playBuffers = new CommandBuffer[batchSize];

            for (var i = 0; i < batchSize; i++)
            {
                playbackWorlds[i] = new World();
                var playbackExisting = CreateEntities(playbackWorlds[i], 32);
                playbackBuffers[i] = new CommandBuffer(playbackWorlds[i]);
                RecordPlayScenario(playbackBuffers[i], playbackExisting);

                playWorlds[i] = new World();
                var playExisting = CreateEntities(playWorlds[i], 32);
                playBuffers[i] = new CommandBuffer(playWorlds[i]);
                RecordPlayScenario(playBuffers[i], playExisting);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var playbackReplayAllocatedBytes = MeasurePlaybackReplayAllocatedBytes(playbackWorlds, playbackBuffers);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var playAllocatedBytes = MeasurePlayAllocatedBytes(playBuffers);

            Assert.True(playAllocatedBytes < playbackReplayAllocatedBytes, $"Play allocated {playAllocatedBytes} bytes, but Playback()+Replay() allocated {playbackReplayAllocatedBytes} bytes.");
        });
    }

    [Fact]
    public void Play_consumes_the_buffer()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);
        var entity = buffer.Create();
        buffer.Add(entity, new Position(1, 2));

        buffer.Play();

        Assert.Throws<InvalidOperationException>(() => buffer.Play());
        Assert.Throws<InvalidOperationException>(() => buffer.Playback());
    }

    private static Entity[] CreateEntities(World world, int count)
    {
        var entities = new Entity[count];
        for (var i = 0; i < count; i++)
        {
            entities[i] = world.Create();
        }

        return entities;
    }

    private static void RecordReference(CommandBuffer buffer, Entity parent, Entity child, int index)
    {
        buffer.Link(parent, child);
        buffer.Add(child, new Position(index, index + 1));
        buffer.Set(child, new Position(index + 10, index + 20));
        buffer.Add(child, new Velocity(index + 30, index + 40));

        if ((index & 1) == 0)
        {
            buffer.Remove<Position>(child);
        }

        if ((index & 3) == 0)
        {
            buffer.Unlink(child);
        }
    }

    private static void RecordRandomizedFrame(World world, CommandBuffer buffer, List<Entity> knownEntities, Random rng, int frameIndex, int maxCreatesPerFrame)
    {
        var createdThisFrame = new List<Entity>();
        var pendingParents = new Dictionary<Entity, Entity?>();
        var operations = 8 + rng.Next(8);

        for (var createIndex = 0; createIndex < maxCreatesPerFrame; createIndex++)
        {
            if (rng.NextDouble() >= 0.45d)
            {
                continue;
            }

            var entity = buffer.Create();
            createdThisFrame.Add(entity);
            knownEntities.Add(entity);

            if (rng.NextDouble() < 0.70d)
            {
                buffer.Add(entity, new Position(frameIndex * 10 + createIndex, frameIndex + createIndex));
            }

            if (rng.NextDouble() < 0.45d)
            {
                buffer.Add(entity, new Velocity(frameIndex + createIndex, frameIndex * 2 + createIndex));
            }

            if (rng.NextDouble() < 0.40d)
            {
                buffer.Add(entity, new Health(frameIndex + 100 + createIndex));
            }
        }

        for (var operationIndex = 0; operationIndex < operations; operationIndex++)
        {
            var living = knownEntities.Where(world.IsAlive).ToArray();
            var candidates = living.Concat(createdThisFrame).Distinct().ToArray();
            if (candidates.Length == 0)
            {
                continue;
            }

            var roll = rng.Next(12);
            switch (roll)
            {
                case 0:
                case 1:
                {
                    var entity = candidates[rng.Next(candidates.Length)];
                    buffer.Add(entity, new Position(rng.Next(1000), rng.Next(1000)));
                    break;
                }
                case 2:
                {
                    var entity = candidates[rng.Next(candidates.Length)];
                    buffer.Add(entity, new Velocity(rng.Next(1000), rng.Next(1000)));
                    break;
                }
                case 3:
                {
                    var entity = candidates[rng.Next(candidates.Length)];
                    buffer.Add(entity, new Health(rng.Next(1, 500)));
                    break;
                }
                case 4:
                {
                    var entity = candidates[rng.Next(candidates.Length)];
                    buffer.Set(entity, new Position(rng.Next(1000), rng.Next(1000)));
                    break;
                }
                case 5:
                {
                    var entity = candidates[rng.Next(candidates.Length)];
                    buffer.Set(entity, new Velocity(rng.Next(1000), rng.Next(1000)));
                    break;
                }
                case 6:
                {
                    var entity = candidates[rng.Next(candidates.Length)];
                    buffer.Set(entity, new Health(rng.Next(1, 500)));
                    break;
                }
                case 7:
                {
                    var entity = candidates[rng.Next(candidates.Length)];
                    switch (rng.Next(3))
                    {
                        case 0:
                            buffer.Remove<Position>(entity);
                            break;
                        case 1:
                            buffer.Remove<Velocity>(entity);
                            break;
                        default:
                            buffer.Remove<Health>(entity);
                            break;
                    }

                    break;
                }
                case 8:
                {
                    if (candidates.Length < 2)
                    {
                        break;
                    }

                    var parent = candidates[rng.Next(candidates.Length)];
                    var child = candidates[rng.Next(candidates.Length)];
                    if (parent == child)
                    {
                        break;
                    }

                    if (CanScheduleLink(world, pendingParents, parent, child))
                    {
                        buffer.Link(parent, child);
                        pendingParents[child] = parent;
                    }

                    break;
                }
                case 9:
                {
                    var entity = candidates[rng.Next(candidates.Length)];
                    buffer.Unlink(entity);
                    pendingParents[entity] = null;
                    break;
                }
                case 10:
                case 11:
                {
                    var entity = candidates[rng.Next(candidates.Length)];
                    buffer.Destroy(entity);
                    break;
                }
            }
        }
    }

    private static long MeasurePlaybackReplayAllocatedBytes(World[] worlds, CommandBuffer[] buffers)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < buffers.Length; i++)
        {
            var frame = buffers[i].Playback();
            worlds[i].Replay(in frame);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long MeasurePlayAllocatedBytes(CommandBuffer[] buffers)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < buffers.Length; i++)
        {
            buffers[i].Play();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void WarmupPlayAllocations()
    {
        var playbackWorld = new World();
        var playbackBuffer = new CommandBuffer(playbackWorld);
        var playbackExisting = CreateEntities(playbackWorld, 8);
        RecordPlayScenario(playbackBuffer, playbackExisting);
        var frame = playbackBuffer.Playback();
        playbackWorld.Replay(in frame);

        var playWorld = new World();
        var playBuffer = new CommandBuffer(playWorld);
        var playExisting = CreateEntities(playWorld, 8);
        RecordPlayScenario(playBuffer, playExisting);
        playBuffer.Play();
    }

    private static void RunOnDedicatedThread(Action action)
    {
        Exception? capturedException = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                capturedException = exception;
            }
        });

        thread.Start();
        thread.Join();

        if (capturedException is not null)
        {
            ExceptionDispatchInfo.Capture(capturedException).Throw();
        }
    }

    private static void AssertWorldStatesMatch(World expected, World actual, params Entity[] entities)
    {
        foreach (var entity in entities)
        {
            Assert.Equal(expected.IsAlive(entity), actual.IsAlive(entity));
            if (!expected.IsAlive(entity))
            {
                continue;
            }

            Assert.True(expected.TryGetLocation(entity, out var expectedInfo));
            Assert.True(actual.TryGetLocation(entity, out var actualInfo));
            Assert.Equal(expectedInfo.Archetype.Signature.ToString(), actualInfo.Archetype.Signature.ToString());

            Assert.Equal(expected.TryGetParent(entity, out var expectedParent), actual.TryGetParent(entity, out var actualParent));
            if (expected.TryGetParent(entity, out expectedParent))
            {
                Assert.Equal(expectedParent, actualParent);
            }

            CompareComponent<Position>(expected, actual, entity, expectedInfo);
            CompareComponent<Velocity>(expected, actual, entity, expectedInfo);
            CompareComponent<Health>(expected, actual, entity, expectedInfo);
        }
    }

    private static void CompareComponent<T>(World expected, World actual, Entity entity, EntityInfo expectedInfo)
    {
        var componentType = expected.Components.GetOrCreate<T>();
        if (!expectedInfo.Archetype.Signature.Contains(componentType))
        {
            return;
        }

        Assert.True(actual.TryGetLocation(entity, out var actualInfo));
        var expectedChunk = expectedInfo.Archetype.GetChunk(expectedInfo.ChunkIndex);
        var actualChunk = actualInfo.Archetype.GetChunk(actualInfo.ChunkIndex);
        Assert.Equal(
            expectedChunk.GetComponent<T>(componentType, expectedInfo.RowIndex),
            actualChunk.GetComponent<T>(actual.Components.GetOrCreate<T>(), actualInfo.RowIndex));
    }

    private static string GetLiveLinks(World world)
    {
        var links = new List<string>();
        for (var id = 0; id < 2048; id++)
        {
            var entity = FindLiveEntity(world, id);
            if (!entity.IsValid || !world.TryGetParent(entity, out var parent))
            {
                continue;
            }

            links.Add($"{parent.Id}:{parent.Version}->{entity.Id}:{entity.Version}");
        }

        links.Sort(StringComparer.Ordinal);
        return string.Join("|", links);
    }

    private static void RecordPlayScenario(CommandBuffer buffer, Entity root, Entity child, Entity transient)
    {
        buffer.Add(root, new Position(10, 20));
        buffer.Add(root, new Health(30));
        buffer.Add(child, new Position(40, 50));
        buffer.Add(child, new Velocity(60, 70));
        buffer.Link(root, child);
        buffer.Set(root, new Health(31));
        buffer.Remove<Position>(root);
        buffer.Set(child, new Position(41, 51));
        buffer.Unlink(child);
        buffer.Link(root, child);
        buffer.Add(transient, new Position(80, 90));
        buffer.Destroy(transient);
    }

    private static void RecordPlayScenario(CommandBuffer buffer, Entity[] existing)
    {
        var root = existing[0];
        var child = existing[1];
        var transient = existing[2];

        RecordPlayScenario(buffer, root, child, transient);

        for (var index = 3; index < existing.Length; index++)
        {
            var entity = existing[index];
            buffer.Add(entity, new Position(index * 10, index * 10 + 1));
            buffer.Set(entity, new Position(index * 10 + 2, index * 10 + 3));

            if ((index & 1) == 0)
            {
                buffer.Add(entity, new Velocity(index * 10 + 4, index * 10 + 5));
            }

            if ((index & 3) == 0)
            {
                buffer.Remove<Position>(entity);
            }

            if ((index & 7) == 0)
            {
                buffer.Link(root, entity);
            }
        }
    }

    private static bool CanScheduleLink(World world, Dictionary<Entity, Entity?> pendingParents, Entity parent, Entity child)
    {
        if (!world.IsAlive(parent) || !world.IsAlive(child) || parent == child)
        {
            return false;
        }

        var current = parent;
        var guard = 0;
        while (current.IsValid && guard++ < 2048)
        {
            if (current == child)
            {
                return false;
            }

            if (pendingParents.TryGetValue(current, out var pendingParent))
            {
                current = pendingParent ?? default;
                continue;
            }

            current = world.TryGetParent(current, out var actualParent) ? actualParent : default;
        }

        return true;
    }

    private static Entity FindLiveEntity(World world, int id)
    {
        for (var version = 1; version < 1024; version++)
        {
            var candidate = new Entity(id, version);
            if (world.IsAlive(candidate))
            {
                return candidate;
            }
        }

        return default;
    }
}
