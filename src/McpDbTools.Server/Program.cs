using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpDbTools.Server.Admin;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using McpDbTools.Server.Security;
using McpDbTools.Server.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

AdminStartupOptions startup = AdminStartupOptions.Parse(args);

if (startup.Mode == RunMode.Mcp)
{
    await RunMcpAsync(args);
}
else
{
    await RunAdminAsync(args, startup);
}

static async Task RunMcpAsync(string[] args)
{
    // MCP over stdio：stdout 是协议通道，所有日志必须走 stderr，否则会破坏协议
    var builder = Host.CreateApplicationBuilder(args);
    ConfigureLogging(builder.Logging);
    ConfigureBusinessServices(builder.Services, builder.Configuration);

    // MCP Server：stdio 传输，从程序集扫描 [McpServerToolType] 工具
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

static async Task RunAdminAsync(string[] args, AdminStartupOptions startup)
{
    var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        WebRootPath = webRoot
    });
    ConfigureLogging(builder.Logging);
    ConfigureBusinessServices(builder.Services, builder.Configuration);
    builder.Services.AddSingleton<AdminConfigService>();
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, startup.AdminPort));

    if (startup.Mode == RunMode.AdminAndMcp)
    {
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
    }

    var app = builder.Build();
    string sessionSecret = GenerateAdminSessionSecret();

    app.Use(async (context, next) =>
    {
        if (IsAdminPageRequest(context.Request))
        {
            SetAdminSessionCookie(context.Response, sessionSecret);
        }
        await next.Invoke();
    });

    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapGet("/", () => Results.Redirect("/admin"));
    app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));
    app.MapGet("/admin/keywords", () => Results.Redirect("/admin/keywords.html"));
    app.MapGet("/admin/session", (HttpResponse response) =>
    {
        SetAdminSessionCookie(response, sessionSecret);
        return Results.NoContent();
    });

    var api = app.MapGroup("/admin/api");
    api.AddEndpointFilter(async (context, next) =>
    {
        var httpContext = context.HttpContext;
        if (!IsAuthorized(httpContext.Request, sessionSecret))
        {
            return Results.Json(new { error = "ADMIN_SESSION_REQUIRED" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        return await next(context);
    });

    api.MapGet("/config", (AdminConfigService service) => Results.Ok(service.GetConfig()));
    api.MapPut("/config", async (AdminConfigRequest request, AdminConfigService service, CancellationToken cancellationToken) =>
    {
        AdminSaveResult result = await service.SaveConfigAsync(request, cancellationToken);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    });

    app.Logger.LogInformation("Admin UI: http://127.0.0.1:{Port}/admin", startup.AdminPort);
    if (startup.Mode == RunMode.AdminAndMcp)
    {
        app.Logger.LogWarning("当前为调试混合模式：同时启用 MCP stdio 与 Admin UI，生产环境不推荐。");
    }

    await app.RunAsync();
}

static void ConfigureBusinessServices(IServiceCollection services, IConfiguration configuration)
{
    // 配置：默认读取程序目录 config.json，可通过环境变量 ConfigStore__ConfigPath 或 appsettings.json 覆盖
    services.Configure<ConfigStoreOptions>(options =>
    {
        options.ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    });
    services.Configure<ConfigStoreOptions>(configuration.GetSection("ConfigStore"));

    // 业务服务
    services.AddSingleton<ConfigStore>();
    services.AddSingleton<ISqlGuard, SqlGuard>();
    services.AddSingleton<DatabaseProviderFactory>();
    services.AddSingleton<AuditLogger>();
    services.AddSingleton<DbQueryTool>();
    services.AddSingleton<DbListTool>();
}

static void ConfigureLogging(ILoggingBuilder logging)
    => logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

const string adminSessionCookieName = "McpDbTools.AdminSession";

static string GenerateAdminSessionSecret()
{
    Span<byte> bytes = stackalloc byte[32];
    RandomNumberGenerator.Fill(bytes);
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

static bool IsAdminPageRequest(HttpRequest request)
{
    string path = request.Path.Value ?? string.Empty;
    return path.Equals("/admin", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/admin/", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/admin/index.html", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/admin/keywords", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/admin/keywords.html", StringComparison.OrdinalIgnoreCase);
}

static void SetAdminSessionCookie(HttpResponse response, string sessionSecret)
{
    response.Cookies.Append(adminSessionCookieName, sessionSecret, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = false,
        Path = "/admin"
    });
}

static bool IsAuthorized(HttpRequest request, string sessionSecret)
{
    return request.Cookies.TryGetValue(adminSessionCookieName, out string? session) &&
           FixedTimeEquals(session, sessionSecret);
}

static bool FixedTimeEquals(string left, string right)
{
    byte[] leftBytes = Encoding.UTF8.GetBytes(left);
    byte[] rightBytes = Encoding.UTF8.GetBytes(right);
    return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

internal enum RunMode
{
    Mcp,
    AdminOnly,
    AdminAndMcp
}

internal sealed record AdminStartupOptions(RunMode Mode, int AdminPort)
{
    public static AdminStartupOptions Parse(string[] args)
    {
        RunMode mode = args.Any(a => a.Equals("--admin-only", StringComparison.OrdinalIgnoreCase))
            ? RunMode.AdminOnly
            : args.Any(a => a.Equals("--admin", StringComparison.OrdinalIgnoreCase))
                ? RunMode.AdminAndMcp
                : RunMode.Mcp;

        int port = 5123;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--admin-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[i + 1], out port) || port <= 0 || port > 65535)
                {
                    throw new ArgumentException($"无效的 --admin-port: {args[i + 1]}");
                }
            }
        }

        return new AdminStartupOptions(mode, port);
    }
}
