using Indx.Storage;

namespace IndxServer.Services
{
    /// <summary>
    /// identity.db (users, roles, API keys) as a backup source (<see cref="IBackupSource"/>): the
    /// ASP.NET Identity store is EF Core's, not the library's, so this small adapter finds its file
    /// from the connection string and copies it the SQLite way (<see cref="SqLiteBackup"/>).
    /// </summary>
    internal sealed class IdentityBackupSource(IConfiguration configuration) : IBackupSource
    {
        public string Name => "identity";

        public BackupResult? TryBackup(string folder) => SqLiteBackup.Take(
            ServerConnectionStrings.ExtractDbPath(ServerConnectionStrings.GetIdentityConnectionString(configuration)),
            folder, Name);
    }
}
