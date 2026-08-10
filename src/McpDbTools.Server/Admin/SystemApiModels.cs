namespace McpDbTools.Server.Admin;

/// <summary>系统设置页 API 请求模型。</summary>

public sealed class PortRequest
{
    /// <summary>要写入 config.json 的端口（1-65535）。</summary>
    public int Port { get; set; }
}

public sealed class AutostartRequest
{
    /// <summary>true=注册 HKCU Run 自启；false=移除。</summary>
    public bool Enabled { get; set; }
}

public sealed class McpRegisterRequest
{
    /// <summary>Claude MCP 作用域：local/user/project，留空默认 user。</summary>
    public string? Scope { get; set; }
}
