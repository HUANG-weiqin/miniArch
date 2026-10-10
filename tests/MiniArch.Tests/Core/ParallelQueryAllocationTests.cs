using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArchTests.Core;

[Collection(nameof(ThreadPoolSensitiveCollection))]
public sealed class ParallelQueryAllocationTests
{
    private readonly record struct Marker(int QueryId);

    private sealed class RowCounter
    {
        public int Rows;
        public void Visit(ChunkView chunk) => Interlocked.Add(ref Rows, chunk.Count);
    }

    private readonly struct CountJob(RowCounter counter) : IChunkForEach
    {
        public void OnChunk(ChunkView chunk) => Interlocked.Add(ref counter.Rows, chunk.Count);
    }

    // Routes: empty, one entity, one four-row chunk (split on multicore), and at least CPU chunks.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public Task BUG_parallel_query_steady_state_allocates_zero_bytes(int route, bool useStruct)
        => RunAllocationChildAsync("matrix", route.ToString(CultureInfo.InvariantCulture),
            useStruct ? "struct" : "delegate");

    [Fact]
    public Task BUG_nested_prebuilt_queries_preserve_rows_and_allocate_zero_bytes()
        => RunAllocationChildAsync("nested");

    internal static void MeasureSteadyState(int route, bool useStruct)
    {
        var (world, query, rows, _) = MakeQuery(route, 1);
        var counter = new RowCounter();
        ChunkAction callback = counter.Visit;
        var job = new CountJob(counter);

        for (int i = 0; i < 4_096; i++)
        {
            if (useStruct) query.ForEachChunkParallel(job);
            else query.ForEachChunkParallel(callback);
        }
        DrainFinalizers();

        counter.Rows = 0;
        const int calls = 32;
        int completedOnCaller = 0;
        long before = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < calls; i++)
        {
            if (useStruct) query.ForEachChunkParallel(job);
            else query.ForEachChunkParallel(callback);
            completedOnCaller++;
        }
        long allocated = GC.GetTotalAllocatedBytes(true) - before;

