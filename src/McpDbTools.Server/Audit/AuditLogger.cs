using System.Globalization;
using System.Text.Json;
using McpDbTools.Server.Configuration;
using Microsoft.Extensions.Logging;

namespace McpDbTools.Server.Audit;

/// <summary>单条审计记录（对应审计日志 JSONL 中的一行）。</summary>
public sealed record AuditEntry
{
    /// <summary>UTC ISO 8601 时间戳。</summary>
    public string Time { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string DatabaseType { get; init; } = string.Empty;
    public string Sql { get; init; } = string.Empty;
    public int RowCount { get; init; }
    public long ElapsedMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 审计日志器：JSONL 格式写入，支持开关、按大小轮转、按天数过期清理。
/// <para>配置实时取自 <see cref="ConfigStore"/>，热重载后立即生效。</para>
/// </summary>
public sealed class AuditLogger
{
    private readonly ConfigStore _configStore;
    private readonly ILogger<AuditLogger> _logger;
    private readonly object _fileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public AuditLogger(ConfigStore configStore, ILogger<AuditLogger> logger)
    {
        _configStore = configStore;
        _logger = logger;
    }

    /// <summary>记录一条查询审计。开关关闭时直接返回。</summary>
    public void Log(AuditEntry entry)
    {
        ResolvedConfig config = _configStore.GetResolved();
        if (!config.Audit.Enabled)
        {
            return;
        }

        try
        {
            string line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            WriteWithRotation(config.Audit, line);
        }
        catch (Exception ex)
        {
            // 审计写入失败不应影响主流程，但必须上报
            _logger.LogError(ex, "审计日志写入失败");
        }
    }

    /// <summary>写入一行，按文件大小轮转，并顺带清理过期文件。</summary>
    private void WriteWithRotation(AuditConfig audit, string line)
    {
        string fullPath = Path.GetFullPath(audit.LogPath);
        string dir = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(dir);

        lock (_fileLock)
        {
            long maxBytes = Math.Max(1, audit.MaxFileSizeMB) * 1024L * 1024L;

            // 轮转：当前文件超阈值则重命名为 .1（覆盖旧 .1）
            if (File.Exists(fullPath) && new FileInfo(fullPath).Length >= maxBytes)
            {
                string rotated = fullPath + ".1";
                if (File.Exists(rotated))
                {
                    File.Delete(rotated);
                }
                File.Move(fullPath, rotated);
                CleanupExpired(dir, audit);
            }

            File.AppendAllText(fullPath, line);
        }
    }

    /// <summary>删除超过保留天数的审计文件（含轮转文件）。</summary>
    private void CleanupExpired(string dir, AuditConfig audit)
    {
        try
        {
            int keep = audit.MaxRetentionDays;
            DateTime cutoff = DateTime.UtcNow.AddDays(-keep);
            string baseName = Path.GetFileName(audit.LogPath);

            foreach (string file in Directory.EnumerateFiles(dir, baseName + "*"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "审计日志过期清理失败");
        }
    }

    /// <summary>构造 UTC ISO 8601 时间戳（避免在热路径反复构造格式化字符串）。</summary>
    public static string NowUtcIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
