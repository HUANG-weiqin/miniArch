using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Hero.Ecs;
using Hero.GameplayEcs.Bootstrap;
using Hero.GameplayEcs.Characters.Actions;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Components;
using Hero.GameplayEcs.Characters.Movement;
using Hero.GameplayEcs.Characters.Spawn;
using Hero.Tests.Fixtures;
using MiniArch;
using MiniArch.Core;

const int CharacterCount = 1000;
const int GridWidth = 100;
const int GridHeight = 100;
const int DurationSeconds = 30;
const int ReportInterval = 100;
const int WarmupRounds = 50;
const string UpdateBaselineArg = "--update-baseline";
const string CheckBaselineArg = "--check-baseline";
const string TrackObserverArg = "--track-observer";
const string CompareOldValueTrackingArg = "--compare-old-value-tracking";
const string HelpArg = "--help";

var updateBaseline = args.Contains(UpdateBaselineArg, StringComparer.OrdinalIgnoreCase);
var checkBaseline = args.Contains(CheckBaselineArg, StringComparer.OrdinalIgnoreCase);
var trackObserver = args.Contains(TrackObserverArg, StringComparer.OrdinalIgnoreCase);
var compareOldValueTracking = args.Contains(CompareOldValueTrackingArg, StringComparer.OrdinalIgnoreCase);
var showHelp = args.Contains(HelpArg, StringComparer.OrdinalIgnoreCase) ||
               args.Contains("-h", StringComparer.OrdinalIgnoreCase);
var knownArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    UpdateBaselineArg,
    CheckBaselineArg,
    TrackObserverArg,
    CompareOldValueTrackingArg,
    HelpArg,
    "-h"
};

if (showHelp)
{
    PrintUsage();
    return;
}

var unknownArgs = args.Where(arg => !knownArgs.Contains(arg)).ToArray();
if (unknownArgs.Length > 0)
{
    Console.Error.WriteLine($"Unknown argument(s): {string.Join(", ", unknownArgs)}");
    PrintUsage();
    Environment.ExitCode = 2;
    return;
}

if (trackObserver && (checkBaseline || updateBaseline))
{
    Console.Error.WriteLine($"{TrackObserverArg} cannot be combined with {CheckBaselineArg} or {UpdateBaselineArg}.");
    PrintUsage();
    Environment.ExitCode = 2;
    return;
}

if (compareOldValueTracking && (checkBaseline || updateBaseline || trackObserver))
{
    Console.Error.WriteLine($"{CompareOldValueTrackingArg} cannot be combined with {CheckBaselineArg}, {UpdateBaselineArg}, or {TrackObserverArg}.");
    PrintUsage();
    Environment.ExitCode = 2;
    return;
}

Console.WriteLine("=== HeroComing ECS Performance Test ===");
Console.WriteLine($"Characters: {CharacterCount}");
Console.WriteLine($"Grid:       {GridWidth}x{GridHeight}");
Console.WriteLine($"Duration:   {DurationSeconds}s per scenario");
Console.WriteLine($"Mode:       measure{(checkBaseline ? " + check baseline" : "")}{(updateBaseline ? " + update baseline" : "")}{(trackObserver ? " + track observer" : "")}{(compareOldValueTracking ? " + compare old-value-tracking" : "")}");
Console.WriteLine();

if (compareOldValueTracking)
{
    RunOldValueTrackingComparison();
    return;
}

var results = new List<(string name, double throughput, double avgMs, int totalRounds, double heapDeltaKB, bool memoryStable)>();

results.Add(RunScenario("Movement", CreateMoveRequests));
results.Add(RunScenario("Attack", CreateAttackRequests));

Console.WriteLine();
Console.WriteLine("=== Final Summary ===");
Console.WriteLine();
Console.WriteLine($"{"Scenario",-18} | {"Rounds/s",10} | {"ms/round",10} | {"Rounds",8} | {"Heap Δ KB",10} | {"Memory",8}");
Console.WriteLine(new string('-', 76));
foreach (var (name, throughput, avgMs, totalRounds, heapDeltaKB, memoryStable) in results)
{
    Console.WriteLine($"{name,-18} | {throughput,10:F1} | {avgMs,10:F3} | {totalRounds,8} | {heapDeltaKB,10:F1} | {(memoryStable ? "OK" : "WARN"),8}");
}

