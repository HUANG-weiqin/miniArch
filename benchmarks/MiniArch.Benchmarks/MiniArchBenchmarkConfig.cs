using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.Globalization;

namespace MiniArch.Benchmarks;

public sealed class MiniArchBenchmarkConfig : ManualConfig
{
    public MiniArchBenchmarkConfig()
    {
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddColumnProvider(new SnapshotSizeColumnProvider());
        AddExporter(MarkdownExporter.GitHub);
        AddLogger(ConsoleLogger.Default);
        AddJob(Job.ShortRun.WithRuntime(CoreRuntime.Core80).WithId("net8-short"));
        Orderer = new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest);
    }

    public static IConfig Create() => new MiniArchBenchmarkConfig();

    private sealed class SnapshotSizeColumnProvider : IColumnProvider
    {
        private static readonly IColumn[] Columns =
        [
            new SnapshotSizeColumn(),
            new SnapshotBytesPerEntityColumn(),
        ];

        public IEnumerable<IColumn> GetColumns(Summary summary)
        {
            foreach (var column in Columns)
            {
                yield return column;
            }
        }
    }

    private sealed class SnapshotSizeColumn : IColumn
    {
        public string Id => nameof(SnapshotSizeColumn);

        public string ColumnName => "Snapshot bytes";

        public ColumnCategory Category => ColumnCategory.Custom;

        public int PriorityInCategory => 0;

        public bool AlwaysShow => true;

        public bool IsNumeric => true;

        public UnitType UnitType => UnitType.Size;

        public string Legend => "Serialized snapshot size in bytes.";

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public bool IsAvailable(Summary summary) => true;

        public bool IsAvailable(Summary summary, BenchmarkCase benchmarkCase)
        {
            return benchmarkCase.Descriptor.WorkloadMethod.DeclaringType == typeof(SnapshotBenchmarks);
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            return TryGetSnapshotSize(benchmarkCase, out var snapshotSizeBytes)
                ? snapshotSizeBytes.ToString()
                : "n/a";
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        {
            return GetValue(summary, benchmarkCase);
        }

        private static bool TryGetSnapshotSize(BenchmarkCase benchmarkCase, out long snapshotSizeBytes)
        {
            foreach (var parameter in benchmarkCase.Parameters.Items)
            {
                if (parameter.Name == nameof(SnapshotBenchmarks.EntityCount) && parameter.Value is int entityCount)
                {
                    return SnapshotBenchmarkMetrics.TryGetSnapshotSize(entityCount, out snapshotSizeBytes);
                }
            }

            snapshotSizeBytes = 0;
            return false;
        }
    }

    private sealed class SnapshotBytesPerEntityColumn : IColumn
    {
        public string Id => nameof(SnapshotBytesPerEntityColumn);

        public string ColumnName => "Bytes/entity";

        public ColumnCategory Category => ColumnCategory.Custom;

        public int PriorityInCategory => 1;

        public bool AlwaysShow => true;

        public bool IsNumeric => true;

        public UnitType UnitType => UnitType.Dimensionless;

        public string Legend => "Serialized snapshot size divided by entity count.";

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public bool IsAvailable(Summary summary) => true;

        public bool IsAvailable(Summary summary, BenchmarkCase benchmarkCase)
        {
            return benchmarkCase.Descriptor.WorkloadMethod.DeclaringType == typeof(SnapshotBenchmarks);
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            if (!TryGetEntityCount(benchmarkCase, out var entityCount) ||
                !SnapshotBenchmarkMetrics.TryGetSnapshotSize(entityCount, out var snapshotSizeBytes) ||
                entityCount == 0)
            {
                return "n/a";
            }

            var bytesPerEntity = (double)snapshotSizeBytes / entityCount;
            return bytesPerEntity.ToString("0.00", CultureInfo.InvariantCulture);
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        {
            return GetValue(summary, benchmarkCase);
        }
    }

    private static bool TryGetEntityCount(BenchmarkCase benchmarkCase, out int entityCount)
    {
        foreach (var parameter in benchmarkCase.Parameters.Items)
        {
            if (parameter.Name == nameof(SnapshotBenchmarks.EntityCount) && parameter.Value is int count)
            {
                entityCount = count;
                return true;
            }
        }

        entityCount = 0;
        return false;
    }
}
