using System.Runtime.ExceptionServices;
using System.Threading;

namespace MiniArch;

internal static class ParallelQueryRunner
{
    [ThreadStatic]
    private static int t_callbackDepth;

    internal static void Run<TForEach>(ChunkView[] chunks, int count, TForEach forEach)
        where TForEach : struct, IChunkForEach
    {
        if (count == 0)
            return;

        var state = Cache<TForEach>.Rent();
        try
        {
            var threads = Environment.ProcessorCount;
            var partitionCount = state.Build(chunks, count, threads);
            if (partitionCount == 0)
                return;
            // Only the split route called one partition directly; even one
            // direct chunk previously went through Parallel.For.
            if (count < threads && partitionCount == 1)
            {
                forEach.OnChunk(state.Partitions[0]);
                return;
            }

            var workerCount = 0;
            // Nested callbacks stay on their current thread when the pool is saturated.
            if (t_callbackDepth == 0)
            {
                ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
                // Leave a slot for another ThreadPool caller instead of joining
                // work that cannot start under a tight worker limit.
                if (Thread.CurrentThread.IsThreadPoolThread)
                    availableWorkers = Math.Max(0, availableWorkers - 1);
                workerCount = Math.Min(partitionCount - 1,
                    Math.Min(threads - 1, availableWorkers));
            }

            state.Prepare(forEach, workerCount);
            Exception? queueError = null;
            var submitted = 0;
            try
            {
                for (; submitted < workerCount; submitted++)
                {
                    if (!ThreadPool.UnsafeQueueUserWorkItem(state.Workers[submitted], preferLocal: false))
                        throw new InvalidOperationException("ThreadPool rejected a parallel query work item.");
                }
            }
            catch (Exception error)
            {
                queueError = error;
                state.RequestStop();
            }
            finally
            {
                state.AbandonUnsubmitted(workerCount - submitted);
            }

            Exception? callerError = null;
            if (queueError is null)
            {
                try
                {
                    state.RunCaller();
                }
                catch (Exception error)
                {
                    callerError = error;
                    state.RequestStop();
                }
            }

            state.WaitForWorkers();
            if (queueError is not null)
                ExceptionDispatchInfo.Capture(queueError).Throw();
            state.ThrowIfFailed(callerError);
        }
        finally
        {
            state.Clear();
        }
    }

    private static class Cache<TForEach> where TForEach : struct, IChunkForEach
    {
        [ThreadStatic]
        private static RunState<TForEach>? t_state;

        internal static RunState<TForEach> Rent()
        {
            var state = t_state;
            if (state is null)
            {
                state = new RunState<TForEach>();
                t_state = state;
            }
            while (state.InUse)
                state = state.Next ??= new RunState<TForEach>();

            state.InUse = true;
            return state;
        }
    }

    private sealed class RunState<TForEach> where TForEach : struct, IChunkForEach
    {
        private static readonly ContextCallback s_runWorker = static state => ((Worker)state!).RunCallbacks();
        private static readonly ContextCallback s_runCaller = static state => ((RunState<TForEach>)state!).RunCallerCallbacks();
        private readonly ManualResetEventSlim _done = new(false);
        private ChunkView[] _partitions = [];
        private ChunkView[] _splitBuffer = [];
        private Worker[] _workers = [];
        private TForEach _forEach;
        private ExecutionContext? _context;
        private int _partitionCount;
        private int _workerCount;
        private int _nextIndex;
        private int _pending;
        private int _completionAcknowledged;
        private int _stop;

        internal bool InUse { get; set; }
        internal RunState<TForEach>? Next { get; set; }
        internal ChunkView[] Partitions => _partitions;
        internal Worker[] Workers => _workers;

        internal int Build(ChunkView[] chunks, int count, int threads)
        {
            if (count >= threads)
            {
                _partitions = chunks;
                _partitionCount = count;
                return count;
            }

            if (_splitBuffer.Length < threads)
                _splitBuffer = new ChunkView[threads];
            _partitions = _splitBuffer;
            var subPerChunk = Math.Max(1, threads / count);
            for (var i = 0; i < count; i++)
            {
                var entities = chunks[i].Count;
                var per = entities / subPerChunk;
                var remainder = entities % subPerChunk;
                var start = 0;
                for (var j = 0; j < subPerChunk; j++)
                {
                    var size = per + (j < remainder ? 1 : 0);
                    if (size > 0)
                        _partitions[_partitionCount++] = chunks[i].Slice(start, size);
                    start += size;
                }
            }

            return _partitionCount;
        }