Console.WriteLine();
var baselinePassed = true;
if (checkBaseline)
{
    baselinePassed = CheckBaseline(results);
}

if (updateBaseline)
{
    UpdateKnowledgePage(results);
    Console.WriteLine("Baseline updated in .knowledge/kb-hero-pipeline-regression.md");
}
else
{
    Console.WriteLine("Baseline not updated. Pass --update-baseline to write current results.");
}

if (!baselinePassed)
{
    Environment.ExitCode = 1;
}

void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run -c Release --project tools/perf/HeroComing.Perf [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --check-baseline   Compare results against thresholds in .knowledge/kb-hero-pipeline-regression.md.");
    Console.WriteLine("  --update-baseline  Update the baseline block in .knowledge/kb-hero-pipeline-regression.md.");
    Console.WriteLine("  --track-observer            Attach TrackValueChanges<T>() observers to hot components.");
    Console.WriteLine("  --compare-old-value-tracking Compare MiniArch TrackValueChanges vs manual dense/dict shadow-diff trackers.");
    Console.WriteLine("  --help                      Show this help.");
}

// --- Scenario runner ---

(string name, double throughput, double avgMs, int totalRounds, double heapDeltaKB, bool memoryStable) RunScenario(
    string name, Action<MiniArchRuntime, List<Entity>, bool[]> createRequests)
{
    var r = RunScenarioInternal(name, createRequests, null, null);
    return (r.name, r.throughput, r.avgMs, r.totalRounds, r.heapDeltaKB, r.memoryStable);
}

