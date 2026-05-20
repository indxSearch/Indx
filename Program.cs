using Asp.Versioning;
using IndxCloudApi.Data;
using IndxCloudApi.Models;
using IndxCloudApi.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Text.Json;
using Utilities;

namespace IndxCloudApi;

/// <summary>
/// Main application entry point for Indx Cloud API
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

        // ============================================
        // RAZOR COMPONENTS (Blazor Server UI)
        // ============================================
        builder.Services.AddMemoryCache();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<Components.Account.IdentityUserAccessor>();
        builder.Services.AddScoped<Components.Account.IdentityRedirectManager>();
        builder.Services.AddScoped<AuthenticationStateProvider, Components.Account.IdentityRevalidatingAuthenticationStateProvider>();

        // ============================================
        // DATABASE CONFIGURATION
        // ============================================
        var identityConnectionString = ConnectionStringHelper.GetIdentityConnectionString(builder.Configuration);
        var dbPath = ConnectionStringHelper.ExtractDbPath(identityConnectionString);
        Console.WriteLine($"Using Identity database: {dbPath}");

        // Ensure database directory exists (works on both local and Azure)
        try
        {
            ConnectionStringHelper.EnsureDatabaseDirectoryExists(identityConnectionString);

            // Additional check: manually ensure directory exists for Azure compatibility
            var dbDirectory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDirectory) && !Directory.Exists(dbDirectory))
            {
                Directory.CreateDirectory(dbDirectory);
                Console.WriteLine($"✓ Created database directory: {dbDirectory}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Warning: Could not ensure database directory exists: {ex.Message}");
            Console.WriteLine("Attempting to continue - database will be created if directory is writable");
        }

        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(identityConnectionString));

        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        // ============================================
        // IDENTITY CONFIGURATION
        // ============================================

        // ============================================
        // REGISTRATION RESTRICTION CONFIGURATION
        // ============================================
        builder.Services.Configure<RegistrationOptions>(
            builder.Configuration.GetSection("Registration"));
        builder.Services.AddScoped<RegistrationValidator>();

        var registrationMode = builder.Configuration["Registration:Mode"] ?? "Open";
        Console.WriteLine($"ℹ Registration mode: {registrationMode}");

        if (registrationMode.Equals("EmailDomain", StringComparison.OrdinalIgnoreCase))
        {
            var allowedDomains = builder.Configuration.GetSection("Registration:AllowedDomains").Get<List<string>>();
            if (allowedDomains?.Any() == true)
            {
                Console.WriteLine($"✓ Allowed email domains: {string.Join(", ", allowedDomains)}");
            }
            else
            {
                Console.WriteLine("⚠ EmailDomain mode configured but no allowed domains specified");
            }
        }
        else if (registrationMode.Equals("Closed", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("⚠ Registration is closed - only admins can create accounts");
        }

        // ============================================
        // EMAIL SERVICE CONFIGURATION (Optional - configurable)
        // ============================================
        var emailProvider = builder.Configuration["Email:Provider"]?.ToLower() ?? "console";

        switch (emailProvider)
        {
            case "azurecommunicationservices":
            case "acs":
                var acsConnectionString = builder.Configuration["Email:AzureCommunicationServices:ConnectionString"];
                if (!string.IsNullOrEmpty(acsConnectionString))
                {
                    builder.Services.AddTransient<IEmailSender, AzureCommunicationEmailSender>();
                    Console.WriteLine("✓ Email configured: Azure Communication Services");
                }
                else
                {
                    builder.Services.AddTransient<IEmailSender, ConsoleEmailSender>();
                    Console.WriteLine("⚠ Azure Communication Services not configured, using Console mode");
                }
                break;

            case "console":
            default:
                builder.Services.AddTransient<IEmailSender, ConsoleEmailSender>();
                Console.WriteLine("ℹ Email configured: Console mode (emails logged to console)");
                break;
        }

        // ============================================
        // DUAL AUTHENTICATION: Cookies + JWT + External
        // ============================================

        // Identity registration (includes cookie authentication automatically)
        builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            // Read email confirmation requirement from configuration
            options.SignIn.RequireConfirmedAccount = builder.Configuration.GetValue<bool>("Identity:RequireConfirmedEmail", false);
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequiredLength = 8;
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

        // Configure Authentication with multiple schemes
        var authBuilder = builder.Services.AddAuthentication();

        // JWT Authentication for API
        var jwtkey = builder.Configuration["Jwt:Key"];
        if (string.IsNullOrEmpty(jwtkey))
        {
            throw new InvalidOperationException("JWT Key is not configured. Please set a secure key in appsettings.json or user secrets.");
        }

        // Refuse to start in Production with the placeholder key. In other environments,
        // emit a warning so local dev and tests keep working.
        var defaultKey = "your-secret-key-minimum-32-characters-change-in-production";
        if (jwtkey == defaultKey)
        {
            if (builder.Environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "Jwt:Key is set to the placeholder value. Configure a unique 32+ character key " +
                    "via Key Vault reference or app settings before running in Production.");
            }
            Console.WriteLine("⚠ WARNING: Using default JWT key from appsettings.json");
            Console.WriteLine("⚠ This is OK for development/testing, but MUST be changed in production!");
            Console.WriteLine("⚠ Set a secure key using: dotnet user-secrets set \"Jwt:Key\" \"your-secure-key-here\"");
        }
        else
        {
            Console.WriteLine("✓ JWT authentication configured with custom key");
        }

        authBuilder.AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = builder.Configuration["Jwt:Issuer"],
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtkey)),
                ValidateIssuer = true,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.Zero
            };

            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    if (context.Exception.GetType() == typeof(SecurityTokenExpiredException))
                    {
                        context.Response.Headers.Append("Token-Expired", "true");
                    }
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    var userId = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (userId == null) { context.Fail("Invalid token."); return; }

                    var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
                    var cacheKey = $"user_exists_{userId}";
                    if (!cache.TryGetValue(cacheKey, out bool exists))
                    {
                        var userManager = context.HttpContext.RequestServices
                            .GetRequiredService<UserManager<ApplicationUser>>();
                        exists = await userManager.FindByIdAsync(userId) != null;
                        cache.Set(cacheKey, exists, TimeSpan.FromMinutes(5));
                    }

                    if (!exists)
                        context.Fail("User no longer exists.");
                }
            };
        });

        // Google Authentication (Optional - configure in appsettings.json or User Secrets)
        var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
        var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
        if (!string.IsNullOrEmpty(googleClientId) &&
            !string.IsNullOrEmpty(googleClientSecret) &&
            !googleClientId.StartsWith("your-"))
        {
            authBuilder.AddGoogle(options =>
            {
                options.ClientId = googleClientId;
                options.ClientSecret = googleClientSecret;
                options.CallbackPath = "/signin-google";

                // Optional: Request additional scopes
                options.Scope.Add("profile");
                options.Scope.Add("email");

                // Save tokens for later use
                options.SaveTokens = true;
            });

            Console.WriteLine("✓ Google authentication configured");
        }
        else
        {
            Console.WriteLine("ℹ Google authentication not configured (optional)");
        }

        // Microsoft Authentication (Optional - configure in appsettings.json or User Secrets)
        var microsoftClientId = builder.Configuration["Authentication:Microsoft:ClientId"];
        var microsoftClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"];
        if (!string.IsNullOrEmpty(microsoftClientId) &&
            !string.IsNullOrEmpty(microsoftClientSecret) &&
            !microsoftClientId.StartsWith("your-"))
        {
            authBuilder.AddMicrosoftAccount(options =>
            {
                options.ClientId = microsoftClientId;
                options.ClientSecret = microsoftClientSecret;
                options.CallbackPath = "/signin-microsoft";

                // Optional: Request additional scopes
                options.Scope.Add("User.Read");

                // Force the account picker so users can choose which Microsoft
                // account to sign in with instead of being silently logged in
                // with the most recently cached account.
                options.Events.OnRedirectToAuthorizationEndpoint = context =>
                {
                    var separator = context.RedirectUri.Contains('?') ? "&" : "?";
                    context.Response.Redirect(context.RedirectUri + separator + "prompt=select_account");
                    return Task.CompletedTask;
                };

                // Save tokens for later use
                options.SaveTokens = true;
            });

            Console.WriteLine("✓ Microsoft authentication configured");
        }
        else
        {
            Console.WriteLine("ℹ Microsoft authentication not configured (optional)");
        }

        // ============================================
        // SWAGGER CONFIGURATION
        // ============================================
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v2.0-alpha", new OpenApiInfo
            {
                Version = "2.0-alpha",
                Title = "Indx Cloud API",
                Description = "JWT Authenticated HTTP API for Indx Search"
            });

            // Include all API descriptions in this doc — there's currently only one
            // version, and ApiExplorer's per-version grouping would otherwise leave
            // the doc empty when group names don't exactly match the doc name.
            c.DocInclusionPredicate((_, _) => true);

            var filePath = Path.Combine(AppContext.BaseDirectory, "IndxCloudApi.xml");
            if (File.Exists(filePath))
            {
                c.IncludeXmlComments(filePath);
            }

            // Add schema filter for proxy class examples
            c.SchemaFilter<IndxCloudApi.Swagger.ProxySchemaFilter>();

            // Add operation filter for Search endpoint examples
            c.OperationFilter<IndxCloudApi.Swagger.SearchExamplesOperationFilter>();

            // Add operation filter for SetSearchableFields endpoint examples
            c.OperationFilter<IndxCloudApi.Swagger.SetSearchableFieldsExamplesOperationFilter>();

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme()
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "JWT Authorization header using the Bearer scheme. Enter your token (without 'Bearer' prefix)."
            });

            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    new string[] {}
                }
            });
        });

        // ============================================
        // CONTROLLERS & API CONFIGURATION
        // ============================================
        builder.Services.AddControllers(options =>
        {
            options.InputFormatters.Add(new TextPlainInputFormatter());
        })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.IncludeFields = true;
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        // API versioning: default 2.0-alpha. Header/query-based so the existing
        // /api/... URLs stay unchanged (no breaking change for clients). Clients
        // that want explicit versioning send `api-version: 2.0-alpha` as a header
        // or query parameter; otherwise the default version applies.
        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(2, 0, "alpha");
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = ApiVersionReader.Combine(
                new HeaderApiVersionReader("api-version"),
                new QueryStringApiVersionReader("api-version"));
        })
        .AddMvc()
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = false;
        });

        builder.Services.Configure<KestrelServerOptions>(options =>
        {
            options.AllowSynchronousIO = true;
        });

        builder.Services.Configure<IISServerOptions>(options =>
        {
            options.AllowSynchronousIO = true;
        });

        // CORS: permissive in dev/test for local frontend work; restricted to
        // Cors:AllowedOrigins (comma-separated) in Production. Marketplace deployments
        // pass the App Service hostname here via Bicep app settings.
        var corsAllowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("NewPolicy", policy =>
            {
                if (builder.Environment.IsProduction() && corsAllowedOrigins.Length > 0)
                {
                    policy.WithOrigins(corsAllowedOrigins)
                        .AllowAnyMethod()
                        .AllowAnyHeader();
                }
                else
                {
                    policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader();
                }
            });
        });

        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
            serverOptions.Limits.MaxRequestBodySize = 2_000_000_000;
            serverOptions.AllowSynchronousIO = true;
        });

        // Health checks for App Service / Container probes and the managed-app dashboard.
        builder.Services.AddHealthChecks();

        // License bootstrapper: downloads the .license file from a SAS URL on first
        // start when the local file is absent. No-op when Indx:LicenseDownloadUrl is unset.
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<Services.ILicenseBootstrapper, Services.LicenseBootstrapper>();

        // Application Insights: no-op when APPLICATIONINSIGHTS_CONNECTION_STRING is unset
        // (so local dev is unaffected). The Bicep template provisions an AI resource and
        // injects the connection string automatically per customer.
        builder.Services.AddApplicationInsightsTelemetry();

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

        // ============================================
        // HTTP PIPELINE CONFIGURATION
        // ============================================
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();

        app.UseCors("NewPolicy");
        app.UseAuthentication();

        // Password-change gate: a token carrying the must_change_password claim is
        // restricted to the change-password endpoint and the password-change UI page.
        // Any other path returns 403 so callers cannot do useful work on the
        // deployment-time initial credentials.
        //
        // The default authentication scheme is the Identity cookie. JWT bearer is
        // only triggered lazily by [Authorize] endpoints, so this middleware has
        // to authenticate the bearer scheme explicitly to inspect the claim.
        app.Use(async (context, next) =>
        {
            var principal = context.User;
            if (principal?.Identity?.IsAuthenticated != true
                && context.Request.Headers.ContainsKey("Authorization"))
            {
                var bearer = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
                if (bearer.Succeeded && bearer.Principal != null)
                {
                    principal = bearer.Principal;
                }
            }

            if (principal?.HasClaim("must_change_password", "true") == true)
            {
                var path = context.Request.Path;
                var allowed =
                    path.StartsWithSegments("/api/changePassword", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase);

                if (!allowed)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync(
                        "Password change required. Call POST /api/changePassword first.");
                    return;
                }
            }
            await next();
        });

        app.UseAuthorization();
        app.UseAntiforgery();


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

        // Health endpoint - anonymous, used by App Service health probes.
        app.MapHealthChecks("/health").AllowAnonymous();

        // Swagger UI
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v2.0-alpha/swagger.json", "Indx Cloud API v2.0-alpha");
            c.RoutePrefix = "swagger";

            // Auto-authenticate with JWT token if user is logged in
            c.InjectJavascript("/swagger-auth.js");
        });

        // ============================================
        // INTERNAL API INITIALIZATION
        // ============================================
        var searchConnectionString = ConnectionStringHelper.GetSearchDataConnectionString(builder.Configuration);
        var searchDbPath = ConnectionStringHelper.ExtractDbPath(searchConnectionString);
        Console.WriteLine($"Using Search database: {searchDbPath}");

        // Ensure search database directory exists (works on both local and Azure)
        try
        {
            ConnectionStringHelper.EnsureDatabaseDirectoryExists(searchConnectionString);

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

        // Bootstrap license from SAS URL if needed (no-op when Indx:LicenseDownloadUrl is unset).
        using (var bootstrapScope = app.Services.CreateScope())
        {
            var bootstrapper = bootstrapScope.ServiceProvider
                .GetRequiredService<Services.ILicenseBootstrapper>();
            bootstrapper.EnsureLocalLicenseAsync().GetAwaiter().GetResult();
        }

        try
        {
            var licensePath = builder.Configuration["Indx:LicenseFile"] ?? "";
            IndxCloudInternalApi.StartUpSystem(searchConnectionString, licensePath);
            Console.WriteLine($"✓ Search system initialized at: {searchDbPath}");

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
        Console.WriteLine("║                  Indx Cloud API Ready                     ║");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════╣");

        var applicationUrl = builder.Configuration["ASPNETCORE_URLS"] ?? "https://localhost:5001";
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

        app.Run();
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
        string[] roleNames = { "Admin", "User", "ApiUser" };
        foreach (var roleName in roleNames)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new IdentityRole(roleName));
                logger.LogInformation("Created role: {RoleName}", roleName);
            }
        }

        // Admin email + initial password come from configuration so each customer
        // deployment can supply its own (Marketplace UI -> Bicep -> App Service Settings
        // -> Key Vault). Defaults preserve the historical dev experience.
        var adminEmail = configuration["Identity:AdminEmail"];
        if (string.IsNullOrWhiteSpace(adminEmail))
        {
            adminEmail = "admin@indx.co";
        }

        var adminPassword = configuration["Identity:AdminInitialPassword"];
        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            adminPassword = "Admin123!@#";
        }

        var skipPasswordChange = configuration.GetValue<bool>(
            "Identity:SkipPasswordChangeForSeed", false);

        var adminUser = await userManager.FindByEmailAsync(adminEmail);
        if (adminUser == null)
        {
            adminUser = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true,
                MustChangePassword = !skipPasswordChange
            };

            var result = await userManager.CreateAsync(adminUser, adminPassword);
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
}