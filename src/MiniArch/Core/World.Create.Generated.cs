using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var archetype = GetOrCreateCreateArchetype<T1>(componentType1);
        var entity = CreateInArchetype(archetype, out var rowIndex);
        archetype.SetComponentAtTyped(0, rowIndex, in component1);
        return entity;
    }

    /// <summary>
    /// Creates an entity with two components.
    /// </summary>
    public Entity Create<T1, T2>(T1 component1, T2 component2) where T1 : unmanaged where T2 : unmanaged
    {
        ThrowIfDisposed();
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var archetype = GetOrCreateCreateArchetype<T1, T2>(componentType1, componentType2);
        var entity = CreateInArchetype(archetype, out var rowIndex);
        archetype.SetComponentAtTyped(archetype.GetComponentIndexFast(componentType1), rowIndex, in component1);
        archetype.SetComponentAtTyped(archetype.GetComponentIndexFast(componentType2), rowIndex, in component2);
        return entity;
    }

    /// <summary>
    /// Creates an entity with three components.
    /// </summary>
    public Entity Create<T1, T2, T3>(T1 component1, T2 component2, T3 component3) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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

    private bool TryGetCreateArchetype<T>([NotNullWhen(true)] out Archetype? archetype) where T : unmanaged
    {
        var entry = CreateArchetypeCache<T>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out archetype))
        {
            return true;
        }

        var ct = Component<T>.ComponentType;
        if (!_archetypes.TryGetValue(new Signature(ct), out archetype))
        {
            return false;
        }

        CreateArchetypeCache<T>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return true;
    }

    private Archetype GetOrCreateCreateArchetype<T1>(ComponentType componentType1)
    {
        var entry = CreateArchetypeCache<T1>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cachedArchetype))
        {
            return cachedArchetype;
        }

        var archetype = GetOrCreateArchetype(new Signature(componentType1));
        CreateArchetypeCache<T1>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2>(ComponentType componentType1, ComponentType componentType2)
    {
        var entry = CreateArchetypeCache<T1, T2>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cachedArchetype))
        {
            return cachedArchetype;
        }

        var archetype = GetOrCreateArchetype(new Signature(componentType1, componentType2));
        CreateArchetypeCache<T1, T2>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3>(ComponentType ct1, ComponentType ct2, ComponentType ct3)
    {
        var entry = CreateArchetypeCache<T1, T2, T3>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3));
        CreateArchetypeCache<T1, T2, T3>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4));
        CreateArchetypeCache<T1, T2, T3, T4>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5));
        CreateArchetypeCache<T1, T2, T3, T4, T5>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13, ComponentType ct14)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13, ct14));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13, ComponentType ct14, ComponentType ct15)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13, ct14, ct15));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private Archetype GetOrCreateCreateArchetype<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(ComponentType ct1, ComponentType ct2, ComponentType ct3, ComponentType ct4, ComponentType ct5, ComponentType ct6, ComponentType ct7, ComponentType ct8, ComponentType ct9, ComponentType ct10, ComponentType ct11, ComponentType ct12, ComponentType ct13, ComponentType ct14, ComponentType ct15, ComponentType ct16)
    {
        var entry = CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>.Entry;
        if (entry is not null && entry.TryGetArchetype(this, _createArchetypeCacheGeneration, out var cached))
        {
            return cached;
        }

        var archetype = GetOrCreateArchetype(new Signature(ct1, ct2, ct3, ct4, ct5, ct6, ct7, ct8, ct9, ct10, ct11, ct12, ct13, ct14, ct15, ct16));
        CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>.Entry = new CachedCreateArchetype(this, _createArchetypeCacheGeneration, archetype);
        return archetype;
    }

    private static void SetCreatedComponent<T>(Archetype archetype, int rowIndex, ComponentType componentType, in T component) where T : unmanaged
    {
        archetype.SetComponentAtTyped(archetype.GetComponentIndexFast(componentType), rowIndex, in component);
    }

    private static class CreateArchetypeCache<T1>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>
    {
        public static CachedCreateArchetype? Entry;
    }

    private static class CreateArchetypeCache<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>
    {
        public static CachedCreateArchetype? Entry;
    }

    private sealed class CachedCreateArchetype
    {
        private readonly WeakReference<World> _world;
        private readonly WeakReference<Archetype> _archetype;

        public CachedCreateArchetype(World world, int generation, Archetype archetype)
        {
            _world = new WeakReference<World>(world);
            _archetype = new WeakReference<Archetype>(archetype);
            Generation = generation;
        }

        public int Generation { get; }

        public bool TryGetArchetype(World world, int generation, [NotNullWhen(true)] out Archetype? archetype)
        {
            archetype = null;
            if (generation != Generation ||
                !_world.TryGetTarget(out var cachedWorld) ||
                !ReferenceEquals(cachedWorld, world))
            {
                return false;
            }

            return _archetype.TryGetTarget(out archetype);
        }
    }

}