(string name, double throughput, double avgMs, int totalRounds, double heapDeltaKB, bool memoryStable, TrackObserver? observer) RunScenarioInternal(
    string name, Action<MiniArchRuntime, List<Entity>, bool[]> createRequests,
    Func<MiniArchRuntime, TrackObserver>? observerFactory,
    string? displayName)
{
    string headerName = displayName ?? name;
    Console.WriteLine($"--- {headerName} ---");

    var fixture = new CharacterTestFixture();
    fixture.AddCoreSystems();
    fixture.Core.AddSpawnSystem();

    var runtime = fixture.Runtime;

    int playersPerRow = GridWidth / 2;
    int playerCount = CharacterCount / 2;
    int enemyCount = CharacterCount / 2;

    for (int i = 0; i < playerCount; i++)
        CharacterSpawnBootstrap.CreatePlayerAt(runtime, i % playersPerRow, i / playersPerRow);

    for (int i = 0; i < enemyCount; i++)
        CharacterSpawnBootstrap.CreateSandbagEnemyAt(runtime, i % playersPerRow, i / playersPerRow + GridHeight / 2);

    fixture.StepUntilStable();

    var players = new List<Entity>();
    var frame = runtime.CurrentFrame;
    var spawnQuery = new QueryDescription().With<SpawnKind>();
    foreach (var chunk in frame.ChunkQuery(spawnQuery).GetChunks())
    {
        var kinds = chunk.GetSpan<SpawnKind>();
        var entities = chunk.GetEntities();
        for (int i = 0; i < chunk.Count; i++)
        {
            if (kinds[i] == CharacterSpawnKinds.Player)
                players.Add(entities[i]);
        }
    }
    Console.WriteLine($"Spawned {CharacterCount} characters, found {players.Count} players.");

    var playerMembership = BuildPlayerMembership(players);
    var observer = observerFactory is not null
        ? observerFactory(runtime)
        : (trackObserver ? CreateTrackObserver(name, runtime) : null);

    // Warmup
    for (int i = 0; i < WarmupRounds; i++)
    {
        observer?.BeforeRound();
        createRequests(runtime, players, playerMembership);
        fixture.StepUntilStable();
        observer?.Drain();
    }
    observer?.ResetMetrics();

    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    long baselineHeap = GC.GetTotalMemory(true);

    int gen0Base = GC.CollectionCount(0);
    int gen1Base = GC.CollectionCount(1);
    int gen2Base = GC.CollectionCount(2);

    Console.WriteLine($"{"Round",7} | {"Rounds/s",10} | {"Heap MB",9} | {"dHeap KB",10} | {"WS MB",8} | {"Gen0",5} | {"Gen1",5} | {"Gen2",5}");
    Console.WriteLine(new string('-', 75));

    var sw = Stopwatch.StartNew();
    int totalRounds = 0;
    long lastReportTime = sw.ElapsedMilliseconds;
    long lastReportRounds = 0;

    while (sw.ElapsedMilliseconds < DurationSeconds * 1000L)
    {
        observer?.BeforeRound();
        createRequests(runtime, players, playerMembership);
        fixture.StepUntilStable();
        observer?.Drain();
        totalRounds++;

        if (totalRounds % ReportInterval == 0)
        {
            long now = sw.ElapsedMilliseconds;
            double elapsed = (now - lastReportTime) / 1000.0;
            double roundsPerSec = (totalRounds - lastReportRounds) / elapsed;

            long currentHeap = GC.GetTotalMemory(false);
            double heapMB = currentHeap / (1024.0 * 1024.0);
            double deltaHeapKB = (currentHeap - baselineHeap) / 1024.0;

            var process = Process.GetCurrentProcess();
            double wsMB = process.WorkingSet64 / (1024.0 * 1024.0);

            int gen0 = GC.CollectionCount(0) - gen0Base;
            int gen1 = GC.CollectionCount(1) - gen1Base;
            int gen2 = GC.CollectionCount(2) - gen2Base;

            Console.WriteLine(
                $"{totalRounds,7} | {roundsPerSec,10:F1} | {heapMB,9:F2} | {deltaHeapKB,10:F1} | {wsMB,8:F1} | {gen0,5} | {gen1,5} | {gen2,5}");

            lastReportTime = now;
            lastReportRounds = totalRounds;
        }
    }

    sw.Stop();
    double totalElapsed = sw.ElapsedMilliseconds / 1000.0;
    double avgThroughput = totalRounds / totalElapsed;
    double avgTimePerRound = totalElapsed / totalRounds * 1000.0;

    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    long finalHeap = GC.GetTotalMemory(true);
    double heapDeltaKB = (finalHeap - baselineHeap) / 1024.0;
    bool memoryStable = heapDeltaKB < 1024;

    int finalGen0 = GC.CollectionCount(0) - gen0Base;
    int finalGen1 = GC.CollectionCount(1) - gen1Base;
    int finalGen2 = GC.CollectionCount(2) - gen2Base;

    Console.WriteLine();
    Console.WriteLine($"  Total rounds:   {totalRounds}");
    Console.WriteLine($"  Avg throughput: {avgThroughput:F1} rounds/s");
    Console.WriteLine($"  Avg time/round: {avgTimePerRound:F3}ms");
    Console.WriteLine($"  Heap delta:     {heapDeltaKB:F1} KB");
    Console.WriteLine($"  GC Gen0/1/2:    {finalGen0}/{finalGen1}/{finalGen2}");
    if (observer is not null)
        Console.WriteLine($"  Observer: {observer.Description}, changes={observer.TotalChanges}, checksum={observer.Checksum}");
    Console.WriteLine(memoryStable ? "  OK: Memory stable" : "  WARN: Memory growing");
    Console.WriteLine();

    return (headerName, avgThroughput, avgTimePerRound, totalRounds, heapDeltaKB, memoryStable, observer);
}

// --- Request creators ---

void CreateMoveRequests(MiniArchRuntime runtime, List<Entity> players, bool[] _)
{
    foreach (var player in players)
        CharacterMovementBootstrap.CreateMoveRequest(runtime, player, 1, 0);
}

void CreateAttackRequests(MiniArchRuntime runtime, List<Entity> players, bool[] playerMembership)
{
    var frame = runtime.CurrentFrame;
    var actionQuery = new QueryDescription().With<ActionEntity>().With<ActionKind>();
    foreach (var chunk in frame.ChunkQuery(actionQuery).GetChunks())
    {
        var kinds = chunk.GetSpan<ActionKind>();
        var entities = chunk.GetEntities();
        for (int i = 0; i < chunk.Count; i++)
        {
            if (kinds[i] == CharacterActionKinds.Attack &&
                frame.TryGetParent(entities[i], out Entity parent) &&
                (uint)parent.Id < (uint)playerMembership.Length && playerMembership[parent.Id])
            {
                CharacterAttackBootstrap.CreateAttackRequest(runtime, entities[i], 0, GridHeight / 2);
            }
        }
    }
}

bool[] BuildPlayerMembership(List<Entity> players)
{
    int maxId = 0;
    foreach (var player in players)
    {
        if (player.Id > maxId)
            maxId = player.Id;
    }

    var membership = new bool[maxId + 1];
    foreach (var player in players)
        membership[player.Id] = true;

    return membership;
}

