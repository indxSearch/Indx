using Asp.Versioning;
using IndxServer.Engine;
using IndxServer.Monitor;
using IndxServer.Data;
using IndxServer.Models;
using IndxServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Text.Json;
using Indx.Utilities;

namespace IndxServer;

/// <summary>
/// Main application entry point for Indx (IndxServer)
/// </summary>
public class Program
{
    /// <summary>
    /// Application entry point - configures and starts the web host
    /// </summary>
    /// <param name="args">Command line arguments</param>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Service registration, grouped by concern (Startup/StartupServices.cs).
        builder.AddIndxRazorUi();
        var dbPath = builder.AddIndxIdentityDatabase();
        var edition = builder.AddIndxDomainServices();
        builder.AddIndxEmailSender();
        builder.AddIndxAuthentication();
        builder.AddIndxSwagger();
        builder.AddIndxApi();
        builder.AddIndxOperations(args, edition);

        var app = builder.Build();

        // ============================================
        // DATABASE INITIALIZATION
        // ============================================
        using (var scope = app.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var logger = services.GetRequiredService<ILogger<Program>>();

            try
            {
                logger.LogInformation("Initializing Identity database...");
                var context = services.GetRequiredService<ApplicationDbContext>();

                // Apply EF migrations. For deployments that originally created the
                // database via EnsureCreated() (no __EFMigrationsHistory table),
                // bootstrap the history table with the initial migration before
                // calling Migrate() so it doesn't try to re-create existing tables.
                BootstrapMigrationHistoryIfNeeded(context, logger);
                ClearStaleMigrationLock(context, logger);
                logger.LogInformation("Applying EF migrations...");
                context.Database.Migrate();
                logger.LogInformation("✓ Database schema is up to date");

                // Enable WAL mode for better concurrency (may not work on all filesystems)
                try
                {
                    context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                    context.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
                    logger.LogInformation("✓ SQLite WAL mode enabled");
                }
                catch (Exception walEx)
                {
                    logger.LogWarning(walEx, "Could not enable WAL mode (may not be supported on this filesystem)");
                }

                logger.LogInformation("✓ Identity database initialized at: {Path}", dbPath);

                // Seed initial data
                logger.LogInformation("Seeding initial data...");
                SeedData(services, builder.Configuration).Wait();
                logger.LogInformation("✓ Initial data seeded successfully");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "FATAL: Error initializing Identity database at {Path}", dbPath);
                logger.LogError("Database directory: {Dir}", Path.GetDirectoryName(dbPath));
                logger.LogError("Directory exists: {Exists}", Directory.Exists(Path.GetDirectoryName(dbPath)));
                throw; // Re-throw to cause app startup failure - this is critical
            }
        }

        // The pipeline, endpoints and search-system startup (Startup/StartupPipeline.cs).
        app.UseIndxHttpPipeline();
        app.MapIndxEndpoints();
        app.InitializeIndxSearchSystem(edition);

        app.Run();
    }

    /// <summary>
    /// EF Core serializes migrations by inserting a row into <c>__EFMigrationsLock</c> and
    /// deleting it when done — but only a surviving process deletes it. A process killed while
    /// holding the lock (e.g. IIS/ANCM's startup time limit) leaves the row behind, and every
    /// later boot then waits forever on a dead owner: EF has no staleness detection on SQLite.
    /// This clears rows older than five minutes before <c>Migrate()</c>. The age threshold is
    /// what keeps the multi-instance case safe: a live migration holds the lock for well under
    /// a second on SQLite, so a five-minute-old row cannot belong to a live migrator. EF writes
    /// the timestamp as sortable UTC text, the same lexical order as SQLite's
    /// <c>datetime('now')</c>, so plain string comparison is correct.
    /// </summary>
    internal static void ClearStaleMigrationLock(ApplicationDbContext context, ILogger logger)
    {
        try
        {
            using var connection = new SqliteConnection(context.Database.GetConnectionString());
            connection.Open();

            using (var exists = connection.CreateCommand())
            {
                exists.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE name = '__EFMigrationsLock' AND type = 'table';";
                if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
                    return; // first boot — Migrate() creates the table itself
            }

            using var delete = connection.CreateCommand();
            delete.CommandText =
                "DELETE FROM __EFMigrationsLock WHERE Timestamp < datetime('now', '-5 minutes');";
            var cleared = delete.ExecuteNonQuery();
            if (cleared > 0)
                logger.LogWarning(
                    "Cleared {Count} stale __EFMigrationsLock row(s) left by a process that died " +
                    "mid-migration; without this, every boot would wait on the dead owner forever.",
                    cleared);
        }
        catch (Exception ex)
        {
            // Best-effort: a failure here must not block startup — worst case Migrate() itself
            // surfaces the real problem.
            logger.LogWarning(ex, "Stale migration-lock check failed; continuing to Migrate()");
        }
    }

    /// <summary>
    /// When upgrading from a deployment that used <c>EnsureCreated()</c>, the database
    /// schema exists but the EF migrations history table does not. Calling
    /// <c>Migrate()</c> in that state would try to re-create existing tables and fail.
    /// This method detects that situation and seeds the history table with the
    /// InitialCreate migration row so <c>Migrate()</c> sees no work to do for the
    /// initial schema and only applies migrations added after.
    /// </summary>
    private static void BootstrapMigrationHistoryIfNeeded(
        ApplicationDbContext context, ILogger<Program> logger)
    {
        if (!context.Database.CanConnect())
        {
            // Fresh deployment - Migrate() will create everything including the history table.
            return;
        }

        var connection = context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) connection.Open();
        try
        {
            using var checkUsersTable = connection.CreateCommand();
            checkUsersTable.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name='AspNetUsers';";
            var usersTableExists = checkUsersTable.ExecuteScalar() != null;

            // Schema patch: run unconditionally when the Identity tables exist.
            // Idempotent - each ALTER fires only when the column is genuinely absent.
            // Recovers from a stale schema regardless of whether __EFMigrationsHistory
            // is present (a previous broken bootstrap may have seeded history without
            // adding the column).
            if (usersTableExists)
            {
                var customColumns = new (string Table, string Column, string AddSql)[]
                {
                    ("AspNetUsers", "MustChangePassword",
                        "ALTER TABLE AspNetUsers ADD COLUMN MustChangePassword INTEGER NOT NULL DEFAULT 0;")
                };

                foreach (var (table, column, addSql) in customColumns)
                {
                    if (!ColumnExists(connection, table, column))
                    {
                        using var alter = connection.CreateCommand();
                        alter.CommandText = addSql;
                        alter.ExecuteNonQuery();
                        logger.LogWarning(
                            "Patched stale schema: added {Column} to {Table}",
                            column, table);
                    }
                }
            }

            using var checkHistoryTable = connection.CreateCommand();
            checkHistoryTable.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
            var historyTableExists = checkHistoryTable.ExecuteScalar() != null;
            if (historyTableExists)
            {
                return;
            }

            if (!usersTableExists)
            {
                // Empty database - Migrate() will set everything up from scratch.
                return;
            }

            logger.LogWarning(
                "Detected pre-migration database (EnsureCreated). Bootstrapping __EFMigrationsHistory table.");

            using var createHistory = connection.CreateCommand();
            createHistory.CommandText =
                "CREATE TABLE \"__EFMigrationsHistory\" (" +
                "\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, " +
                "\"ProductVersion\" TEXT NOT NULL);";
            createHistory.ExecuteNonQuery();

            // Mark every migration discovered so far as already applied. The
            // schema was created via EnsureCreated() so it matches the latest
            // model snapshot, not just the initial migration.
            var infrastructure = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)context).Instance;
            var historyRepo = infrastructure.GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository>();
            var migrationsAssembly = infrastructure.GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly>();
            var productVersion = typeof(ApplicationDbContext).Assembly.GetName().Version?.ToString() ?? "10.0.0";

            foreach (var migration in migrationsAssembly.Migrations.Keys)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO \"__EFMigrationsHistory\" VALUES ($id, $ver);";
                var idParam = insert.CreateParameter();
                idParam.ParameterName = "$id";
                idParam.Value = migration;
                insert.Parameters.Add(idParam);
                var verParam = insert.CreateParameter();
                verParam.ParameterName = "$ver";
                verParam.Value = productVersion;
                insert.Parameters.Add(verParam);
                insert.ExecuteNonQuery();
            }

            logger.LogInformation(
                "Bootstrapped {Count} migration(s) into history table",
                migrationsAssembly.Migrations.Count);
        }
        finally
        {
            if (!wasOpen) connection.Close();
        }
    }

    private static bool ColumnExists(System.Data.Common.DbConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // PRAGMA table_info returns: cid, name, type, notnull, dflt_value, pk
            var name = reader.GetString(reader.GetOrdinal("name"));
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // ============================================
    // SEED DATA METHOD
    // ============================================
    private static async Task SeedData(IServiceProvider services, IConfiguration configuration)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var logger = services.GetRequiredService<ILogger<Program>>();

        // Create roles
        string[] roleNames = { "Admin", "Member" };
        foreach (var roleName in roleNames)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new IdentityRole(roleName));
                logger.LogInformation("Created role: {RoleName}", roleName);
            }
        }

        // First-run onboarding completion flag. An instance that already has users (upgraded
        // from before the flag existed) — or a managed deploy that seeds its own admin via
        // Identity:AdminEmail — is already set up and must skip the first-run setup wizard.
        var settingsService = services.GetRequiredService<InstanceSettingsService>();
        var instanceSettings = settingsService.Load();
        if (!instanceSettings.SetupComplete &&
            (userManager.Users.Any() || !string.IsNullOrWhiteSpace(configuration["Identity:AdminEmail"])))
        {
            instanceSettings.SetupComplete = true;
            settingsService.Save(instanceSettings);
            logger.LogInformation("Marked instance SetupComplete=true (existing users or seeded admin).");
        }

        // Only seed an admin user when Identity:AdminEmail is explicitly configured
        // (e.g. Azure Managed App deployment via ARM template). In dev/self-hosted
        // deployments the first registered user is promoted to Admin automatically.
        var adminEmail = configuration["Identity:AdminEmail"];
        if (string.IsNullOrWhiteSpace(adminEmail))
        {
            logger.LogInformation("No Identity:AdminEmail configured — first registered user will become Admin.");
            return;
        }

        var skipPasswordChange = configuration.GetValue<bool>(
            "Identity:SkipPasswordChangeForSeed", false);

        var adminUser = await userManager.FindByEmailAsync(adminEmail);
        if (adminUser == null)
        {
            // Identity:AdminEmail without Identity:AdminInitialPassword used to fall back to a
            // fixed password, i.e. a known credential on every such instance. Generate one
            // instead and print it once: the operator reads it from the startup log, signs in,
            // and the must-change gate forces a new password on that first login.
            var adminPassword = configuration["Identity:AdminInitialPassword"];
            var generatedPassword = string.IsNullOrWhiteSpace(adminPassword);
            if (generatedPassword)
            {
                adminPassword = GenerateInitialPassword();
                logger.LogWarning(
                    "Identity:AdminEmail is set but Identity:AdminInitialPassword is not. " +
                    "A one-time password was generated for {Email} — it is shown ONLY here, ONCE:\n" +
                    "==================================================================\n" +
                    "  Initial admin password: {Password}\n" +
                    "==================================================================\n" +
                    "You will be asked to choose a new password on first login. To avoid this " +
                    "step, set Identity:AdminInitialPassword before the first start.",
                    adminEmail, adminPassword);
            }

            adminUser = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true,
                MustChangePassword = !skipPasswordChange
            };

            // A generated password is worthless if it can be skipped over: always gate it.
            if (generatedPassword) adminUser.MustChangePassword = true;

            var result = await userManager.CreateAsync(adminUser, adminPassword!);
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(adminUser, "Admin");
                logger.LogInformation(
                    "Admin user created: {Email} (must change password: {MustChange})",
                    adminEmail,
                    adminUser.MustChangePassword);
            }
            else
            {
                logger.LogError(
                    "Failed to create admin user {Email}: {Errors}",
                    adminEmail,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
            }
        }
    }

    /// <summary>
    /// 20 characters from a URL-safe alphabet with at least one lowercase, uppercase, digit and
    /// symbol, so it always satisfies the Identity password policy configured above.
    /// </summary>
    private static string GenerateInitialPassword()
    {
        const string lower = "abcdefghjkmnpqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string symbols = "!@#$%&*-_+=";
        const string all = lower + upper + digits + symbols;

        static char Pick(string alphabet) =>
            alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];

        var chars = new List<char> { Pick(lower), Pick(upper), Pick(digits), Pick(symbols) };
        while (chars.Count < 20) chars.Add(Pick(all));

        // Fisher–Yates so the guaranteed classes are not always in the first four positions.
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }
}
