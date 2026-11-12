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
    internal sealed class StoreCache<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _values = [];
        private readonly Dictionary<TKey, long> _generations = [];
        private readonly Lock _lock = new();

        /// <summary>The kept value of <paramref name="key"/>, or <paramref name="load"/>'s, kept
        /// unless a write dropped the key while it was loading.</summary>
        public TValue Get(TKey key, Func<TValue> load)
        {
            long generation;
            lock (_lock)
            {
                if (_values.TryGetValue(key, out var kept))
                    return kept;
                generation = _generations.GetValueOrDefault(key);
            }
            var value = load();
            lock (_lock)
            {
                if (_generations.GetValueOrDefault(key) == generation)
                    _values[key] = value;
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
            }
        }
    }
}
