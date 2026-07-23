using System.Reflection;

namespace McpDbTools.Server;

/// <summary>
/// 当前应用版本，从 AssemblyInformationalVersion 读取（build 时由 git tag 注入）。
/// 读取失败回落基线 0.5.1。
/// 剥去 .NET SDK 自动追加的 +commit-sha 后缀，UI 只显干净 tag 版本。
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Normalize(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? "0.5.1";

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        int plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }
}

