using System.ComponentModel;
using System.Text.Json;
using McpDbTools.Server.Configuration;
using ModelContextProtocol.Server;

namespace McpDbTools.Server.Tools;

/// <summary>
/// 只读工具：列出当前配置中所有项目及其环境。
/// 供 Claude 在调用 db_query 前发现可用的 project 与 environment。
/// </summary>
[McpServerToolType]
public sealed class DbListTool
{
    private readonly ConfigStore _configStore;

    // 与 QueryResult.ToJson 保持一致的序列化风格：驼峰 + 中文不转义
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public DbListTool(ConfigStore configStore)
    {
        _configStore = configStore;
    }

    /// <summary>
    /// 列出所有项目及其环境。
    /// 返回 JSON：{ projects: [{ name, defaultEnvironment, environments: ["dev","prod"] }] }。
    /// </summary>
    [McpServerTool(Name = "db_list")]
    [Description("列出当前配置中所有项目及其环境，便于在调用 db_query 前确定可用的 project 与 environment。返回 JSON：{ projects: [{ name, defaultEnvironment, environments: [\"dev\",\"prod\"] }] }。")]
    public Task<string> ListProjects(CancellationToken cancellationToken = default)
    {
        ResolvedConfig config = _configStore.GetResolved();

        var projects = config.Projects
            .Select(p => new
            {
                name = p.Key,
                defaultEnvironment = p.Value.DefaultEnvironment,
                environments = p.Value.Environments.Keys.ToList()
            })
            .ToList();

        return Task.FromResult(JsonSerializer.Serialize(new { projects }, JsonOptions));
    }
}
