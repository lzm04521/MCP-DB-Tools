using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpDbTools.Server.Admin;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using McpDbTools.Server.Maintenance;
using McpDbTools.Server.Security;
using McpDbTools.Server.Tools;
using McpDbTools.Server;
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

    // host shutdown 时给 AuditLogger.DisposeAsync 排空审计队列留足时间（其内部上限 5s）
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));

    using IHost host = builder.Build();

    // 进程退出兜底：Ctrl+C 取消 token → host 优雅停止 → DI Singleton DisposeAsync 排空审计。
    // 注意：TerminateProcess 硬杀无法兜底，db_query 审计可靠性由 host 优雅停止 → AuditLogger.DisposeAsync 排空保证（stdin EOF 路径已实证）。
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    try
    {
        await host.RunAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        // 正常关闭：Ctrl+C 或外部取消
    }
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
    // 运维清理后台服务：依赖 AdminConfigService，仅在 Admin/混合模式注册（D2 决策：方案 a）
    builder.Services.AddHostedService<MaintenanceHostedService>();
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
    app.MapGet("/admin/keywords", () => Results.Redirect("/admin/#/keywords", permanent: false));
    app.MapGet("/admin/keywords.html", () => Results.Redirect("/admin/#/keywords", permanent: false));
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

    // 版本信息：从 AssemblyInformationalVersion（build 时 git tag 注入）读取，供 Admin UI 展示。
    api.MapGet("/version", () => Results.Ok(new { version = AppVersion.Current }));

    api.MapGet("/config", (AdminConfigService service) => Results.Ok(service.GetConfig()));
    api.MapPut("/config", async (AdminConfigRequest request, AdminConfigService service, CancellationToken cancellationToken) =>
    {
        AdminSaveResult result = await service.SaveConfigAsync(request, cancellationToken);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    });

    // 全局设置（maintenance 节点）：独立读写，只改 maintenance，不触碰 projects/keywords。
    // 复用 api 组的 session cookie 鉴权 filter（见上方 AddEndpointFilter）。
    api.MapGet("/maintenance", (AdminConfigService service) => Results.Ok(service.GetMaintenance()));
    api.MapPut("/maintenance", async (MaintenanceSettingsRequest request, AdminConfigService service, CancellationToken cancellationToken) =>
    {
        try
        {
            MaintenanceSettingsResponse result = await service.SaveMaintenanceAsync(request, cancellationToken);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    });

    // 审计日志查询：GET /admin/api/audit-logs?project=&environment=&databaseType=&success=&fromTime=&toTime=&sqlContains=&page=&pageSize=
    // success 取 true/false，未传或其它值表示不限定。查询参数全部可选。
    api.MapGet("/audit-logs", (AuditLogger audit,
        string? project, string? environment, string? databaseType,
        string? success, string? fromTime, string? toTime, string? sqlContains,
        int? page, int? pageSize) =>
    {
        bool? successFilter = null;
        if (bool.TryParse(success, out bool parsedSuccess))
        {
            successFilter = parsedSuccess;
        }

        var query = new AuditLogQuery
        {
            Project = project,
            Environment = environment,
            DatabaseType = databaseType,
            Success = successFilter,
            FromTime = fromTime,
            ToTime = toTime,
            SqlContains = sqlContains,
            Page = page ?? 1,
            PageSize = pageSize ?? 50
        };
        return Results.Ok(audit.Query(query));
    });

    // 审计日志查询结果（懒加载）：按主表 id 拉取子表 result_json。
    // 子表无数据（老记录/失败查询/开关关闭时记录）返回 404。
    api.MapGet("/audit-logs/{id:long}/result", (long id, AuditLogger audit) =>
    {
        string? json = audit.GetResultJson(id);
        return json is null
            ? Results.NotFound()
            : Results.Ok(new { resultJson = json });
    });

    // 审计日志清理：删除指定天数前的记录。days 取 30/60/90 等，由调用方传。
    api.MapPost("/audit-logs/cleanup", (AuditLogger audit, RestoreBackupRequest request) =>
    {
        string name = request.Name ?? string.Empty;
        if (!int.TryParse(name, out int days) || days <= 0)
        {
            return Results.Json(new { error = "清理天数必须为正整数" }, statusCode: StatusCodes.Status400BadRequest);
        }
        try
        {
            int deleted = audit.DeleteOlderThan(days);
            return Results.Ok(new { success = true, deleted, days });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    });

    // 备份文件清理：删除指定天数前的备份（按文件最后写入时间判断）。供全局设置页手动清理使用。
    api.MapPost("/backups/cleanup", (AdminConfigService service, RestoreBackupRequest request) =>
    {
        string name = request.Name ?? string.Empty;
        if (!int.TryParse(name, out int days) || days <= 0)
        {
            return Results.Json(new { error = "清理天数必须为正整数" }, statusCode: StatusCodes.Status400BadRequest);
        }
        try
        {
            int deleted = service.DeleteBackupsOlderThan(days);
            return Results.Ok(new { success = true, deleted, days });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    });

    // 测试连接：用入参连接串即时测试，不落盘、不影响当前配置
    api.MapPost("/test-connection", async (TestConnectionRequest request, AdminConfigService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.TestConnectionAsync(request, cancellationToken)));

    // 备份管理：列表 / 下载 / 恢复 / 删除
    api.MapGet("/backups", (AdminConfigService service) => Results.Ok(service.ListBackups()));

    // 下载备份（流式返回文件，Content-Disposition 触发浏览器下载）
    api.MapGet("/backups/download", (string? name, AdminConfigService service) =>
    {
        string? path = service.GetBackupPath(name ?? string.Empty);
        if (path is null)
        {
            return Results.Json(new { error = "备份不存在" }, statusCode: StatusCodes.Status404NotFound);
        }
        Stream stream = File.OpenRead(path);
        return Results.File(stream, "application/json", fileDownloadName: name);
    });

    api.MapPost("/backups/restore", async (RestoreBackupRequest request, AdminConfigService service) =>
    {
        RestoreResult result = service.RestoreBackup(request.Name);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    });

    api.MapPost("/backups/delete", (RestoreBackupRequest request, AdminConfigService service) =>
    {
        DeleteBackupResult result = service.DeleteBackup(request.Name);
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
    // 配置：数据目录由 DataDirectoryResolver 集中解析，统一确定 config.json / audit.db / backups 位置。
    // 解析优先级：环境变量 ConfigStore__ConfigPath > %USERPROFILE%\.mcpdbtools > exe 同目录。
    // 这样无论以何种账户（当前用户 / LocalSystem）启动，程序自身都能定位到一致的数据目录，
    // 不依赖外部脚本配置环境变量。
    services.Configure<ConfigStoreOptions>(options =>
    {
        string dataDir = DataDirectoryResolver.Resolve();
        options.ConfigPath = Path.Combine(dataDir, "config.json");
    });
    services.Configure<ConfigStoreOptions>(configuration.GetSection("ConfigStore"));

    // 业务服务
    services.AddSingleton<ConfigStore>();
    services.AddSingleton<ISqlGuard, SqlGuard>();
    services.AddSingleton<DatabaseProviderFactory>();
    services.AddSingleton<IQueryConcurrencyLimiter, QueryConcurrencyLimiter>();
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
