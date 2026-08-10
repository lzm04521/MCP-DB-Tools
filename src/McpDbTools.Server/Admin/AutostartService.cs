using System.Runtime.Versioning;
using Microsoft.Win32;

namespace McpDbTools.Server.Admin;

/// <summary>
/// 登录自启动管理：读写注册表 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>。
/// <para>当前用户级，无需管理员权限，Admin UI 系统设置页直接控制。</para>
/// <para>注册值为当前 exe 路径（不带 <c>--admin-port</c>，让程序读 config.json 的 port）。</para>
/// <para>Windows 登录时由系统拉起，WinExi 形态无控制台窗口。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "McpDbTools";

    private readonly string _exePath;

    public AutostartService()
    {
        // Environment.ProcessPath：当前 exe 完整路径（.NET 6+）；兜底用命令行首参
        _exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
    }

    /// <summary>是否已注册自启动（HKCU Run 下存在 McpDbTools 项）。</summary>
    public bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }

    /// <summary>启用：写入 exe 路径到 HKCU Run。已存在则覆盖。</summary>
    public void Enable()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, $"\"{_exePath}\"");
    }

    /// <summary>禁用：删除 HKCU Run 下的 McpDbTools 项。不存在则无操作。</summary>
    public void Disable()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
