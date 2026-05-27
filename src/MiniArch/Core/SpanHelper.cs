namespace MiniArch.Core;

internal static class SpanHelper
{
    public static int SortAndDeduplicate(Span<ComponentType> values)
    {
        values.Sort();

        if (values.Length <= 1)
            return values.Length;

        int writeIndex = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] != values[writeIndex])
            {
                writeIndex++;
                values[writeIndex] = values[i];
            }
        }

        return writeIndex + 1;
    }

    public static int CombineHashCodes(ReadOnlySpan<ComponentType> values)
    {
        int hash = 17;
        for (int i = 0; i < values.Length; i++)
        {
            hash = unchecked(hash * 31 + values[i].Value);
        }

        return hash;
    }

    public static int CombineHashCodes(ReadOnlySpan<Type> types)
    {
        int hash = 17;
        for (int i = 0; i < types.Length; i++)
        {
            hash = unchecked(hash * 31 + types[i].TypeHandle.GetHashCode());
        }

        return hash;
    }
}
