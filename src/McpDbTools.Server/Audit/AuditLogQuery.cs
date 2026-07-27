namespace McpDbTools.Server.Audit;

/// <summary>
/// 审计日志查询条件（供 Admin 查看页使用）。所有字段均可选，为 null/空表示不限定。
/// <para>时间字段使用 UTC ISO 8601 字符串，按字典序比较即可正确排序。</para>
/// </summary>
public sealed record AuditLogQuery
{
    /// <summary>项目名筛选（精确匹配，大小写不敏感）。</summary>
    public string? Project { get; init; }

    /// <summary>环境名筛选（精确匹配，大小写不敏感）。</summary>
    public string? Environment { get; init; }

    /// <summary>数据库类型筛选（sqlserver/mysql/oracle）。</summary>
    public string? DatabaseType { get; init; }

    /// <summary>执行结果筛选：true 仅成功，false 仅失败，null 全部。</summary>
    public bool? Success { get; init; }

    /// <summary>起始时间（UTC ISO 8601，含），筛选 time &gt;=。</summary>
    public string? FromTime { get; init; }

    /// <summary>结束时间（UTC ISO 8601，含），筛选 time &lt;=。</summary>
    public string? ToTime { get; init; }

    /// <summary>SQL 关键词模糊匹配（子串，大小写不敏感）。</summary>
    public string? SqlContains { get; init; }

    /// <summary>页码，从 1 开始。</summary>
    public int Page { get; init; } = 1;

    /// <summary>每页条数。</summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>审计日志查询结果。</summary>
public sealed record AuditLogPage
{
    /// <summary>当前页记录（按时间倒序）。</summary>
    public required IReadOnlyList<AuditEntry> Items { get; init; }

    /// <summary>符合条件的总记录数。</summary>
    public long Total { get; init; }

    /// <summary>当前页码。</summary>
    public int Page { get; init; }

    /// <summary>每页条数。</summary>
    public int PageSize { get; init; }

    /// <summary>审计计数器对账载荷（计数器总数 / 当日计数器 / 今日落盘 COUNT）。</summary>
    public required AuditCounters Counters { get; init; }
}

/// <summary>审计计数器对账载荷。前端三个数字并排显示，不等则高亮（差值即丢失日志）。</summary>
public sealed record AuditCounters
{
    /// <summary>计数器总数（应写累计，清理后按删除条数增量扣减）。</summary>
    public long TotalCounter { get; init; }

    /// <summary>当日计数器（本地时区日，按日历史行）。</summary>
    public long TodayCounter { get; init; }

    /// <summary>今日落盘数（audit_log 中今日 time 的 COUNT(*)，本地时区日界）。</summary>
    public long TodayPersisted { get; init; }

    /// <summary>本地今日 yyyy-MM-dd，便于 UI 显示。</summary>
    public string TodayDateKey { get; init; } = "";
}
