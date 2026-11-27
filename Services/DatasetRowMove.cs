using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// Moves a dataset's row in a store's table to the dataset's new name or team, in SQL and in
    /// one transaction (16 Nov 2026), for the stores whose table is keyed (TeamId, DatasetName).
    /// They used to read the value, save it under the new key and delete the old row. A read that
    /// failed there gave "nothing stored", so nothing was saved and the delete took the real row;
    /// and the save reported a change of rules or parameters that nobody had made.
    /// </summary>
    internal static class DatasetRowMove
    {
        /// <summary>The row of (<paramref name="fromTeam"/>, <paramref name="fromName"/>) becomes
        /// that of the new key, replacing a row left there. A table that does not exist yet holds
        /// nothing to move. Throws on any other failure, with nothing moved.</summary>
        public static void Move(SqliteConnection conn, string table, string fromTeam, string fromName, string toTeam, string toName)
        {
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.Parameters.AddWithValue("$t", fromTeam);
                cmd.Parameters.AddWithValue("$d", fromName);
                cmd.Parameters.AddWithValue("$nt", toTeam);
                cmd.Parameters.AddWithValue("$nd", toName);
                cmd.CommandText = $"DELETE FROM {table} WHERE TeamId = $nt AND DatasetName = $nd";
                cmd.ExecuteNonQuery();
                cmd.CommandText = $"UPDATE {table} SET TeamId = $nt, DatasetName = $nd WHERE TeamId = $t AND DatasetName = $d";
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
            }
        }
    }
}