        internal void Prepare(TForEach forEach, int workerCount)
        {
            if (_workers.Length < workerCount)
            {
                var expanded = new Worker[workerCount];
                Array.Copy(_workers, expanded, _workers.Length);
                for (var i = _workers.Length; i < expanded.Length; i++)
                    expanded[i] = new Worker(this);
                _workers = expanded;
            }

            _forEach = forEach;
            _context = ExecutionContext.Capture();
            _workerCount = workerCount;
            _nextIndex = 0; // The caller owns partition zero.
            _pending = workerCount;
            _completionAcknowledged = 0;
            _stop = 0;
            _done.Reset();
        }

        internal void RunCaller()
        {
            if (_context is null)
                RunCallerCallbacks();
            else
                ExecutionContext.Run(_context, s_runCaller, this);
        }

        private void RunCallerCallbacks()
        {
            t_callbackDepth++;
            try
            {
                if (Volatile.Read(ref _stop) == 0)
                    _forEach.OnChunk(_partitions[0]);
                RunRemaining();
            }
            finally
            {
                t_callbackDepth--;
            }
        }

        private void RunRemaining()
        {
            while (Volatile.Read(ref _stop) == 0)
            {
                var index = Interlocked.Increment(ref _nextIndex);
                if (index >= _partitionCount)
                    return;
                _forEach.OnChunk(_partitions[index]);
            }
        }

        internal void RequestStop() => Volatile.Write(ref _stop, 1);

        internal void AbandonUnsubmitted(int count)
        {
            if (count > 0 && Interlocked.Add(ref _pending, -count) == 0)
                SignalCompletion();
        }

        private void SignalCompletion()
        {
            _done.Set();
            // IsSet becomes true before Set finishes notifying waiters.
            // The caller must see this tail acknowledgement before reusing the state.
            Volatile.Write(ref _completionAcknowledged, 1);
        }

        internal void WaitForWorkers()
        {
            ThreadInterruptedException? interruption = null;
            while (_workerCount > 0 && Volatile.Read(ref _completionAcknowledged) == 0)
            {
                try
                {
                    if (_done.IsSet)
                        Thread.Yield();
                    else
                        _done.Wait();
                }
                catch (ThreadInterruptedException error)
                {
                    interruption ??= error;
                }
            }

            if (interruption is not null)
                ExceptionDispatchInfo.Capture(interruption).Throw();
        }

        internal void ThrowIfFailed(Exception? callerError)
        {
            List<Exception>? errors = callerError is null ? null : [callerError];
            for (var i = 0; i < _workerCount; i++)
            {
                var error = _workers[i].Error;
                if (error is not null)
                    (errors ??= []).Add(error);
            }
            if (errors is not null)
                throw new AggregateException(errors);
        }

        internal void Clear()
        {
            // Direct chunks belong to QueryCache; clear only our split copies.
            if (ReferenceEquals(_partitions, _splitBuffer))
                Array.Clear(_splitBuffer, 0, _partitionCount);
            _partitions = [];
            for (var i = 0; i < _workerCount; i++)
                _workers[i].Error = null;
            _forEach = default;
            _context = null;
            _partitionCount = 0;
            _workerCount = 0;
            InUse = false;
        }

        internal sealed class Worker(RunState<TForEach> owner) : IThreadPoolWorkItem
        {
            internal Exception? Error;

            public void Execute()
            {
                try
                {
                    var context = owner._context;
                    if (context is null)
                        RunCallbacks();
                    else
                        ExecutionContext.Run(context, s_runWorker, this);
                }
                catch (Exception error)
                {
                    Error = error;
                    owner.RequestStop();
                }
                finally
                {
                    if (Interlocked.Decrement(ref owner._pending) == 0)
                        owner.SignalCompletion();
                }
            }

            internal void RunCallbacks()
            {
                t_callbackDepth++;
                try
                {
                    owner.RunRemaining();
                }
                finally
                {
                    t_callbackDepth--;
                }
            }
        }
    }
}
