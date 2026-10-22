namespace IndxServer.Services
{
    /// <summary>
    /// The one folder IndxServer keeps its data in: <c>Indx:DataDirectory</c>, absolute or
    /// relative to the working folder, else <c>./IndxData</c> as always. Every data file follows
    /// it: the databases (unless a connection string names an absolute path, which wins), the
    /// statistics and the embeddings (beside the search database), the instance settings, the
    /// licence, the JWT key file and the backups (unless <c>Backup:Folder</c> is set).
    ///
    /// Why (22 Oct 2026): the default sits inside the app's own folder, so a deploy that cleaned
    /// it took every file with it on cloud.indx.co. The Managed Application keeps its data on an
    /// Azure Files share at /home/data, but only the two databases could be pointed there; the
    /// settings, the licence and the backups stayed in the app's folder, where a customer's
    /// update could clear them. With this one setting the template puts all of it on the share.
    /// </summary>
    internal static class DataPaths
    {
        public const string Setting = "Indx:DataDirectory";
        public const string Default = "./IndxData";

        /// <summary>The configured folder, or null where none is set (each file then keeps its
        /// default, which is ./IndxData for all of them).</summary>
        public static string? Configured(IConfiguration configuration) =>
            configuration[Setting] is { Length: > 0 } dir && !string.IsNullOrWhiteSpace(dir) ? dir.Trim() : null;

        /// <summary>The folder for code that has no configuration to hand (the engine's licence
        /// files), set once at startup by <see cref="Use"/>.</summary>
        public static string Current { get; private set; } = Default;

        public static void Use(IConfiguration configuration) => Current = Configured(configuration) ?? Default;

        /// <summary>The small files an instance had in the default folder before the setting
        /// pointed elsewhere: the instance settings, the licence, the JWT key file. Copied into the
        /// configured folder once, where it lacks them, so an instance whose template gains the
        /// setting keeps its settings and licence. Only where the databases already are (see the
        /// first check). Never overwrites, never deletes; the databases
        /// are not among them, since a template that moves them names them itself.</summary>
        public static IReadOnlyList<string> CarryOver(string from, string to)
        {
            var copied = new List<string>();
            if (!Directory.Exists(from) || Path.GetFullPath(from) == Path.GetFullPath(to)) return copied;
            // Only for an instance whose databases are already there (the Managed App's, which had
            // them on the share and the rest in the app's folder). A new or empty folder gets
            // nothing: a fresh install must not inherit whatever the app folder holds (the
            // tests' temp folders took a developer's real licence token, 22 Oct 2026).
            if (!File.Exists(Path.Combine(to, "indx.db")) && !File.Exists(Path.Combine(to, "identity.db"))) return copied;
            foreach (var file in Directory.EnumerateFiles(from).Where(f => Path.GetFileName(f) is "settings.json" or "jwt.key" || f.EndsWith(".license", StringComparison.OrdinalIgnoreCase)))
            {
                var target = Path.Combine(to, Path.GetFileName(file));
                if (File.Exists(target)) continue;
                File.Copy(file, target);
                copied.Add(Path.GetFileName(file));
            }
            return copied;
        }
    }
}
