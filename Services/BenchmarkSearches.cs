using System.Runtime.CompilerServices;
using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>
    /// How many of a loaded copy's searches were the console's own performance checks (Nov 2026).
    /// The engine counts every search it answers ("Searches since load"), a check's 210 included,
    /// which read as visitors. The engine's counter is the library's, so the console keeps its own
    /// tally beside it, per engine instance: a reload makes a new instance and both start at zero.
    /// Statistics never see these searches either way: they go to the engine directly, and only the
    /// HTTP search endpoint records.
    /// </summary>
    internal static class BenchmarkSearches
    {
        private sealed class Tally { public long Count; }
        private static readonly ConditionalWeakTable<IServerSearchEngine, Tally> Tallies = new();

        public static void Add(IServerSearchEngine engine, long searches) =>
            Interlocked.Add(ref Tallies.GetOrCreateValue(engine).Count, searches);

        public static long Of(IServerSearchEngine? engine) =>
            engine is not null && Tallies.TryGetValue(engine, out var t) ? Interlocked.Read(ref t.Count) : 0;
    }
}
