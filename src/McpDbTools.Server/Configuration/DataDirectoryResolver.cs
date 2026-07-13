namespace McpDbTools.Server.Configuration;

/// <summary>
/// 用户数据目录的集中解析器。
/// <para>
/// 统一定位 config.json / audit.db / backups 的存放位置，避免各处路径推导不一致。
/// </para>
/// <para>
/// 解析优先级（从高到低）：
/// <list type="number">
/// <item><paramref name="configPathOverride"/>：调用方传入的 config.json 完整路径，取其所在目录。
/// 用于尊重 DI 容器中 ConfigStoreOptions.ConfigPath 的设置（测试场景、或显式覆盖）。</item>
/// <item>环境变量 <c>ConfigStore__ConfigPath</c>：高级用户逃生通道，取其所在目录。</item>
/// <item><c>%ProgramData%\McpDbTools</c>：默认，Windows 跨用户共享数据目录。
/// LocalSystem 服务（默认服务账户）与当前用户进程（MCP/Claude）都能读写同一份数据。</item>
/// <item><see cref="AppContext.BaseDirectory"/>：exe 同目录，兼容便携部署与异常环境。</item>
/// </list>
/// </para>
/// <para>
/// 程序自身（不依赖外部配置）保证无论以何种账户、何种方式启动都能定位到一致的目录。
/// </para>
/// </summary>
public static class DataDirectoryResolver
{
    /// <summary>数据目录下的默认子目录名（相对 ProgramData）。</summary>
    public const string DefaultDataFolderName = "McpDbTools";

    /// <summary>环境变量名：完整覆盖 config.json 路径（.NET 配置层级分隔符 __）。</summary>
    public const string ConfigPathEnvironmentVariable = "ConfigStore__ConfigPath";

    /// <summary>
    /// 解析数据目录绝对路径（不创建目录）。
    /// </summary>
    public static string Resolve()
    {
        return ResolveCore(null);
    }

    /// <summary>
    /// 解析数据目录绝对路径，允许调用方传入 config.json 路径作为最高优先级覆盖。
    /// </summary>
    /// <param name="configPathOverride">config.json 完整路径。null 或空时忽略；非空时取其所在目录。</param>
    public static string Resolve(string? configPathOverride)
    {
        return ResolveCore(configPathOverride);
    }

    /// <summary>
    /// 解析数据目录并确保目录存在。
    /// </summary>
    public static string EnsureExists()
    {
        string dir = ResolveCore(null);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 解析数据目录并确保目录存在，允许调用方传入 config.json 路径作为最高优先级覆盖。
    /// </summary>
    public static string EnsureExists(string? configPathOverride)
    {
        string dir = ResolveCore(configPathOverride);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ResolveCore(string? configPathOverride)
    {
        // 优先级 1：调用方显式传入的 config.json 路径（DI 中 ConfigStoreOptions.ConfigPath）
        if (!string.IsNullOrWhiteSpace(configPathOverride))
        {
            string overrideFull = Path.GetFullPath(configPathOverride);
            string? dir = Path.GetDirectoryName(overrideFull);
            if (!string.IsNullOrEmpty(dir))
            {
                return dir;
            }
        }

        // 优先级 2：环境变量 ConfigStore__ConfigPath
        string? envConfigPath = Environment.GetEnvironmentVariable(ConfigPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envConfigPath))
        {
            string envConfigFull = Path.GetFullPath(envConfigPath);
            string? dir = Path.GetDirectoryName(envConfigFull);
            if (!string.IsNullOrEmpty(dir))
            {
                return dir;
            }
        }

        // 优先级 3：%ProgramData%\McpDbTools（Windows 跨用户共享数据目录，
        // LocalSystem 服务与当前用户进程都能读写同一份数据）
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData) && Directory.Exists(programData))
        {
            return Path.GetFullPath(Path.Combine(programData, DefaultDataFolderName));
        }

        // 优先级 4：exe 同目录（fallback，便携部署/异常环境）
        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}