        GC.KeepAlive(world);
        Assert.Equal(calls, completedOnCaller);
        Assert.Equal(rows * calls, counter.Rows);
        Assert.Equal(0, allocated);
    }

    internal static void MeasureNested()
    {
        var (outerWorld, outer, outerRows, outerChunks) = MakeQuery(3, 1);
        var (innerWorld, inner, innerRows, _) = MakeQuery(2, 2);
        var probe = new NestedProbe(inner);
        ChunkAction callback = probe.VisitOuter;

        for (int i = 0; i < 4_096; i++) outer.ForEachChunkParallel(callback);
        DrainFinalizers();
        probe.Reset();

        const int calls = 16;
        int completedOnCaller = 0;
        long before = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < calls; i++)
        {
            outer.ForEachChunkParallel(callback);
            completedOnCaller++;
        }
        long allocated = GC.GetTotalAllocatedBytes(true) - before;

        GC.KeepAlive(outerWorld);
        GC.KeepAlive(innerWorld);
        Assert.Equal(calls, completedOnCaller);
        Assert.Equal(outerRows * calls, probe.OuterRows);
        Assert.Equal(outerChunks * innerRows * calls, probe.InnerRows);
        Assert.Equal(0, probe.WrongRows);
        Assert.Equal(0, allocated);
    }

    private static async Task RunAllocationChildAsync(params string[] caseArguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(ParallelQueryAllocationTests).Assembly.Location);
        start.ArgumentList.Add("--parallel-query-allocation");
        foreach (string argument in caseArguments)
            start.ArgumentList.Add(argument);

        using var child = Process.Start(start) ?? throw new InvalidOperationException("Allocation child did not start.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // The child exited between the check and Kill.
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await child.WaitForExitAsync(cleanup.Token); }
            catch (OperationCanceledException) { }
            throw new TimeoutException($"Allocation child timed out: {string.Join(' ', caseArguments)}\n{ChildResult(child, stdout, stderr)}");
        }

        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Allocation child output timed out: {string.Join(' ', caseArguments)}\n{ChildResult(child, stdout, stderr)}");
        }
        Assert.True(child.ExitCode == 0,
            $"Allocation child failed: {string.Join(' ', caseArguments)}\n{ChildResult(child, stdout, stderr)}");
    }

    private static string ChildResult(Process child, Task<string> stdout, Task<string> stderr)
        => $"exit={(child.HasExited ? child.ExitCode.ToString(CultureInfo.InvariantCulture) : "still running")}\n" +
           $"stdout:\n{(stdout.IsCompletedSuccessfully ? stdout.Result : "<stream not closed>")}\n" +
           $"stderr:\n{(stderr.IsCompletedSuccessfully ? stderr.Result : "<stream not closed>")}";

    private static void DrainFinalizers()
    {
        // Keep startup and Gen2 callbacks out of the steady-state allocation window.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public void Parallel_query_does_not_root_completed_callback_world_or_ambient_value()
    {
        var references = InvokeAndDropReferences();
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(references.Callback.IsAlive);
        Assert.False(references.CapturedMarker.IsAlive);
        Assert.False(references.World.IsAlive);
        Assert.False(references.AmbientMarker.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Callback, WeakReference CapturedMarker,
        WeakReference World, WeakReference AmbientMarker) InvokeAndDropReferences()
    {
        var (world, query, rows, _) = MakeQuery(3, 1);
        var capturedMarker = new object();
        var ambientMarker = new object();
        var ambient = new AsyncLocal<object?>();
        ambient.Value = ambientMarker;
        int visited = 0;
        ChunkAction callback = chunk =>
        {
            GC.KeepAlive(capturedMarker);
            GC.KeepAlive(world);
            GC.KeepAlive(ambient.Value);
            Interlocked.Add(ref visited, chunk.Count);
        };

        try { query.ForEachChunkParallel(callback); }
        finally { ambient.Value = null; }
        Assert.Equal(rows, visited);
        return (new WeakReference(callback), new WeakReference(capturedMarker),
            new WeakReference(world), new WeakReference(ambientMarker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parallel_query_return_or_exception_waits_for_running_callback(bool throwOnOtherCallback)
    {
        if (Environment.ProcessorCount < 2) return;

        var (world, query, _, _) = MakeQuery(2, 1);
        var probe = new CompletionProbe(throwOnOtherCallback);
        ChunkAction callback = probe.Visit;
        Task? call = null;
        ThreadPool.GetMinThreads(out int originalWorkers, out int originalIo);
        try
        {
            Assert.True(ThreadPool.SetMinThreads(Math.Max(2, originalWorkers), originalIo));
            call = Task.Run(() => query.ForEachChunkParallel(callback));
            try
            {
                Assert.True(probe.BlockerStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(probe.OtherStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(SpinWait.SpinUntil(() => call.IsCompleted, TimeSpan.FromMilliseconds(100)));
            }
            finally
            {
                probe.Release.Set();
            }

            Assert.True(SpinWait.SpinUntil(() => call.IsCompleted, TimeSpan.FromSeconds(6)) || call.IsCompleted);
            var error = Record.Exception(() => call.GetAwaiter().GetResult());
            if (throwOnOtherCallback)
            {
                Assert.NotNull(error);
                Assert.True(error is CallbackFailure ||
                    error is AggregateException aggregate &&
                    aggregate.Flatten().InnerExceptions.Any(e => e is CallbackFailure));
            }
            else Assert.Null(error);
            Assert.Equal(1, probe.BlockerFinished);
            Assert.Equal(0, probe.TimedOut);
            GC.KeepAlive(world);
        }
        finally
        {
            probe.Release.Set();
            ThreadPool.SetMinThreads(originalWorkers, originalIo);
            if (call is null || call.IsCompleted ||
                SpinWait.SpinUntil(() => call.IsCompleted, TimeSpan.FromSeconds(6)))
                probe.Dispose();
        }
    }

    [Fact]
    public void Parallel_query_can_be_reused_on_the_same_caller_after_callback_failure()
    {
        var (world, query, rows, _) = MakeQuery(3, 1);
        var counter = new RowCounter();
        ChunkAction failing = static _ => throw new CallbackFailure();
        ChunkAction succeeding = counter.Visit;

        for (int i = 0; i < 3; i++)
        {
            Assert.Throws<AggregateException>(() => query.ForEachChunkParallel(failing));
            counter.Rows = 0;
            query.ForEachChunkParallel(succeeding);
            Assert.Equal(rows, counter.Rows);
        }
        GC.KeepAlive(world);
    }

    [Fact]
    public void Concurrent_read_only_queries_keep_their_own_rows()
    {
        var (leftWorld, left, leftRows, _) = MakeQuery(3, 1);
        var (rightWorld, right, rightRows, _) = MakeQuery(3, 2);
        var leftEntered = new ManualResetEventSlim();
        var rightEntered = new ManualResetEventSlim();
        var leftProbe = new ConcurrentProbe(1, leftEntered, rightEntered);
        var rightProbe = new ConcurrentProbe(2, rightEntered, leftEntered);
        ChunkAction leftCallback = leftProbe.Visit;
        ChunkAction rightCallback = rightProbe.Visit;
        Exception? leftError = null, rightError = null;
        var leftThread = new Thread(() =>
        {
            try { left.ForEachChunkParallel(leftCallback); }
            catch (Exception error) { leftError = error; }
        }) { IsBackground = true };
        var rightThread = new Thread(() =>
        {
            try { right.ForEachChunkParallel(rightCallback); }
            catch (Exception error) { rightError = error; }
        }) { IsBackground = true };

        try
        {
            leftThread.Start();
            rightThread.Start();
            Assert.True(leftThread.Join(TimeSpan.FromSeconds(6)));
            Assert.True(rightThread.Join(TimeSpan.FromSeconds(6)));
        }
        finally
        {
            leftEntered.Set();
            rightEntered.Set();
            if (leftThread.IsAlive) leftThread.Join(TimeSpan.FromSeconds(6));
            if (rightThread.IsAlive) rightThread.Join(TimeSpan.FromSeconds(6));
            if (!leftThread.IsAlive && !rightThread.IsAlive)
            {
                leftEntered.Dispose();
                rightEntered.Dispose();
            }
        }

        Assert.Null(leftError);
        Assert.Null(rightError);
        Assert.Equal(leftRows, leftProbe.Rows);
        Assert.Equal(rightRows, rightProbe.Rows);
        Assert.Equal(0, leftProbe.WrongRows + rightProbe.WrongRows);
        GC.KeepAlive(leftWorld);
        GC.KeepAlive(rightWorld);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task Parallel_queries_finish_with_a_tight_ThreadPool_limit(int maxWorkers, int rootCount)
    {
        var (world, query, rows, _) = MakeQuery(2, 1);
        var counter = new RowCounter();
        ChunkAction callback = counter.Visit;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new Thread(() =>
        {
            try
            {
                RunPoolRoots(query, callback, maxWorkers, rootCount);
                completion.TrySetResult(true);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true };
        coordinator.Start();

        // Await releases xUnit's worker while the coordinator occupies none of the limited pool slots.
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(rows * rootCount, counter.Rows);
        GC.KeepAlive(world);
    }

    private static void RunPoolRoots(Query query, ChunkAction callback, int maxWorkers, int rootCount)
    {
        var start = new ManualResetEventSlim();
        var roots = new Task[rootCount];
        int ready = 0;
        ThreadPool.GetMinThreads(out int originalMinWorkers, out int originalMinIo);
        ThreadPool.GetMaxThreads(out int originalMaxWorkers, out int originalMaxIo);
        try
        {
            // Start roots before tightening the limit: xUnit may still occupy a pool
            // worker while awaiting this test, so limiting first can starve setup.
            for (int i = 0; i < rootCount; i++)
            {
                roots[i] = Task.Run(() =>
                {
                    Interlocked.Increment(ref ready);
                    if (!start.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Root start gate timed out.");
                    query.ForEachChunkParallel(callback);
                });
            }

            if (!SpinWait.SpinUntil(() => Volatile.Read(ref ready) == rootCount,
                    TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Pool roots did not start before limiting workers.");
            if (!ThreadPool.SetMinThreads(maxWorkers, originalMinIo) ||
                !ThreadPool.SetMaxThreads(maxWorkers, originalMaxIo))
                throw new InvalidOperationException("Unable to set the ThreadPool worker limit.");
            start.Set();
            if (!SpinWait.SpinUntil(() => AllTasksCompleted(roots), TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Parallel queries did not finish with the pool occupied.");
            foreach (var root in roots) root.GetAwaiter().GetResult();
        }
        finally
        {
            start.Set();
            ThreadPool.SetMaxThreads(originalMaxWorkers, originalMaxIo);
            ThreadPool.SetMinThreads(originalMinWorkers, originalMinIo);
            // Leave the gate alive on failure if a callback is still running.
            if (SpinWait.SpinUntil(() => AllTasksCompleted(roots), TimeSpan.FromSeconds(5)))
                start.Dispose();
        }
    }

    private static bool AllTasksCompleted(Task[] tasks)
    {
        foreach (var task in tasks)
            if (task is not null && !task.IsCompleted) return false;
        return true;
    }

    [Fact]
    public void Parallel_struct_job_shares_one_copy_within_call_but_not_with_caller()
    {
        var (world, query, rows, chunks) = MakeQuery(3, 1);
        var box = new SharedCopyBox();
        var job = new SharedCopyJob(box);
        query.ForEachChunkParallel(job);

        Assert.True(chunks >= 2);
        Assert.Equal(chunks, box.Calls);
        Assert.Equal(rows, box.Rows);
        Assert.Equal(0, job.Calls);
        GC.KeepAlive(world);
    }

    [Fact]
    public void Parallel_callbacks_read_the_callers_AsyncLocal()
    {
        var (world, query, rows, _) = MakeQuery(3, 1);
        var context = new AsyncLocal<int>();
        var probe = new FlowProbe(context, 42);
        ChunkAction callback = probe.Visit;
        context.Value = 42;
        try { query.ForEachChunkParallel(callback); }
        finally { context.Value = 0; }

        Assert.Equal(rows, probe.Rows);
        Assert.Equal(0, probe.WrongRows);
        GC.KeepAlive(world);
    }

    [Fact]
    public void Nested_parallel_callbacks_restore_AsyncLocal_and_culture_after_mutation()
    {
        var (outerWorld, outer, outerRows, outerChunks) = MakeQuery(3, 1);
        var (innerWorld, inner, innerRows, _) = MakeQuery(2, 2);
        var ambient = new AsyncLocal<object?>();
        object baselineValue = new(), outerValue = new(), innerValue = new();
        var previousValue = ambient.Value;
        var previousCulture = CultureInfo.CurrentCulture;
        var baselineCulture = CultureInfo.GetCultureInfo("en-US");
        var outerCulture = CultureInfo.GetCultureInfo("fr-FR");
        var innerCulture = CultureInfo.GetCultureInfo("ja-JP");
        try
        {
            ambient.Value = baselineValue;
            CultureInfo.CurrentCulture = baselineCulture;
            int callerThread = Environment.CurrentManagedThreadId;
            var probe = new AmbientMutationProbe(inner, ambient, outerValue, innerValue,
                outerCulture, innerCulture, callerThread);
            ChunkAction callback = probe.VisitOuter;
            outer.ForEachChunkParallel(callback);

            Assert.True(probe.CallerVisits > 0, "The caller must process an outer callback.");
            Assert.Equal(outerRows, probe.OuterRows);
            Assert.Equal(outerChunks * innerRows, probe.InnerRows);
            Assert.Equal(0, probe.WrongAfterInner);
            Assert.Same(baselineValue, ambient.Value);
            Assert.Same(baselineCulture, CultureInfo.CurrentCulture);
        }
        finally
        {
            ambient.Value = previousValue;
            CultureInfo.CurrentCulture = previousCulture;
        }

        GC.KeepAlive(outerWorld);
        GC.KeepAlive(innerWorld);
    }

    [Fact]
    public void Multicore_query_actually_runs_callbacks_on_different_threads()
    {
        if (Environment.ProcessorCount < 2) return;

        var (world, query, _, _) = MakeQuery(3, 1);
        using var probe = new ThreadProbe();
        ChunkAction callback = probe.Visit;
        ThreadPool.GetMinThreads(out int originalWorkers, out int originalIo);
        try
        {
            Assert.True(ThreadPool.SetMinThreads(Math.Max(2, originalWorkers), originalIo));
            query.ForEachChunkParallel(callback);
        }
        finally
        {
            ThreadPool.SetMinThreads(originalWorkers, originalIo);
        }

        Assert.Equal(1, probe.SawAnotherThread);
        Assert.Equal(0, probe.TimedOut);
        GC.KeepAlive(world);
    }

    private static (World World, Query Query, int Rows, int Chunks) MakeQuery(int route, int queryId)
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);
        var description = new QueryDescription().With<Marker>();
        if (route == 0) return (world, world.Query(in description), 0, 0);
        if (route == 1)
        {
            world.Create(new Marker(queryId));
            return (world, world.Query(in description), 1, 1);
        }

        int chunks = route == 2 ? 1 : Math.Max(2, Environment.ProcessorCount);
        var first = world.Create(new Marker(queryId));
        Assert.True(world.TryGetLocation(first, out var location));
        var archetype = location.Archetype;
        archetype.ForceChunkedForTesting();

        int rows = 1;
        for (int segment = 0; segment < chunks; segment++)
        {
            if (segment > 0) archetype.AddSegmentForTesting();
            for (int row = segment == 0 ? 1 : 0; row < 4; row++)
            {
                int index = archetype.AddEntity(new Entity(10_000 + rows, 1));
                archetype.SetComponentAtTyped(0, index, new Marker(queryId));
                rows++;
            }
        }

        var query = world.Query(in description);
        Assert.Equal(chunks, query.GetChunks().Length);
        return (world, query, rows, chunks);
    }

    private sealed class NestedProbe
    {
        private readonly Query _inner;
        private readonly InnerJob _innerJob;
        public int OuterRows, InnerRows, WrongRows;

        public NestedProbe(Query inner)
        {
            _inner = inner;
            _innerJob = new InnerJob(this);
        }

        public void Reset() => OuterRows = InnerRows = WrongRows = 0;

        public void VisitOuter(ChunkView chunk)
        {
            _inner.ForEachChunkParallel(_innerJob);
            foreach (ref readonly var marker in chunk.GetSpan<Marker>())
            {
                if (marker.QueryId != 1) Interlocked.Increment(ref WrongRows);
                Interlocked.Increment(ref OuterRows);
            }
        }

        private void VisitInner(ChunkView chunk)
        {
            foreach (ref readonly var marker in chunk.GetSpan<Marker>())
            {
                if (marker.QueryId != 2) Interlocked.Increment(ref WrongRows);
                Interlocked.Increment(ref InnerRows);
            }
        }

        private readonly struct InnerJob(NestedProbe owner) : IChunkForEach
        {
            public void OnChunk(ChunkView chunk) => owner.VisitInner(chunk);
        }
    }

    private sealed class CompletionProbe(bool throwOnOtherCallback) : IDisposable
    {
        private int _claimed, _thrown;
        public readonly ManualResetEventSlim BlockerStarted = new();
        public readonly ManualResetEventSlim OtherStarted = new();
        public readonly ManualResetEventSlim Release = new();
        public int BlockerFinished, TimedOut;

        public void Visit(ChunkView chunk)
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0)
            {
                BlockerStarted.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(5))) Interlocked.Increment(ref TimedOut);
                Interlocked.Exchange(ref BlockerFinished, 1);
            }
            else
            {
                OtherStarted.Set();
                if (throwOnOtherCallback && Interlocked.CompareExchange(ref _thrown, 1, 0) == 0)
                    throw new CallbackFailure();
            }
        }

        public void Dispose()
        {
            BlockerStarted.Dispose();
            OtherStarted.Dispose();
            Release.Dispose();
        }
    }

    private sealed class CallbackFailure : Exception;

    private sealed class ConcurrentProbe(int expected, ManualResetEventSlim entered, ManualResetEventSlim other)
    {
        public int Rows, WrongRows;

        public void Visit(ChunkView chunk)
        {
            entered.Set();
            if (!other.Wait(TimeSpan.FromSeconds(5))) Interlocked.Increment(ref WrongRows);
            foreach (ref readonly var marker in chunk.GetSpan<Marker>())
            {
                if (marker.QueryId != expected) Interlocked.Increment(ref WrongRows);
                Interlocked.Increment(ref Rows);
            }
        }
    }

    private sealed class SharedCopyBox
    {
        public readonly object Gate = new();
        public int Calls, Rows;
    }

    private struct SharedCopyJob(SharedCopyBox box) : IChunkForEach
    {
        public int Calls;

        public void OnChunk(ChunkView chunk)
        {
            lock (box.Gate)
            {
                Calls++;
                box.Calls = Calls;
                box.Rows += chunk.Count;
            }
        }
    }

    private sealed class FlowProbe(AsyncLocal<int> context, int expected)
    {
        public int Rows, WrongRows;

        public void Visit(ChunkView chunk)
        {
            if (context.Value != expected) Interlocked.Increment(ref WrongRows);
            Interlocked.Add(ref Rows, chunk.Count);
        }
    }

    private sealed class AmbientMutationProbe
    {
        private readonly Query _inner;
        private readonly AsyncLocal<object?> _ambient;
        private readonly object _outerValue, _innerValue;
        private readonly CultureInfo _outerCulture, _innerCulture;
        private readonly int _callerThread;
        private readonly InnerJob _innerJob;
        public int OuterRows, InnerRows, CallerVisits, WrongAfterInner;

        public AmbientMutationProbe(Query inner, AsyncLocal<object?> ambient,
            object outerValue, object innerValue, CultureInfo outerCulture,
            CultureInfo innerCulture, int callerThread)
        {
            _inner = inner;
            _ambient = ambient;
            _outerValue = outerValue;
            _innerValue = innerValue;
            _outerCulture = outerCulture;
            _innerCulture = innerCulture;
            _callerThread = callerThread;
            _innerJob = new InnerJob(this);
        }

        public void VisitOuter(ChunkView chunk)
        {
            if (Environment.CurrentManagedThreadId == _callerThread)
                Interlocked.Increment(ref CallerVisits);
            _ambient.Value = _outerValue;
            CultureInfo.CurrentCulture = _outerCulture;
            _inner.ForEachChunkParallel(_innerJob);
            if (!ReferenceEquals(_outerValue, _ambient.Value) ||
                !ReferenceEquals(_outerCulture, CultureInfo.CurrentCulture))
                Interlocked.Increment(ref WrongAfterInner);
            Interlocked.Add(ref OuterRows, chunk.Count);
        }

        private void VisitInner(ChunkView chunk)
        {
            _ambient.Value = _innerValue;
            CultureInfo.CurrentCulture = _innerCulture;
            Interlocked.Add(ref InnerRows, chunk.Count);
        }

        private readonly struct InnerJob(AmbientMutationProbe owner) : IChunkForEach
        {
            public void OnChunk(ChunkView chunk) => owner.VisitInner(chunk);
        }
    }

    private sealed class ThreadProbe : IDisposable
    {
        private readonly ManualResetEventSlim _anotherEntered = new();
        private int _firstThreadId;
        public int SawAnotherThread, TimedOut;

        public void Visit(ChunkView chunk)
        {
            int threadId = Environment.CurrentManagedThreadId;
            if (Interlocked.CompareExchange(ref _firstThreadId, threadId, 0) == 0)
            {
                if (!_anotherEntered.Wait(TimeSpan.FromSeconds(5))) Interlocked.Increment(ref TimedOut);
            }
            else if (threadId != Volatile.Read(ref _firstThreadId))
            {
                Interlocked.Exchange(ref SawAnotherThread, 1);
                _anotherEntered.Set();
            }
        }

        public void Dispose() => _anotherEntered.Dispose();
    }
}
