using System.Text.Json.Serialization;

namespace McpDbTools.Server.Configuration;

/// <summary>
/// 运维清理设置（对应 config.json 中 maintenance 节点）。
/// <para>控制审计日志与备份文件的自动清理。节点缺省（null）时按默认值处理：全部关闭。</para>
/// <para>此节点独立于 databases，由 Admin UI「全局设置」页维护；
/// 保存 projects/keywords 时后端会原样透传，不会被覆盖丢失。</para>
/// </summary>
public sealed class MaintenanceConfig
{
    /// <summary>保留天数的内置默认值，作为天数缺失/非法时的单一真源。</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>是否启用审计日志自动清理。默认关闭。</summary>
    [JsonPropertyName("auditLogAutoCleanup")]
    public bool AuditLogAutoCleanup { get; init; }

    /// <summary>审计日志保留天数。启用自动清理时删除该天数之前的记录。默认 30。</summary>
    [JsonPropertyName("auditLogRetentionDays")]
    public int AuditLogRetentionDays { get; init; } = DefaultRetentionDays;

    /// <summary>是否启用备份文件自动清理。默认关闭。</summary>
    [JsonPropertyName("backupAutoCleanup")]
    public bool BackupAutoCleanup { get; init; }

    /// <summary>备份文件保留天数。启用自动清理时删除该天数之前的备份。默认 30。</summary>
    [JsonPropertyName("backupRetentionDays")]
    public int BackupRetentionDays { get; init; } = DefaultRetentionDays;

    /// <summary>是否记录查询结果到 audit_log_result 子表。默认关闭。开启后请关注 audit.db 体积。</summary>
    [JsonPropertyName("auditRecordResults")]
    public bool AuditRecordResults { get; init; }

    /// <summary>返回内置默认配置（全部关闭、保留 30 天），供 maintenance 节点缺失时回退。</summary>
    public static MaintenanceConfig Default => new();
}
