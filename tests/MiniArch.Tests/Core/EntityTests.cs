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
        var entity = new Entity(0, 0);

        Assert.True(entity.IsValid);
    }
}
