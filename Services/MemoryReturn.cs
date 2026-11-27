using System.Diagnostics;
using Indx.Api;
using IndxServer.Engine;

namespace IndxServer.Services
{
#pragma warning disable 1591
    internal enum MemoryCollection { None, Background, Aggressive }

    /// <summary>What the runtime said of its last full collection: its number, and how much was live after it.</summary>
    internal readonly record struct LastFullCollection(long Index, long LiveBytes);

    /// <summary>
    /// Which collection to run, from what the registered engines looked like a moment ago and look
    /// like now (26 Nov 2026). The same after a
    /// build and after a dataset left memory:
    /// <list type="bullet">
    /// <item>no other dataset Ready: the aggressive collection at once;</item>
    /// <item>others Ready: a background one at once, and when it has run, the aggressive one if little is
    /// live (<see cref="LittleLiveBytes"/>), else when no dataset has been used for a while
    /// (<see cref="IdleFor"/>).</item>
    /// </list>
    /// An aggressive collection stops every search on the instance while it runs, about 200 ms a GB
    /// left live on a 14-core laptop and four to eight times that on a VM of six cores. Until then a dataset
    /// that left memory always got the aggressive one, whatever was serving.
    /// The collection itself is <see cref="MemoryReturn"/>'s.
    /// </summary>
    internal sealed class MemoryReturnPolicy(long littleLiveBytes = MemoryReturnPolicy.DefaultLittleLiveBytes, TimeSpan? idleFor = null)
    {
        internal const long DefaultLittleLiveBytes = 300L << 20;
        internal long LittleLiveBytes { get; } = littleLiveBytes;
        internal TimeSpan IdleFor { get; } = idleFor ?? TimeSpan.FromMinutes(1);

        private sealed record Seen(WeakReference<object> Engine, SystemState State);

        private Dictionary<string, Seen> _before = [];
        // What has happened since the last collection.
        private bool _freed;
        private bool _rebuilt;
        private readonly HashSet<string> _built = [];
        // A background collection run for it, and the number of the last full one before it: the
        // aggressive one is weighed once the runtime has counted another.
        private long? _backgroundAfter;
        // Little was not live: the aggressive one when the instance is idle.
        private bool _whenIdle;
        // Why the last answer was given, for the log.
        internal string Reason { get; private set; } = "";
        /// <summary>Whether the last answer is the aggressive collection after a background one (little
        /// live, or idle), not one for a new event.</summary>
        internal bool FollowsBackground { get; private set; }

        /// <summary>Takes a reading; returns the collection due, or None when nothing is due or a
        /// dataset is being loaded or indexed (the collection then waits for it).</summary>
        /// <param name="engines">The engines in memory, with when each was last used.</param>
        /// <param name="now">The time.</param>
        /// <param name="last">The runtime's last full collection.</param>
        internal MemoryCollection Observe(IReadOnlyCollection<(string Key, object Engine, SystemState State, DateTimeOffset LastUsed)> engines,
            DateTimeOffset now, LastFullCollection last)
        {
            var seen = new Dictionary<string, Seen>();
            bool busy = false;
            foreach (var (key, engine, state, _) in engines)
            {
                seen[key] = new Seen(new WeakReference<object>(engine), state);
                busy |= state is SystemState.Loading or SystemState.Indexing;
                _before.TryGetValue(key, out var was);
                bool sameEngine = was != null && was.Engine.TryGetTarget(out var earlier) && ReferenceEquals(earlier, engine);
                if (state == SystemState.Ready && !(sameEngine && was!.State == SystemState.Ready))
                {
                    _built.Add(key);
                    // Ready before and Ready now, of another engine: built in the shadow and swapped in.
                    _rebuilt |= was?.State == SystemState.Ready;
                }
            }
            foreach (var (key, was) in _before)
                if (was.State != SystemState.Created && (!seen.TryGetValue(key, out var left) || left.State == SystemState.Created))
                {
                    _freed = true;
                    _built.Remove(key);
                }
            _before = seen;

            if (busy)
                return Answer(MemoryCollection.None, "a dataset is being built");
            if (_freed || _built.Count > 0)
            {
                // A dataset that has just become Ready has had no searches to disturb; one rebuilt, or
                // any other that is Ready, has.
                bool othersAreServing = _rebuilt || engines.Any(e => e.State == SystemState.Ready && !_built.Contains(e.Key));
                return othersAreServing
                    ? Answer(MemoryCollection.Background, "others are serving: in the background first")
                    : Answer(MemoryCollection.Aggressive, "no other dataset is serving");
            }
            if (_backgroundAfter is long before && last.Index > before)
            {
                if (last.LiveBytes <= LittleLiveBytes)
                    return Answer(MemoryCollection.Aggressive, $"little is live ({last.LiveBytes >> 20} MB)", follows: true);
                _backgroundAfter = null;
                _whenIdle = true;
            }
            if (_whenIdle)
            {
                var lastUsed = engines.Where(e => e.State == SystemState.Ready).Select(e => e.LastUsed).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
                if (now - lastUsed >= IdleFor)
                    return Answer(MemoryCollection.Aggressive, $"no dataset used for {(int)IdleFor.TotalSeconds} s", follows: true);
            }
            return Answer(MemoryCollection.None, "");
        }

