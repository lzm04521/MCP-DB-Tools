using System.Text.Json.Serialization;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Admin;

public sealed class AdminConfigResponse
{
    [JsonPropertyName("configPath")]
    public string ConfigPath { get; init; } = string.Empty;

    [JsonPropertyName("defaultDisabledKeywords")]
    public List<string> DefaultDisabledKeywords { get; init; } = new();

    /// <summary>第一层写池：AllowWrite=true 环境的全局阻止关键字。空时回退 BuiltInWrite。</summary>
    [JsonPropertyName("defaultWriteDisabledKeywords")]
    public List<string> DefaultWriteDisabledKeywords { get; init; } = new();

    /// <summary>内置只读池关键字（只读环境默认阻止集合），前端只读展示。单一真源 = 后端 DefaultDisabledKeywords.BuiltInReadOnly。</summary>
    [JsonPropertyName("builtInReadOnlyKeywords")]
    public List<string> BuiltInReadOnlyKeywords { get; init; } = new();

    /// <summary>内置写池关键字（写环境默认阻止集合），前端只读展示。单一真源 = 后端 DefaultDisabledKeywords.BuiltInWrite。</summary>
    [JsonPropertyName("builtInWriteKeywords")]
    public List<string> BuiltInWriteKeywords { get; init; } = new();

    /// <summary>按数据库类型追加的内置阻止关键字。key 为 DatabaseType 枚举的 lowerInvariant 字符串。</summary>
    [JsonPropertyName("builtInDisabledKeywordsByType")]
    public Dictionary<string, List<string>> BuiltInDisabledKeywordsByType { get; init; } = new();

    [JsonPropertyName("defaultDisabledKeywordsByType")]
    public Dictionary<string, List<string>> DefaultDisabledKeywordsByType { get; init; } = new();

    /// <summary>每环境最大并发查询数的全局默认值。0/未配置表示用内置默认 10。</summary>
    [JsonPropertyName("defaultMaxConcurrency")]
    public int DefaultMaxConcurrency { get; init; }

    /// <summary>超载排队最长等待秒数的全局默认值。0/未配置表示用内置默认 5。</summary>
    [JsonPropertyName("defaultMaxConcurrencyWaitSeconds")]
    public int DefaultMaxConcurrencyWaitSeconds { get; init; }

    /// <summary>连接池上限的全局默认值。0/未配置表示用内置默认 100。</summary>
    [JsonPropertyName("defaultMaxPoolSize")]
    public int DefaultMaxPoolSize { get; init; }

    /// <summary>建立连接超时秒数的全局默认值。0/未配置表示用内置默认 60。</summary>
    [JsonPropertyName("defaultConnectTimeoutSeconds")]
    public int DefaultConnectTimeoutSeconds { get; init; }

    [JsonPropertyName("projects")]
    public List<AdminProjectDto> Projects { get; init; } = new();
}

public sealed class AdminConfigRequest
{
    [JsonPropertyName("defaultDisabledKeywords")]
    public List<string>? DefaultDisabledKeywords { get; init; }

    /// <summary>写池全局阻止关键字。null 表示保持当前配置，空列表表示用内置默认 BuiltInWrite。</summary>
    [JsonPropertyName("defaultWriteDisabledKeywords")]
    public List<string>? DefaultWriteDisabledKeywords { get; init; }

    [JsonPropertyName("defaultDisabledKeywordsByType")]
    public Dictionary<string, List<string>>? DefaultDisabledKeywordsByType { get; init; }

    /// <summary>每环境最大并发查询数的全局默认值。null/0/非法表示用内置默认。</summary>
    [JsonPropertyName("defaultMaxConcurrency")]
    public int? DefaultMaxConcurrency { get; init; }

    [JsonPropertyName("defaultMaxConcurrencyWaitSeconds")]
    public int? DefaultMaxConcurrencyWaitSeconds { get; init; }

    [JsonPropertyName("defaultMaxPoolSize")]
    public int? DefaultMaxPoolSize { get; init; }

    [JsonPropertyName("defaultConnectTimeoutSeconds")]
    public int? DefaultConnectTimeoutSeconds { get; init; }