TrackObserver CreateTrackObserver(string scenarioName, MiniArchRuntime runtime)
{
    return scenarioName switch
    {
        "Movement" => CreateMovementObserver(runtime),
        "Attack" => CreateAttackObserver(runtime),
        _ => throw new InvalidOperationException($"Unknown scenario '{scenarioName}' for track observer."),
    };
}

static TrackObserver CreateMovementObserver(MiniArchRuntime runtime)
{
    var posQ = runtime.World.TrackValueChanges<PositionQValue>();
    var posR = runtime.World.TrackValueChanges<PositionRValue>();
    return TrackObserver.Create(
        "Track PositionQValue + PositionRValue (value tracking)",
        obs =>
        {
            DrainValueChanges(posQ.Changes, obs, static v => v.Value);
            posQ.ClearAll();
            DrainValueChanges(posR.Changes, obs, static v => v.Value);
            posR.ClearAll();
        });
}

static TrackObserver CreateAttackObserver(MiniArchRuntime runtime)
{
    var hp = runtime.World.TrackValueChanges<CurrentHpValue>();
    return TrackObserver.Create(
        "Track CurrentHpValue (value tracking)",
        obs =>
        {
            DrainValueChanges(hp.Changes, obs, static v => v.Value);
            hp.ClearAll();
        });
}

// --- Value change drain helper ---

static void DrainValueChanges<T>(ReadOnlySpan<ValueChange<T>> changes, TrackObserver obs, Func<T, int> toInt) where T : unmanaged
{
    foreach (ref readonly var change in changes)
    {
        obs.TotalChanges++;
        obs.Checksum = HashCode.Combine(obs.Checksum, change.Entity.Id, toInt(change.Old), toInt(change.New));
    }
}
// --- Old-value tracking comparison runner ---

void RunOldValueTrackingComparison()
{
    Console.WriteLine();
    Console.WriteLine("=== Old-Value Tracking Comparison ===");
    Console.WriteLine();

    RunComparisonForScenario("Movement", CreateMoveRequests,
        static runtime => CreateMovementObserver(runtime),
        static runtime => CreateManualDenseMovementObserver(runtime),
        static runtime => CreateManualDictMovementObserver(runtime));

    RunComparisonForScenario("Attack", CreateAttackRequests,
        static runtime => CreateAttackObserver(runtime),
        static runtime => CreateManualDenseAttackObserver(runtime),
        static runtime => CreateManualDictAttackObserver(runtime));
}

void RunComparisonForScenario(
    string name,
    Action<MiniArchRuntime, List<Entity>, bool[]> createRequests,
    Func<MiniArchRuntime, TrackObserver> apiObserverFactory,
    Func<MiniArchRuntime, TrackObserver> manualDenseObserverFactory,
    Func<MiniArchRuntime, TrackObserver> manualDictObserverFactory)
{
    Console.WriteLine($">>> {name}");
    Console.WriteLine();

    // 1) MiniArch API strategy — fresh world with TrackValueChanges
    var apiResult = RunScenarioInternal(
        name, createRequests,
        observerFactory: apiObserverFactory,
        displayName: $"{name}/API");

    // 2) Manual generic shadow-diff strategy — fresh world with pre/post scan
    var manualDenseResult = RunScenarioInternal(
        name, createRequests,
        observerFactory: manualDenseObserverFactory,
        displayName: $"{name}/ManualDense");

    // 3) Manual dictionary shadow-diff strategy — fresh world with pre/post scan
    var manualDictResult = RunScenarioInternal(
        name, createRequests,
        observerFactory: manualDictObserverFactory,
        displayName: $"{name}/ManualDict");

    Console.WriteLine(
        $"  {name,-10} | {"Strategy",-12} | {"Rounds/s",10} | {"ms/round",9} | {"Rounds",7} | {"Heap Δ KB",10} | {"Changes",8} | {"Ch/Rd",7} | {"Checksum",12}");
    Console.WriteLine(new string('-', 102));
    double? apiChPerRd = null;
    foreach (var (label, result) in new[] {
        ("API", apiResult),
        ("ManualDense", manualDenseResult),
        ("ManualDict", manualDictResult) })
    {
        var (_, throughput, avgMs, totalRounds, heapDeltaKB, _, obs) = result;
        long changes = obs?.TotalChanges ?? 0;
        long checksum = obs?.Checksum ?? 0;
        double chPerRd = totalRounds > 0 ? (double)changes / totalRounds : 0;
        if (label == "API")
            apiChPerRd = chPerRd;
        Console.WriteLine(
            $"  {name,-10} | {label,-12} | {throughput,10:F1} | {avgMs,9:F3} | {totalRounds,7} | {heapDeltaKB,10:F1} | {changes,8} | {chPerRd,7:F2} | {checksum,12}");

        if (label != "API" && apiChPerRd.HasValue && Math.Abs(apiChPerRd.Value - chPerRd) > 0.01)
            Console.WriteLine($"  WARN: {name} changes/round differ materially (API={apiChPerRd:F2}, {label}={chPerRd:F2})");
    }
    Console.WriteLine("  Note: checksum is an anti-JIT-sink value, not expected to match across strategies or runs.");
    Console.WriteLine();
}

