using System.Globalization;
using MiniArchTests.Core;

namespace MiniArchTests;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--parallel-query-allocation" && args[1] == "nested")
            {
                ParallelQueryAllocationTests.MeasureNested();
                Console.WriteLine("PASS nested");
                return 0;
            }

            if (args.Length == 4 && args[0] == "--parallel-query-allocation" && args[1] == "matrix" &&
                int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int route) &&
                route is >= 0 and <= 3 && (args[3] == "delegate" || args[3] == "struct"))
            {
                ParallelQueryAllocationTests.MeasureSteadyState(route, args[3] == "struct");
                Console.WriteLine($"PASS matrix {route} {args[3]}");
                return 0;
            }

            Console.Error.WriteLine("Unknown allocation case. Expected --parallel-query-allocation nested or matrix <0..3> <delegate|struct>.");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
