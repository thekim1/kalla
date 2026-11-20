using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Kalla.Domain.Admin;
using Kalla.Infrastructure.Gateway;
using Kalla.Infrastructure.Persistence;
using Kalla.Infrastructure.Security;

namespace Kalla.AdminApi;

// Adapted from the gateway's src/Fors.AdminApi/AdminAuthentication.cs (identity design §1.2): ApplyClaimMapping,
// CopyClaims and AdminAntiforgeryMiddleware copied; cookie names, rag-* roles and policies, the oid actor, the
// tenant/guest check (§1.7) and the member-group resolver (§2.3) adapted or added. The sign-in values are the effective ones
// (identity design §19, AdminSignIn), rebuilt into the options when they change.
public static partial class AdminAuthentication
{
    /// <summary>Where every sign-in starts; it reads the effective sign-in values first (<see cref="AdminSignIn.Refreshes"/>).</summary>
    public const string LoginPath = "/bff/login";

    public const string LogoutPath = "/bff/logout";

    public const string UserPath = "/bff/user";

    /// <summary>
    /// Normalises identity-provider claims to the names the API authorises on ("roles", "departmentCodes", "teamCodes"):
    /// expands JSON-array claims, copies differently named claims, and derives roles from group membership. Copied.
    /// </summary>
    public static void ApplyClaimMapping(ClaimsPrincipal? principal, OidcOptions options, IReadOnlyDictionary<string, string[]>? settingRoleGroups = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }
        var sources = new[] { options.RoleClaim, options.DepartmentClaim, options.TeamClaim, options.GroupClaim };
        foreach (var claim in identity.Claims.Where(c => sources.Contains(c.Type) && c.Value.StartsWith('[')).ToArray())
        {
            using var json = JsonDocument.Parse(claim.Value);
            identity.RemoveClaim(claim);
            foreach (var value in json.RootElement.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    identity.AddClaim(new Claim(claim.Type, value.GetString()!));
                }
            }
        }
        CopyClaims(identity, options.RoleClaim, AdminClaims.Roles);
        CopyClaims(identity, options.DepartmentClaim, AdminClaims.DepartmentCodes);
        CopyClaims(identity, options.TeamClaim, AdminClaims.TeamCodes);
        var groups = identity.FindAll(options.GroupClaim).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Settings.Identity.RoleGroups merged with the deployment's Oidc:RoleGroups, the break-glass (M-17 C10).
        foreach (var (role, mapped) in RoleGroupMap.Merge(options.RoleGroups, settingRoleGroups ?? new Dictionary<string, string[]>()))
        {
            if (!identity.HasClaim(AdminClaims.Roles, role) && mapped.Any(groups.Contains))
            {
                identity.AddClaim(new Claim(AdminClaims.Roles, role));
            }
        }
    }

    private static void CopyClaims(ClaimsIdentity identity, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return;
        }
        foreach (var claim in identity.FindAll(from).ToArray())
        {
            // The group claim may serve as the team claim too; a group is then copied once however many settings name it.
            if (!identity.HasClaim(to, claim.Value))
            {
                identity.AddClaim(new Claim(to, claim.Value));
            }
        }
    }

    /// <summary>
    /// The sign-in steps of <c>OnTokenValidated</c> (identity design §1.1, §1.7, §2.3): the tenant/guest check, then the
    /// claim mapping, then department scope from member groups against the mirror's active departments. Returns the
    /// refusal reason (<c>wrong_tenant</c>, <c>guest_refused</c>), or null when the user may sign in.
    /// </summary>
    public static async Task<string?> ValidateSignInAsync(ClaimsPrincipal principal, OidcOptions options, RagDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(db);
        if (TenantCheck.Refusal(principal, options) is { } refusal)
        {
            return refusal;
        }

        // Read at each sign-in, so a changed mapping applies to the next sign-in without a restart (M-17 C10).
        var setting = await db.Settings.AsNoTracking().Select(s => s.IdentityRoleGroups).FirstOrDefaultAsync(cancellationToken);
        ApplyClaimMapping(principal, options, RoleGroupMap.Parse(setting));
        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.IsActive && d.MemberGroups.Length > 0)
            .Select(d => new DepartmentGroups(d.CostCenterCode, d.IsActive, d.MemberGroups))
            .ToListAsync(cancellationToken);
        DepartmentMembershipResolver.Apply(principal, departments, options);
        return null;
    }

    /// <summary>
    /// Cookie session (BFF) with OpenID Connect sign-in, antiforgery for the SPA and the authorization policies. The OIDC
    /// authority, client, secret and extra scopes are the effective sign-in values (<see cref="AdminSignIn"/>), applied each
    /// time the options are built.
    /// </summary>
    public static void AddAdminAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-XSRF-TOKEN";
            o.Cookie.Name = AdminCookies.Antiforgery;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
        });
        builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        }).AddCookie(o =>
        {
            o.Cookie.Name = AdminCookies.Session;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = AdminSessionLimit.Lifetime;
            o.SlidingExpiration = false;
            o.Events.OnSigningIn = ctx =>
            {
                var services = ctx.HttpContext.RequestServices;
                AdminSessionLimit.OnSigningIn(ctx, TimeSpan.FromHours(services.GetRequiredService<IOptions<AdminOptions>>().Value.MaxSessionHours),
                    services.GetRequiredService<TimeProvider>().GetUtcNow());
                // A new sign-in (not an extension, which signs in again with the same principal) gets its session id.
                if (ctx.Principal?.Identity is ClaimsIdentity identity && !identity.HasClaim(c => c.Type == AdminClaims.Session))
                {
                    identity.AddClaim(new Claim(AdminClaims.Session, AdminActor.NewSessionId()));
                    var actor = AdminActor.Of(ctx.HttpContext, ctx.Principal);
                    SecurityEvents.AdminSignIn(SecurityEvents.CreateLogger(services.GetRequiredService<ILoggerFactory>()), actor.Subject,
                        actor.SourceAddress, actor.Roles, actor.Departments, actor.SessionId);
                }

                return Task.CompletedTask;
            };
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        }).AddOpenIdConnect(o =>
        {
            o.ResponseType = "code";
            o.UsePkce = true;
            o.SaveTokens = false;
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            o.TokenValidationParameters.NameClaimType = "name";
            o.TokenValidationParameters.RoleClaimType = AdminClaims.Roles;
            o.Scope.Add("email");
            o.Events.OnTokenValidated = async ctx =>
            {
                var services = ctx.HttpContext.RequestServices;
                // The values this handler's options were built from, with the deployment's role groups (identity design §19, §2.5).
                var values = services.GetRequiredService<AdminSignIn>().For(ctx.Options)
                    .ToOidcOptions(services.GetRequiredService<IOptions<OidcOptions>>().Value.RoleGroups);
                var refusal = await ValidateSignInAsync(ctx.Principal!, values, services.GetRequiredService<RagDbContext>(), ctx.HttpContext.RequestAborted);
                if (refusal is not null)
                {
                    // No session; the admin UI explains the refusal (identity design §1.7).
                    SecurityEvents.AuthFailed(SecurityEvents.CreateLogger(services.GetRequiredService<ILoggerFactory>()), refusal, "oidc-signin",
                        "/signin-oidc", AdminActor.AddressOf(ctx.HttpContext), null, null, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    ctx.HandleResponse();
                    ctx.Response.Redirect("/?signin=refused&reason=" + refusal);
                }
            };
            // An expired session on an API call must answer 401 so the UI can send the user to sign in; a redirect to the
            // identity provider would be followed by fetch and fail on CORS. Only /bff/login starts a real sign-in: any other
            // challenge is sent there, because /bff/login reads the current sign-in values before its handler is created
            // (identity design §19, "Reload").
            o.Events.OnRedirectToIdentityProvider = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                }

                if (!ctx.Request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.Redirect(QueryHelpers.AddQueryString(LoginPath, "returnUrl", ctx.Properties.RedirectUri ?? "/"));
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                }

                var services = ctx.HttpContext.RequestServices;
                if (services.GetRequiredService<AdminSignIn>().For(ctx.Options).NotConfiguredReason is { } reason)
                {
                    // No identity provider to send the person to; the admin UI reads signIn.configured from /bff/user.
                    LogSignInNotConfigured(services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminAuthentication)), reason);
                    throw AdminFaults.Of(AdminErrorCodes.SignInNotConfigured);
                }

                return Task.CompletedTask;
            };
            // A sign-in that cannot be completed gives no session and no 500: a state from before the sign-in values changed (its
            // code is refused by the new provider or client), an expired or replayed callback, the provider's error. The person
            // starts again from the sign-in page.
            o.Events.OnRemoteFailure = ctx =>
            {
                var loggers = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
                LogSignInFailed(loggers.CreateLogger(typeof(AdminAuthentication)), ctx.Failure?.GetType().Name ?? "unknown");
                // A refused sign-in like the others (security event 5001): a forged or replayed callback shows here.
                SecurityEvents.AuthFailed(SecurityEvents.CreateLogger(loggers), "callback_failed", "oidc-signin", "/signin-oidc",
                    AdminActor.AddressOf(ctx.HttpContext), null, null, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                ctx.HandleResponse();
                ctx.Response.Redirect("/?signin=failed");
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToIdentityProviderForSignOut = async ctx =>
            {
                if (!ctx.HttpContext.RequestServices.GetRequiredService<AdminSignIn>().For(ctx.Options).IsConfigured)
                {
                    // The session is already gone (the cookie scheme signs out first); there is no provider to sign out at.
                    await ctx.Response.WriteAsJsonAsync(new { redirectUrl = "/" });
                    ctx.HandleResponse();
                    return;
                }

                ctx.ProtocolMessage.ClientId = ctx.Options.ClientId;
                ctx.ProtocolMessage.State = ctx.Options.StateDataFormat.Protect(ctx.Properties);
                if (string.IsNullOrEmpty(ctx.ProtocolMessage.IssuerAddress))
                {
                    throw new InvalidOperationException("OIDC end-session endpoint is missing.");
                }
                await ctx.Response.WriteAsJsonAsync(new { redirectUrl = ctx.ProtocolMessage.CreateLogoutRequestUrl() });
                ctx.HandleResponse();
            };
        });
        builder.Services.AddAuthorization(o =>
        {
            // From the permission matrix (admin design §3.1): several roles add up.
            foreach (var policy in AdminPolicies.All)
            {
                o.AddPolicy(policy, p => p.RequireRole(PermissionMatrix.RolesFor(policy)));
            }
        });
        // Sessions live on the server: the cookie only names one, and signing out deletes it (a copied cookie stops working).
        builder.Services.AddSingleton<ITicketStore, AdminTicketStore>();
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((o, store) => o.SessionStore = store);
        // The effective sign-in values, applied at every build of the options: the first, and each rebuild after they changed.
        builder.Services.AddSingleton<AdminSignIn>();
        builder.Services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme).Configure<AdminSignIn>((o, signIn) => signIn.Configure(o));
    }

    [LoggerMessage(LogLevel.Warning, "Sign-in refused: sign-in is not configured ({Reason})")]
    private static partial void LogSignInNotConfigured(ILogger logger, string reason);

    [LoggerMessage(LogLevel.Warning, "A sign-in callback could not be completed ({FailureType}); the person is sent back to sign in")]
    private static partial void LogSignInFailed(ILogger logger, string failureType);

    public static void MapAdminAuthentication(this WebApplication app)
    {
        app.MapGet(LoginPath, (string? returnUrl) =>
        {
            var destination = returnUrl ?? "/";
            if (!destination.StartsWith('/') || destination.StartsWith("//", StringComparison.Ordinal) || destination.Contains('\\', StringComparison.Ordinal) ||
                destination.Any(char.IsControl))
            {
                return Results.Problem(statusCode: 400, detail: "The return address must be a local path.");
            }
            return Results.Challenge(new AuthenticationProperties { RedirectUri = destination }, [OpenIdConnectDefaults.AuthenticationScheme]);
        });
        app.MapGet(UserPath, async (HttpContext http, IAntiforgery antiforgery, IHostEnvironment environment, IOptions<AdminOptions> admin, GatewayFeatures gateway,
            AdminSignIn signIn, TimeProvider time, IConfiguration configuration) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            http.Response.Cookies.Append(AdminCookies.XsrfToken, tokens.RequestToken!, new CookieOptions
            {
                Secure = true, HttpOnly = false, SameSite = SameSiteMode.Lax, Path = "/",
            });
            var auth = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var fromGroups = http.User.FindAll(AdminClaims.GroupDepartmentCodes).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
            return Results.Ok(new
            {
                isAuthenticated = http.User.Identity?.IsAuthenticated == true,
                environment = environment.EnvironmentName,
                // The gateway's organisation (its context's organisation_name, M-17 C12); null before the first context answer.
                organisationName = gateway.OrganisationName,
                name = http.User.Identity?.Name,
                email = http.User.FindFirstValue("email"),
                roles = http.User.FindAll(AdminClaims.Roles).Select(c => c.Value).ToArray(),
                departmentCodes = http.User.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value).ToArray(),
                // identity design §1.5: where each department came from; no group ids are returned.
                departmentSources = http.User.FindAll(AdminClaims.DepartmentCodes)
                    .Select(c => new { code = c.Value, source = fromGroups.Contains(c.Value) ? "group" : "claim" }).ToArray(),
                teamCodes = http.User.FindAll(AdminClaims.TeamCodes).Select(c => c.Value).ToArray(),
                sessionExpiresAt = auth.Properties?.ExpiresUtc,
                sessionLimitAt = AdminSessionLimit.LimitOf(auth.Properties, TimeSpan.FromHours(admin.Value.MaxSessionHours)),
                // Read before this request (AdminSignIn.Refreshes): false while no usable identity provider is set (identity design §19).
                signIn = new { configured = signIn.Current.IsConfigured },
                // From the same read: whether setup mode is open, so the admin UI goes to /setup (identity design §19).
                setup = new { open = SetupMode.IsOpen(signIn.Current.Values, time.GetUtcNow()) },
                // Whether the deployment runs Open WebUI (OpenWebUi:Enabled, as setup's state reads it), so the admin UI leaves it out when not.
                openWebUi = new { enabled = OpenWebUiSignIn.Enabled(configuration) },
            });
        });
        app.MapPost(LogoutPath, (HttpContext http, ILoggerFactory loggers) =>
        {
            var actor = AdminActor.Of(http);
            SecurityEvents.AdminSignOut(SecurityEvents.CreateLogger(loggers), actor.Subject, actor.SourceAddress, actor.SessionId);
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        }).RequireAuthorization();
        // Extends within the absolute limit; the session (and its sign-in time) stays the same, only its expiry moves.
        app.MapPost("/bff/session/extend", async (HttpContext http, TimeProvider time, IOptions<AdminOptions> admin, ILoggerFactory loggers) =>
        {
            var auth = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var properties = auth.Properties ?? new AuthenticationProperties();
            var maxSession = TimeSpan.FromHours(admin.Value.MaxSessionHours);
            if (AdminSessionLimit.Extend(properties, maxSession, time.GetUtcNow()) is not { } expires)
            {
                return Results.Problem(statusCode: 409, title: "The session cannot be extended",
                    detail: $"Sessions last at most {admin.Value.MaxSessionHours} hours. Save your work and sign in again.");
            }

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, http.User, properties);
            var actor = AdminActor.Of(http);
            SecurityEvents.AdminSessionExtended(SecurityEvents.CreateLogger(loggers), actor.Subject, actor.SessionId, expires, actor.SourceAddress);
            return Results.Ok(new { sessionExpiresAt = expires, sessionLimitAt = AdminSessionLimit.LimitOf(properties, maxSession) });
        }).RequireAuthorization();
    }
}