        private MemoryCollection Answer(MemoryCollection due, string reason, bool follows = false)
        {
            Reason = reason;
            FollowsBackground = follows;
            return due;
        }

        /// <summary>The collection due has been run, or was not worth running; <paramref name="last"/>
        /// is the runtime's last full collection before it.</summary>
        internal void Done(MemoryCollection ran, LastFullCollection last)
        {
            _freed = _rebuilt = false;
            _built.Clear();
            _backgroundAfter = ran == MemoryCollection.Background ? last.Index : null;
            _whenIdle = false;
        }
    }

    /// <summary>
    /// Gives memory back to the operating system when a dataset has been built or has left memory
    /// (24 Nov 2026), by the rule of <see cref="MemoryReturnPolicy"/> (26 Nov). The runtime keeps what
    /// the build's peak took: on a dataset of 646 000 records (1.1 GB live) the process held 3 GB after
    /// Index() and 2.6 to 3 GB after Unload, 1.2 GB and 0.12 GB after an aggressive collection; on ESCI US
    /// (1.2 M records) 6.2 GB after the build, 3.0 after it.
    /// <para>The library itself runs no collection: that is its host's to choose. The engines are
    /// watched rather than every place that builds or unloads one calling here; reading them never
    /// resolves a dataset, so it wakes none and counts as no use of it.</para>
    /// <para><c>MemoryReturn:IntervalSeconds</c> (5) is how often, 0 turns it off;
    /// <c>MemoryReturn:LittleLiveMB</c> (300) and <c>MemoryReturn:IdleSeconds</c> (60) set the rule.</para>
    /// </summary>
    internal sealed class MemoryReturn(IConfiguration configuration, ILogger<MemoryReturn> logger, TimeProvider? clock = null) : BackgroundService
    {
        // Below this the process holds too little for a collection to be worth its pause.
        private const long WorthwhileBytes = 512L << 20;
        // A row of datasets going to sleep one after the other is one collection, not one each.
        private static readonly TimeSpan BetweenCollections = TimeSpan.FromMinutes(1);

        private readonly MemoryReturnPolicy _policy = new(
            (configuration.GetValue<long?>("MemoryReturn:LittleLiveMB") ?? MemoryReturnPolicy.DefaultLittleLiveBytes >> 20) << 20,
            TimeSpan.FromSeconds(configuration.GetValue<int?>("MemoryReturn:IdleSeconds") ?? 60));
        private long _last;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int seconds = configuration.GetValue<int?>("MemoryReturn:IntervalSeconds") ?? 5;
            if (seconds <= 0)
                return;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                    try
                    {
                        Look();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "MemoryReturn failed");
                    }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>The runtime's last full collection, blocking or in the background, whichever was later:
        /// its number and the bytes it left live.</summary>
        internal static LastFullCollection LastFull()
        {
            var blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            var background = GC.GetGCMemoryInfo(GCKind.Background);
            var later = background.Index > blocking.Index ? background : blocking;
            return new LastFullCollection(later.Index, later.PromotedBytes);
        }

        private void Look()
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var last = LastFull();
            var due = _policy.Observe(IndxServerInternalApi.Manager.EnginesInMemory(), now, last);
            // A row of datasets going to sleep one after the other is one collection, not one each; the
            // aggressive one that follows a background one is not held back by it.
            if (due == MemoryCollection.None || (!_policy.FollowsBackground && _last != 0 && Stopwatch.GetElapsedTime(_last) < BetweenCollections))
                return;
            // The runtime's own figures: Process.PrivateMemorySize64 reads 0 on macOS (26 Nov 2026).
            var before = GC.GetGCMemoryInfo(GCKind.Any);
            _policy.Done(due, last);
            if (before.TotalCommittedBytes < WorthwhileBytes)
                return;
            if (due == MemoryCollection.Aggressive)
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            else
                GC.Collect(2, GCCollectionMode.Forced, blocking: false);
            _last = Stopwatch.GetTimestamp();
            if (due == MemoryCollection.Aggressive)
            {
                var after = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                logger.LogInformation("Memory given back ({Reason}): committed {Before} MB to {After} MB, {Live} MB live, a pause of {Pause} ms",
                    _policy.Reason, before.TotalCommittedBytes >> 20, after.TotalCommittedBytes >> 20, after.PromotedBytes >> 20,
                    (long)after.PauseDurations[0].TotalMilliseconds);
            }
            else
                logger.LogDebug("Memory: a background collection ({Reason})", _policy.Reason);
        }
    }
}
