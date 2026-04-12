using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class EntityTests
{
    [Fact]
    public void Version_is_part_of_identity()
    {
        var oldEntity = new Entity(7, 1);
        var recycledEntity = new Entity(7, 2);

        Assert.NotEqual(oldEntity, recycledEntity);
        Assert.False(oldEntity.MatchesVersion(recycledEntity.Version));
    }

    [Fact]
    public void Valid_entity_has_non_negative_id_and_version()
    {
        var entity = new Entity(0, 1);

        Assert.True(entity.IsValid);
    }

    [Fact]
    public void Default_entity_is_invalid()
    {
        var entity = default(Entity);

        Assert.False(entity.IsValid);
    }

    [Fact]
    public void Negative_identity_is_invalid()
    {
        var entity = new Entity(-1, -1);

        Assert.False(entity.IsValid);
    }
}
