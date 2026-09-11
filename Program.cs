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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Text.Json;
using Indx.Utilities;

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
            options.UseSqlite(identityConnectionString)
                   .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        // ============================================
        // IDENTITY CONFIGURATION
        // ============================================

        // ============================================
        // REGISTRATION RESTRICTION CONFIGURATION
        // ============================================
        // Edition service: single source of truth for self-host vs Azure Managed Application
        // behavior (feature gating, licensing visibility, team/member guardrails). SelfHost enables
        // everything; Managed gates by the app-owned plan.
        var edition = Services.IEditionService.ReadEdition(builder.Configuration);
        if (edition == Services.IndxEdition.Managed)
            builder.Services.AddSingleton<Services.IEditionService, Services.ManagedEditionService>();
        else
            builder.Services.AddSingleton<Services.IEditionService, Services.SelfHostEditionService>();
        Console.WriteLine($"ℹ Edition: {edition}"
            + (edition == Services.IndxEdition.Managed
                ? $" (plan: {builder.Configuration["Indx:Plan"] ?? "Free"})"
                : ""));

        builder.Services.Configure<RegistrationOptions>(
            builder.Configuration.GetSection("Registration"));
        builder.Services.AddScoped<RegistrationValidator>();
        builder.Services.AddSingleton<InstanceSettingsService>();
        builder.Services.AddScoped<BrowserFiles>();
        builder.Services.AddSingleton<IDatasetEngines, ManagerDatasetEngines>();
        builder.Services.AddSingleton<ITeamDatasets>(sp => sp.GetRequiredService<IDatasetEngines>());
        builder.Services.AddSingleton<IndxCloudApi.Services.BoostRuleStore>();
        builder.Services.AddSingleton<IndxCloudApi.Services.DatasetMetadataStore>();

        // MCP server: read-only retrieval tools over /mcp (Streamable HTTP), behind JWT auth.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<IndxCloudApi.Mcp.IndxMcpTools>();

        builder.Services.AddScoped<IndxCloudApi.Services.NotificationService>();
        builder.Services.AddScoped<IndxCloudApi.Services.TeamService>();
        builder.Services.AddScoped<IndxCloudApi.Services.UserProvisioningService>();
        builder.Services.AddScoped<IndxCloudApi.Services.ActiveTeamState>();
        builder.Services.AddScoped<IndxCloudApi.Services.TeamContextResolver>();
        builder.Services.AddScoped<IndxCloudApi.Services.DataMigrationService>();
        builder.Services.AddHostedService<IndxCloudApi.Services.TokenExpiryNotificationJob>();
        builder.Services.AddHostedService<IndxCloudApi.Services.BoostRuleExpiryNotificationJob>();
        builder.Services.AddHostedService<IndxCloudApi.Services.DatasetIdleSweeper>();

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

        // API clients must never be redirected to the HTML login page: when the
        // cookie scheme challenges on an /api path it answers 401/403 instead.
        // (GetToken keeps cookie support for web-UI users fetching a token, and
        // an unauthenticated API call gets a proper 401 rather than a 302.)
        builder.Services.ConfigureApplicationCookie(options =>
        {
            var onLogin = options.Events.OnRedirectToLogin;
            options.Events.OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                return onLogin(ctx);
            };
            var onDenied = options.Events.OnRedirectToAccessDenied;
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
                return onDenied(ctx);
            };
        });

        // Configure Authentication with multiple schemes
        var authBuilder = builder.Services.AddAuthentication();

        // JWT Authentication for API
        var jwtkey = builder.Configuration["Jwt:Key"];
        var defaultKey = "your-secret-key-minimum-32-characters-change-in-production";
        // Anchor the key file to the content root, NOT the process CWD. IndxCloudApi is launched
        // many ways (various VS profiles, `dotnet run`, the published app) whose working directories
        // differ; a CWD-relative path silently resolves to the wrong place and the key isn't found.
        var jwtKeyFile = Path.Combine(builder.Environment.ContentRootPath, "IndxData", "jwt.key");
        var isPlaceholder = jwtkey == defaultKey;
        var keyMissingOrPlaceholder = string.IsNullOrEmpty(jwtkey) || isPlaceholder;

        // Reject the well-known placeholder sample value in Production: hitting this means
        // someone copied the example config verbatim, which would leave the token-signing
        // secret known to anyone with the source. In the Azure Marketplace managed app the key is
        // supplied from Key Vault as the Jwt__Key app setting (see marketplace/main.bicep), so
        // Production normally takes the "custom key" branch below and never touches the file.
        if (builder.Environment.IsProduction() && isPlaceholder)
        {
            throw new InvalidOperationException(
                "Jwt:Key is set to the placeholder sample value in Production. Set Jwt:Key to a real " +
                "secret (>= 32 chars) via Key Vault or an environment variable, or remove it to use " +
                "the persisted IndxData/jwt.key.");
        }

        if (keyMissingOrPlaceholder)
        {
            if (File.Exists(jwtKeyFile))
            {
                // Reuse the persisted key so previously-issued tokens keep validating.
                jwtkey = File.ReadAllText(jwtKeyFile).Trim();
                Console.WriteLine("✓ JWT key loaded from IndxData/jwt.key");

                // A local key file is fine for a single node, but scale-out/multi-instance hosting
                // needs a shared secret or tokens issued by one node fail validation on another.
                if (builder.Environment.IsProduction())
                {
                    Console.WriteLine(
                        "⚠ Running on the local IndxData/jwt.key in Production. For multi-instance " +
                        "deployments set Jwt:Key explicitly (Key Vault or environment variable).");
                }
            }
            else
            {
                // No configured key and no persisted file: generate and persist a strong one so a
                // freshly downloaded / clean-deployed IndxCloudApi starts with zero configuration
                // (self-hosted customers don't have to set anything). The Marketplace managed app
                // supplies Jwt:Key from Key Vault and so never reaches here.
                Directory.CreateDirectory(Path.GetDirectoryName(jwtKeyFile)!);
                jwtkey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
                File.WriteAllText(jwtKeyFile, jwtkey);
                Console.WriteLine("✓ JWT key auto-generated and saved to IndxData/jwt.key");

                // An auto-generated key lives only on this instance's disk. Fine for a single node,
                // but scale-out/multi-instance hosting needs a shared secret, or tokens issued by one
                // node fail validation on another. It also rotates if IndxData isn't persisted,
                // invalidating previously-issued tokens — set Jwt:Key explicitly to pin it.
                if (builder.Environment.IsProduction())
                {
                    Console.WriteLine(
                        "⚠ Running on an auto-generated JWT key in Production. For multi-instance " +
                        "deployments set Jwt:Key explicitly (Key Vault or environment variable).");
                }
            }
        }
        else
        {
            Console.WriteLine("✓ JWT authentication configured with custom key");
        }

        // The LoginController signs tokens via _config["Jwt:Key"], but the validation
        // below may have resolved a different effective key (the auto-generated
        // IndxData/jwt.key fallback used when Jwt:Key is missing/placeholder). Write the
        // resolved key back so signing and validation always agree — otherwise tokens are
        // rejected with "The signature key was not found".
        builder.Configuration["Jwt:Key"] = jwtkey;

        authBuilder.AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = builder.Configuration["Jwt:Issuer"],
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtkey!)),
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

                    // Current security stamp, or "" when the user no longer exists. Cached; the
                    // password change/reset paths evict it (TokenValidationCache.EvictUser).
                    var userKey = IndxCloudApi.Services.TokenValidationCache.UserKey(userId);
                    if (!cache.TryGetValue(userKey, out string? currentStamp))
                    {
                        var userManager = context.HttpContext.RequestServices
                            .GetRequiredService<UserManager<ApplicationUser>>();
                        var user = await userManager.FindByIdAsync(userId);
                        currentStamp = user == null ? "" : (user.SecurityStamp ?? "");
                        cache.Set(userKey, currentStamp, IndxCloudApi.Services.TokenValidationCache.CacheDuration);
                    }

                    if (currentStamp == "") { context.Fail("User no longer exists."); return; }

                    // Login tokens carry the stamp they were issued under; a password change or
                    // reset rotates it, which retires every earlier login token at once. Named
                    // API keys deliberately omit the claim so integrations survive a password
                    // change — they are retired through revocation instead.
                    var issuedStamp = context.Principal?.FindFirst(IndxCloudApi.Services.TokenValidationCache.SecurityStampClaim)?.Value;
                    if (issuedStamp != null && issuedStamp != currentStamp)
                    {
                        context.Fail("Token was issued before the password was last changed.");
                        return;
                    }

                    var jti = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
                    if (jti != null)
                    {
                        var revokeCacheKey = IndxCloudApi.Services.TokenValidationCache.JtiKey(jti);
                        if (!cache.TryGetValue(revokeCacheKey, out bool revoked))
                        {
                            var db = context.HttpContext.RequestServices
                                .GetRequiredService<ApplicationDbContext>();
                            revoked = await db.ApiKeys.AnyAsync(k => k.Jti == jti && k.IsRevoked);
                            cache.Set(revokeCacheKey, revoked, IndxCloudApi.Services.TokenValidationCache.CacheDuration);
                        }
                        if (revoked) context.Fail("Token has been revoked.");
                    }
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
            c.SwaggerDoc("v2.0-beta", new OpenApiInfo
            {
                Version = "2.0-beta",
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

        // API versioning: default 2.0-beta. Header/query-based so the existing
        // /api/... URLs stay unchanged (no breaking change for clients). Clients
        // that want explicit versioning send `api-version: 2.0-beta` as a header
        // or query parameter; otherwise the default version applies.
        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(2, 0, "beta");
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
                if (!builder.Environment.IsProduction())
                {
                    policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader();
                }
                else if (corsAllowedOrigins.Length > 0)
                {
                    policy.WithOrigins(corsAllowedOrigins)
                        .AllowAnyMethod()
                        .AllowAnyHeader();
                }
                else
                {
                    // Production with no origins configured: allow no cross-origin browser
                    // access rather than falling open. Same-origin traffic (the Blazor UI,
                    // Swagger, MCP, any server-to-server client) is unaffected by CORS.
                    policy.WithOrigins();
                    Console.WriteLine(
                        "⚠ Cors:AllowedOrigins is not set. Browser apps on other origins cannot call " +
                        "this API until it is (comma-separated list of origins, e.g. " +
                        "https://app.example.com). Same-origin use is unaffected.");
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

        // License bootstrapper: downloads the .license file from the Indx portal (hardcoded URL)
        // using a configured license token. No-op when no token is configured.
        builder.Services.AddHttpClient();
        // The license endpoint may sit behind a CDN/proxy that gzip/brotli-compresses the
        // response. The license file is encrypted binary, so without automatic decompression
        // we'd write the still-compressed bytes and the file would fail validation (a browser
        // download decodes transparently, which is why uploading the downloaded file works).
        builder.Services.AddHttpClient(nameof(Services.LicenseBootstrapper))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            });
        builder.Services.AddSingleton<Services.ILicenseBootstrapper, Services.LicenseBootstrapper>();
        // Daily background re-fetch so a long-running instance never lets its on-disk license
        // go stale. No-op until auto-fetch is configured. See LicenseRefreshJob. Skipped in
        // Managed mode, where licensing is irrelevant.
        if (edition == Services.IndxEdition.SelfHost)
            builder.Services.AddHostedService<Services.LicenseRefreshJob>();

        // Application Insights: only activate when a connection string is configured.
        // The Bicep template provisions an AI resource and injects the connection string
        // automatically per customer.
        if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
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
                    .CreateLogger("IndxCloudApi.ApiErrors")
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
                && !context.RequestServices.GetRequiredService<IndxCloudApi.Services.InstanceSettingsService>().Load().McpEnabled)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
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
            c.SwaggerEndpoint("/swagger/v2.0-beta/swagger.json", "Indx Cloud API v2.0-beta");
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
            var licensePath = builder.Configuration["Indx:LicenseFile"] ?? "";
            IndxCloudInternalApi.StartUpSystem(searchConnectionString, licensePath);
            Console.WriteLine($"✓ Search system initialized at: {searchDbPath}");

            // Ensure the DataSetAccess table exists for existing databases (idempotent).
            var accessTableManager = new Indx.Storage.SqLiteManager(searchConnectionString);
            if (accessTableManager.DatabaseExists())
            {
                accessTableManager.EnsureDataSetAccessTableExists();
                // Synonym lists and the per-dataset attachment column, for databases created before
                // synonyms existed. Owned by the library, unlike the boost/metadata tables below.
                accessTableManager.EnsureSynonymSchema();
            }

            // Per-dataset boost rules: ensure the cloud-owned table and wire the store (+ the
            // saturation ceiling) into the search path.
            var boostStore = app.Services.GetRequiredService<IndxCloudApi.Services.BoostRuleStore>();
            boostStore.EnsureTable();
            var metadataStore = app.Services.GetRequiredService<IndxCloudApi.Services.DatasetMetadataStore>();
            metadataStore.EnsureTable();
            var boostCeiling = builder.Configuration.GetValue<int?>("Indx:BoostSaturationCeiling") ?? 6;
            IndxCloudInternalApi.Manager.AttachBoostStore(boostStore, boostCeiling);
            IndxCloudInternalApi.Manager.AttachMetadataStore(metadataStore);

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

        // Reset static Manager on shutdown so a subsequent startup (e.g. test factories)
        // can call StartUpSystem again cleanly.
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopped.Register(IndxCloudInternalApi.Shutdown);

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
                    IndxCloudInternalApi.Manager.WarmUpPersistedDatasets();
                }
                catch (Exception ex)
                {
                    app.Services.GetRequiredService<ILogger<Program>>()
                        .LogError(ex, "Background dataset warm-up failed");
                }
            });
        });

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
