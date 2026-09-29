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
    /// Configuration (appsettings "Alerts"): <c>ApiErrorThreshold</c> (default 5; 0 disables)
    /// unhandled errors within <c>ApiErrorWindowMinutes</c> (default 60). Raised at most once
    /// per 20 hours — one bad night is one email, and the individual errors are all in the log
    /// regardless.
    /// </summary>
    public sealed class ApiErrorAlert(
        IConfiguration configuration,
        OperationalAlerts alerts,
        ILogger<ApiErrorAlert> logger)
    {
        private static readonly TimeSpan RaiseInterval = TimeSpan.FromHours(20);
        private readonly ConcurrentQueue<long> _errorTimes = new();
        private long _lastRaisedMs;

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
                int threshold = configuration.GetValue("Alerts:ApiErrorThreshold", 5);
                if (threshold <= 0) return;
                int windowMinutes = Math.Max(1, configuration.GetValue("Alerts:ApiErrorWindowMinutes", 60));

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long windowStart = now - windowMinutes * 60_000L;
                _errorTimes.Enqueue(now);
                while (_errorTimes.TryPeek(out var t) && t < windowStart)
                    _errorTimes.TryDequeue(out _);

                if (_errorTimes.Count < threshold) return;
                if (now - Interlocked.Read(ref _lastRaisedMs) < RaiseInterval.TotalMilliseconds) return;
                Interlocked.Exchange(ref _lastRaisedMs, now);

                var body = $"{_errorTimes.Count} unhandled API errors in the last {windowMinutes} minutes " +
                           $"on this instance. Latest: {method} {path} (traceId {traceId}). " +
                           "Each error is in IndxServer.log with its stack.";
                logger.LogWarning("repeated API errors: {Body}", body);
                await alerts.NotifyAdminsAsync(NotificationType.RepeatedApiErrors, "Repeated API errors", body);
                await alerts.RaiseAsync("api-errors", "Indx: repeated API errors", body);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "the API-error detector failed; the original error is already logged");
            }
        }
    }
}
