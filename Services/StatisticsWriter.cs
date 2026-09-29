using System.Collections.Concurrent;

namespace IndxServer.Services
{
    /// <summary>
    /// The asynchronous batch writer between the request paths and <see cref="StatisticsStore"/>.
    /// Enqueue costs a struct and a queue append — that is all the search path ever pays — and a
    /// background loop flushes batches on an interval. It is the store's only writer.
    ///
    /// Loss on restart is bounded and honest: the unflushed batch is gone, shutdown logs what was
    /// flushed over the writer's lifetime, and startup (store.LogState) shows what survived. The
    /// queue is capped so a stalled disk degrades to dropped statistics — counted and logged —
    /// never to unbounded memory.
    /// </summary>
    public sealed class StatisticsWriter(StatisticsStore store, ILogger<StatisticsWriter> logger) : IDisposable
    {
        /// <summary>Flush cadence. Short enough that a restart window is small, long enough to batch.</summary>
        public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);

        /// <summary>Cap on queued events; beyond it new events are dropped and counted.</summary>
        public int MaxQueued { get; init; } = 200_000;

        private readonly ConcurrentQueue<object> _queue = new();
        private int _queued;
        private long _dropped;
        private long _flushedTotal;
        private long _flushes;
        private long _flushMsTotal;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public long DroppedTotal => Interlocked.Read(ref _dropped);
        public long FlushedTotal => Interlocked.Read(ref _flushedTotal);
        public long Flushes => Interlocked.Read(ref _flushes);
        public long FlushMsTotal => Interlocked.Read(ref _flushMsTotal);

        public void RecordSearch(in SearchEventRow row) => Enqueue(row);
        public void RecordSelect(in SelectEventRow row) => Enqueue(row);
        public void RecordConvert(in ConvertEventRow row) => Enqueue(row);

        private void Enqueue(object row)
        {
            if (Interlocked.Increment(ref _queued) > MaxQueued)
            {
                Interlocked.Decrement(ref _queued);
                Interlocked.Increment(ref _dropped);
                return;
            }
            _queue.Enqueue(row);
        }

        public void Start()
        {
            if (_loop != null) throw new InvalidOperationException("The statistics writer is already started.");
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(FlushInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                    Flush();
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>Drains the queue into one store transaction. Also called directly by tests.</summary>
        public void Flush()
        {
            if (_queue.IsEmpty) return;
            var searches = new List<SearchEventRow>();
            var selects = new List<SelectEventRow>();
            var converts = new List<ConvertEventRow>();
            while (_queue.TryDequeue(out var row))
            {
                Interlocked.Decrement(ref _queued);
                switch (row)
                {
                    case SearchEventRow s: searches.Add(s); break;
                    case SelectEventRow s: selects.Add(s); break;
                    case ConvertEventRow c: converts.Add(c); break;
                }
            }
            var count = searches.Count + selects.Count + converts.Count;
            if (count == 0) return;
            var started = Environment.TickCount64;
            try
            {
                store.WriteBatch(searches, selects, converts);
                Interlocked.Add(ref _flushedTotal, count);
                Interlocked.Increment(ref _flushes);
                Interlocked.Add(ref _flushMsTotal, Environment.TickCount64 - started);
            }
            catch (Exception ex)
            {
                // The batch is lost rather than retried: a poisoned batch retried forever would
                // block every later one. Loudly, with the count, so the loss is on record.
                logger.LogError(ex, "statistics flush failed; {Count} events lost", count);
            }
        }

        /// <summary>Stops the loop, flushes what remains, and logs the lifetime totals.</summary>
        public void Stop()
        {
            _cts?.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
            Flush();
            var dropped = DroppedTotal;
            logger.LogInformation("statistics writer stopped: {Flushed} events flushed in {Flushes} batches{Dropped}",
                FlushedTotal, Flushes, dropped == 0 ? "" : $", {dropped} dropped at the queue cap");
            _loop = null;
            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose() => Stop();
    }
}
