namespace MiniArch.Core;

internal sealed partial class Archetype
{
    internal void ForceChunkedForTesting()
    {
        if (!IsChunked)
            ConvertToChunked();
    }

    internal void AddSegmentForTesting() => GrowChunked(1);
}
