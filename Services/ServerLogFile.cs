using IndxServer.Engine;
using System.IO;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
namespace IndxServer.Services
{
    /// <summary>
    /// <c>IndxServer.log</c>: an NLog file target added to the host's logging as one more provider,
    /// beside the console, the monitor and Application Insights.
    ///
    /// <para>Until Sep 2026 this was <c>FileLoggerFactory</c>, which built a separate
    /// <see cref="Microsoft.Extensions.Logging.ILoggerFactory"/> for the engine registry and for
    /// every engine. That made two pipelines that did not know about each other: what the registry
    /// and the engines logged went only to the file, so Application Insights never saw their
    /// errors, and what the rest of the server logged never reached the file. Each call also
    /// replaced NLog's process-wide configuration, closing and reopening the file while other
    /// threads wrote to it, and made a factory nobody disposed. And the file had no size limit.</para>
    ///
    /// <para>Now there is one pipeline. The registry takes the host's factory
    /// (<c>IndxServerInternalApi.StartUpSystem</c>) and hands it to every engine, and this provider
    /// is one of its outputs. Which categories reach the file is set in <c>appsettings.json</c>
    /// under <c>Logging:NLog:LogLevel</c>, like any provider; the NLog rule itself passes
    /// everything. The provider owns a <see cref="LogFactory"/> of its own rather than NLog's
    /// global <c>LogManager</c>, so nothing else in the process is reconfigured.</para>
    ///
    /// <para>The history of the file's location on App Service is on <see cref="ResolveLogPath"/>.
    /// This used to live in the library (<c>Indx.Utilities</c>); see the IndxSearchLib changelog.</para>
    /// </summary>
    public static class ServerLogFile
    {
        /// <summary>The file is rolled over to an archive when it passes this size.</summary>
        public const long ArchiveAboveBytes = 10L * 1024 * 1024;

        /// <summary>Archives kept beside the live file; the oldest goes first. With
        /// <see cref="ArchiveAboveBytes"/> this caps the log at about 100 MB, which matters on App
        /// Service, where it shares the <c>/home</c> quota with the datasets.</summary>
        public const int MaxArchiveFiles = 9;

        /// <summary>
        /// Adds the file as a provider of <paramref name="logging"/>. Call it after anything that
        /// clears the providers (the interactive monitor does), or the file is cleared with them.
        /// </summary>
        public static ILoggingBuilder AddIndxLogFile(this ILoggingBuilder logging, string logFileName)
        {
            var logFactory = new LogFactory { Configuration = CreateConfiguration(logFileName) };
            // ShutdownOnDispose: the host disposes its providers on stop, and this LogFactory is
            // ours alone, so shutting it down flushes the file and lets go of it.
            logging.AddProvider(new NLogLoggerProvider(new NLogProviderOptions { ShutdownOnDispose = true }, logFactory));
            return logging;
        }

        /// <summary>The NLog side: one file target with archiving, and a rule that passes every
        /// level, because the host's filters decide.</summary>
        public static LoggingConfiguration CreateConfiguration(string logFileName)
        {
            var file = new NLog.Targets.FileTarget
            {
                Name = "file",
                FileName = ResolveLogPath(logFileName),
                KeepFileOpen = true,
                ArchiveAboveSize = ArchiveAboveBytes,
                MaxArchiveFiles = MaxArchiveFiles,
                // Stated rather than left to NLog's default: an exception is written whole, with
                // its stack and every inner exception's (Exception.ToString), on the lines after
                // the message. A failure logged without its stack cannot be traced afterwards.
                Layout = "${longdate}|${level:uppercase=true}|${logger}|${message}"
                    + "${onexception:inner=${newline}${exception:format=tostring}}",
            };
            var config = new LoggingConfiguration();
            config.AddTarget(file);
            config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, file, "*");
            return config;
        }

        /// <summary>
        /// Where the log file goes.
        ///
        /// <para>On Azure App Service the working directory is not writable in the way a log file
        /// wants, so the log belongs under the site's home. That used to be hardcoded as
        /// <c>D:/home/site/wwwroot/</c>, which is the Windows path: <c>WEBSITE_SITE_NAME</c> is set
        /// on a <b>Linux</b> plan too, where the home is <c>/home</c>, and the old string is not
        /// rooted there — NLog would have created a directory called <c>D:</c> under the working
        /// directory and written the log inside it.</para>
        ///
        /// <para>Azure sets <c>HOME</c> on both, so that is what decides. A path the caller has
        /// already made absolute is left alone.</para>
        /// </summary>
        public static string ResolveLogPath(string logFileName)
        {
            if (string.IsNullOrEmpty(logFileName) || Path.IsPathRooted(logFileName))
                return logFileName;

            // Only on App Service. Anywhere else the working directory is the right answer.
            if (Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") == null)
                return logFileName;

            var home = Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrEmpty(home)
                ? logFileName
                : Path.Combine(home, "site", "wwwroot", logFileName);
        }
    }
}
