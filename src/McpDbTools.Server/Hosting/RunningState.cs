namespace McpDbTools.Server.Hosting;

/// <summary>
/// 运行时状态：保存启动时确定、运行中不可变的值（如监听端口），供 Admin API 端点返回。
/// 与 config.json 的持久化配置区分：这里存的是"本次启动实际生效"的值。
/// </summary>
internal sealed class RunningState
{
    /// <summary>本次启动实际监听的端口（启动时由 ParsePort 确定，重启前不变）。</summary>
    internal int Port { get; set; }
}
