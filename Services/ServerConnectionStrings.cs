using Microsoft.Extensions.Configuration;
namespace IndxServer.Services
{
    /// <summary>
    /// Where the server's two SQLite databases live: identity.db and indx.db.
    ///
    /// <para>This was <c>Indx.Utilities.ConnectionStringHelper</c> until Sep 2026, in the library,
    /// which made it public API of IndxSearchLib and made the package depend on
    /// Microsoft.Extensions.Configuration for the server's sake. The new name, not only the new
    /// namespace, is deliberate: this project also builds against the package, RC260926 still
    /// carries the old type, and Program.cs imports both namespaces.</para>
    /// </summary>
    internal static class ServerConnectionStrings
    {
        internal const string defaultDataPath = "./IndxData";
        internal const string azureDataPath = "d:/home/IndxData";
        internal const string identityDbName = "identity.db";
        internal const string searchDbName = "indx.db";

        /// <summary>
        /// Detects if running on Azure App Service
        /// </summary>
        private static bool IsRunningOnAzure()
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
        }

        /// <summary>
        /// Where the databases go when no connection string is configured.
        ///
        /// <para>On App Service this was hardcoded to <c>d:/home/IndxData</c>, the Windows path.
        /// <c>WEBSITE_SITE_NAME</c> is set on a <b>Linux</b> plan too, where the home is
        /// <c>/home</c>, and the old string is not rooted there: SQLite would have created
        /// <c>d:/home/IndxData</c> under the working directory, which a Linux container loses on
        /// restart. Azure sets <c>HOME</c> on both, so that decides, as it does for the log file
        /// (<see cref="ServerLogFile.ResolveLogPath"/>). Without it, the old path, as before.</para>
        /// </summary>
        internal static string GetDataPath()
        {
            if (!IsRunningOnAzure())
                return defaultDataPath;
            var home = Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrEmpty(home) ? azureDataPath : Path.Combine(home, "IndxData");
        }

        /// <summary>
        /// Gets the Identity database connection string (identity.db)
        /// </summary>
        public static string GetIdentityConnectionString(IConfiguration configuration)
        {
            // Check for explicit override first
            var configuredConnection = configuration.GetConnectionString("IdentityConnection");
            if (!string.IsNullOrWhiteSpace(configuredConnection))
            {
                // If it's already a full connection string with "Data Source=", return as-is
                if (configuredConnection.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                {
                    return configuredConnection;
                }
                // Otherwise, wrap it
                return $"Data Source={configuredConnection}";
            }

            // Fallback to auto-detected path
            var dataPath = GetDataPath();
            var dbPath = Path.Combine(dataPath, identityDbName);
            return $"Data Source={dbPath}";
        }

        /// <summary>
        /// Gets the Search Data database connection string (indx.db)
        /// </summary>
        public static string GetSearchDataConnectionString(IConfiguration configuration)
        {
            // Check for explicit override first
            var configuredConnection = configuration.GetConnectionString("SearchDataConnection");
            if (!string.IsNullOrWhiteSpace(configuredConnection))
            {
                return configuredConnection;
            }

            // Fallback to auto-detected path
            var dataPath = GetDataPath();
            var dbPath = Path.Combine(dataPath, searchDbName);
            return dbPath;
        }

        /// <summary>
        /// Ensures the directory for the database exists
        /// </summary>
        public static void EnsureDatabaseDirectoryExists(string connectionString)
        {
            var dbPath = ExtractDbPath(connectionString);
            if (!string.IsNullOrEmpty(dbPath))
            {
                var directory = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
        }

        /// <summary>
        /// Extracts the file path from a SQLite connection string
        /// </summary>
        public static string ExtractDbPath(string connectionString)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                connectionString,
                @"Data Source\s*=\s*([^;]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return match.Success ? match.Groups[1].Value.Trim() : connectionString;
        }
    }
}