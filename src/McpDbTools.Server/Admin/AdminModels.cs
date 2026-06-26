using System.Text.Json.Serialization;

namespace McpDbTools.Server.Admin;

/// <summary>测试连接请求：用入参的连接字符串即时测试，不落盘。</summary>
public sealed class TestConnectionRequest
{
    [JsonPropertyName("databaseType")]
    public string DatabaseType { get; init; } = "sqlserver";

    [JsonPropertyName("connectionString")]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>连接超时秒数，默认 5。非法值由后端归一化。</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>测试连接结果。</summary>
public sealed class TestConnectionResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>单个备份文件信息（列表项）。</summary>
public sealed class BackupItem
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>生成时间（UTC ISO 8601，取文件最后写入时间）。</summary>
    [JsonPropertyName("time")]
    public string Time { get; init; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }
}

/// <summary>备份列表响应。</summary>
public sealed class BackupListResponse
{
    [JsonPropertyName("items")]
    public List<BackupItem> Items { get; init; } = new();

    [JsonPropertyName("directory")]
    public string Directory { get; init; } = string.Empty;
}

/// <summary>恢复备份请求。</summary>
public sealed class RestoreBackupRequest
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

/// <summary>恢复备份结果。</summary>
public sealed class RestoreResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>恢复的备份文件名。</summary>
    [JsonPropertyName("restoredName")]
    public string? RestoredName { get; init; }

    /// <summary>恢复前自动产生的当前配置快照名（可用于撤销）。</summary>
    [JsonPropertyName("snapshotName")]
    public string? SnapshotName { get; init; }
}

/// <summary>删除备份结果。</summary>
public sealed class DeleteBackupResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
