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
/// Service registration for the host, split out of <see cref="Program"/> by concern.
/// The methods are called once each from Main, in this file's order; the order is part
/// of the contract (e.g. the monitor swaps logging providers before the log file is added).
/// </summary>
internal static class StartupServices
{
    public static void AddIndxRazorUi(this WebApplicationBuilder builder)
    {
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
    }

    public static string AddIndxIdentityDatabase(this WebApplicationBuilder builder)
    {
        // ============================================
        // DATABASE CONFIGURATION
        // ============================================
        var identityConnectionString = ServerConnectionStrings.GetIdentityConnectionString(builder.Configuration);
        var dbPath = ServerConnectionStrings.ExtractDbPath(identityConnectionString);
        Console.WriteLine($"Using Identity database: {dbPath}");

        // Ensure database directory exists (works on both local and Azure)
        try
        {
            ServerConnectionStrings.EnsureDatabaseDirectoryExists(identityConnectionString);

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
        return dbPath;
    }

    public static IndxEdition AddIndxDomainServices(this WebApplicationBuilder builder)
    {
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
        builder.Services.AddSingleton<IndxServer.Services.BoostRuleStore>();
        builder.Services.AddSingleton<IndxServer.Services.DatasetMetadataStore>();
        builder.Services.AddSingleton<IndxServer.Services.QueryParameterStore>();

        // MCP server: read-only retrieval tools over /mcp (Streamable HTTP), behind JWT auth.
        builder.Services.AddHttpContextAccessor();
        // ServerInstructions is sent once at the initialization handshake, so the workflow and the
        // two or three Indx-specific facts live here rather than being repeated in every tool
        // description. The audience is an agent that arrives with no documentation and no context:
        // whatever it needs to get the first call right has to be in this handshake or in the tool
        // descriptions, because there is nothing else.
        // The instructions and the tool list are both shaped per session, from the key that
        // opened it: see IndxServer.Mcp.McpSession. They used to be fixed, which told a Search key
        // to call tools it could not reach.
        builder.Services.AddMcpServer(options =>
                options.Filters.Request.ListToolsFilters.Add(IndxServer.Mcp.McpSession.FilterListedTools))
            .WithHttpTransport(transport => transport.ConfigureSessionOptions = IndxServer.Mcp.McpSession.ConfigureAsync)
            .WithTools<IndxServer.Mcp.IndxMcpTools>();

        builder.Services.AddScoped<IndxServer.Services.NotificationService>();
        builder.Services.AddScoped<IndxServer.Services.TeamService>();
        builder.Services.AddScoped<IndxServer.Services.UserProvisioningService>();
        builder.Services.AddScoped<IndxServer.Services.ActiveTeamState>();
        builder.Services.AddScoped<IndxServer.Services.HeaderState>();
        builder.Services.AddScoped<IndxServer.Services.TeamContextResolver>();
        builder.Services.AddScoped<IndxServer.Services.ApiKeyService>();
        builder.Services.AddScoped<IndxServer.Services.DataMigrationService>();
        // Search statistics: the store, the batch writer and the rollup live in one singleton
        // that also runs as the hosted service (Notes/statistics-design.md).
        builder.Services.AddSingleton<IndxServer.Services.StatisticsService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IndxServer.Services.StatisticsService>());
        // Nightly verified backups of the SQLite databases (Notes/backup-design.md). Singleton +
        // hosted over the same instance, so the monitor/admin pages can read LastSuccess.
        builder.Services.AddSingleton<IndxServer.Services.OperationalAlerts>();
        builder.Services.AddSingleton<IndxServer.Services.ApiErrorAlert>();
        builder.Services.AddSingleton<IndxServer.Services.BackupService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IndxServer.Services.BackupService>());
        builder.Services.AddHostedService<IndxServer.Services.TokenExpiryNotificationJob>();
        builder.Services.AddHostedService<IndxServer.Services.BoostRuleExpiryNotificationJob>();
        builder.Services.AddHostedService<IndxServer.Services.DatasetIdleSweeper>();

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
        return edition;
    }

    public static void AddIndxEmailSender(this WebApplicationBuilder builder)
    {
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
    }

    public static void AddIndxAuthentication(this WebApplicationBuilder builder)
    {
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
        // Anchor the key file to the content root, NOT the process CWD. IndxServer is launched
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
                // freshly downloaded / clean-deployed IndxServer starts with zero configuration
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
                // Both, and that is not tidiness. Tokens have been signed with "IndxCloudApi"
                // since before the 12 Sep 2026 rename and are valid for up to a year, so dropping
                // it would invalidate every key already in the wild. New tokens carry whatever
                // Jwt:Issuer says; this keeps the old ones working until they expire.
                ValidIssuers = [builder.Configuration["Jwt:Issuer"], "IndxCloudApi", "IndxServer"],
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtkey!)),
                ValidateIssuer = true,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.Zero
            };

            options.Events = new JwtBearerEvents
            {
                // An unauthenticated /mcp call used to be a bare 401 with an empty body, and the
                // endpoint has no discovery document either — so everything the server knows how
                // to say about itself was behind the token, including the sentence that would tell
                // you what kind of token to ask for. Someone handed the URL and nothing else had
                // to delete "/mcp" off it and land on Swagger to learn what the product was.
                OnChallenge = async context =>
                {
                    if (!context.Request.Path.StartsWithSegments("/mcp"))
                        return;

                    context.HandleResponse();   // ours, not the default empty body
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                    var origin = $"{context.Request.Scheme}://{context.Request.Host}";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        type = "about:blank",
                        title = "Authentication required",
                        status = 401,
                        detail = "This is the Model Context Protocol endpoint of an Indx search server. "
                                 + "It needs an API key as a bearer token. Sign in to the web console "
                                 + $"at {origin} and create one under Account, API keys. A Search key can "
                                 + "list and search datasets; a Read key can also inspect their fields. "
                                 + "Every tool here is read-only.",
                        code = "authenticationRequired",
                        console = origin,
                    }, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
                },
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
                    var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
                    var jti = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;

                    // A team key has no user behind it, so there is no account to check and no
                    // password to be retired by: revocation is its only off switch, and it must
                    // carry both a jti and a scope that parses, or it is no team key at all.
                    if (context.Principal?.FindFirst(IndxServer.Services.ApiKeyScope.TeamKeyClaim) != null)
                    {
                        var teamScope = IndxServer.Services.ApiKeyScope.FromPrincipal(context.Principal);
                        if (jti == null || teamScope is not { IsTeamKey: true } || teamScope.TeamId == Guid.Empty)
                        { context.Fail("Invalid token."); return; }
                        if (await IsRevokedAsync(jti)) context.Fail("Token has been revoked.");
                        return;
                    }

                    var userId = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (userId == null) { context.Fail("Invalid token."); return; }

                    // Current security stamp, or "" when the user no longer exists. Cached; the
                    // password change/reset paths evict it (TokenValidationCache.EvictUser).
                    var userKey = IndxServer.Services.TokenValidationCache.UserKey(userId);
                    if (!cache.TryGetValue(userKey, out string? currentStamp))
                    {
                        var userManager = context.HttpContext.RequestServices
                            .GetRequiredService<UserManager<ApplicationUser>>();
                        var user = await userManager.FindByIdAsync(userId);
                        currentStamp = user == null ? "" : (user.SecurityStamp ?? "");
                        cache.Set(userKey, currentStamp, IndxServer.Services.TokenValidationCache.CacheDuration);
                    }

                    if (currentStamp == "") { context.Fail("User no longer exists."); return; }

                    // Login tokens carry the stamp they were issued under; a password change or
                    // reset rotates it, which retires every earlier login token at once. Named
                    // API keys deliberately omit the claim so integrations survive a password
                    // change — they are retired through revocation instead.
                    var issuedStamp = context.Principal?.FindFirst(IndxServer.Services.TokenValidationCache.SecurityStampClaim)?.Value;
                    if (issuedStamp != null && issuedStamp != currentStamp)
                    {
                        context.Fail("Token was issued before the password was last changed.");
                        return;
                    }

                    if (jti != null && await IsRevokedAsync(jti)) context.Fail("Token has been revoked.");

                    async Task<bool> IsRevokedAsync(string id)
                    {
                        var revokeCacheKey = IndxServer.Services.TokenValidationCache.JtiKey(id);
                        if (!cache.TryGetValue(revokeCacheKey, out bool revoked))
                        {
                            var db = context.HttpContext.RequestServices
                                .GetRequiredService<ApplicationDbContext>();
                            revoked = await db.ApiKeys.AnyAsync(k => k.Jti == id && k.IsRevoked);
                            cache.Set(revokeCacheKey, revoked, IndxServer.Services.TokenValidationCache.CacheDuration);
                        }
                        return revoked;
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
    }

    public static void AddIndxSwagger(this WebApplicationBuilder builder)
    {
        // ============================================
        // SWAGGER CONFIGURATION
        // ============================================
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v2.0-beta", new OpenApiInfo
            {
                Version = "2.0-beta",
                Title = "Indx",
                Description = "JWT Authenticated HTTP API for Indx Search. "
                          + "This server also exposes a Model Context Protocol endpoint at /mcp "
                          + "(Streamable HTTP, same API keys, every tool read-only), which is not "
                          + "described here because it is JSON-RPC rather than REST."
            });

            // Include all API descriptions in this doc — there's currently only one
            // version, and ApiExplorer's per-version grouping would otherwise leave
            // the doc empty when group names don't exactly match the doc name.
            c.DocInclusionPredicate((_, _) => true);

            var filePath = Path.Combine(AppContext.BaseDirectory, "IndxServer.xml");
            if (File.Exists(filePath))
            {
                c.IncludeXmlComments(filePath);
            }

            // Add schema filter for proxy class examples
            c.SchemaFilter<IndxServer.Swagger.ProxySchemaFilter>();

            // Add operation filter for Search endpoint examples
            c.OperationFilter<IndxServer.Swagger.SearchExamplesOperationFilter>();

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
    }

    public static void AddIndxApi(this WebApplicationBuilder builder)
    {
        // ============================================
        // CONTROLLERS & API CONFIGURATION
        // ============================================
        builder.Services.AddControllers(options =>
        {
            options.InputFormatters.Add(new TextPlainInputFormatter());
            // Scoped API keys: team, dataset and level limits on every action (Services/ApiKeyScope.cs).
            options.Filters.Add<IndxServer.Services.ApiKeyScopeFilter>();
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
            // Under IIS (Windows App Service) the app runs in-process and Kestrel's limit below does
            // not apply; this one does, and it defaults to 30,000,000 bytes. Left unset it refused a
            // 79 MB analyze on cloud.indx.co (Oct 2026) on every route without its own
            // [RequestSizeLimit]. web.config raises IIS's request filtering, the limit in front.
            options.MaxRequestBodySize = 2_000_000_000;
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
                    // Indx-Query-Id must be EXPOSED, not merely sent: a cross-origin client
                    // (the storefront) can only read CORS-safelisted response headers, and the
                    // statistics select/convert events reference exactly this header's value.
                    policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .WithExposedHeaders("Indx-Query-Id", IndxServer.Services.QueryParameterResolution.ResponseHeader);
                }
                else if (corsAllowedOrigins.Length > 0)
                {
                    policy.WithOrigins(corsAllowedOrigins)
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .WithExposedHeaders("Indx-Query-Id", IndxServer.Services.QueryParameterResolution.ResponseHeader);
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
    }

    public static void AddIndxOperations(this WebApplicationBuilder builder, string[] args, IndxEdition edition)
    {
        // Terminal monitor: a live view of datasets and process health, in the console the server
        // was started from. On by default when a terminal is attached, opt-in when stdout is
        // redirected (Azure log stream, docker logs, systemd). --no-monitor turns it off.
        // See Notes/terminal-monitor-ideas.md.
        builder.AddIndxMonitor(args);
        // After the monitor, which clears the providers in interactive mode. See ServerLogFile.
        // Indx:LogFile moves it, e.g. onto a container volume; relative paths follow ResolveLogPath.
        builder.Logging.AddIndxLogFile(builder.Configuration["Indx:LogFile"] is { Length: > 0 } logFile
            ? logFile : IndxServerInternalApi.logFileName);

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

        // Per-IP window on the anonymous auth endpoints (see AuthRateLimiting); applied by
        // UseRateLimiter after routing so the path is known.
        builder.Services.AddAuthRateLimiting(builder.Configuration);
    }
}
