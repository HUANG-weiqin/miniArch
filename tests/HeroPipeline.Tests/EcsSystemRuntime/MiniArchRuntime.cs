using System;
using System.Collections.Generic;
using System.Linq;
using CoreCommandBuffer = MiniArch.Core.CommandBuffer;

namespace Hero.Ecs;

public sealed class MiniArchRuntime
{
    private static readonly Type[] ComponentTypes = typeof(Request).Assembly
        .GetTypes()
        .Where(static type =>
            type.IsValueType &&
            !type.IsGenericTypeDefinition &&
            !type.IsNestedPrivate &&
            type.Namespace is not null &&
            type.Namespace.StartsWith("Hero.", StringComparison.Ordinal))
        .OrderBy(static type => type.FullName, StringComparer.Ordinal)
        .ToArray();

    private readonly List<ISystem> _systems = [];

    public MiniArchRuntime()
        : this(new MiniArch.World())
    {
    }

    private MiniArchRuntime(MiniArch.World world)
    {
        World = world;
        PreRegisterComponentTypes();
        Commands = new CoreCommandBuffer(world);
        CurrentFrame = new FrameView(World);
    }

    public MiniArch.World World { get; }

    public CoreCommandBuffer Commands { get; }

    public FrameView CurrentFrame { get; private set; }

    public bool IsStable { get; private set; } = true;

    public void AddSystem(ISystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        _systems.Add(system);
    }

    public void Tick()
    {
        Commands.CompileAndReplay();
        CurrentFrame = new FrameView(World);

        FrameContext context = new(Commands, CurrentFrame);

        if (_systems.Count > 0)
        {
            RunSystems(context);
        }

        IsStable = !Commands.CompileAndReplay();
        CurrentFrame = new FrameView(World);
    }

    private void RunSystems(FrameContext context)
    {
        foreach (ISystem system in _systems)
        {
            system.Execute(in context);
        }
    }

    private void PreRegisterComponentTypes()
    {
        foreach (Type type in ComponentTypes)
        {
            World.Components.GetOrCreate(type);
        }
    }
}


