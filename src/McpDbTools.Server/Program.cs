using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpDbTools.Server.Admin;
using McpDbTools.Server.Audit;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using McpDbTools.Server.Hosting;
using McpDbTools.Server.Logging;
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
using Velopack;

VelopackApp.Build().Run();
int adminPort = AdminStartupOptions.ParsePort(args);
await RunAsync(args, adminPort);

static async Task RunAsync(string[] args, int adminPort)
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
    // 系统设置页依赖：自启动注册表读写、Claude MCP 注册、运行时端口状态
    builder.Services.AddSingleton<AutostartService>();
    builder.Services.AddSingleton<ClaudeMcpRegistrar>();
    // 应用更新（Velopack）：更新源 URL 由 UpdateSource 环境变量/appsettings 配置，未配置则禁用检查
    builder.Services.AddSingleton(new UpdateChecker(builder.Configuration["UpdateSource"]));
    builder.Services.AddSingleton(new RunningState { Port = adminPort });
    // 运维清理后台服务：依赖 AdminConfigService（D2 决策：方案 a）
    builder.Services.AddHostedService<MaintenanceHostedService>();
    // host shutdown 时给 AuditLogger.DisposeAsync 排空审计队列留足时间（其内部上限 5s）
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, adminPort));

    // MCP HTTP transport：与 Admin 同进程同端口，端点 /mcp
    builder.Services
        .AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

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
    // MCP Streamable HTTP 端点（不鉴权，仅 127.0.0.1，spec 第 6 节）
    app.MapMcp("/mcp");
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

    // ============ 系统设置：端口 / 自启动 / 重启 / MCP 注册 ============

    // 端口：runningPort=本次启动实际端口（只读，重启前不变）；configPort=config.json 的 port（下次启动生效）
    api.MapGet("/port", (RunningState rs, AdminConfigService svc) => Results.Ok(new
    {
        runningPort = rs.Port,
        configPort = svc.GetConfigPort()
    }));

    // 端口写入：仅改 port 字段，透传其余。改后需重启（Kestrel 启动时绑定），前端保存后引导重启
    api.MapPut("/port", async (PortRequest request, AdminConfigService svc, CancellationToken cancellationToken) =>
    {
        try
        {
            int saved = await svc.SavePortAsync(request.Port, cancellationToken);
            return Results.Ok(new { port = saved });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    });

    // 自启动开关：读写 HKCU\...\Run（当前用户级，无需管理员）
    api.MapGet("/autostart", (AutostartService svc) => Results.Ok(new { enabled = svc.IsEnabled() }));

    api.MapPut("/autostart", (AutostartRequest request, AutostartService svc) =>
    {
        if (request.Enabled)
        {
            svc.Enable();
        }
        else
        {
            svc.Disable();
        }
        return Results.Ok(new { enabled = svc.IsEnabled() });
    });

    // 重启服务：延迟拉新实例 + 停当前实例。响应返回后旧实例退出，新实例在新端口（若改了）启动
    api.MapPost("/restart", (IHostApplicationLifetime lifetime) =>
    {
        RestartHelper.RestartAndExit(lifetime);
        return Results.Ok(new { restarting = true });
    });

    // 注册到 Claude Code CLI：claude mcp add --transport http --scope <scope> db-tools <url>
    api.MapPost("/register-mcp", async (McpRegisterRequest request, RunningState rs,
        ClaudeMcpRegistrar registrar, CancellationToken cancellationToken) =>
    {
        string scope = string.IsNullOrWhiteSpace(request.Scope) ? "user" : request.Scope;
        McpRegisterResult result = await registrar.RegisterAsync(rs.Port, scope, cancellationToken);
        return result.Success
            ? Results.Ok(result)
            : Results.Json(result, statusCode: StatusCodes.Status400BadRequest);
    });

    // ============ 应用更新（Velopack）============

    // 更新状态：currentVersion（当前版本）+ 检查结果。页面加载时调用，反映是否已安装/有新版
    api.MapGet("/update/status", async (UpdateChecker uc) =>
    {
        UpdateStatus s = await uc.CheckAsync();
        return Results.Ok(new
        {
            currentVersion = AppVersion.Current,
            s.Configured,
            s.Installed,
            s.HasUpdate,
            s.TargetVersion,
            s.Downloaded,
            s.Error
        });
    });

    // 手动触发检查
    api.MapPost("/update/check", async (UpdateChecker uc) => Results.Ok(await uc.CheckAsync()));

    // 下载上次检查到的更新
    api.MapPost("/update/download", async (UpdateChecker uc) => Results.Ok(await uc.DownloadAsync()));

    // 应用已下载的更新并重启（Velopack 接管退出+替换+重启，响应返回后本进程随即结束）
    api.MapPost("/update/apply", (UpdateChecker uc) =>
    {
        uc.ApplyAndRestart();
        return Results.Ok(new { applying = true });
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

    // 项目配置导入：预览（dry-run，不落盘）与应用（原子落盘 + 自动备份）。
    // 复用 api 组的 session cookie 鉴权 filter。导出由前端直接生成，无后端端点。
    api.MapPost("/projects/import-preview", (ImportRequest request, AdminConfigService service) =>
        Results.Ok(service.GetImportPreview(request.Json ?? string.Empty)));

    api.MapPost("/projects/import-apply", async (ImportRequest request, AdminConfigService service, CancellationToken cancellationToken) =>
    {
        ImportApplyResult result = await service.ApplyImportAsync(request.Json ?? string.Empty, cancellationToken);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    });

    app.Logger.LogInformation("Admin UI: http://127.0.0.1:{Port}/admin", adminPort);
    app.Logger.LogInformation("MCP HTTP: http://127.0.0.1:{Port}/mcp", adminPort);

    // 托盘模式：Web 宿主在后台 Task 运行（不阻塞），主线程跑 WinForms 消息循环。
    // Web 宿主异常退出时触发 StopApplication，让 TrayHost 的 ApplicationStopped 回调退出消息循环。
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    _ = Task.Run(async () =>
    {
        try
        {
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Web 宿主异常退出");
            lifetime.StopApplication();
        }
    });

    // 主线程消息循环；TrayHost 退出（Web 宿主已优雅停止）后返回，随后 DisposeAsync 释放资源
    using var tray = new TrayHost(lifetime, adminPort);
    tray.Run();
    await app.DisposeAsync();
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
    // 审计计数器（AuditLogger 依赖）：持久化总数 + 按本地日，对账用
    services.AddSingleton<AuditCounter>();
    services.AddSingleton<AuditLogger>();
    services.AddSingleton<DbQueryTool>();
    services.AddSingleton<DbListTool>();
}

static void ConfigureLogging(ILoggingBuilder logging)
{
    // console → stderr：服务/计划任务承载下不可见，交互式启动时仍有用，保留。
    logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    // 文件日志：托盘模式无控制台窗口，文件是唯一可见的诊断通道。
    // 写入数据目录 logs/app-yyyyMMdd.txt，UTF-8、按日滚动、启动清理 30 天前旧文件。
    string logDir = Path.Combine(DataDirectoryResolver.EnsureExists(), "logs");
    logging.AddDailyFile(logDir);
}

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

internal sealed record AdminStartupOptions(int AdminPort)
{
    /// <summary>
    /// 解析启动端口，优先级：命令行 --admin-port &gt; config.json 的 port 字段 &gt; 默认 61123。
    /// </summary>
    public static int ParsePort(string[] args)
    {
        const int defaultPort = 61123;

        // 优先级 1：命令行 --admin-port（调试/一次性覆盖）
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--admin-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[i + 1], out int cli) || cli <= 0 || cli > 65535)
                {
                    throw new ArgumentException($"无效的 --admin-port: {args[i + 1]}");
                }
                return cli;
            }
        }

        // 优先级 2：config.json 的 port 字段
        int? configPort = TryReadPortFromConfig();
        if (configPort is int cp && cp > 0 && cp <= 65535)
        {
            return cp;
        }

        // 优先级 3：内置默认
        return defaultPort;
    }

    /// <summary>
    /// 直接读 config.json 的 port 字段（容错：文件缺失/解析失败/无字段 → null）。
    /// 在 DI 容器构造前调用，不能走 ConfigStore，按 DataDirectoryResolver 定位文件用 JsonDocument 轻量读取。
    /// </summary>
    private static int? TryReadPortFromConfig()
    {
        try
        {
            string configPath = Path.Combine(DataDirectoryResolver.Resolve(), "config.json");
            if (!File.Exists(configPath))
            {
                return null;
            }
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("port", out JsonElement portEl) &&
                portEl.ValueKind == JsonValueKind.Number &&
                portEl.TryGetInt32(out int p))
            {
                return p;
            }
        }
        catch
        {
            // 读取失败不阻断启动，回落默认端口
        }
        return null;
    }
}
