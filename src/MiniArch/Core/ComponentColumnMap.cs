namespace MiniArch.Core;

internal static class ComponentColumnMap
{
    public static int[] Build(Signature signature)
    {
        var maxComponentId = -1;
        var components = signature.AsSpan();
        for (var index = 0; index < components.Length; index++)
        {
            var componentId = components[index].Value;
            if (componentId > maxComponentId)
            {
                maxComponentId = componentId;
            }
        }

        if (maxComponentId < 0)
        {
            return Array.Empty<int>();
        }

        var lookup = new int[maxComponentId + 1];
        Array.Fill(lookup, -1);

        for (var index = 0; index < components.Length; index++)
        {
            lookup[components[index].Value] = index;
        }

        return lookup;
    }
}
