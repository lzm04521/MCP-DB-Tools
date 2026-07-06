using System.Text.Json.Serialization;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Server.Admin;

/// <summary>
/// 全局设置页读取响应。maintenance 节点缺失时由后端填充内置默认（全部关闭、保留 30 天）。
/// 字段恒非空，前端无需处理 null。
/// </summary>
public sealed class MaintenanceSettingsResponse
{
    [JsonPropertyName("auditLogAutoCleanup")]
    public bool AuditLogAutoCleanup { get; init; }

    [JsonPropertyName("auditLogRetentionDays")]
    public int AuditLogRetentionDays { get; init; } = MaintenanceConfig.DefaultRetentionDays;

    [JsonPropertyName("backupAutoCleanup")]
    public bool BackupAutoCleanup { get; init; }

    [JsonPropertyName("backupRetentionDays")]
    public int BackupRetentionDays { get; init; } = MaintenanceConfig.DefaultRetentionDays;

    /// <summary>是否记录查询结果到审计子表。默认关闭。</summary>
    [JsonPropertyName("auditRecordResults")]
    public bool AuditRecordResults { get; init; }
}

/// <summary>全局设置页保存请求。天数校验：开关开启时必须 &gt; 0。</summary>
public sealed class MaintenanceSettingsRequest
{
    [JsonPropertyName("auditLogAutoCleanup")]
    public bool AuditLogAutoCleanup { get; init; }

    [JsonPropertyName("auditLogRetentionDays")]
    public int AuditLogRetentionDays { get; init; } = MaintenanceConfig.DefaultRetentionDays;

    [JsonPropertyName("backupAutoCleanup")]
    public bool BackupAutoCleanup { get; init; }

    [JsonPropertyName("backupRetentionDays")]
    public int BackupRetentionDays { get; init; } = MaintenanceConfig.DefaultRetentionDays;

    /// <summary>是否记录查询结果到审计子表。默认关闭。</summary>
    [JsonPropertyName("auditRecordResults")]
    public bool AuditRecordResults { get; init; }
}

/// <summary>手动清理审计日志结果（复用现有 /admin/api/audit-logs/cleanup 契约）。</summary>
public sealed class AuditCleanupResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("deleted")]
    public int Deleted { get; init; }

    [JsonPropertyName("days")]
    public int Days { get; init; }
}
