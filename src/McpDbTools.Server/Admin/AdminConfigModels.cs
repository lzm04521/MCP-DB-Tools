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

    [JsonPropertyName("projects")]
    public List<AdminProjectDto> Projects { get; init; } = new();

    [JsonPropertyName("audit")]
    public AuditConfig Audit { get; init; } = new();
}

public sealed class AdminConfigRequest
{
    [JsonPropertyName("defaultDisabledKeywords")]
    public List<string>? DefaultDisabledKeywords { get; init; }

    [JsonPropertyName("defaultDisabledKeywordsByType")]
    public Dictionary<string, List<string>>? DefaultDisabledKeywordsByType { get; init; }

    [JsonPropertyName("projects")]
    public List<AdminProjectDto> Projects { get; init; } = new();

    [JsonPropertyName("audit")]
    public AuditConfig Audit { get; init; } = new();
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
    public int CommandTimeout { get; init; } = 30;

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