// --- Manual shadow-diff observer factories ---

static TrackObserver CreateManualDenseMovementObserver(MiniArchRuntime runtime)
{
    var frame = runtime.CurrentFrame;
    var posQTracker = new ManualGenericTracker<PositionQValue>(frame, static v => v.Value);
    var posRTracker = new ManualGenericTracker<PositionRValue>(frame, static v => v.Value);

    return TrackObserver.Create(
        "Manual dense shadow diff (PositionQValue+PositionRValue)",
        obs =>
        {
            posQTracker.Drain(obs);
            posRTracker.Drain(obs);
            posQTracker.Clear();
            posRTracker.Clear();
        },
        () =>
        {
            posQTracker.BeforeRound();
            posRTracker.BeforeRound();
        });
}

static TrackObserver CreateManualDenseAttackObserver(MiniArchRuntime runtime)
{
    var frame = runtime.CurrentFrame;
    var hpTracker = new ManualGenericTracker<CurrentHpValue>(frame, static v => v.Value);

    return TrackObserver.Create(
        "Manual dense shadow diff (CurrentHpValue)",
        obs =>
        {
            hpTracker.Drain(obs);
            hpTracker.Clear();
        },
        () =>
        {
            hpTracker.BeforeRound();
        });
}

static TrackObserver CreateManualDictMovementObserver(MiniArchRuntime runtime)
{
    var frame = runtime.CurrentFrame;
    var posQTracker = new ManualDictionaryTracker<PositionQValue>(frame, static v => v.Value);
    var posRTracker = new ManualDictionaryTracker<PositionRValue>(frame, static v => v.Value);

    return TrackObserver.Create(
        "Manual dict shadow diff (PositionQValue+PositionRValue)",
        obs =>
        {
            posQTracker.Drain(obs);
            posRTracker.Drain(obs);
            posQTracker.Clear();
            posRTracker.Clear();
        },
        () =>
        {
            posQTracker.BeforeRound();
            posRTracker.BeforeRound();
        });
}

static TrackObserver CreateManualDictAttackObserver(MiniArchRuntime runtime)
{
    var frame = runtime.CurrentFrame;
    var hpTracker = new ManualDictionaryTracker<CurrentHpValue>(frame, static v => v.Value);

    return TrackObserver.Create(
        "Manual dict shadow diff (CurrentHpValue)",
        obs =>
        {
            hpTracker.Drain(obs);
            hpTracker.Clear();
        },
        () =>
        {
            hpTracker.BeforeRound();
        });
}

// --- Knowledge page updater ---

void UpdateKnowledgePage(List<(string name, double throughput, double avgMs, int totalRounds, double heapDeltaKB, bool memoryStable)> results)
{
    string? kbPath = FindKnowledgePage();
    if (kbPath is null)
    {
        Console.Error.WriteLine("WARN: Could not find kb-hero-pipeline-regression.md, skipping baseline update.");
        return;
    }

    string content = File.ReadAllText(kbPath);
    string date = DateTime.Now.ToString("yyyy-MM-dd");

    // Update the date in front matter
    content = Regex.Replace(content, @"updated: \d{4}-\d{2}-\d{2}", $"updated: {date}");

    var baselineBlock = BuildBaselineBlock(results, date).TrimEnd();
    var replaced = Regex.Replace(
        content,
        @"## 当前 baseline（.*?）\r?\n.*?(?=\r?\n### 如果失败|\z)",
        baselineBlock,
        RegexOptions.Singleline);

    if (replaced == content)
    {
        content += Environment.NewLine + baselineBlock + Environment.NewLine;
    }
    else
    {
        content = replaced;
    }

    File.WriteAllText(kbPath, content);
    Console.WriteLine($"Updated: {kbPath}");
}

