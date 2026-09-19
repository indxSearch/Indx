using Indx.Api;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace IndxServer.Models
{
    /// <summary>A console-started wake in progress. <c>Indexing</c> is false while the documents
    /// are still being read from the store; <c>Cancelling</c> is true from the moment a cancel is
    /// asked for until the engine is back asleep.</summary>
    public sealed record WakeProgress(int LoadPercent, int IndexPercent, bool Indexing, bool Cancelling);

    /// <summary>
    /// Waking a hibernated dataset from the console, as an operation the server owns.
    ///
    /// It used to run inside the page component, which had two consequences. The progress lived in
    /// that one circuit, so leaving the page and coming back showed a different screen with no
    /// percentage. And nothing could stop it: a large dataset woken by mistake had to be waited
    /// out, or deleted. Here the operation is keyed by dataset, so any page visit finds it and
    /// shows the same bars, and <see cref="CancelWakeAsync"/> sends it back to hibernation.
    ///
    /// Cancel leans on what the engine already does: <c>LoadFromDatabaseSync</c> and <c>Index</c>
    /// both poll <c>ProcessMonitor.ShouldAbort</c>. A cancelled load leaves the engine in Error
    /// and a cancelled index leaves it Loaded, and neither matters, because the engine is then
    /// disposed and dropped from the registry. The next touch builds an empty shell over the
    /// stored records, which is exactly what hibernated means here. The store is never written
    /// by a wake, so there is nothing to roll back.
    /// </summary>
    internal sealed partial class IndxServerInternalApi
    {
        private sealed class WakeOperation
        {
            internal readonly ProcessMonitor LoadMonitor = new();
            internal readonly ProcessMonitor IndexMonitor = new();
            internal volatile bool Indexing;
            internal volatile bool CancelRequested;
            internal Task Task = Task.CompletedTask;
        }

        /// <summary>Test seam: called on the wake thread before the load (false) and before the index
        /// build (true), so a test can hold the operation at a phase boundary and cancel it there.
        /// A small test dataset otherwise wakes faster than any cancel can be aimed.</summary>
        internal Action<bool>? WakePhaseStarting { get; set; }

        private readonly ConcurrentDictionary<string, WakeOperation> _wakes = new();
        private readonly ConcurrentDictionary<string, string> _wakeErrors = new();

        /// <summary>Starts waking the dataset in the background, or joins the wake already running
        /// for it. False when the dataset does not exist.</summary>
        internal bool StartWake(string dataSetName, string teamId)
        {
            var instance = GetOrCreateInstance(dataSetName, teamId);
            if (instance?.theInstance == null)
                return false;

            var key = MakeKey(dataSetName, teamId);
            var op = new WakeOperation();
            if (!_wakes.TryAdd(key, op))
                return true; // already waking: the caller attaches to that one

            _wakeErrors.TryRemove(key, out _);
            op.Task = Task.Run(() => RunWake(op, instance, dataSetName, teamId, key));
            return true;
        }

        private void RunWake(WakeOperation op, SearchEngineInstance instance, string dataSetName, string teamId, string key)
        {
            var prefix = MakeLogPrefix(teamId, dataSetName);
            try
            {
                // Same lock the lazy auto-load takes, so a request arriving mid-wake cannot start a
                // second load of the same engine.
                lock (instance.DbLock)
                {
                    var engine = instance.theInstance;
                    if (engine == null || engine.IsDisposed || engine.Status.SystemState != SystemState.Created)
                        return; // someone else got it up (or is on it) since the button was pressed

                    WakePhaseStarting?.Invoke(false);
                    using (TrackMonitor(dataSetName, teamId, op.LoadMonitor))
                    {
                        if (!op.CancelRequested)
                        {
                            engine.LoadFromDatabaseSync(op.LoadMonitor);
                            op.LoadMonitor.WaitForCompletion();
                        }
                    }
                    if (op.CancelRequested) return;
                    if (!op.LoadMonitor.Succeeded)
                    {
                        _wakeErrors[key] = "Wake failed: " + (string.IsNullOrEmpty(op.LoadMonitor.ErrorMessage)
                            ? "could not load from store" : op.LoadMonitor.ErrorMessage);
                        return;
                    }

                    op.Indexing = true;
                    WakePhaseStarting?.Invoke(true);
                    using (TrackMonitor(dataSetName, teamId, op.IndexMonitor))
                    {
                        if (!op.CancelRequested)
                        {
                            engine.Index(monitor: op.IndexMonitor);
                            op.IndexMonitor.WaitForCompletion();
                        }
                    }
                    if (op.CancelRequested) return;
                    if (!op.IndexMonitor.Succeeded)
                        _wakeErrors[key] = string.IsNullOrEmpty(op.IndexMonitor.ErrorMessage)
                            ? "Indexing failed. See the server log for details."
                            : "Indexing failed: " + op.IndexMonitor.ErrorMessage;
                    else
                        instance.Touch(TimeProvider.GetUtcNow());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(prefix + "wake failed: " + ex);
                _wakeErrors[key] = "Wake failed: " + ex.Message;
            }
            finally
            {
                try
                {
                    // Back to hibernation. A cancel that lost the race with the last index cycle
                    // finds a Ready engine, and that one is kept: the work is done and paid for.
                    if (op.CancelRequested && instance.theInstance is not { IsDisposed: false, Status.SystemState: SystemState.Ready })
                    {
                        DisposeDataSetInstance(dataSetName, teamId);
                        _logger.LogInformation(prefix + "wake cancelled, dataset returned to hibernation");
                    }
                }
                finally
                {
                    // Removed last, so "no wake in progress" is never reported while the engine is
                    // still being torn down.
                    _wakes.TryRemove(key, out _);
                }
            }
        }

        /// <summary>Progress of the wake running for this dataset, or null when none is.</summary>
        internal WakeProgress? GetWakeProgress(string dataSetName, string teamId)
        {
            if (!_wakes.TryGetValue(MakeKey(dataSetName, teamId), out var op))
                return null;
            bool indexing = op.Indexing;
            return new WakeProgress(indexing ? 100 : op.LoadMonitor.ProgressPercent,
                                    indexing ? op.IndexMonitor.ProgressPercent : 0,
                                    indexing, op.CancelRequested);
        }

        /// <summary>Why the last wake of this dataset failed, or null. Cleared by the next wake.</summary>
        internal string? GetWakeError(string dataSetName, string teamId) =>
            _wakeErrors.TryGetValue(MakeKey(dataSetName, teamId), out var error) ? error : null;

        /// <summary>Stops the wake running for this dataset and returns it to hibernation. Completes
        /// when the engine is gone; does nothing when no wake is running.</summary>
        internal async Task CancelWakeAsync(string dataSetName, string teamId)
        {
            if (!_wakes.TryGetValue(MakeKey(dataSetName, teamId), out var op))
                return;
            op.CancelRequested = true;

            // Repeated, not sent once: a monitor swaps in a fresh cancellation source when its
            // process starts, so a cancel that lands just before the load or the index begins is
            // lost. Asking again until the operation ends closes that window for both phases.
            while (!op.Task.IsCompleted)
            {
                try { op.LoadMonitor.Cancel(); op.IndexMonitor.Cancel(); }
                catch (ObjectDisposedException) { }
                await Task.WhenAny(op.Task, Task.Delay(100));
            }
        }
    }
}
