using IndxServer.Data;
using System.Collections.Concurrent;

namespace IndxServer.Services
{
    /// <summary>
    /// The repeated-failure detector Jens asked for: every unhandled /api exception (already
    /// caught, logged with its traceId and answered as a 500 by the pipeline's handler) is also
    /// counted here in a sliding window, and crossing the threshold raises the operational
    /// alert — in-app (<see cref="NotificationType.RepeatedApiErrors"/>) and by email through
    /// <see cref="OperationalAlerts"/>, with its own key and content. Search is most of the
    /// traffic, so "repeated search failures" is what this catches in practice, without
    /// pretending other endpoints matter less.
    ///
    /// Configuration (appsettings "Alerts"): <c>ApiErrorThreshold</c> — errors before the first
    /// alert (default 1: a single internal 500 IS a bug worth hearing about; 0 disables) within
    /// <c>ApiErrorWindowMinutes</c> (default 60), which also ends an episode after a quiet spell.
    ///
    /// A persisting condition escalates LOGARITHMICALLY instead of repeating itself — the series
    /// is 1, 10, 100, 1000, …: the first error opens an episode and sends the first alert;
    /// further alerts come only when the accumulated count reaches the next power of ten, each
    /// with the count in the subject so no two mails are identical — identical repeats are what
    /// spam filters bury. A full quiet window closes the episode, and its total goes to the log.
    /// Worst case is a handful of mails for millions of errors; every error is in
    /// IndxServer.log regardless.
    /// </summary>
    public sealed class ApiErrorAlert(
        IConfiguration configuration,
        OperationalAlerts alerts,
        ILogger<ApiErrorAlert> logger)
    {
        private readonly ConcurrentQueue<long> _errorTimes = new();
        private readonly object _episodeLock = new();
        private long _episodeStartMs;   // 0 = no active episode
        private long _episodeCount;
        private long _nextEscalation;
        private long _lastErrorMs;

        /// <summary>
        /// Client-caused conditions do not count: only INTERNAL faults are an operational failure. A client
        /// that disconnects mid-request surfaces as OperationCanceledException, and a malformed
        /// request body as Kestrel's BadHttpRequestException - neither says anything about the
        /// server's health. Everything else that reaches the /api exception handler is by
        /// definition our defect, even when a request provoked it.
        /// </summary>
        internal static bool IsClientCaused(Exception? error, bool requestAborted) =>
            requestAborted
            || error is OperationCanceledException
            || error is Microsoft.AspNetCore.Http.BadHttpRequestException;

        /// <summary>Called by the API exception handler for every unhandled /api error. Never
        /// throws: alerting must not be able to break the error response itself.</summary>
        public async Task RecordAsync(string method, string path, string traceId,
            Exception? error = null, bool requestAborted = false)
        {
            try
            {
                if (IsClientCaused(error, requestAborted)) return;
                int threshold = configuration.GetValue("Alerts:ApiErrorThreshold", 1);
                if (threshold <= 0) return;
                int windowMinutes = Math.Max(1, configuration.GetValue("Alerts:ApiErrorWindowMinutes", 60));

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long windowStart = now - windowMinutes * 60_000L;
                string? subject = null, body = null, key = null;
                lock (_episodeLock)
                {
                    _errorTimes.Enqueue(now);
                    while (_errorTimes.TryPeek(out var t) && t < windowStart)
                        _errorTimes.TryDequeue(out _);

                    // A full quiet window since the previous error ends the episode; its total
                    // goes to the log so the aftermath is on record without another email.
                    if (_episodeStartMs != 0 && now - _lastErrorMs >= windowMinutes * 60_000L)
                    {
                        logger.LogInformation(
                            "the repeated-API-error episode that began at {Start:u} is over: {Count} errors in total",
                            DateTimeOffset.FromUnixTimeMilliseconds(_episodeStartMs), _episodeCount);
                        _episodeStartMs = 0;
                    }
                    _lastErrorMs = now;

                    if (_episodeStartMs == 0)
                    {
                        if (_errorTimes.Count < threshold) return;
                        _episodeStartMs = now;
                        _episodeCount = _errorTimes.Count;
                        _nextEscalation = 10;
                        while (_nextEscalation <= _episodeCount) _nextEscalation *= 10;
                        key = "api-errors";
                        subject = $"Indx: repeated API errors ({_episodeCount} in {windowMinutes} minutes)";
                        body = $"{_episodeCount} internal API errors in the last {windowMinutes} minutes " +
                               $"on this instance. Latest: {method} {path} (traceId {traceId}). " +
                               $"Further alerts only at {_nextEscalation}, {_nextEscalation * 10}, … accumulated errors; " +
                               "each error is in IndxServer.log with its stack.";
                    }
                    else
                    {
                        _episodeCount++;
                        if (_episodeCount < _nextEscalation) return;
                        key = $"api-errors-{_nextEscalation}";
                        subject = $"Indx: repeated API errors ({_episodeCount} and counting)";
                        body = $"{_episodeCount} internal API errors since " +
                               $"{DateTimeOffset.FromUnixTimeMilliseconds(_episodeStartMs):u}. " +
                               $"Latest: {method} {path} (traceId {traceId}). " +
                               $"Next alert at {_nextEscalation * 10}.";
                        _nextEscalation *= 10;
                    }
                }
                logger.LogWarning("repeated API errors: {Body}", body);
                await alerts.NotifyAdminsAsync(NotificationType.RepeatedApiErrors, subject!, body!);
                await alerts.RaiseAsync(key!, subject!, body!);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "the API-error detector failed; the original error is already logged");
            }
        }
    }
}