string BuildBaselineBlock(List<(string name, double throughput, double avgMs, int totalRounds, double heapDeltaKB, bool memoryStable)> results, string date)
{
    var sb = new StringBuilder();
    sb.AppendLine($"## 当前 baseline（{date}）");
    sb.AppendLine();
    sb.AppendLine("| 链路 | 吞吐量 rounds/s | 平均耗时 ms/round | 总轮数 | 内存稳定性 |");
    sb.AppendLine("|---|---|---|---|---|");
    foreach (var (name, throughput, avgMs, totalRounds, heapDeltaKB, memoryStable) in results)
    {
        string desc = name.Contains("Movement") ? "无 collision" : "含 collision";
        sb.AppendLine($"| {name}（{desc}） | {throughput:F1} | {avgMs:F1} | {totalRounds} | {(memoryStable ? "稳定" : "增长")} |");
    }

    var movResult = results.First(r => r.name.Contains("Movement"));
    var atkResult = results.First(r => r.name.Contains("Attack"));
    sb.AppendLine();
    sb.AppendLine("### 回归阈值");
    sb.AppendLine();
    sb.AppendLine($"- Movement 吞吐量：≥{(int)(movResult.throughput * 0.8)} rounds/s（baseline 的 80%）");
    sb.AppendLine($"- Attack 吞吐量：≥{(int)(atkResult.throughput * 0.8)} rounds/s（baseline 的 80%）");
    sb.AppendLine("- 内存：heap delta 不能持续增长（允许 ±10% 波动）");
    return sb.ToString();
}

bool CheckBaseline(List<(string name, double throughput, double avgMs, int totalRounds, double heapDeltaKB, bool memoryStable)> results)
{
    Console.WriteLine();
    Console.WriteLine("=== Baseline Check ===");

    if (!TryReadThresholds(out var movementThreshold, out var attackThreshold))
    {
        Console.Error.WriteLine("FAIL: Could not read regression thresholds from .knowledge/kb-hero-pipeline-regression.md.");
        return false;
    }

    var movement = results.First(r => r.name.Contains("Movement"));
    var attack = results.First(r => r.name.Contains("Attack"));
    var movementOk = movement.throughput >= movementThreshold;
    var attackOk = attack.throughput >= attackThreshold;
    var memoryOk = results.All(r => r.memoryStable);

    Console.WriteLine($"Movement: {movement.throughput:F1} rounds/s >= {movementThreshold:F1} => {(movementOk ? "OK" : "FAIL")}");
    Console.WriteLine($"Attack:   {attack.throughput:F1} rounds/s >= {attackThreshold:F1} => {(attackOk ? "OK" : "FAIL")}");
    Console.WriteLine($"Memory:   {(memoryOk ? "OK" : "FAIL")}");

    return movementOk && attackOk && memoryOk;
}

bool TryReadThresholds(out double movementThreshold, out double attackThreshold)
{
    movementThreshold = 0;
    attackThreshold = 0;

    string? kbPath = FindKnowledgePage();
    if (kbPath is null) return false;

    var content = File.ReadAllText(kbPath);
    var movementMatch = Regex.Match(content, @"Movement 吞吐量：≥(?<value>\d+(?:\.\d+)?) rounds/s");
    var attackMatch = Regex.Match(content, @"Attack 吞吐量：≥(?<value>\d+(?:\.\d+)?) rounds/s");
    return movementMatch.Success &&
           attackMatch.Success &&
           double.TryParse(movementMatch.Groups["value"].Value, out movementThreshold) &&
           double.TryParse(attackMatch.Groups["value"].Value, out attackThreshold);
}

