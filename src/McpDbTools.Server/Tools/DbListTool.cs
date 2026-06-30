using System.ComponentModel;
using McpDbTools.Server.Configuration;
using ModelContextProtocol.Server;

namespace McpDbTools.Server.Tools;

/// <summary>
/// 只读工具：列出数据库项目与环境，按需加载避免环境多时返回数据量过大。
/// <para>行为矩阵（project/environment 均可选，空白字符串等同未传）：</para>
/// <para>- 不传 project：返回所有项目名索引（轻量，不含环境详情）。</para>
/// <para>- 传 project（存在）、不传 environment：返回该项目全部环境详情。</para>
/// <para>- 传 project（存在）+ environment（存在）：返回单环境详情。</para>
/// <para>- 传 project（存在）+ environment（不存在）：ENVIRONMENT_NOT_FOUND + 该项目全环境详情。</para>
/// <para>- 传 project（不存在）：PROJECT_NOT_FOUND + 所有项目名列表（availableProjects）。</para>
/// <para>判断顺序：先校验 project，project 不存在时无论 environment 传什么都直接返回 PROJECT_NOT_FOUND。</para>
/// </summary>
[McpServerToolType]
public sealed class DbListTool
{
    private readonly ConfigStore _configStore;

    public DbListTool(ConfigStore configStore)
    {
        _configStore = configStore;
    }

    /// <summary>
    /// 列出数据库项目与环境，按需加载避免返回过多数据。
    /// project 可选：不传则返回所有项目名索引(轻量，不含环境)；传项目名则返回该项目全部环境详情；
    /// 同时传 environment 则返回单环境详情。
    /// environment 可选：配合 project 使用，单独传(不传 project)无意义。
    /// 空白字符串等同未传。项目不存在时返回 PROJECT_NOT_FOUND 并附带 availableProjects(项目名数组)；
    /// 环境不存在时返回 ENVIRONMENT_NOT_FOUND 并附带该项目 environments(环境详情数组)。
    /// 返回 JSON 含 success 字段。
    /// </summary>
    [McpServerTool(Name = "db_list")]
    [Description("列出数据库项目与环境，按需加载避免返回过多数据。project 可选：不传则返回所有项目名索引(轻量，不含环境)；传项目名则返回该项目全部环境详情；同时传 environment 则返回单环境详情。environment 可选：配合 project 使用，单独传(不传 project)无意义。空白字符串等同未传。项目不存在时返回 PROJECT_NOT_FOUND 并附带 availableProjects(项目名数组)；环境不存在时返回 ENVIRONMENT_NOT_FOUND 并附带该项目 environments(环境详情数组)。返回 JSON 含 success 字段。")]
    public Task<string> ListProjects(
        string? project = null,
        string? environment = null,
        CancellationToken cancellationToken = default)
    {
        ResolvedConfig config = _configStore.GetResolved();

        // 空白字符串等同未传（与 db_query 环境解析逻辑一致）
        bool hasProject = !string.IsNullOrWhiteSpace(project);
        bool hasEnvironment = !string.IsNullOrWhiteSpace(environment);

        // 行为 1：不传 project → 项目索引（不带环境）
        if (!hasProject)
        {
            List<object> index = ProjectListBuilder.BuildProjectIndex(config);
            return Task.FromResult(ProjectListBuilder.SerializeSuccess(index));
        }

        // 判断顺序：先校验 project
        if (!config.Projects.TryGetValue(project!, out ResolvedProject? proj))
        {
            // 行为 5：project 不存在 → PROJECT_NOT_FOUND + 项目名列表（无论 environment 传什么）
            List<string> names = ProjectListBuilder.BuildProjectNameList(config);
            string available = string.Join(", ", names);
            string json = ProjectListBuilder.SerializeFail(
                "PROJECT_NOT_FOUND",
                $"项目不存在: {project}。可用项目: {available}",
                new { availableProjects = names });
            return Task.FromResult(json);
        }

        // project 存在：找到对应 KeyValuePair 供详情构造使用
        var projPair = new KeyValuePair<string, ResolvedProject>(project!, proj);

        // 行为 2/3：不传 environment → 该项目全环境详情
        if (!hasEnvironment)
        {
            object full = ProjectListBuilder.BuildProjectWithEnvironments(projPair);
            return Task.FromResult(ProjectListBuilder.SerializeSuccess(new List<object> { full }));
        }

        // 传了 environment：校验是否存在
        if (!proj.Environments.TryGetValue(environment!, out ResolvedDatabase? _))
        {
            // 行为 4：environment 不存在 → ENVIRONMENT_NOT_FOUND + 该项目全环境详情
            List<object> envDetails = ProjectListBuilder.BuildEnvironmentDetails(proj);
            string available = string.Join(", ", proj.Environments.Keys);
            string json = ProjectListBuilder.SerializeFail(
                "ENVIRONMENT_NOT_FOUND",
                $"环境不存在: {environment}。项目 {project} 可用环境: {available}",
                new { environments = envDetails });
            return Task.FromResult(json);
        }

        // 行为 3：project + environment 均存在 → 单环境详情
        object single = ProjectListBuilder.BuildSingleEnvironment(projPair, environment!);
        return Task.FromResult(ProjectListBuilder.SerializeSuccess(new List<object> { single }));
    }
}
