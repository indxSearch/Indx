using System.Collections.Concurrent;

namespace IndxServer.Monitor
{
    /// <summary>Where log lines go once the screen has taken the console.</summary>
    internal interface IMonitorLogSink
    {
        void Add(MonitorEvent entry);
    }

    /// <summary>
    /// The console logger, until the screen exists — then the event pane.
    ///
    /// <para>Replacing the console provider outright was a mistake: a startup that fails (port in
    /// use, a bad connection string) then has nowhere to print, and the operator is left with a
    /// half-drawn screen and no message. So this provider writes to the console exactly like the
    /// simple console logger, and only starts diverting once
    /// <see cref="AttachTo"/> is called — which <see cref="TuiRenderer"/> does after the screen is
    /// up and undoes when it comes down, so shutdown messages are visible again.</para>
    ///
    /// <para>NLog's <c>IndxServer.log</c> is unaffected either way: the engine registry writes to
    /// it outside the ASP.NET logging pipeline, and it remains the durable trail.</para>
    /// </summary>
    internal sealed class MonitorLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, MonitorLogger> _loggers = new();
        private volatile IMonitorLogSink? _sink;

        internal void AttachTo(IMonitorLogSink sink) => _sink = sink;
        internal void Detach() => _sink = null;

        public ILogger CreateLogger(string categoryName) =>
            _loggers.GetOrAdd(categoryName, name => new MonitorLogger(name, this));

        public void Dispose() => _loggers.Clear();

        private void Write(string category, LogLevel level, string message, Exception? exception)
        {
            var sink = _sink;
            if (sink is null)
            {
                // Same shape as the simple console logger, so a startup failure reads the way it
                // always has.
                var writer = level >= LogLevel.Error ? Console.Error : Console.Out;
                writer.WriteLine($"{Short(level)}: {category}");
                writer.WriteLine($"      {message}");
                if (exception is not null)
                    writer.WriteLine($"      {exception}");
                return;
            }

            string text = exception is null ? message : $"{message} — {exception.GetType().Name}: {exception.Message}";
            sink.Add(new MonitorEvent(DateTimeOffset.UtcNow, Source(category), text));
        }

        /// <summary>The last segment of the category: "DatasetIdleSweeper", not the namespace.</summary>
        private static string Source(string category)
        {
            int dot = category.LastIndexOf('.');
            return dot < 0 || dot == category.Length - 1 ? category : category[(dot + 1)..];
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };

        private sealed class MonitorLogger(string category, MonitorLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            // The level filters configured in appsettings still apply above this; everything that
            // reaches here was already judged worth emitting.
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;
                owner.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
