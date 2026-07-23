using System.Text.Json;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Tools;

/// <summary>
/// 构造 db_list 返回的项目/环境结构（成功与错误响应共享）。
/// 方法正交：按"项目索引 / 单项目全环境 / 单项目单环境 / 兜底列表"分别提供构造器，
/// 由 DbListTool 按行为矩阵组合调用。环境详情对象结构在所有响应中保持一致。
/// </summary>
public static class ProjectListBuilder
{
    // 与 QueryResult.ToJson 保持一致的序列化风格：驼峰 + 中文不转义
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ───────── 成功响应构造 ─────────

    /// <summary>
    /// 项目索引：所有项目的 name + defaultEnvironment，**不带环境详情**（轻量）。
    /// 用于 db_list 不传 project 时的首次发现。
    /// </summary>
    public static List<object> BuildProjectIndex(ResolvedConfig config) => config.Projects
        .Select(p => (object)new
        {
            name = p.Key,
            defaultEnvironment = p.Value.DefaultEnvironment
        })
        .ToList();

    /// <summary>
    /// 单项目全环境：该项目的 name + defaultEnvironment + 全部环境详情。
    /// 用于 db_list 传 project（存在）、不传 environment 时。
    /// </summary>
    public static object BuildProjectWithEnvironments(KeyValuePair<string, ResolvedProject> p) => new
    {
        name = p.Key,
        defaultEnvironment = p.Value.DefaultEnvironment,
        environments = p.Value.Environments.Select(BuildEnvironment).ToList()
    };

    /// <summary>
    /// 单项目单环境：该项目的 name + defaultEnvironment + 仅指定环境的详情。
    /// 用于 db_list 传 project + environment（均存在）时。
    /// </summary>
    public static object BuildSingleEnvironment(KeyValuePair<string, ResolvedProject> p, string envKey) => new
    {
        name = p.Key,
        defaultEnvironment = p.Value.DefaultEnvironment,
        environments = new[] { BuildEnvironment(new(envKey, p.Value.Environments[envKey])) }
    };

    // ───────── 错误响应兜底构造 ─────────

    /// <summary>
    /// 项目名兜底列表：所有项目的 name 字符串数组。
    /// 用于 db_list 的 PROJECT_NOT_FOUND 错误响应（availableProjects 字段）。
    /// 故意用字符串数组而非对象数组：项目名是简单标识，最省 token，且与成功响应的 projects（对象数组）类型区分。
    /// </summary>
    public static List<string> BuildProjectNameList(ResolvedConfig config) =>
        config.Projects.Keys.ToList();

    /// <summary>
    /// 环境详情兜底列表：指定项目的全部环境详情对象数组。
    /// 用于 db_list 的 ENVIRONMENT_NOT_FOUND 错误响应（environments 字段）。
    /// 故意返回详情而非纯名字：Agent 调用 db_query 前需要数据库类型、生产标识、连接/超时等运行参数。
    /// </summary>
    public static List<object> BuildEnvironmentDetails(ResolvedProject proj) =>
        proj.Environments.Select(BuildEnvironment).ToList();

    // ───────── 环境详情（所有响应共用） ─────────

    /// <summary>
    /// 单个环境的运行参数。包含 Agent 调用前关心的全部信息：
    /// 数据库类型、生产标识、行数上限、并发/连接池/超时配置。
    /// </summary>
    public static object BuildEnvironment(KeyValuePair<string, ResolvedDatabase> e) => new
    {
        name = e.Key,
        type = e.Value.Type.ToString().ToLowerInvariant(),
        databaseName = e.Value.DatabaseName,
        isProduction = e.Value.IsProduction,
        maxRows = e.Value.MaxRows,
        maxConcurrency = e.Value.MaxConcurrency,
        maxPoolSize = e.Value.MaxPoolSize,
        connectTimeoutSeconds = e.Value.ConnectTimeoutSeconds,
        commandTimeout = e.Value.CommandTimeout
    };

    // ───────── 序列化 ─────────

    /// <summary>序列化成功响应：{ success:true, projects:[...] }。projects 数组由调用方传入。</summary>
    public static string SerializeSuccess(List<object> projects) =>
        JsonSerializer.Serialize(new { success = true, projects }, JsonOptions);

    /// <summary>
    /// 序列化错误响应：{ success:false, errorCode, error, + 兜底字段 }。
    /// 兜底字段由调用方用匿名对象传入（如 new { availableProjects = list } 或 new { environments = list }），
    /// 保证字段名语义清晰且与行为矩阵一致。
    /// </summary>
    public static string SerializeFail(string errorCode, string error, object? fallback = null)
    {
        // 合并基础错误字段与兜底字段；fallback 为 null 时只输出基础字段
        var withFallback = fallback is not null
            ? MergeFailPayload(errorCode, error, fallback)
            : (object)new { success = false, errorCode, error };
        return JsonSerializer.Serialize(withFallback, JsonOptions);
    }

    /// <summary>
    /// 用 Dictionary 合并错误基础字段与兜底匿名对象，保证字段顺序（success, errorCode, error, 兜底...）。
    /// 匿名对象无法直接合并，故走 Dictionary；键名与 JSON 输出一致。
    /// </summary>
    private static Dictionary<string, object?> MergeFailPayload(string errorCode, string error, object fallback)
    {
        var dict = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["errorCode"] = errorCode,
            ["error"] = error
        };
        // 遍历兜底对象的所有属性，追加到 payload（如 availableProjects / environments）
        foreach (var prop in fallback.GetType().GetProperties())
        {
            dict[prop.Name] = prop.GetValue(fallback);
        }
        return dict;
    }
}
