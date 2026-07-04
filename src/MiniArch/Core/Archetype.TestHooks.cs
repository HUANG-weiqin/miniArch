namespace MiniArch.Core;

internal sealed partial class Archetype
{
    internal void ForceChunkedForTesting()
    {
        if (!_isChunked)
            ConvertToChunked();
    }

    internal void AddSegmentForTesting() => GrowChunked(1);
}
