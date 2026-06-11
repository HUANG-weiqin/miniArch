using System.Reflection;
using Hero.Ecs;

namespace Hero.Tests;

internal static class TestEntities
{
    private static readonly MethodInfo AddMethod = typeof(TestEntities)
        .GetMethod(nameof(AddGeneric), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static MiniArch.Entity Create(MiniArchRuntime runtime, params object[] components)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return Create(runtime.World, components);
    }

    public static MiniArch.Entity Create(MiniArch.World world, params object[] components)
    {
        ArgumentNullException.ThrowIfNull(world);

        MiniArch.Entity entity = world.Create();

        foreach (object component in components)
        {
            MethodInfo method = AddMethod.MakeGenericMethod(component.GetType());
            _ = method.Invoke(null, [world, entity, component]);
        }

        return entity;
    }

    private static void AddGeneric<T>(MiniArch.World world, MiniArch.Entity entity, object component) where T : unmanaged =>
        world.Add(entity, (T)component);
}


