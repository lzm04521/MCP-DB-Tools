using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace McpDbTools.Server.Hosting;

/// <summary>
/// 应用重启：启动一个 detached 的 powershell 等"当前进程退出后"再拉起新实例，再优雅停止当前实例。
/// <para>
/// 不能直接 Process.Start(自身) 再退出——新实例会立即 bind 同一端口，而旧实例还占着，启动失败。
/// 用 powershell 脚本等待当前进程退出（端口释放）后再 Start-Process 新实例。
/// </para>
/// <para>
/// 端口变更场景：先 SavePortAsync 写 config.json → 调本方法 → 旧实例退 → 新实例读 config.json 新端口启动。
/// </para>
/// </summary>
internal static class RestartHelper
{
    internal static void RestartAndExit(IHostApplicationLifetime lifetime)
    {
        int pid = Environment.ProcessId;
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            // 无法定位自身 exe：仅停止当前实例，用户需手动重启（兜底）
            lifetime.StopApplication();
            return;
        }

        // powershell 单引号字符串转义：' → ''
        string exePs = exePath.Replace("'", "''");
        // 等当前进程退出（最多 30s）→ 等端口释放 600ms → 启动新实例（不带 --admin-port，读 config.json）
        string script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            $"if (Get-Process -Id {pid}) {{ Wait-Process -Id {pid} -Timeout 30 }}; " +
            "Start-Sleep -Milliseconds 600; " +
            $"Start-Process -FilePath '{exePs}'";

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -WindowStyle Hidden -Command \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            Process.Start(psi);
        }
        catch
        {
            // 启动重启脚本失败：仅停止当前实例，用户需手动重启（日志已记，兜底）
        }

        lifetime.StopApplication();
    }
}
