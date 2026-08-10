using System.Diagnostics;
using System.Runtime.Versioning;

namespace McpDbTools.Server.Admin;

/// <summary>
/// 注册 MCP 服务到 Claude Code CLI：调用 <c>claude mcp add/remove</c>。
/// <para>claude 在 Windows 通常是 claude.cmd（npm shim），需经 cmd /c 执行并捕获输出。</para>
/// <para>先 remove 再 add，保证幂等（重复注册/改端口后重注册都不会残留旧条目）。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClaudeMcpRegistrar
{
    private const string McpName = "db-tools";

    /// <summary>
    /// 注册到 Claude Code（指定作用域 local/user/project，默认 user）。
    /// </summary>
    public async Task<McpRegisterResult> RegisterAsync(int port, string scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            scope = "user";
        }

        string url = $"http://127.0.0.1:{port}/mcp";

        // remove（忽略错误：首次注册时条目不存在）
        await RunClaudeAsync($"mcp remove --scope {scope} {McpName}", cancellationToken);

        // add
        (int exit, string stdout, string stderr) = await RunClaudeAsync(
            $"mcp add --transport http --scope {scope} {McpName} {url}", cancellationToken);

        if (exit != 0)
        {
            return new McpRegisterResult
            {
                Success = false,
                Error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr,
                Url = url
            };
        }

        return new McpRegisterResult { Success = true, Url = url, Scope = scope };
    }

    /// <summary>执行 claude 命令（经 cmd /c 执行 .cmd shim），返回退出码与 stdout/stderr。</summary>
    private static async Task<(int Exit, string StdOut, string StdErr)> RunClaudeAsync(string claudeArgs, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c claude {claudeArgs}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, stdout.Trim(), stderr.Trim());
    }
}

/// <summary>Claude MCP 注册结果。</summary>
public sealed class McpRegisterResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Url { get; set; }
    public string? Scope { get; set; }
}
