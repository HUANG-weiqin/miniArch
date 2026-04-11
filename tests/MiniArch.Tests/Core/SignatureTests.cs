using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class SignatureTests
{
    [Fact]
    public void Signatures_with_same_components_are_equal()
    {
        var idA = new ComponentType(1);
        var idB = new ComponentType(2);

        var first = new Signature(idA, idB);
        var second = new Signature(idB, idA);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Hash_code_is_stable_for_same_content()
    {
        var signature = new Signature(new ComponentType(1), new ComponentType(3), new ComponentType(8));

        Assert.Equal(signature.GetHashCode(), new Signature(new ComponentType(8), new ComponentType(1), new ComponentType(3)).GetHashCode());
    }

    [Fact]
    public void Add_and_remove_produce_expected_component_sets()
    {
        var idA = new ComponentType(1);
        var idB = new ComponentType(2);
        var signature = Signature.Empty.Add(idB).Add(idA);

        Assert.Equal(new Signature(idA, idB), signature);
        Assert.Equal(new Signature(idB), signature.Remove(idA));
    }
}
