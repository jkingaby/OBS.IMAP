using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Server;
using OBS.IMAP.Core;
using OBS.IMAP.Infrastructure;
using OBS.IMAP.MCP;

namespace OBS.IMAP.Host;

public static class ConnectorHost
{
    public static IHost CreateStdio(ConnectorOptions options, LocalOptions local)
    {
        ConfigurationValidator.Validate(options);
        var allowed = local.AllowedAccountIds ?? options.Accounts.Select(a => a.Id).ToArray();
        ValidateGrants(options, allowed);
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        ConfigureLogging(builder.Logging);
        AddServices(builder.Services, options);
        var identity = new RequestIdentity("local", "operator", allowed, allowWrites: true);
        builder.Services.AddSingleton<IRequestAccessContext>(new FixedAccessContext(identity));
        var mcp = builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<MailTools>();
        var policies = options.Accounts.Where(account => identity.CanAccess(account.Id)).Select(account => account.Writes).ToArray();
        var tools = new List<McpServerTool>();
        AddWriteTool(nameof(MailWriteTools.MoveMessage), policies.Any(writes => writes?.Move == true));
        AddWriteTool(nameof(MailWriteTools.TrashMessage), policies.Any(writes => writes?.Trash == true));
        AddWriteTool(nameof(MailWriteTools.CreateDraft), policies.Any(writes => writes?.CreateDraft == true));
        AddWriteTool(nameof(MailWriteTools.SendMessage), policies.Any(writes => writes?.Send == true));
        if (tools.Count > 0)
        {
            builder.Services.AddSingleton(services => new MailWriteTools(
                (MailReadService)services.GetRequiredService<IMailReadService>(), services.GetRequiredService<IRequestAccessContext>(),
                options, services.GetRequiredService<ILogger<MailWriteTools>>()));
            mcp.WithTools(tools);
        }
        return builder.Build();

        void AddWriteTool(string method, bool enabled)
        {
            if (enabled)
                tools.Add(McpServerTool.Create(typeof(MailWriteTools).GetMethod(method)!,
                    request => request.Services!.GetRequiredService<MailWriteTools>()));
        }
    }

    public static WebApplication CreateHttp(ConnectorOptions options, RemoteOptions remote,
        Action<JwtBearerOptions>? configureAuthentication = null, IMailReadService? serviceOverride = null)
    {
        ConfigurationValidator.Validate(options);
        remote.Validate();
        foreach (var grant in remote.Grants) ValidateGrants(options, grant.AccountIds);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        ConfigureLogging(builder.Logging);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Parse(remote.ListenAddress), remote.Port);
            server.Limits.MaxRequestBodySize = 64 * 1024;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
        });
        AddServices(builder.Services, options, serviceOverride);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(remote);
        builder.Services.AddSingleton<IRequestAccessContext, HttpAccessContext>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.Authority = remote.Issuer;
            jwt.Audience = remote.Audience;
            jwt.RequireHttpsMetadata = true;
            jwt.MapInboundClaims = false;
            jwt.IncludeErrorDetails = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = remote.Issuer,
                ValidateAudience = true,
                ValidAudience = remote.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512],
                ClockSkew = TimeSpan.FromSeconds(30)
            };
            jwt.Events = new JwtBearerEvents
            {
                OnChallenge = context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{new Uri(new Uri(remote.ResourceUrl), "/.well-known/oauth-protected-resource")}\", scope=\"{remote.RequiredScope}\"";
                    return Task.CompletedTask;
                },
                OnForbidden = context =>
                {
                    context.Response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_scope\", scope=\"{remote.RequiredScope}\"";
                    return Task.CompletedTask;
                }
            };
            configureAuthentication?.Invoke(jwt);
        });
        builder.Services.AddAuthorization(auth => auth.AddPolicy("mail", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        {
            var issuer = context.User.FindFirstValue("iss");
            var subject = context.User.FindFirstValue("sub");
            var scopes = context.User.FindAll("scope").Concat(context.User.FindAll("scp"))
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return issuer == remote.Issuer && subject is not null && scopes.Contains(remote.RequiredScope, StringComparer.Ordinal)
                && remote.Grants.Any(g => g.Subject == subject);
        })));
        builder.Services.AddRateLimiter(rate =>
        {
            rate.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rate.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter("global",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })),
                PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetTokenBucketLimiter(
                    context.User.FindFirstValue("sub") ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new TokenBucketRateLimiterOptions { TokenLimit = 10, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromSeconds(1), AutoReplenishment = true, QueueLimit = 0 })));
        });
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<MailTools>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!remote.AllowedHosts.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (context.Request.Headers.TryGetValue("Origin", out var origins)
                && (origins.Count != 1 || !remote.AllowedOrigins.Contains(origins[0]!, StringComparer.Ordinal)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });
        // No forwarded-header middleware: the backend never trusts user-supplied proxy identity or scheme.
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new
        {
            resource = remote.ResourceUrl,
            authorization_servers = new[] { remote.Issuer },
            scopes_supported = new[] { remote.RequiredScope },
            bearer_methods_supported = new[] { "header" }
        })).AllowAnonymous();
        app.MapMcp("/mcp").RequireAuthorization("mail");
        return app;
    }

    private static void AddServices(IServiceCollection services, ConnectorOptions options, IMailReadService? serviceOverride = null)
    {
        services.AddSingleton(options);
        if (serviceOverride is null) services.AddSingleton<IMailReadService>(_ => new MailReadService(options));
        else services.AddSingleton(serviceOverride);
    }

    private static void ValidateGrants(ConnectorOptions options, IEnumerable<string> allowed)
    {
        var known = options.Accounts.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        if (allowed.Any(id => !known.Contains(id))) throw new InvalidOperationException("Access policy refers to an unknown account.");
    }

    private static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        // Framework exceptions and request diagnostics can contain untrusted identifiers or URLs.
        logging.AddFilter("Microsoft", LogLevel.None);
        logging.AddFilter("ModelContextProtocol", LogLevel.None);
    }
}

internal sealed class FixedAccessContext(RequestIdentity identity) : IRequestAccessContext
{
    public RequestIdentity Identity => identity;
}

internal sealed class HttpAccessContext(IHttpContextAccessor accessor, RemoteOptions options) : IRequestAccessContext
{
    public RequestIdentity Identity
    {
        get
        {
            var user = accessor.HttpContext?.User;
            var issuer = user?.FindFirstValue("iss");
            var subject = user?.FindFirstValue("sub");
            if (user?.Identity?.IsAuthenticated != true || issuer != options.Issuer || subject is null)
                throw new ConnectorException(ErrorCodes.AccessDenied);
            var grant = options.Grants.SingleOrDefault(g => g.Subject == subject);
            if (grant is null) throw new ConnectorException(ErrorCodes.AccessDenied);
            return new RequestIdentity(issuer, subject, grant.AccountIds);
        }
    }
}
