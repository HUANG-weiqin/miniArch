using System.Runtime.ExceptionServices;
using MiniArch.Core;
using MiniQuery = MiniArch.Core.Query;

namespace MiniArchTests.Core;

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
        Assert.Equal(0, CreateQuery<Position>(world).GetChunkSpan().Length);
        Assert.Single(frame.CreatedEntities);

        world.Replay(in frame);

        Assert.True(world.IsAlive(entity));
        Assert.Equal(1, CreateQuery<Position>(world).GetChunkSpan().Length);

        replica.Replay(in frame);
        Assert.True(replica.IsAlive(entity));
        Assert.Equal(1, CreateQuery<Position>(replica).GetChunkSpan().Length);
    }

    [Fact]
    public void Playback_clears_the_buffer_and_allows_reuse()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);
        var entity = buffer.Create();
        buffer.Add(entity, new Position(1, 2));

        var frame = buffer.Playback();

        Assert.Single(frame.CreatedEntities);
        Assert.Empty(buffer.Playback().CreatedEntities);

        var secondEntity = buffer.Create();
        buffer.Add(secondEntity, new Position(3, 4));

        var secondFrame = buffer.Playback();

        Assert.Single(secondFrame.CreatedEntities);
        Assert.Equal(secondEntity, secondFrame.CreatedEntities[0].Entity);
        Assert.Empty(secondFrame.AddCommands);
        Assert.False(buffer.Play());
    }

    [Fact]
    public void Playback_does_not_mutate_world_and_replay_with_reverse_can_restore_the_previous_public_state()
    {
        var world = new World();
        var entity = world.Create();
        world.Add(entity, new Position(1, 2));
        var buffer = new CommandBuffer(world);
        buffer.Set(entity, new Position(9, 9));

        var frame = buffer.Playback();

        Assert.Equal(new Position(1, 2), GetComponentValue<Position>(world, entity));

        var reverse = world.ReplayWithReverse(in frame);

        Assert.Equal(new Position(9, 9), GetComponentValue<Position>(world, entity));

        world.Rewind(in reverse);

        Assert.Equal(new Position(1, 2), GetComponentValue<Position>(world, entity));
    }

    [Fact]
    public void Play_with_reverse_matches_playback_plus_replay_with_reverse_and_both_worlds_can_be_rewound()
    {
        var playbackWorld = new World();
        var playWorld = new World();

        var playbackEntity = playbackWorld.Create();
        playbackWorld.Add(playbackEntity, new Position(1, 2));
        var playEntity = playWorld.Create();
        playWorld.Add(playEntity, new Position(1, 2));

        var playbackBuffer = new CommandBuffer(playbackWorld);
        playbackBuffer.Set(playbackEntity, new Position(9, 9));
        var playBuffer = new CommandBuffer(playWorld);
        playBuffer.Set(playEntity, new Position(9, 9));

        var frame = playbackBuffer.Playback();
        var playbackReverse = playbackWorld.ReplayWithReverse(in frame);
        var playReverse = playBuffer.PlayWithReverse();

        Assert.Equal(new Position(9, 9), GetComponentValue<Position>(playbackWorld, playbackEntity));
        Assert.Equal(new Position(9, 9), GetComponentValue<Position>(playWorld, playEntity));
        Assert.False(playBuffer.Play());

        playbackWorld.Rewind(in playbackReverse);
        playWorld.Rewind(in playReverse);

        Assert.Equal(new Position(1, 2), GetComponentValue<Position>(playbackWorld, playbackEntity));
        Assert.Equal(new Position(1, 2), GetComponentValue<Position>(playWorld, playEntity));
    }

    [Fact]
    public void Rewind_restores_previous_value_after_set_on_existing_entity()
    {
        var world = new World();
        var entity = world.Create();
        world.Add(entity, new Position(1, 2));
        var buffer = new CommandBuffer(world);
        buffer.Set(entity, new Position(9, 9));

        var frame = buffer.Playback();
        var reverse = world.ReplayWithReverse(in frame);

        Assert.Equal(new Position(9, 9), GetComponentValue<Position>(world, entity));

        world.Rewind(in reverse);

        Assert.Equal(new Position(1, 2), GetComponentValue<Position>(world, entity));
    }

    [Fact]
    public void Rewind_removes_component_added_to_existing_entity()
    {
        var world = new World();
        var entity = world.Create();
        var buffer = new CommandBuffer(world);
        buffer.Add(entity, new Velocity(3, 4));

        var frame = buffer.Playback();
        var reverse = world.ReplayWithReverse(in frame);

        Assert.True(HasComponent<Velocity>(world, entity));

        world.Rewind(in reverse);

        Assert.False(HasComponent<Velocity>(world, entity));
    }

    [Fact]
    public void Rewind_restores_removed_component_on_existing_entity()
    {
        var world = new World();
        var entity = world.Create();
        world.Add(entity, new Velocity(3, 4));
        var buffer = new CommandBuffer(world);
        buffer.Remove<Velocity>(entity);

        var frame = buffer.Playback();
        var reverse = world.ReplayWithReverse(in frame);

        Assert.False(HasComponent<Velocity>(world, entity));

        world.Rewind(in reverse);

        Assert.True(HasComponent<Velocity>(world, entity));
        Assert.Equal(new Velocity(3, 4), GetComponentValue<Velocity>(world, entity));
    }

    [Fact]
    public void Rewind_destroys_entities_created_by_replay()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);
        var entity = buffer.Create();
        buffer.Add(entity, new Position(1, 2));
        var frame = buffer.Playback();

        var reverse = world.ReplayWithReverse(in frame);

        Assert.True(world.IsAlive(entity));
        Assert.Equal(1, CountQueryEntities(CreateQuery<Position>(world)));

        world.Rewind(in reverse);

        Assert.False(world.IsAlive(entity));
        Assert.Equal(0, CountQueryEntities(CreateQuery<Position>(world)));
    }

    [Fact]
    public void Rewind_restores_link_added_during_replay()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        var buffer = new CommandBuffer(world);
        buffer.Link(parent, child);
        var frame = buffer.Playback();

        var reverse = world.ReplayWithReverse(in frame);

        Assert.True(world.TryGetParent(child, out var resolvedParent));
        Assert.Equal(parent, resolvedParent);

        world.Rewind(in reverse);

        Assert.False(world.TryGetParent(child, out _));
        Assert.Empty(world.GetChildren(parent));
    }

    [Fact]
    public void Rewind_restores_parent_after_unlink_during_replay()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        world.Link(parent, child);
        var buffer = new CommandBuffer(world);
        buffer.Unlink(child);
        var frame = buffer.Playback();

        var reverse = world.ReplayWithReverse(in frame);

        Assert.False(world.TryGetParent(child, out _));

        world.Rewind(in reverse);

        Assert.True(world.TryGetParent(child, out var resolvedParent));
        Assert.Equal(parent, resolvedParent);
        Assert.Equal([child], world.GetChildren(parent));
    }

    [Fact]
    public void Rewind_restores_mixed_script_across_command_buckets()
    {
        var world = new World();
        var root = world.Create();
        var child = world.Create();
        var sibling = world.Create();
        world.Add(root, new Position(1, 1));
        world.Add(root, new Health(10));
        world.Add(child, new Position(2, 2));
        world.Add(child, new Health(20));
        world.Link(root, child);

        var buffer = new CommandBuffer(world);
        buffer.Unlink(child);
        buffer.Link(sibling, child);
        buffer.Set(root, new Position(9, 9));
        buffer.Add(root, new Velocity(3, 4));
        buffer.Remove<Health>(child);
        var frame = buffer.Playback();

        var reverse = world.ReplayWithReverse(in frame);

        Assert.Equal(new Position(9, 9), GetComponentValue<Position>(world, root));
        Assert.True(HasComponent<Velocity>(world, root));
        Assert.False(HasComponent<Health>(world, child));
        Assert.True(world.TryGetParent(child, out var replayParent));
        Assert.Equal(sibling, replayParent);

        world.Rewind(in reverse);

        Assert.Equal(new Position(1, 1), GetComponentValue<Position>(world, root));
        Assert.False(HasComponent<Velocity>(world, root));
        Assert.True(HasComponent<Health>(world, child));
        Assert.Equal(new Health(20), GetComponentValue<Health>(world, child));
        Assert.True(world.TryGetParent(child, out var rewindParent));
        Assert.Equal(root, rewindParent);
    }

    [Fact]
    public void Rewind_restores_destroyed_existing_subtree_but_not_newly_created_descendants()
    {
        var world = new World();
        var root = world.Create();
        var child = world.Create();
        var grandChild = world.Create();
        var outside = world.Create();
        world.Add(root, new Position(1, 1));
        world.Add(child, new Position(2, 2));
        world.Add(grandChild, new Position(3, 3));
        world.Add(outside, new Position(4, 4));
        world.Link(root, child);
        world.Link(child, grandChild);

        var before = CapturePublicState(world, [root, child, grandChild, outside]);

        var buffer = new CommandBuffer(world);
        var newBranch = buffer.Create();
        buffer.Add(newBranch, new Position(9, 9));
        buffer.Link(child, newBranch);
        buffer.Destroy(root);

        var frame = buffer.Playback();
        var reverse = world.ReplayWithReverse(in frame);

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.False(world.IsAlive(grandChild));
        Assert.False(world.IsAlive(newBranch));
        Assert.True(world.IsAlive(outside));

        world.Rewind(in reverse);

        Assert.Equal(before, CapturePublicState(world, [root, child, grandChild, outside]));
        Assert.False(world.IsAlive(newBranch));
        Assert.True(world.TryGetParent(child, out var restoredParent));
        Assert.Equal(root, restoredParent);
        Assert.True(world.TryGetParent(grandChild, out var restoredGrandParent));
        Assert.Equal(child, restoredGrandParent);
    }

    [Fact]
    public void Multiple_reverse_frames_rewind_in_lifo_order_back_to_the_initial_public_state()
    {
        var world = new World();
        var root = world.Create();
        var child = world.Create();
        var sibling = world.Create();
        world.Add(root, new Position(1, 1));
        world.Add(root, new Health(10));
        world.Add(child, new Position(2, 2));
        world.Add(child, new Velocity(3, 3));
        world.Add(sibling, new Position(4, 4));
        world.Link(root, child);

        var initialEntities = new[] { root, child, sibling };
        var initial = CapturePublicState(world, initialEntities);

        var frame1Buffer = new CommandBuffer(world);
        var branch = frame1Buffer.Create();
        frame1Buffer.Add(branch, new Position(5, 5));
        frame1Buffer.Add(branch, new Health(20));
        frame1Buffer.Link(child, branch);
        frame1Buffer.Set(root, new Position(9, 9));
        frame1Buffer.Remove<Velocity>(child);
        var frame1 = frame1Buffer.Playback();
        var reverse1 = world.ReplayWithReverse(in frame1);

        var frame1Entities = new[] { root, child, sibling, branch };
        var frame1State = CapturePublicState(world, frame1Entities);

        var frame2Buffer = new CommandBuffer(world);
        var transient = frame2Buffer.Create();
        frame2Buffer.Add(transient, new Position(7, 7));
        frame2Buffer.Link(root, transient);
        frame2Buffer.Destroy(root);
        var frame2 = frame2Buffer.Playback();
        var reverse2 = world.ReplayWithReverse(in frame2);

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.False(world.IsAlive(branch));
        Assert.False(world.IsAlive(transient));

        world.Rewind(in reverse2);

        Assert.Equal(frame1State, CapturePublicState(world, frame1Entities));
        Assert.False(world.IsAlive(transient));

        world.Rewind(in reverse1);

        Assert.Equal(initial, CapturePublicState(world, initialEntities));
        Assert.False(world.IsAlive(branch));
        Assert.False(world.IsAlive(transient));
    }

    [Fact]
    public void Frames_after_rewinding_to_a_middle_history_point_can_be_replayed_and_restore_the_same_final_public_state()
    {
        var world = new World();
        var root = world.Create();
        var childA = world.Create();
        var childB = world.Create();
        var anchor = world.Create();
        world.Add(root, new Position(1, 1));
        world.Add(root, new Health(10));
        world.Add(childA, new Position(2, 2));
        world.Add(childA, new Velocity(20, 21));
        world.Add(childB, new Position(3, 3));
        world.Add(childB, new Health(30));
        world.Add(anchor, new Position(4, 4));
        world.Link(root, childA);
        world.Link(root, childB);

        var trackedEntities = new List<Entity> { root, childA, childB, anchor };

        var frame1Buffer = new CommandBuffer(world);
        var branch = frame1Buffer.Create();
        trackedEntities.Add(branch);
        frame1Buffer.Add(branch, new Position(5, 5));
        frame1Buffer.Add(branch, new Health(25));
        frame1Buffer.Link(childA, branch);
        frame1Buffer.Set(root, new Position(10, 10));
        frame1Buffer.Add(root, new Velocity(1, 2));
        frame1Buffer.Unlink(childB);
        frame1Buffer.Link(anchor, childB);
        var frame1 = frame1Buffer.Playback();
        var reverse1 = world.ReplayWithReverse(in frame1);

        var frame2Buffer = new CommandBuffer(world);
        var scout = frame2Buffer.Create();
        trackedEntities.Add(scout);
        frame2Buffer.Add(scout, new Position(6, 6));
        frame2Buffer.Add(scout, new Velocity(7, 7));
        frame2Buffer.Set(childA, new Position(20, 21));
        frame2Buffer.Remove<Velocity>(childA);
        frame2Buffer.Add(childA, new Health(40));
        frame2Buffer.Remove<Health>(childB);
        frame2Buffer.Set(childB, new Position(30, 31));
        frame2Buffer.Set(branch, new Health(26));
        var frame2 = frame2Buffer.Playback();
        var reverse2 = world.ReplayWithReverse(in frame2);

        var frame3Buffer = new CommandBuffer(world);
        var relay = frame3Buffer.Create();
        trackedEntities.Add(relay);
        var ephemeral = frame3Buffer.Create();
        trackedEntities.Add(ephemeral);
        frame3Buffer.Add(relay, new Position(50, 50));
        frame3Buffer.Add(relay, new Health(60));
        frame3Buffer.Link(root, relay);
        frame3Buffer.Link(relay, scout);
        frame3Buffer.Add(ephemeral, new Position(50, 50));
        frame3Buffer.Link(relay, ephemeral);
        frame3Buffer.Destroy(ephemeral);
        frame3Buffer.Set(root, new Health(99));
        frame3Buffer.Remove<Velocity>(root);
        frame3Buffer.Add(branch, new Velocity(8, 8));
        var frame3 = frame3Buffer.Playback();
        var reverse3 = world.ReplayWithReverse(in frame3);

        var frame4Buffer = new CommandBuffer(world);
        var leaf = frame4Buffer.Create();
        trackedEntities.Add(leaf);
        frame4Buffer.Add(leaf, new Position(70, 71));
        frame4Buffer.Add(leaf, new Health(72));
        frame4Buffer.Link(root, leaf);
        frame4Buffer.Link(branch, scout);
        frame4Buffer.Set(childB, new Position(300, 301));
        frame4Buffer.Add(childB, new Health(35));
        frame4Buffer.Remove<Health>(childA);
        frame4Buffer.Set(scout, new Position(80, 81));
        frame4Buffer.Set(branch, new Position(55, 56));
        var frame4 = frame4Buffer.Playback();
        var reverse4 = world.ReplayWithReverse(in frame4);

        var middleWorld = new World();
        var middleRoot = middleWorld.Create();
        var middleChildA = middleWorld.Create();
        var middleChildB = middleWorld.Create();
        var middleAnchor = middleWorld.Create();
        middleWorld.Add(middleRoot, new Position(1, 1));
        middleWorld.Add(middleRoot, new Health(10));
        middleWorld.Add(middleChildA, new Position(2, 2));
        middleWorld.Add(middleChildA, new Velocity(20, 21));
        middleWorld.Add(middleChildB, new Position(3, 3));
        middleWorld.Add(middleChildB, new Health(30));
        middleWorld.Add(middleAnchor, new Position(4, 4));
        middleWorld.Link(middleRoot, middleChildA);
        middleWorld.Link(middleRoot, middleChildB);
        middleWorld.Replay(in frame1);
        middleWorld.Replay(in frame2);

        var middleState = CapturePublicState(middleWorld, trackedEntities);
        var finalState = CapturePublicState(world, trackedEntities);

        world.Rewind(in reverse4);
        world.Rewind(in reverse3);

        Assert.Equal(middleState, CapturePublicState(world, trackedEntities));

        world.Replay(in frame3);
        world.Replay(in frame4);

        Assert.Equal(finalState, CapturePublicState(world, trackedEntities));

        GC.KeepAlive(reverse1);
        GC.KeepAlive(reverse2);
    }

    [Fact]
    public void Randomized_multi_frame_replay_with_reverse_rewinds_back_to_the_initial_public_state()
    {
        RunOnDedicatedThread(() =>
        {
            const int frameCount = 24;
            const int maxCreatesPerFrame = 3;
            const int seed = 0x7A21;

            var world = new World();
            var root = world.Create();
            var child = world.Create();
            var branch = world.Create();
            world.Add(root, new Position(1, 1));
            world.Add(root, new Health(10));
            world.Add(child, new Position(2, 2));
            world.Add(child, new Velocity(3, 3));
            world.Add(branch, new Position(4, 4));
            world.Link(root, child);
            world.Link(root, branch);

            var knownEntities = new List<Entity> { root, child, branch };
            var initialEntities = knownEntities.ToArray();
            var initial = CapturePublicState(world, initialEntities);
            var reverses = new Stack<ReverseFrameCommands>();
            var rng = new Random(seed);

            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                var buffer = new CommandBuffer(world);
                RecordRandomizedFrame(world, buffer, knownEntities, rng, frameIndex, maxCreatesPerFrame);

                if (frameIndex == 8 && world.IsAlive(root))
                {
                    buffer.Destroy(root);
                }

                var frame = buffer.Playback();
                var reverse = world.ReplayWithReverse(in frame);
                reverses.Push(reverse);
            }

            while (reverses.Count > 0)
            {
                var reverse = reverses.Pop();
                world.Rewind(in reverse);
            }

            Assert.Equal(initial, CapturePublicState(world, initialEntities));
        });
    }

    [Fact]
    public void Play_with_reverse_matches_playback_plus_replay_with_reverse_for_multi_frame_complex_scripts()
    {
        RunOnDedicatedThread(() =>
        {
            var playbackWorld = new World();
            var playWorld = new World();

            var playbackRoot = playbackWorld.Create();
            var playbackChild = playbackWorld.Create();
            var playbackSibling = playbackWorld.Create();
            playbackWorld.Add(playbackRoot, new Position(1, 1));
            playbackWorld.Add(playbackRoot, new Health(10));
            playbackWorld.Add(playbackChild, new Position(2, 2));
            playbackWorld.Add(playbackChild, new Velocity(3, 3));
            playbackWorld.Add(playbackSibling, new Position(4, 4));
            playbackWorld.Link(playbackRoot, playbackChild);

            var playRoot = playWorld.Create();
            var playChild = playWorld.Create();
            var playSibling = playWorld.Create();
            playWorld.Add(playRoot, new Position(1, 1));
            playWorld.Add(playRoot, new Health(10));
            playWorld.Add(playChild, new Position(2, 2));
            playWorld.Add(playChild, new Velocity(3, 3));
            playWorld.Add(playSibling, new Position(4, 4));
            playWorld.Link(playRoot, playChild);

            var playbackKnownEntities = new List<Entity> { playbackRoot, playbackChild, playbackSibling };
            var playKnownEntities = new List<Entity> { playRoot, playChild, playSibling };
            var initialPlaybackEntities = playbackKnownEntities.ToArray();
            var initialPlayEntities = playKnownEntities.ToArray();
            var initialPlaybackState = CapturePublicState(playbackWorld, initialPlaybackEntities);
            var initialPlayState = CapturePublicState(playWorld, initialPlayEntities);

            var playbackReverses = new Stack<ReverseFrameCommands>();
            var playReverses = new Stack<ReverseFrameCommands>();

            var playbackFrame1 = new CommandBuffer(playbackWorld);
            var playbackBranch = playbackFrame1.Create();
            playbackFrame1.Add(playbackBranch, new Position(5, 5));
            playbackFrame1.Link(playbackChild, playbackBranch);
            playbackFrame1.Set(playbackRoot, new Position(9, 9));
            playbackFrame1.Remove<Velocity>(playbackChild);
            playbackKnownEntities.Add(playbackBranch);
            var playbackCompiledFrame1 = playbackFrame1.Playback();
            playbackReverses.Push(playbackWorld.ReplayWithReverse(in playbackCompiledFrame1));

            var playFrame1 = new CommandBuffer(playWorld);
            var playBranch = playFrame1.Create();
            playFrame1.Add(playBranch, new Position(5, 5));
            playFrame1.Link(playChild, playBranch);
            playFrame1.Set(playRoot, new Position(9, 9));
            playFrame1.Remove<Velocity>(playChild);
            playKnownEntities.Add(playBranch);
            var playFrame1Reverse = playFrame1.PlayWithReverse();
            playReverses.Push(playFrame1Reverse);

            AssertWorldStatesMatch(playbackWorld, playWorld, playbackKnownEntities.Concat(playKnownEntities).Distinct().ToArray());

            var playbackFrame2 = new CommandBuffer(playbackWorld);
            var playbackTransient = playbackFrame2.Create();
            playbackFrame2.Add(playbackTransient, new Position(7, 7));
            playbackFrame2.Link(playbackRoot, playbackTransient);
            playbackFrame2.Destroy(playbackRoot);
            playbackKnownEntities.Add(playbackTransient);
            var playbackCompiledFrame2 = playbackFrame2.Playback();
            playbackReverses.Push(playbackWorld.ReplayWithReverse(in playbackCompiledFrame2));

            var playFrame2 = new CommandBuffer(playWorld);
            var playTransient = playFrame2.Create();
            playFrame2.Add(playTransient, new Position(7, 7));
            playFrame2.Link(playRoot, playTransient);
            playFrame2.Destroy(playRoot);
            playKnownEntities.Add(playTransient);
            var playFrame2Reverse = playFrame2.PlayWithReverse();
            playReverses.Push(playFrame2Reverse);

            AssertWorldStatesMatch(playbackWorld, playWorld, playbackKnownEntities.Concat(playKnownEntities).Distinct().ToArray());

            while (playbackReverses.Count > 0)
            {
                var playbackReverse = playbackReverses.Pop();
                var playReverse = playReverses.Pop();
                playbackWorld.Rewind(in playbackReverse);
                playWorld.Rewind(in playReverse);
            }

            Assert.Equal(initialPlaybackState, CapturePublicState(playbackWorld, initialPlaybackEntities));
            Assert.Equal(initialPlayState, CapturePublicState(playWorld, initialPlayEntities));
            AssertWorldStatesMatch(playbackWorld, playWorld, playbackKnownEntities.Concat(playKnownEntities).Distinct().ToArray());
        });
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
    public void Same_frame_create_destroy_frame_can_replay_after_rewind_and_keeps_the_same_result()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);
        var transient = buffer.Create();
        buffer.Add(transient, new Position(3, 3));
        buffer.Destroy(transient);
        var frame = buffer.Playback();
        var emptyState = CapturePublicState(world, [transient]);

        var firstReverse = world.ReplayWithReverse(in frame);
        var firstState = CapturePublicState(world, [transient]);

        world.Rewind(in firstReverse);

        Assert.Equal(emptyState, CapturePublicState(world, [transient]));

        var secondReverse = world.ReplayWithReverse(in frame);
        var secondState = CapturePublicState(world, [transient]);

        Assert.Equal(firstState, secondState);

        world.Rewind(in secondReverse);
        Assert.False(world.IsAlive(transient));
    }

    [Fact]
    public void Create_survivor_frame_can_replay_after_rewind_and_keeps_the_same_result()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);
        var survivor = buffer.Create();
        buffer.Add(survivor, new Position(7, 8));
        buffer.Add(survivor, new Health(10));
        var frame = buffer.Playback();

        var firstReverse = world.ReplayWithReverse(in frame);
        var firstState = CapturePublicState(world, [survivor]);

        world.Rewind(in firstReverse);

        Assert.False(world.IsAlive(survivor));

        var secondReverse = world.ReplayWithReverse(in frame);
        var secondState = CapturePublicState(world, [survivor]);

        Assert.Equal(firstState, secondState);
        Assert.True(world.IsAlive(survivor));
        Assert.Equal(new Position(7, 8), GetComponentValue<Position>(world, survivor));
        Assert.Equal(new Health(10), GetComponentValue<Health>(world, survivor));

        world.Rewind(in secondReverse);
        Assert.False(world.IsAlive(survivor));
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
    public void Play_clears_the_buffer_and_allows_reuse()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);
        var entity = buffer.Create();
        buffer.Add(entity, new Position(1, 2));

        Assert.True(buffer.Play());

        Assert.False(buffer.Play());
        Assert.Empty(buffer.Playback().CreatedEntities);

        var secondEntity = buffer.Create();
        buffer.Add(secondEntity, new Position(3, 4));

        Assert.True(buffer.Play());

        Assert.True(world.IsAlive(entity));
        Assert.True(world.IsAlive(secondEntity));
        Assert.Equal(new Position(1, 2), GetComponentValue<Position>(world, entity));
        Assert.Equal(new Position(3, 4), GetComponentValue<Position>(world, secondEntity));
    }

    [Fact]
    public void Play_returns_false_when_buffer_is_empty()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var played = buffer.Play();

        Assert.False(played);
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

    private static T GetComponentValue<T>(World world, Entity entity)
    {
        Assert.True(world.TryGetLocation(entity, out var info));
        var componentType = world.Components.GetOrCreate<T>();
        return info.Archetype.GetChunk(info.ChunkIndex).GetComponent<T>(componentType, info.RowIndex);
    }

    private static bool HasComponent<T>(World world, Entity entity)
    {
        return world.TryGetLocation(entity, out var info) && info.Archetype.Signature.Contains(world.Components.GetOrCreate<T>());
    }

    private static string CapturePublicState(World world, IEnumerable<Entity> trackedEntities)
    {
        var parts = new List<string>();
        foreach (var entity in trackedEntities
                     .Where(static entity => entity.IsValid)
                     .Distinct()
                     .OrderBy(static entity => entity.Id)
                     .ThenBy(static entity => entity.Version))
        {
            if (!world.IsAlive(entity))
            {
                parts.Add($"{entity.Id}:{entity.Version}:dead");
                continue;
            }

            var parentText = world.TryGetParent(entity, out var parent)
                ? $"{parent.Id}:{parent.Version}"
                : "none";

            parts.Add(string.Join(",",
                $"{entity.Id}:{entity.Version}:alive",
                $"parent={parentText}",
                $"position={FormatOptionalComponent(world, entity, HasComponent<Position>, GetComponentValue<Position>)}",
                $"velocity={FormatOptionalComponent(world, entity, HasComponent<Velocity>, GetComponentValue<Velocity>)}",
                $"health={FormatOptionalComponent(world, entity, HasComponent<Health>, GetComponentValue<Health>)}"));
        }

        parts.Add($"links={GetLiveLinks(world)}");
        parts.Add($"query:Position={CaptureQueryMembers<Position>(world)}");
        parts.Add($"query:Velocity={CaptureQueryMembers<Velocity>(world)}");
        parts.Add($"query:Health={CaptureQueryMembers<Health>(world)}");
        return string.Join("|", parts);
    }

    private static string FormatOptionalComponent<T>(World world, Entity entity, Func<World, Entity, bool> hasComponent, Func<World, Entity, T> getComponent)
    {
        return hasComponent(world, entity)
            ? getComponent(world, entity)?.ToString() ?? "null"
            : "none";
    }

    private static string CaptureQueryMembers<T>(World world)
    {
        var entities = new List<string>();
        foreach (ref readonly var chunk in CreateQuery<T>(world).GetChunkSpan())
        {
            foreach (var entity in chunk.GetEntities())
            {
                entities.Add($"{entity.Id}:{entity.Version}");
            }
        }

        entities.Sort(StringComparer.Ordinal);
        return string.Join(",", entities);
    }

    private static int CountQueryEntities(MiniArch.Core.Query query)
    {
        var total = 0;
        foreach (ref readonly var chunk in query.GetChunkSpan())
        {
            total += chunk.GetEntities().Length;
        }

        return total;
    }

    private static MiniQuery CreateQuery<T>(World world)
    {
        var description = new QueryDescription().With<T>();
        return MiniQuery.Create(world, in description);
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
