using System.Text.Json.Serialization;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Admin;

public sealed class AdminConfigResponse
{
    [JsonPropertyName("configPath")]
    public string ConfigPath { get; init; } = string.Empty;

    [JsonPropertyName("defaultDisabledKeywords")]
    public List<string> DefaultDisabledKeywords { get; init; } = new();

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

    [JsonPropertyName("projects")]
    public List<AdminProjectDto> Projects { get; init; } = new();
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
