using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IndxServer.Engine;
using IndxServer.Monitor;

namespace IndxServer.Services.Support
{
    /// <summary>
    /// What "Include diagnostics" sends with a ticket, built once and shown to the writer in full
    /// before it goes: the versions, the edition, the machine, memory, each dataset's state and
    /// size (read as the monitor reads them, never waking one), and the last warnings and errors
    /// of the server log. Never records, search text or settings' values; in the log lines, email
    /// addresses and anything shaped like a key or a token are masked. SupportDiagnosticsTests
    /// pins what it may contain.
    /// </summary>
    internal sealed partial class SupportDiagnostics(IConfiguration configuration, IEditionService edition)
    {
        public const int MaxLogLines = 50;
        public const int MaxDatasets = 100;
        private const int LogTailBytes = 256 * 1024;

        public JsonObject Build()
        {
            var snapshot = new MonitorCollector().Collect(DateTimeOffset.UtcNow);
            var p = snapshot.Process;
            var datasets = new JsonArray();
            foreach (var d in snapshot.Datasets.OrderBy(d => d.DataSetName).Take(MaxDatasets))
            {
                var line = new JsonObject
                {
                    ["name"] = d.DataSetName,
                    ["state"] = d.Phase,
                    ["documents"] = d.DocumentCount,
                    ["recordsOnDisk"] = d.RecordsOnDisk,
                };
                if (d.IndexedTextTruncated) line["indexedTextTruncated"] = true;
                if (!string.IsNullOrEmpty(d.ErrorMessage)) line["error"] = Mask(d.ErrorMessage);
                datasets.Add(line);
            }
            return new JsonObject
            {
                ["versions"] = IndxServer.Shared.AdminNav.VersionLine,
                ["edition"] = edition.Edition.ToString(),
                ["os"] = $"{RuntimeInformation.OSDescription.Trim()}, {RuntimeInformation.OSArchitecture}",
                ["runtime"] = RuntimeInformation.FrameworkDescription,
                ["memoryMb"] = new JsonObject { ["gcHeap"] = p.GcHeapMb, ["workingSet"] = p.WorkingSetMb, ["available"] = p.AvailableMb },
                ["datasets"] = datasets,
                ["log"] = new JsonArray(RecentProblems().Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
            };
        }

        /// <summary>The last warning and error lines of IndxServer.log, masked and cut to a line each.</summary>
        private IEnumerable<string> RecentProblems()
        {
            var name = configuration["Indx:LogFile"] is { Length: > 0 } configured ? configured : IndxServerInternalApi.logFileName;
            var path = ServerLogFile.ResolveLogPath(name);
            if (!Path.IsPathRooted(path)) path = Path.Combine(AppContext.BaseDirectory, path);
            if (!File.Exists(path)) return [];
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > LogTailBytes) stream.Seek(-LogTailBytes, SeekOrigin.End);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var lines = reader.ReadToEnd().Split('\n');
                return lines
                    .Where(l => l.Contains("|WARN|") || l.Contains("|ERROR|") || l.Contains("|FATAL|"))
                    .TakeLast(MaxLogLines)
                    .Select(l => Mask(l.TrimEnd('\r')))
                    .Select(l => l.Length > 500 ? l[..500] + "…" : l)
                    .ToList();
            }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
        }

        [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")] private static partial Regex EmailShape();
        [GeneratedRegex(@"(?i)\bbearer\s+\S+")] private static partial Regex BearerShape();
        [GeneratedRegex(@"(?i)\b(key|token|secret|password|pwd|apikey|api_key|authorization)(\s*[=:]\s*)\S+")] private static partial Regex AssignedSecret();
        [GeneratedRegex(@"[A-Za-z0-9_\-+/=]{32,}")] private static partial Regex LongToken();

        /// <summary>A line with its email addresses, bearer tokens, assigned secrets and any long
        /// key-like run replaced. Errs on the side of hiding: a long hash in a path goes too.</summary>
        public static string Mask(string line)
        {
            line = BearerShape().Replace(line, "Bearer [hidden]");
            line = AssignedSecret().Replace(line, "$1$2[hidden]");
            line = EmailShape().Replace(line, "[email]");
            return LongToken().Replace(line, "[hidden]");
        }
    }
}