/// <summary>
/// Cookie names. They differ from the gateway's: browsers ignore the port, so on localhost the gateway admin (5173) and
/// ours (5174) would otherwise overwrite each other's cookies (identity design §1.2).
/// </summary>
public static class AdminCookies
{
    public const string Session = "__Host-kalla-admin";
    public const string Antiforgery = "__Host-kalla-csrf";

    /// <summary>The readable antiforgery token; the admin UI's fetch helper sends it back as <c>X-XSRF-TOKEN</c>.</summary>
    public const string XsrfToken = "RAG-XSRF-TOKEN";
}

/// <summary>Claims the admin API reads from the signed-in user, after <see cref="AdminAuthentication.ApplyClaimMapping"/>.</summary>
public static class AdminClaims
{
    public const string Subject = "sub";

    /// <summary>The Entra object id, the same user id in every registration of the tenant: the audit actor (identity design §1.1).</summary>
    public const string ObjectId = "oid";

    public const string Roles = "roles";
    public const string DepartmentCodes = "departmentCodes";
    public const string TeamCodes = "teamCodes";

    /// <summary>The department codes added from member groups (identity design §2.3), for <c>departmentSources</c>.</summary>
    public const string GroupDepartmentCodes = "ume_group_departments";

    /// <summary>A random id given at sign-in and kept while the session lasts (audit entries and security events).</summary>
    public const string Session = "ume_session";
}

/// <summary>Copied from the gateway: CSRF protection for state-changing calls to <c>/api</c> and <c>/bff</c>.</summary>
public sealed class AdminAntiforgeryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(antiforgery);
        if ((http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || http.Request.Path.StartsWithSegments("/bff", StringComparison.OrdinalIgnoreCase)) &&
            !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
        {
            if (http.Request.Headers["X-Requested-With"] != "XMLHttpRequest")
            {
                await Results.Problem(statusCode: 400, detail: "CSRF protection is missing. Reload the page.").ExecuteAsync(http);
                return;
            }
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem(statusCode: 400, detail: "The CSRF token is invalid. Reload the page.").ExecuteAsync(http);
                return;
            }
        }
        await next(http);
    }
}
