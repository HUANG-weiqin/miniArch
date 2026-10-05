using MiniArch.Core;

#nullable enable

namespace MiniArch;

public sealed partial class World
{
    /// <summary>
    /// Creates an entity with one component.
    /// </summary>
    public Entity Create<T1>(T1 component1) where T1 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var archetype = GetOrCreateCreateArchetype<T1>(componentType1);
        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        return entity;
    }

    /// <summary>
    /// Creates an entity with two components.
    /// </summary>
    public Entity Create<T1, T2>(T1 component1, T2 component2) where T1 : unmanaged where T2 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var archetype = GetOrCreateCreateArchetype<T1, T2>(componentType1, componentType2);
        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        return entity;
    }

    /// <summary>
    /// Creates an entity with three components.
    /// </summary>
    public Entity Create<T1, T2, T3>(T1 component1, T2 component2, T3 component3) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3>(componentType1, componentType2, componentType3);
        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        return entity;
    }

    /// <summary>
    /// Creates an entity with four components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4>(T1 component1, T2 component2, T3 component3, T4 component4) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4>(componentType1, componentType2, componentType3, componentType4);
        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        return entity;
    }

    /// <summary>
    /// Creates an entity with five components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5>(componentType1, componentType2, componentType3, componentType4, componentType5);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        return entity;
    }

    /// <summary>
    /// Creates an entity with six components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        return entity;
    }

    /// <summary>
    /// Creates an entity with seven components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        return entity;
    }

    /// <summary>
    /// Creates an entity with eight components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        return entity;
    }

    /// <summary>
    /// Creates an entity with nine components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged where T9 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        return entity;
    }

    /// <summary>
    /// Creates an entity with ten components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged where T9 : unmanaged where T10 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        return entity;
    }

    /// <summary>
    /// Creates an entity with eleven components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged where T9 : unmanaged where T10 : unmanaged where T11 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        return entity;
    }

    /// <summary>
    /// Creates an entity with twelve components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged where T9 : unmanaged where T10 : unmanaged where T11 : unmanaged where T12 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, rowIndex, componentType12, in component12);
        return entity;
    }

    /// <summary>
    /// Creates an entity with thirteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged where T9 : unmanaged where T10 : unmanaged where T11 : unmanaged where T12 : unmanaged where T13 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, rowIndex, componentType13, in component13);
        return entity;
    }

    /// <summary>
    /// Creates an entity with fourteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged where T9 : unmanaged where T10 : unmanaged where T11 : unmanaged where T12 : unmanaged where T13 : unmanaged where T14 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var componentType14 = GetComponentType<T14>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, rowIndex, componentType13, in component13);
        SetCreatedComponent(archetype, rowIndex, componentType14, in component14);
        return entity;
    }

    /// <summary>
    /// Creates an entity with fifteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged where T9 : unmanaged where T10 : unmanaged where T11 : unmanaged where T12 : unmanaged where T13 : unmanaged where T14 : unmanaged where T15 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var componentType14 = GetComponentType<T14>();
        var componentType15 = GetComponentType<T15>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14, componentType15);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, rowIndex, componentType13, in component13);
        SetCreatedComponent(archetype, rowIndex, componentType14, in component14);
        SetCreatedComponent(archetype, rowIndex, componentType15, in component15);
        return entity;
    }

    /// <summary>
    /// Creates an entity with sixteen components.
    /// </summary>
    public Entity Create<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(T1 component1, T2 component2, T3 component3, T4 component4, T5 component5, T6 component6, T7 component7, T8 component8, T9 component9, T10 component10, T11 component11, T12 component12, T13 component13, T14 component14, T15 component15, T16 component16) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged where T6 : unmanaged where T7 : unmanaged where T8 : unmanaged where T9 : unmanaged where T10 : unmanaged where T11 : unmanaged where T12 : unmanaged where T13 : unmanaged where T14 : unmanaged where T15 : unmanaged where T16 : unmanaged
    {
        AssertNotDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
        var componentType4 = GetComponentType<T4>();
        var componentType5 = GetComponentType<T5>();
        var componentType6 = GetComponentType<T6>();
        var componentType7 = GetComponentType<T7>();
        var componentType8 = GetComponentType<T8>();
        var componentType9 = GetComponentType<T9>();
        var componentType10 = GetComponentType<T10>();
        var componentType11 = GetComponentType<T11>();
        var componentType12 = GetComponentType<T12>();
        var componentType13 = GetComponentType<T13>();
        var componentType14 = GetComponentType<T14>();
        var componentType15 = GetComponentType<T15>();
        var componentType16 = GetComponentType<T16>();
        var archetype = GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(componentType1, componentType2, componentType3, componentType4, componentType5, componentType6, componentType7, componentType8, componentType9, componentType10, componentType11, componentType12, componentType13, componentType14, componentType15, componentType16);

        var entity = CreateInArchetype(archetype, out var rowIndex);
        SetCreatedComponent(archetype, rowIndex, componentType1, in component1);
        SetCreatedComponent(archetype, rowIndex, componentType2, in component2);
        SetCreatedComponent(archetype, rowIndex, componentType3, in component3);
        SetCreatedComponent(archetype, rowIndex, componentType4, in component4);
        SetCreatedComponent(archetype, rowIndex, componentType5, in component5);
        SetCreatedComponent(archetype, rowIndex, componentType6, in component6);
        SetCreatedComponent(archetype, rowIndex, componentType7, in component7);
        SetCreatedComponent(archetype, rowIndex, componentType8, in component8);
        SetCreatedComponent(archetype, rowIndex, componentType9, in component9);
        SetCreatedComponent(archetype, rowIndex, componentType10, in component10);
        SetCreatedComponent(archetype, rowIndex, componentType11, in component11);
        SetCreatedComponent(archetype, rowIndex, componentType12, in component12);
        SetCreatedComponent(archetype, rowIndex, componentType13, in component13);
        SetCreatedComponent(archetype, rowIndex, componentType14, in component14);
        SetCreatedComponent(archetype, rowIndex, componentType15, in component15);
        SetCreatedComponent(archetype, rowIndex, componentType16, in component16);
        return entity;
    }

    private static int _nextCreateArchetypeCacheId = -1;
    private Archetype?[] _createArchetypeCache = [];

    private static int NextCreateArchetypeCacheId() => Interlocked.Increment(ref _nextCreateArchetypeCacheId);

    private Archetype? GetCachedCreateArchetype(int id)
    {
        var cache = _createArchetypeCache;
        return (uint)id < (uint)cache.Length ? cache[id] : null;
    }

    private void CacheCreateArchetype(int id, Archetype archetype)
    {
        if (id >= _createArchetypeCache.Length)
            Array.Resize(ref _createArchetypeCache, Math.Max(id + 1, Math.Max(4, _createArchetypeCache.Length * 2)));

        _createArchetypeCache[id] = archetype;
    }

    internal void ClearCreateArchetypeCache() => _createArchetypeCache = [];

    private Archetype GetOrCreateCreateArchetype<T1>(ComponentType componentType1)
    {
        var cacheId = CreateArchetypeCache<T1>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cachedArchetype)
        {
            return cachedArchetype;
        }

        var archetype = GetOrCreateArchetype(new Signature(componentType1));
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2>(ComponentType componentType1, ComponentType componentType2)
    {
        var cacheId = CreateArchetypeCache<T1, T2>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cachedArchetype)
        {
            return cachedArchetype;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(2, componentType1, componentType2);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3>(ComponentType ct1, ComponentType ct2, ComponentType ct3)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(3, ct1, ct2, ct3);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(4, ct1, ct2, ct3, ct4);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(5, ct1, ct2, ct3, ct4, ct5);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(6, ct1, ct2, ct3, ct4, ct5, ct6);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(7, ct1, ct2, ct3, ct4, ct5, ct6, ct7);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(8, ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(9, ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(10, ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(11, ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(12, ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(13, ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13, ComponentType ct14)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(14, ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13, ct14);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13, ComponentType ct14, ComponentType ct15)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(15, ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13, ct14, ct15);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13, ComponentType ct14, ComponentType ct15, ComponentType ct16)
    {
        var cacheId = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>.Id;
        if (GetCachedCreateArchetype(cacheId) is { } cached)
        {
            return cached;
        }

        var archetype = GetOrCreateUniqueCreateArchetype(16, ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13, ct14, ct15, ct16);
        CacheCreateArchetype(cacheId, archetype);
        return archetype;
    }

    private Archetype GetOrCreateUniqueCreateArchetype(
        int expectedComponentCount,
        params ComponentType[] componentTypes)
    {
        var signature = new Signature(componentTypes);
        if (signature.Count != expectedComponentCount)
            throw new InvalidOperationException("World.Create component types must be unique.");

        return GetOrCreateArchetype(signature);
    }

    private static void SetCreatedComponent<T>(Archetype archetype, int rowIndex, ComponentType componentType, in T component) where T : unmanaged
    {
        archetype.SetComponentAtTyped(archetype.GetComponentIndexFast(componentType), rowIndex, in component);
    }

    private static class CreateArchetypeCache<T1>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>
    {
        public static readonly int Id = NextCreateArchetypeCacheId();
    }

}
