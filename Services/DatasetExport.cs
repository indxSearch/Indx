using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// Reads a dataset's documents back out of the search database as one JSON array — the
    /// records exactly as they were loaded, replaced or last updated, including fields that are not
    /// configured. The stored JSON is the source of truth for the documents (search indexes are
    /// memory-only), so this works whatever state the engine is in: Ready, hibernated or evicted.
    ///
    /// Streams one record at a time, so memory stays flat on a dataset of any size. Records come
    /// out ordered by document key so two exports of the same data are byte-identical. That is
    /// also why this reads the table itself rather than going through the lib's
    /// <c>IPersistence.GetJsonRecords</c>, which is unordered and which WakeUp relies on as it is.
    /// </summary>
    public static class DatasetExport
    {
        private static string DbPath => IndxServer.Models.IndxServerInternalApi.SearchDbConnectionString;

        private static SqliteConnection Open()
        {
            var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
            conn.Open();
            return conn;
        }

        /// <summary>Whether the team owns a dataset by this name.</summary>
        public static bool Exists(string teamId, string dataSetName)
        {
            if (string.IsNullOrEmpty(DbPath) || !File.Exists(DbPath)) return false;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM DataSet WHERE Name = $d AND UserName = $t LIMIT 1";
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$t", teamId);
            return cmd.ExecuteScalar() != null;
        }

        /// <summary>
        /// Writes the dataset as a JSON array to <paramref name="output"/>: <c>[</c>, the records
        /// separated by commas, <c>]</c>. An empty dataset is <c>[]</c>. Returns the record count.
        /// </summary>
        public static async Task<long> WriteJsonArrayAsync(string teamId, string dataSetName, Stream output, CancellationToken ct)
        {
            await using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true);
            await writer.WriteAsync("[");

            long count = 0;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT RawText FROM JsonData WHERE Name = $d AND UserName = $t ORDER BY Id";
                cmd.Parameters.AddWithValue("$d", dataSetName);
                cmd.Parameters.AddWithValue("$t", teamId);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    await writer.WriteAsync(count == 0 ? "\n" : ",\n");
                    await writer.WriteAsync(reader.GetString(0).Trim());
                    count++;
                    // Hand full buffers to the response as they fill, not all at the end.
                    if (count % 1000 == 0) await writer.FlushAsync(ct);
                }
            }

            await writer.WriteAsync(count == 0 ? "]" : "\n]\n");
            await writer.FlushAsync(ct);
            return count;
        }

        /// <summary>A download file name from the dataset name — already a valid file name.</summary>
        public static string FileName(string dataSetName) => $"{dataSetName}.json";
    }
}
