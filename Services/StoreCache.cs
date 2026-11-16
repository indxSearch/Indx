namespace IndxServer.Services
{
    /// <summary>
    /// A store's copy in memory of what it keeps in the database, per key, read on the first use
    /// and dropped by every write (12 Nov 2026). It replaces a ConcurrentDictionary read with
    /// GetOrAdd and dropped with TryRemove, which let a read that began before a write keep what it
    /// had read after the write had dropped the copy: every later read then had the old value until
    /// the next write (a saved bury not applied, BuryRuleTests). Here a write raises the key's
    /// generation, and a read keeps what it read only if the generation is as it was when the read
    /// began; otherwise it returns what it read, which was true when it began, and the next read
    /// reads again. The lock covers the dictionaries only, never the database read.
    /// </summary>
    /// <remarks>
    /// A read that failed is kept for a short while only (16 Nov 2026). The stores answer a read
    /// they could not make with "nothing stored", so that a search goes on without its boosts
    /// rather than failing, and say so with <see cref="StoreRead.Failed"/>. Kept like any value,
    /// that answer stood until the next write or a restart: one failed read switched a dataset's
    /// boost rules off, or gave its next load the default key field. Now it is read again after
    /// <see cref="RetryAfterMs"/>; not at once, since a row that cannot be read at all would
    /// otherwise be read, and logged, by every search.
    /// </remarks>
    internal sealed class StoreCache<TKey, TValue> where TKey : notnull
    {
        internal const int RetryAfterMs = 30_000;

        private readonly Dictionary<TKey, TValue> _values = [];
        private readonly Dictionary<TKey, long> _generations = [];
        private readonly Dictionary<TKey, long> _retryAt = [];
        private readonly Lock _lock = new();

        /// <summary>The clock a failed read's retry is measured on; a test moves it.</summary>
        internal Func<long> Ticks { get; set; } = () => Environment.TickCount64;

        /// <summary>The kept value of <paramref name="key"/>, or <paramref name="load"/>'s, kept
        /// unless a write dropped the key while it was loading.</summary>
        public TValue Get(TKey key, Func<TValue> load)
        {
            long generation;
            lock (_lock)
            {
                if (_values.TryGetValue(key, out var kept)
                    && !(_retryAt.TryGetValue(key, out var retryAt) && Ticks() >= retryAt))
                    return kept;
                generation = _generations.GetValueOrDefault(key);
            }
            StoreRead.Begin();
            var value = load();
            bool failed = StoreRead.TakeFailed();
            lock (_lock)
            {
                if (_generations.GetValueOrDefault(key) == generation)
                {
                    _values[key] = value;
                    if (failed) _retryAt[key] = Ticks() + RetryAfterMs;
                    else _retryAt.Remove(key);
                }
            }
            return value;
        }

        /// <summary>Keeps <paramref name="value"/> for <paramref name="key"/> as if it had been read:
        /// for a test that renders without a database.</summary>
        internal void SeedForTest(TKey key, TValue value)
        {
            lock (_lock)
                _values[key] = value;
        }

        /// <summary>Drops the kept value of <paramref name="key"/>, and keeps a read that began
        /// before this from keeping what it read.</summary>
        public void Invalidate(TKey key)
        {
            lock (_lock)
            {
                _generations[key] = _generations.GetValueOrDefault(key) + 1;
                _values.Remove(key);
                _retryAt.Remove(key);
            }
        }
    }

    /// <summary>
    /// How a store's read tells its <see cref="StoreCache{TKey, TValue}"/> that what it returns is
    /// a stand-in for a read it could not make. On the reading thread, between the cache's call of
    /// the loader and its return: the loaders are synchronous database reads.
    /// </summary>
    internal static class StoreRead
    {
        [ThreadStatic] private static bool _failed;

        internal static void Begin() => _failed = false;

        /// <summary>Called from a loader's catch. A table that does not exist yet is not a failed
        /// read: nothing has been stored, and that answer is kept.</summary>
        internal static void Failed(Exception ex)
        {
            if (ex is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 1 } && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
                return;
            _failed = true;
        }

        internal static bool TakeFailed()
        {
            bool failed = _failed;
            _failed = false;
            return failed;
        }
    }
}
