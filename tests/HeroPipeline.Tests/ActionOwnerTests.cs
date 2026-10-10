using Hero.GameplayEcs.Bootstrap;
using Hero.GameplayEcs.Cards;
using Hero.GameplayEcs.Characters.Actions;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Components;
using Hero.Tests.Fixtures;

namespace Hero.Tests;

public sealed class ActionOwnerTests
{
    [Fact]
    public void Spawned_actions_and_cards_keep_their_character_owner()
    {
        CoreTestFixture core = new();
        core.AddCoreSystems();
        core.AddSpawnSystem();

        var first = CharacterSpawnBootstrap.CreatePlayerAt(core.Runtime, 0, 0);
        var second = CharacterSpawnBootstrap.CreatePlayerAt(core.Runtime, 1, 0);
        core.StepUntilStable();

        var frame = core.Runtime.CurrentFrame;
        var actionQuery = new MiniArch.QueryDescription().With<ActionEntity>().With<ActionOwner>();
        int firstActions = 0, secondActions = 0;
        foreach (var chunk in frame.ChunkQuery(actionQuery).GetChunks())
        {
            var owners = chunk.GetSpan<ActionOwner>();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (owners[i].Value == first) firstActions++;
                else if (owners[i].Value == second) secondActions++;
                else throw new Xunit.Sdk.XunitException($"Unexpected action owner {owners[i].Value}");
            }
        }

        Assert.Equal(18, firstActions); // two character actions and sixteen deck cards
        Assert.Equal(18, secondActions);
        Assert.Equal(16, CardCollector.CollectInZone(frame, first, CardZoneKind.Discard).Count);
        Assert.Equal(16, CardCollector.CollectInZone(frame, second, CardZoneKind.Discard).Count);
        var grouped = CardCollector.GroupInZoneByOwner(frame, CardZoneKind.Discard);
        Assert.Equal(2, grouped.Count);
        Assert.Equal(16, grouped[first].Count);
        Assert.Equal(16, grouped[second].Count);
    }

    [Fact]
    public void Owned_attack_action_still_damages_the_target()
    {
        CharacterTestFixture fixture = new();
        fixture.AddCoreSystems();
        fixture.Core.AddSpawnSystem();

        var runtime = fixture.Runtime;
        var player = CharacterSpawnBootstrap.CreatePlayerAt(runtime, 0, 0);
        var target = CharacterSpawnBootstrap.CreateSandbagEnemyAt(runtime, 0, 50);
        fixture.StepUntilStable();

        var frame = runtime.CurrentFrame;
        var query = new MiniArch.QueryDescription()
            .With<ActionEntity>().With<ActionKind>().With<ActionOwner>();
        MiniArch.Entity attackAction = default;
        foreach (var chunk in frame.ChunkQuery(query).GetChunks())
        {
            var kinds = chunk.GetSpan<ActionKind>();
            var owners = chunk.GetSpan<ActionOwner>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (kinds[i] == CharacterActionKinds.Attack && owners[i].Value == player)
                    attackAction = entities[i];
            }
        }

        Assert.NotEqual(default, attackAction);
        Assert.Equal(100, frame.Get<CurrentHpValue>(target).Value);
        CharacterAttackBootstrap.CreateAttackRequest(runtime, attackAction, 0, 50);
        fixture.StepUntilStable();
        Assert.True(runtime.CurrentFrame.Get<CurrentHpValue>(target).Value < 100);
    }
}
