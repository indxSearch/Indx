using System.IO;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
namespace IndxServer.Services
{
    /// <summary>
    /// Static class to support creation of an NLog logger
    ///
    /// <para>This is the server's, not the library's. It lived in <c>Utilities</c> until Sep 2026,
    /// which is merged into <c>Indx.dll</c>, so it shipped in IndxSearchLib as public API and made
    /// NLog a dependency of every consumer. <see cref="GetFactory"/> assigns
    /// <c>LogManager.Configuration</c>, which is process-wide: a library customer on NLog who called
    /// it lost their own logging configuration. The library takes a
    /// <c>Microsoft.Extensions.Logging.ILoggerFactory</c> and leaves the choice of logging system
    /// to the caller; this is the server's choice.</para>
    ///
    /// <para>The namespace is not <c>Indx.Utilities</c> on purpose: this project also builds against
    /// the IndxSearchLib package, and 5.0.0-RC260926 still carries <c>Indx.Utilities.FileLoggerFactory</c>,
    /// so the same full name here would be ambiguous against it.</para>
    /// </summary>
    public static class FileLoggerFactory
    {
        #region Public Methods
        /// <summary>
        /// Creates an NLog log instance
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="logFileName"></param>
        /// <returns></returns>
        public static ILogger<T> Create<T>(string logFileName)
        {
            var logger = GetFactory(logFileName).CreateLogger<T>();
            return logger;
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

        /// <summary>
        /// returns an Nlog factory for the given type
        /// </summary>
        /// <param name="logFileName"></param>
        /// <returns></returns>
        public static Microsoft.Extensions.Logging.ILoggerFactory GetFactory(string logFileName)
        {
            logFileName = ResolveLogPath(logFileName);
            var lf = new LoggerFactory();
            lf.AddProvider(new NLog.Extensions.Logging.NLogLoggerProvider());
            var ftg = new NLog.Targets.FileTarget
            {
                Name = "file",
                FileName = logFileName,
                KeepFileOpen = true
            };
            var config = new LoggingConfiguration();
            config.AddTarget(ftg);
            var fileRule = new LoggingRule("*", NLog.LogLevel.Info, ftg);
            config.LoggingRules.Add(fileRule);
            LogManager.Configuration = config;
            return lf;
        }
        #endregion Public Methods
    }
}