using System.Text.Json;

namespace IndxServer.Monitor
{
    /// <summary>
    /// Where the divider between Datasets and Events was last dragged, so the monitor opens the
    /// way it was left. Its own file in ~/.indx, per machine and never in the repo: where a border
    /// sits is a fact about one person's screen. A missing or unreadable file means the built-in
    /// split, and a layout that cannot be saved is not worth an error on the way out.
    /// </summary>
    public sealed class MonitorLayout
    {
        public int? DatasetsHeight { get; set; }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".indx", "monitor.layout.json");

        public static MonitorLayout Load()
        {
            try { return JsonSerializer.Deserialize<MonitorLayout>(File.ReadAllText(FilePath)) ?? new(); }
            catch { return new(); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            }
            catch { /* not worth an error on the way out */ }
        }
    }
}
