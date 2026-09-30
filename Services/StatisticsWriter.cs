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

        /// <summary>Each session's latest search with text, for the keystroke rule. Only the
        /// flush touches it (one thread), and entries older than the window are dropped each
        /// flush, so it holds the sessions typing right now and no more.</summary>
        private readonly Dictionary<(string Team, string DataSet, string Session), (string QueryId, string Text, long Timestamp)>
            _lastBySession = new();
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
            var superseded = MarkKeystrokes(searches);
            var started = Environment.TickCount64;
            try
            {
                store.WriteBatch(searches, selects, converts, superseded);
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

        /// <summary>
        /// The keystroke rule (Notes/statistics-design.md, "What counts as a search"): a search
        /// with text is superseded when the same session's next search with text, within
        /// <see cref="StatisticsStore.KeystrokeWindowMs"/>, extends it - "o", "os", "osl", "oslo"
        /// is one search. Returns the query ids to mark, which may belong to an earlier batch.
        /// Here rather than in the reads because a read that sorts a day of keystrokes per
        /// session cost a tab load seconds (StatisticsReadCostProbeTests); the search thread pays
        /// nothing either way, this is the background flush. One writer per instance: several
        /// instances without session affinity would each see part of a session and miss some
        /// collapses, which only ever leaves a keystroke counted, never a search lost.
        /// </summary>
        private List<string> MarkKeystrokes(List<SearchEventRow> searches)
        {
            var superseded = new List<string>();
            long newest = long.MinValue;
            foreach (var s in searches)
            {
                if (s.Timestamp > newest) newest = s.Timestamp;
                if (s.Session == null || string.IsNullOrWhiteSpace(s.QueryText) || s.Source != null) continue;
                var key = (s.TeamId, s.DataSet, s.Session);
                var text = s.QueryText.ToLowerInvariant();
                if (_lastBySession.TryGetValue(key, out var last)
                    && s.Timestamp - last.Timestamp <= StatisticsStore.KeystrokeWindowMs
                    && text.Length > last.Text.Length
                    && text.StartsWith(last.Text, StringComparison.Ordinal))
                    superseded.Add(last.QueryId);
                _lastBySession[key] = (s.QueryId, text, s.Timestamp);
            }
            if (newest != long.MinValue)
                foreach (var stale in _lastBySession.Where(kv => newest - kv.Value.Timestamp > StatisticsStore.KeystrokeWindowMs)
                                                    .Select(kv => kv.Key).ToList())
                    _lastBySession.Remove(stale);
            return superseded;
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