    /// <summary>
    /// 项目列表。缺失/显式 null 视为非法请求（拒绝保存，防止误清空配置）；
    /// 空列表 = 用户主动删除全部项目，合法。
    /// </summary>
    [JsonPropertyName("projects")]
    public List<AdminProjectDto>? Projects { get; init; }
}

public sealed class AdminProjectDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("originalName")]
    public string? OriginalName { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("defaultEnvironment")]
    public string? DefaultEnvironment { get; init; }

    [JsonPropertyName("environments")]
    public List<AdminEnvironmentDto> Environments { get; init; } = new();
}

public sealed class AdminEnvironmentDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("originalName")]
    public string? OriginalName { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("isProduction")]
    public bool IsProduction { get; init; }

    /// <summary>是否允许写操作（DML/DDL）。与 IsProduction 互斥；生产环境不能开启。</summary>
    [JsonPropertyName("allowWrite")]
    public bool AllowWrite { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "sqlserver";

    [JsonPropertyName("connectionString")]
    public string? ConnectionString { get; init; }

    [JsonPropertyName("connectionStringMasked")]
    public string ConnectionStringMasked { get; init; } = string.Empty;

    [JsonPropertyName("maxRows")]
    public int MaxRows { get; init; } = 1000;

    [JsonPropertyName("commandTimeout")]
    public int CommandTimeout { get; init; } = 600;

    /// <summary>连接池上限。0 表示未配置，回退全局默认。</summary>
    [JsonPropertyName("maxPoolSize")]
    public int MaxPoolSize { get; init; }

    /// <summary>建连超时秒数。0 表示未配置，回退全局默认。</summary>
    [JsonPropertyName("connectTimeoutSeconds")]
    public int ConnectTimeoutSeconds { get; init; }

    /// <summary>该环境最大并发查询数。0 表示未配置，回退全局默认。</summary>
    [JsonPropertyName("maxConcurrency")]
    public int MaxConcurrency { get; init; }

    [JsonPropertyName("disabledKeywords")]
    public List<string> DisabledKeywords { get; init; } = new();
}

public sealed class AdminSaveResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("errors")]
    public List<string> Errors { get; init; } = new();

    [JsonPropertyName("backupName")]
    public string? BackupName { get; init; }

    [JsonPropertyName("config")]
    public AdminConfigResponse? Config { get; init; }
}

/// <summary>导入请求：json 为导入文件的原始 JSON 文本（宽容解析）。</summary>
public sealed class ImportRequest
{
    [JsonPropertyName("json")]
    public string Json { get; init; } = string.Empty;
}

/// <summary>导入合并计划：将发生的变更分类。环境项格式 "projectKey/envKey"。</summary>
public sealed class ImportPlan
{
    [JsonPropertyName("addedProjects")]
    public List<string> AddedProjects { get; init; } = new();

    [JsonPropertyName("updatedProjects")]
    public List<string> UpdatedProjects { get; init; } = new();

    [JsonPropertyName("addedEnvironments")]
    public List<string> AddedEnvironments { get; init; } = new();

    [JsonPropertyName("updatedEnvironments")]
    public List<string> UpdatedEnvironments { get; init; } = new();
}

/// <summary>导入预览响应：dry-run 结果，不论是否有 errors 都返回（前端展示后由用户决定）。</summary>
public sealed class ImportPreviewResponse
{
    [JsonPropertyName("plan")]
    public ImportPlan Plan { get; init; } = new();

    [JsonPropertyName("errors")]
    public List<string> Errors { get; init; } = new();

    /// <summary>导入文件解析出的项目总数（不论是否校验通过）。</summary>
    [JsonPropertyName("parsedProjectCount")]
    public int ParsedProjectCount { get; init; }
}

/// <summary>导入应用结果。success=true 时 backupName 为本次自动产生的备份名。</summary>
public sealed class ImportApplyResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("backupName")]
    public string? BackupName { get; init; }

    [JsonPropertyName("plan")]
    public ImportPlan Plan { get; init; } = new();

    [JsonPropertyName("errors")]
    public List<string> Errors { get; init; } = new();
}