string? FindKnowledgePage()
{
    // Try relative to working directory
    string[] candidates = [
        ".knowledge/kb-hero-pipeline-regression.md",
        "../.knowledge/kb-hero-pipeline-regression.md",
        "../../.knowledge/kb-hero-pipeline-regression.md",
    ];
    foreach (var c in candidates)
    {
        if (File.Exists(c)) return Path.GetFullPath(c);
    }

    // Walk up from assembly location
    string? dir = AppContext.BaseDirectory;
    while (dir != null)
    {
        string path = Path.Combine(dir, ".knowledge", "kb-hero-pipeline-regression.md");
        if (File.Exists(path)) return path;
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}

file sealed class TrackObserver
{
    private readonly Action<TrackObserver> _drain;
    private readonly Action _beforeRound;

    private TrackObserver(string description, Action<TrackObserver> drain, Action beforeRound)
    {
        Description = description;
        _drain = drain;
        _beforeRound = beforeRound;
    }

    public string Description { get; }

    public long TotalChanges { get; set; }

    public long Checksum { get; set; }

    public void BeforeRound() => _beforeRound();

    public void Drain() => _drain(this);

    public void ResetMetrics()
    {
        TotalChanges = 0;
        Checksum = 0;
    }

    public static TrackObserver Create(string description, Action<TrackObserver> drain)
        => new(description, drain, () => { });

    public static TrackObserver Create(string description, Action<TrackObserver> drain, Action beforeRound)
        => new(description, drain, beforeRound);
}

// --- Manual generic shadow-diff tracker: scans FrameView before/after round ---

file sealed class ManualGenericTracker<T> where T : unmanaged
{
    private readonly FrameView _frame;
    private readonly Func<T, int> _toInt;
    private int[] _oldValues = Array.Empty<int>();
    private int[] _touchedEntities = Array.Empty<int>();
    private int _touchedCount;

    public ManualGenericTracker(FrameView frame, Func<T, int> toInt)
    {
        _frame = frame;
        _toInt = toInt;
    }

    public void BeforeRound()
    {
        _touchedCount = 0;
        var query = new QueryDescription().With<T>();
        foreach (var chunk in _frame.ChunkQuery(query).GetChunks())
        {
            var span = chunk.GetSpan<T>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                int entityId = entities[i].Id;
                int value = _toInt(span[i]);

                if (entityId >= _oldValues.Length)
                    Array.Resize(ref _oldValues, Math.Max(entityId + 1, _oldValues.Length * 2));
                _oldValues[entityId] = value;

                if (_touchedCount >= _touchedEntities.Length)
                    Array.Resize(ref _touchedEntities, Math.Max(_touchedCount + 1, _touchedEntities.Length * 2));
                _touchedEntities[_touchedCount++] = entityId;
            }
        }
    }

    public void Drain(TrackObserver obs)
    {
        var query = new QueryDescription().With<T>();
        foreach (var chunk in _frame.ChunkQuery(query).GetChunks())
        {
            var span = chunk.GetSpan<T>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                int entityId = entities[i].Id;
                int oldVal = entityId < _oldValues.Length ? _oldValues[entityId] : 0;
                int newVal = _toInt(span[i]);

                if (oldVal != newVal)
                {
                    obs.TotalChanges++;
                    obs.Checksum = HashCode.Combine(obs.Checksum, entityId, oldVal, newVal);
                }
            }
        }
    }

    public void Clear()
    {
        for (int i = 0; i < _touchedCount; i++)
            _oldValues[_touchedEntities[i]] = 0;
        _touchedCount = 0;
    }
}

file sealed class ManualDictionaryTracker<T> where T : unmanaged
{
    private readonly FrameView _frame;
    private readonly Func<T, int> _toInt;
    private readonly Dictionary<int, int> _oldValues = [];

    public ManualDictionaryTracker(FrameView frame, Func<T, int> toInt)
    {
        _frame = frame;
        _toInt = toInt;
    }

    public void BeforeRound()
    {
        var query = new QueryDescription().With<T>();
        foreach (var chunk in _frame.ChunkQuery(query).GetChunks())
        {
            var span = chunk.GetSpan<T>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
                _oldValues[entities[i].Id] = _toInt(span[i]);
        }
    }

    public void Drain(TrackObserver obs)
    {
        var query = new QueryDescription().With<T>();
        foreach (var chunk in _frame.ChunkQuery(query).GetChunks())
        {
            var span = chunk.GetSpan<T>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                int entityId = entities[i].Id;
                int newVal = _toInt(span[i]);
                if (_oldValues.TryGetValue(entityId, out int oldVal) && oldVal != newVal)
                {
                    obs.TotalChanges++;
                    obs.Checksum = HashCode.Combine(obs.Checksum, entityId, oldVal, newVal);
                }
            }
        }
    }

    public void Clear() => _oldValues.Clear();
}
