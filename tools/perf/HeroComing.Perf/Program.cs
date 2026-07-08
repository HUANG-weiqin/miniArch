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
const string HelpArg = "--help";

var updateBaseline = args.Contains(UpdateBaselineArg, StringComparer.OrdinalIgnoreCase);
var checkBaseline = args.Contains(CheckBaselineArg, StringComparer.OrdinalIgnoreCase);
var trackObserver = args.Contains(TrackObserverArg, StringComparer.OrdinalIgnoreCase);
var showHelp = args.Contains(HelpArg, StringComparer.OrdinalIgnoreCase) ||
               args.Contains("-h", StringComparer.OrdinalIgnoreCase);
var knownArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    UpdateBaselineArg,
    CheckBaselineArg,
    TrackObserverArg,
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

Console.WriteLine("=== HeroComing ECS Performance Test ===");
Console.WriteLine($"Characters: {CharacterCount}");
Console.WriteLine($"Grid:       {GridWidth}x{GridHeight}");
Console.WriteLine($"Duration:   {DurationSeconds}s per scenario");
Console.WriteLine($"Mode:       measure{(checkBaseline ? " + check baseline" : "")}{(updateBaseline ? " + update baseline" : "")}{(trackObserver ? " + track observer" : "")}");
Console.WriteLine();

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
    Console.WriteLine("  --track-observer   Attach Track().Previous().ValueChanges<T>() observers to hot components.");
    Console.WriteLine("  --help             Show this help.");
}

// --- Scenario runner ---

(string name, double throughput, double avgMs, int totalRounds, double heapDeltaKB, bool memoryStable) RunScenario(
    string name, Action<MiniArchRuntime, List<Entity>, bool[]> createRequests)
{
    Console.WriteLine($"--- {name} ---");

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
    var observer = trackObserver ? CreateTrackObserver(name, runtime.World) : null;

    // Warmup
    for (int i = 0; i < WarmupRounds; i++)
    {
        createRequests(runtime, players, playerMembership);
        fixture.StepUntilStable();
        observer?.Drain();
    }

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
        Console.WriteLine($"  Track observer: {observer.Description}, transitions={observer.TotalTransitions}, changes={observer.TotalChanges}, checksum={observer.Checksum}");
    Console.WriteLine(memoryStable ? "  OK: Memory stable" : "  WARN: Memory growing");
    Console.WriteLine();

    return (name, avgThroughput, avgTimePerRound, totalRounds, heapDeltaKB, memoryStable);
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

TrackObserver CreateTrackObserver(string scenarioName, World world)
{
    return scenarioName switch
    {
        "Movement" => TrackObserver.Create(
            "Capture PositionQValue + PositionRValue (no Previous, no filter)",
            world,
            static world =>
            {
                _ = world.Track().Capture<PositionQValue>().Capture<PositionRValue>();
                return static _ => { };
            }),
        "Attack" => TrackObserver.Create(
            "Capture CurrentHpValue (no Previous, no filter)",
            world,
            static world =>
            {
                _ = world.Track().Capture<CurrentHpValue>();
                return static _ => { };
            }),
        _ => throw new InvalidOperationException($"Unknown scenario '{scenarioName}' for track observer."),
    };
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

    private TrackObserver(string description, Action<TrackObserver> drain)
    {
        Description = description;
        _drain = drain;
    }

    public string Description { get; }

    public long TotalChanges { get; set; }

    public long TotalTransitions { get; set; }

    public long Checksum { get; set; }

    public void Drain() => _drain(this);

    public static TrackObserver Create(string description, World world, Func<World, Action<TrackObserver>> factory)
        => new(description, factory(world));
}
