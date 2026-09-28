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
/// The HTTP pipeline and post-build startup, split out of <see cref="Program"/>.
/// Middleware order inside UseIndxHttpPipeline is load-bearing and commented in place.
/// </summary>
internal static class StartupPipeline
{
    public static void UseIndxHttpPipeline(this WebApplication app)
    {
        // ============================================
        // HTTP PIPELINE CONFIGURATION
        // ============================================

        // API errors are JSON, never HTML: an unhandled exception on an /api
        // path returns a ProblemDetails (application/problem+json) with a trace
        // id and no internal details — in every environment. Non-API paths keep
        // the Razor error page (Production) / developer page (Development).
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api"),
            branch => branch.UseExceptionHandler(errApp => errApp.Run(async context =>
            {
                // The Detail below promises the incident has been logged, and until this was added
                // nothing here logged anything — so a 500 handed the caller a traceId that led
                // nowhere. That is how SweeperConcurrencyTests' one HTTP 500 under eviction fire
                // ended up untraceable: the response was the only evidence, and it carries no cause
                // by design. Log the exception against the same traceId the caller is given.
                var failure = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("IndxServer.ApiErrors")
                    .LogError(failure?.Error,
                        "Unhandled exception on {Method} {Path} (traceId {TraceId})",
                        context.Request.Method, context.Request.Path, context.TraceIdentifier);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Internal server error",
                    Detail = "An unexpected error occurred. The incident has been logged.",
                    Extensions = { ["code"] = "internalError", ["traceId"] = context.TraceIdentifier }
                };
                await context.Response.WriteAsJsonAsync(problem,
                    (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
            })));

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        // Skip HTTPS redirection under the in-memory test server (no HTTPS port) — it otherwise
        // 307-redirects the MCP SSE stream and breaks the transport.
        if (!app.Environment.IsEnvironment("Testing"))
            app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();

        // CORS before the limiter, not after: a 429 short-circuits the pipeline, so a limiter
        // placed first means UseCors never runs on a rejection and the response carries no
        // Access-Control-Allow-Origin. A browser then reports an opaque network error, and the
        // rateLimited code and retryAfterSeconds the client was given to read are unreachable.
        app.UseCors("NewPolicy");

        // Resolve the bearer token once, before the limiter, so the limiter can ask whether the
        // caller is real rather than whether a header is present. Without it, Authorization:
        // Bearer <anything> left the anonymous window and landed in a per-token bucket a fresh
        // garbage token reset every time — an unlimited path for exactly the traffic the
        // anonymous window exists to bound.
        app.UseBearerIdentity();

        // Before authentication on purpose: the per-key policy partitions on the validated jti
        // that UseBearerIdentity just resolved, and the per-IP windows need no principal at all.
        app.UseRateLimiter();

        app.UseAuthentication();

        // Password-change gate: a token carrying the must_change_password claim is
        // restricted to the change-password endpoint and the password-change UI page.
        // Any other path returns 403 so callers cannot do useful work on the
        // deployment-time initial credentials.
        //
        // The default authentication scheme is the Identity cookie, so ctx.User is not populated
        // from a bearer token here — JWT bearer is only triggered lazily by [Authorize]. The
        // bearer principal comes from UseBearerIdentity above, which already validated it; this
        // used to repeat that AuthenticateAsync call and pay for a second validation per request.
        app.Use(async (context, next) =>
        {
            var principal = context.User;
            if (principal?.Identity?.IsAuthenticated != true)
                principal = IndxServer.Services.BearerIdentity.Principal(context) ?? principal;

            if (principal?.HasClaim("must_change_password", "true") == true)
            {
                var path = context.Request.Path;
                var allowed =
                    path.StartsWithSegments("/api/changePassword", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/account/change-password", StringComparison.OrdinalIgnoreCase);

                if (!allowed)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                        {
                            Status = StatusCodes.Status403Forbidden,
                            Title = "Password change required",
                            Detail = "Password change required. Call POST /api/changePassword first.",
                            Extensions = { ["code"] = "passwordChangeRequired" }
                        };
                        await context.Response.WriteAsJsonAsync(problem,
                            (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
                    }
                    else
                    {
                        await context.Response.WriteAsync(
                            "Password change required. Call POST /api/changePassword first.");
                    }
                    return;
                }
            }
            await next();
        });

        app.UseAuthorization();
        app.UseAntiforgery();
    }

    public static void MapIndxEndpoints(this WebApplication app)
    {
        // ============================================
        // ENDPOINT MAPPING
        // ============================================

        // Map Blazor Components (UI)
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        // Map Identity endpoints (login, register, etc.)
        app.MapAdditionalIdentityEndpoints();

        // Map API Controllers
        app.MapControllers();

        // Runtime master-switch: when an admin disables MCP (Instance Settings), /mcp 404s without
        // a restart. Only touches settings on the /mcp path, off the normal hot path.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp")
                && !context.RequestServices.GetRequiredService<IndxServer.Services.InstanceSettingsService>().Load().McpEnabled)
            {
                // A bare 404 reads to an agent as a wrong URL or a server without MCP at all. Say
                // which it is, in the same problem+json shape as every other error in the API.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "about:blank",
                    title = "MCP disabled",
                    status = 404,
                    detail = "The MCP endpoint is switched off on this instance. An administrator can enable it under Instance Settings.",
                    code = "mcpDisabled",
                });
                return;
            }
            await next();
        });

        // MCP endpoint (Streamable HTTP). Requires a valid bearer token (API key) on the JWT
        // scheme specifically — so an unauthenticated call gets a 401 (what MCP clients expect),
        // not a 302 redirect to the cookie login. Tools scope access to the caller's teams.
        app.MapMcp("/mcp").RequireAuthorization(
            new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
                    Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build());

        // Health endpoint - anonymous, used by App Service health probes.
        app.MapHealthChecks("/health").AllowAnonymous();

        // Swagger UI
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v2.0-beta/swagger.json", "Indx v2.0-beta");
            c.RoutePrefix = "swagger";

            // Auto-authenticate with JWT token if user is logged in
            c.InjectJavascript("/swagger-auth.js");
        });
    }

    public static void InitializeIndxSearchSystem(this WebApplication app, IndxEdition edition)
    {
        // ============================================
        // INTERNAL API INITIALIZATION
        // ============================================
        var searchConnectionString = ServerConnectionStrings.GetSearchDataConnectionString(app.Configuration);
        var searchDbPath = ServerConnectionStrings.ExtractDbPath(searchConnectionString);
        Console.WriteLine($"Using Search database: {searchDbPath}");

        // Ensure search database directory exists (works on both local and Azure)
        try
        {
            ServerConnectionStrings.EnsureDatabaseDirectoryExists(searchConnectionString);

            // Additional check: manually ensure directory exists for Azure compatibility
            var searchDbDirectory = Path.GetDirectoryName(searchDbPath);
            if (!string.IsNullOrEmpty(searchDbDirectory) && !Directory.Exists(searchDbDirectory))
            {
                Directory.CreateDirectory(searchDbDirectory);
                Console.WriteLine($"✓ Created search database directory: {searchDbDirectory}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Warning: Could not ensure search database directory exists: {ex.Message}");
        }

        string? detectedLicenseFile = null;
        int datasetCount = 0;
        int userCount = 0;

        // Bootstrap license from the Indx portal if a token is configured (no-op otherwise).
        // Skipped in Managed mode, where licensing is irrelevant.
        if (edition == Services.IndxEdition.SelfHost)
        {
            using var bootstrapScope = app.Services.CreateScope();
            var bootstrapper = bootstrapScope.ServiceProvider
                .GetRequiredService<Services.ILicenseBootstrapper>();
            var fetch = bootstrapper.EnsureLocalLicenseAsync().GetAwaiter().GetResult();
            if (fetch.Outcome == Services.LicenseFetchOutcome.Failed)
                Console.WriteLine($"⚠ License auto-fetch failed: {fetch.Message}");
            else if (fetch.Outcome == Services.LicenseFetchOutcome.Fetched)
                Console.WriteLine($"✓ {fetch.Message}");
        }

        // Migrate per-user dataset ownership to team ownership BEFORE warming up engines, so the
        // search instances are keyed by team id from the start. Idempotent — safe on every boot.
        using (var migrationScope = app.Services.CreateScope())
        {
            try
            {
                var migrator = migrationScope.ServiceProvider
                    .GetRequiredService<Services.DataMigrationService>();
                migrator.MigrateAsync(searchConnectionString).GetAwaiter().GetResult();
            }
            catch (Exception migEx)
            {
                migrationScope.ServiceProvider.GetRequiredService<ILogger<Program>>()
                    .LogError(migEx, "Team-ownership data migration failed");
                throw;
            }
        }

        try
        {
            var licensePath = app.Configuration["Indx:LicenseFile"] ?? "";
            // Before the registry starts, so its very first log line already names the team rather
            // than its GUID. Falls back to the id if the name cannot be read.
            IndxServerInternalApi.TeamNameResolver =
                app.Services.GetRequiredService<IndxServer.Monitor.TeamNames>().NameOf;
            IndxServerInternalApi.StartUpSystem(searchConnectionString,
                app.Services.GetRequiredService<ILoggerFactory>(), licensePath);
            Console.WriteLine($"✓ Search system initialized at: {searchDbPath}");

            // Ensure the DataSetAccess table exists for existing databases (idempotent).
            var accessTableManager = new Indx.Storage.SqLiteManager(searchConnectionString);
            if (accessTableManager.DatabaseExists())
            {
                accessTableManager.EnsureDataSetAccessTableExists();
                // Synonym lists and the per-dataset attachment column, for databases created before
                // synonyms existed. Owned by the library, unlike the boost/metadata tables below.
                accessTableManager.EnsureSynonymSchema();
                // The (UserName, Name) index on JsonData, for databases created before it: without
                // it every wake of a small dataset walked the team's whole table.
                accessTableManager.EnsureDatasetIndex();
            }

            // Per-dataset boost rules: ensure the server-owned table and wire the store (+ the
            // saturation ceiling) into the search path.
            var boostStore = app.Services.GetRequiredService<IndxServer.Services.BoostRuleStore>();
            boostStore.EnsureTable();
            var metadataStore = app.Services.GetRequiredService<IndxServer.Services.DatasetMetadataStore>();
            metadataStore.EnsureTable();
            var boostCeiling = app.Configuration.GetValue<int?>("Indx:BoostSaturationCeiling") ?? 6;
            IndxServerInternalApi.Manager.AttachBoostStore(boostStore, boostCeiling);
            IndxServerInternalApi.Manager.AttachMetadataStore(metadataStore);

            // Detect license file for summary
            if (!string.IsNullOrWhiteSpace(licensePath) && File.Exists(licensePath))
            {
                detectedLicenseFile = Path.GetFileName(licensePath);
                Console.WriteLine($"✓ Using license file: {licensePath}");
            }
            else if (Directory.Exists("./IndxData"))
            {
                var licenses = Directory.GetFiles("./IndxData", "*.license");
                if (licenses.Length > 0)
                {
                    // Prefer company licenses over developer licenses (same logic as GetLicensePath)
                    var companyLicense = licenses.FirstOrDefault(l =>
                        !Path.GetFileName(l).Equals("indx-developer.license", StringComparison.OrdinalIgnoreCase));
                    var selectedLicense = companyLicense ?? licenses[0];
                    detectedLicenseFile = Path.GetFileName(selectedLicense);
                    Console.WriteLine($"✓ Using license file: {selectedLicense}");
                }
                else
                {
                    Console.WriteLine("ℹ No license file found - running with 100,000 document limit");
                    Console.WriteLine("  Place your license file (.license) in ./IndxData/ to remove the limit");
                }
            }
            else
            {
                Console.WriteLine("ℹ No license file found - running with 100,000 document limit");
            }

            // Count users and datasets for summary
            try
            {
                var sqLiteManager = new Indx.Storage.SqLiteManager(searchConnectionString);
                if (sqLiteManager.DatabaseExists())
                {
                    var users = sqLiteManager.GetUsers();
                    userCount = users.Count;
                    foreach (var user in users)
                    {
                        var dataSets = sqLiteManager.GetUserDataSets(user);
                        datasetCount += dataSets.Count;
                    }
                }
                else
                {
                    sqLiteManager.CreateDatabase();
                    userCount = 0;
                    datasetCount = 0;
                }

            }
            catch { /* Ignore errors counting datasets */ }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Warning: Search system initialization failed: {ex.Message}");
            Console.WriteLine("The application will start but search functionality may be limited");
        }

        // Display startup summary
        Console.WriteLine();
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                        Indx Ready                         ║");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════╣");

        var applicationUrl = app.Configuration["ASPNETCORE_URLS"] ?? "https://localhost:5001";
        var baseUrl = applicationUrl.Split(';')[0]; // Take first URL if multiple
        Console.WriteLine($"║ API:      {baseUrl,-48}║");
        Console.WriteLine($"║ Swagger:  {baseUrl + "/swagger",-48}║");

        var licenseDisplay = detectedLicenseFile ?? "None (100k limit)";
        Console.WriteLine($"║ License:  {licenseDisplay,-48}║");

        var userDisplay = userCount == 1 ? "1 user" : $"{userCount} users";
        var datasetDisplay = datasetCount == 1 ? "1 dataset" : $"{datasetCount} datasets";
        Console.WriteLine($"║ Users:    {userDisplay,-48}║");
        Console.WriteLine($"║ Datasets: {datasetDisplay,-48}║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Reset static Manager on shutdown so a subsequent startup (e.g. test factories)
        // can call StartUpSystem again cleanly.
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopped.Register(IndxServerInternalApi.Shutdown);

        // Warm persisted datasets AFTER the server starts listening, on a background thread.
        // Inline warm-up used to block startup for minutes on a large store over Azure's SMB
        // content share, so IIS/ANCM killed the process at its 120 s startup limit and the app
        // boot-looped. Requests that arrive before a dataset is warm auto-load it on demand
        // (ResolveEngine); until then the dataset honestly reports its non-Ready state.
        lifetime.ApplicationStarted.Register(() =>
        {
            _ = Task.Run(() =>
            {
                try
                {
                    IndxServerInternalApi.Manager.WarmUpPersistedDatasets();
                }
                catch (Exception ex)
                {
                    app.Services.GetRequiredService<ILogger<Program>>()
                        .LogError(ex, "Background dataset warm-up failed");
                }
            });
        });
    }
}
