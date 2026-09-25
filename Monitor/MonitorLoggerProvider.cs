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

            // A client's own mistake is not this server's incident. Kestrel and the MCP library
            // both log routine client behaviour at Error, which in a log file nobody reads is
            // harmless and in a pane meant for human attention is how an operator learns to stop
            // looking at the pane. Those lines stay, because they are still worth knowing, but
            // they are attributed to the client and carry no stack trace.
            if (ClientCaused(category, message, exception) is { } what)
            {
                sink.Add(new MonitorEvent(DateTimeOffset.UtcNow, "client", what));
                return;
            }

            string text = exception is null ? message : $"{message} — {exception.GetType().Name}: {exception.Message}";
            sink.Add(new MonitorEvent(DateTimeOffset.UtcNow, Source(category), text));
        }

        /// <summary>
        /// A short line when this is something a client did, or null when it is the server's.
        ///
        /// <para>Deliberately narrow. Only two shapes qualify, both of them answered correctly
        /// over the wire before they were ever logged: a connection that went away mid-response,
        /// which is ordinary for a streamed MCP session and for any client that stops reading, and
        /// a call for a tool that does not exist, which is a 32602 the client already received.
        /// Anything else keeps its category, its message and its exception.</para>
        /// </summary>
        internal static string? ClientCaused(string category, string message, Exception? exception)
        {
            // Reported rather than diagnosed: a write to a client that stopped reading looks like
            // this, and so does a TLS teardown the client did not finish. The pane says what
            // happened and to whom, without claiming to know which.
            if (category.StartsWith("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal)
                && exception is IOException or OperationCanceledException)
                return $"connection to a client failed mid-response ({exception.GetType().Name}: {exception.Message})";

            if (category.StartsWith("ModelContextProtocol", StringComparison.Ordinal))
            {
                const string unknown = "Unknown tool";
                foreach (var text in new[] { exception?.Message ?? "", message })
                {
                    int at = text.IndexOf(unknown, StringComparison.Ordinal);
                    if (at >= 0)
                        return "MCP client asked for " + text[(at + unknown.Length)..].Trim();
                }

                // A refusal we wrote on purpose. The library reports a tool throwing as an
                // unhandled exception, so "this key is limited to Search only" -- the tool working
                // exactly as intended, and already delivered to the client in its reply -- arrives
                // here looking like a fault.
                if (exception?.GetType().Name == "McpToolException")
                    return "MCP call refused: " + exception.Message;
            }

            return null;
        }

        /// <summary>
        /// How much of a category is worth a human's attention.
        ///
        /// <para>The pane is not a log tail. Our own categories are worth reading at Information --
        /// "idle-evicted", "warming up 4 datasets" -- because they say what the server is doing.
        /// Everything else has to be at least a warning, because libraries narrate: the MCP server
        /// logs "request handler called" and "completed in 4.85ms" for every single call, and a
        /// pane that shows those is a pane an operator learns to ignore.</para>
        /// </summary>
        internal static LogLevel Minimum(string category)
            => category.StartsWith("IndxServer", StringComparison.Ordinal)
               // The one Microsoft category ASP.NET's own default configuration keeps at
               // Information, and rightly: "Now listening on ..." and "Application is shutting
               // down" are the server telling you what it is doing, not a library narrating.
               || category.StartsWith("Microsoft.Hosting.Lifetime", StringComparison.Ordinal)
                ? LogLevel.Information
                : LogLevel.Warning;

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
            public bool IsEnabled(LogLevel logLevel) => logLevel >= Minimum(category);

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
