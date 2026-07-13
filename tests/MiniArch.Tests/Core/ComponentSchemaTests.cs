using MiniArch;
using MiniArch.Core;
using System.Reflection;
using System.Reflection.Emit;

namespace MiniArchTests.Core;

public sealed class ComponentSchemaTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);
    private readonly record struct Health(int Value);

    [Fact]
    public void Fingerprint_is_stable_for_identical_registration_order()
    {
        var reg1 = new ComponentRegistry();
        reg1.GetOrCreate<Position>();
        reg1.GetOrCreate<Velocity>();

        var reg2 = new ComponentRegistry();
        reg2.GetOrCreate<Position>();
        reg2.GetOrCreate<Velocity>();

        Assert.Equal(reg1.GetFingerprint(), reg2.GetFingerprint());
    }

    [Fact]
    public void Fingerprint_differs_when_registration_order_differs()
    {
        var reg1 = new ComponentRegistry();
        reg1.GetOrCreate<Position>();
        reg1.GetOrCreate<Velocity>();

        var reg2 = new ComponentRegistry();
        reg2.GetOrCreate<Velocity>();
        reg2.GetOrCreate<Position>();

        Assert.NotEqual(reg1.GetFingerprint(), reg2.GetFingerprint());
    }

    [Fact]
    public void Fingerprint_differs_when_type_set_differs()
    {
        var reg1 = new ComponentRegistry();
        reg1.GetOrCreate<Position>();
        reg1.GetOrCreate<Velocity>();

        var reg2 = new ComponentRegistry();
        reg2.GetOrCreate<Position>();
        reg2.GetOrCreate<Health>();

        Assert.NotEqual(reg1.GetFingerprint(), reg2.GetFingerprint());
    }

    [Fact]
    public void Fingerprint_differs_when_extra_type_registered()
    {
        var reg1 = new ComponentRegistry();
        reg1.GetOrCreate<Position>();

        var reg2 = new ComponentRegistry();
        reg2.GetOrCreate<Position>();
        reg2.GetOrCreate<Velocity>();

        Assert.NotEqual(reg1.GetFingerprint(), reg2.GetFingerprint());
    }

    [Fact]
    public void Fingerprint_returns_32_byte_sha256()
    {
        var fp = ComponentSchema.Fingerprint();
        Assert.Equal(32, fp.Length);
    }

    [Fact]
    public void Empty_registry_produces_stable_fingerprint()
    {
        var reg1 = new ComponentRegistry();
        var reg2 = new ComponentRegistry();

        Assert.Equal(32, reg1.GetFingerprint().Length);
        Assert.Equal(reg1.GetFingerprint(), reg2.GetFingerprint());
    }

    [Fact]
    public void Fingerprint_reflects_current_state_as_types_are_added()
    {
        var reg = new ComponentRegistry();
        reg.GetOrCreate<Position>();
        var fp1 = reg.GetFingerprint();

        reg.GetOrCreate<Velocity>();
        var fp2 = reg.GetFingerprint();

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_differs_for_same_full_name_in_different_assemblies()
    {
        var leftType = DefineDynamicValueType("MiniArch.SchemaFingerprint.Left");
        var rightType = DefineDynamicValueType("MiniArch.SchemaFingerprint.Right");

        Assert.Equal(leftType.FullName, rightType.FullName);
        Assert.NotEqual(leftType.AssemblyQualifiedName, rightType.AssemblyQualifiedName);

        var left = new ComponentRegistry();
        left.GetOrCreate(leftType);

        var right = new ComponentRegistry();
        right.GetOrCreate(rightType);

        Assert.NotEqual(left.GetFingerprint(), right.GetFingerprint());
    }

    private static Type DefineDynamicValueType(string assemblyName)
    {
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(assemblyName),
            AssemblyBuilderAccess.Run);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName);
        var typeBuilder = moduleBuilder.DefineType(
            "Game.Position",
            TypeAttributes.Public |
            TypeAttributes.Sealed |
            TypeAttributes.SequentialLayout |
            TypeAttributes.AnsiClass |
            TypeAttributes.BeforeFieldInit,
            typeof(ValueType));

        typeBuilder.DefineField("X", typeof(int), FieldAttributes.Public);
        return typeBuilder.CreateTypeInfo()!.AsType();
    }
}
