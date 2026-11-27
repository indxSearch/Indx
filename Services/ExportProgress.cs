using System.Collections.Concurrent;

namespace IndxServer.Services
{
    /// <summary>
    /// How far a dataset download has come, for the console's progress bar. The download itself is
    /// a plain link the browser streams (<see cref="Controllers.DatasetExportController"/>), so the
    /// page cannot see it; the link carries a token it made, the endpoint counts the records it has
    /// written under that token, and the page reads the count back. Until the first record is
    /// written the database is still sorting the dataset by key, which on a large dataset is most
    /// of the wait (Oct 2026), so the page says "Preparing" then. Entries hold two numbers and
    /// expire, so an abandoned download leaves nothing behind.
    /// </summary>
    public sealed class ExportProgress
    {
        public sealed record Entry(long Written, bool Done, bool Failed, DateTimeOffset Touched);

        private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(30);
        private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

        public void Started(Guid token)
        {
            Sweep();
            _entries[token] = new Entry(0, false, false, DateTimeOffset.UtcNow);
        }

        public void Wrote(Guid token, long written) =>
            _entries.AddOrUpdate(token, _ => new Entry(written, false, false, DateTimeOffset.UtcNow),
                (_, e) => e with { Written = written, Touched = DateTimeOffset.UtcNow });

        public void Finished(Guid token, bool failed) =>
            _entries.AddOrUpdate(token, _ => new Entry(0, true, failed, DateTimeOffset.UtcNow),
                (_, e) => e with { Done = true, Failed = failed, Touched = DateTimeOffset.UtcNow });

        /// <summary>Null until the endpoint has seen the token: the browser has not asked yet.</summary>
        public Entry? Get(Guid token) => _entries.TryGetValue(token, out var e) ? e : null;

        public void Forget(Guid token) => _entries.TryRemove(token, out _);

        private void Sweep()
        {
            var cutoff = DateTimeOffset.UtcNow - Expiry;
            foreach (var (token, e) in _entries)
                if (e.Touched < cutoff) _entries.TryRemove(token, out _);
        }
    }
}
