namespace MiniArch.Core;

internal sealed class CommandBufferEntityAllocator(World world)
{
    public Entity ReserveEntity()
    {
        return world.ReserveDeferredEntity();
    }
}
