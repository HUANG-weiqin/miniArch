namespace MiniArch.Core;

internal sealed class CommandBufferEntityAllocator
{
    private readonly World _world;
    private readonly object _gate = new();

    public CommandBufferEntityAllocator(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public Entity ReserveEntity()
    {
        lock (_gate)
        {
            return _world.ReserveDeferredEntity();
        }
    }
}
