using System.Security.AccessControl;
using System.Security.Principal;

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
/// 跨用户共享写权限由 <see cref="EnsureSharedWritable"/> 在首次创建目录时显式授予
/// BUILTIN\Users 修改权限保证（当前用户级托盘进程，非 LocalSystem 服务）。</item>
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
        EnsureSharedWritable(dir);
        return dir;
    }

    /// <summary>
    /// 解析数据目录并确保目录存在，允许调用方传入 config.json 路径作为最高优先级覆盖。
    /// </summary>
    public static string EnsureExists(string? configPathOverride)
    {
        string dir = ResolveCore(configPathOverride);
        Directory.CreateDirectory(dir);
        EnsureSharedWritable(dir);
        return dir;
    }

    /// <summary>
    /// 仅对位于 %ProgramData% 下的共享数据目录幂等授予 BUILTIN\Users 修改权限（可继承），
    /// 使同机任意本地用户都能读写 config.json / audit.db，实现真正的跨用户共享。
    /// <para>
    /// ProgramData 默认 ACL 只允许 Users 在目录内"新建"文件，不允许修改他人创建的文件——
    /// 不显式授权会导致第二个用户保存配置时 AccessDenied、写审计失败。
    /// </para>
    /// <para>
    /// 非 ProgramData 路径（覆盖路径、exe 目录 fallback）不改动 ACL，避免擅自放宽用户自有目录权限。
    /// 权限操作失败（非 owner 改不了他人目录的 DACL）静默降级，不阻塞启动；运行期保存配置遇到真正
    /// 写阻塞由 AdminConfigService 明确提示。
    /// </para>
    /// </summary>
    internal static void EnsureSharedWritable(string dir)
    {
        try
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(programData))
            {
                return;
            }

            // 严格限定为 ProgramData 子目录，避免 C:\ProgramDataFake 之类前缀误匹配
            if (!dir.StartsWith(programData, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (dir.Length > programData.Length
                && dir[programData.Length] != Path.DirectorySeparatorChar
                && dir[programData.Length] != Path.AltDirectorySeparatorChar)
            {
                return;
            }

            ApplyUsersModify(dir);
        }
        catch (Exception)
        {
            // 静默降级：非 owner 用户无法修改他人创建目录的 DACL。
            // 该用户要么已受益于他人补的继承 ACE（对所有人生效），要么本进程即 owner 可改——
            // 此处失败不阻塞启动。
        }
    }

    /// <summary>
    /// 幂等给目录补 BUILTIN\Users 修改权限（容器 + 文件继承）。
    /// 依赖 NTFS 继承传播自动覆盖已存在及新建的子项（config.json / audit.db / backups / logs），无需手动递归。
    /// 标记 internal 供单元测试直接验证（不判定路径，调用方自行保证目标目录合法）。
    /// </summary>
    internal static void ApplyUsersModify(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var info = new DirectoryInfo(dir);
        DirectorySecurity security = info.GetAccessControl();

        if (HasUsersAllowModify(security, usersSid))
        {
            return; // 已有可继承的 Users 修改权限，幂等跳过
        }

        security.AddAccessRule(new FileSystemAccessRule(
            usersSid,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>
    /// 检查目录 ACL 是否已含（显式或继承的）指定 SID 的 Allow Modify。
    /// </summary>
    private static bool HasUsersAllowModify(DirectorySecurity security, SecurityIdentifier sid)
    {
        AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules.OfType<FileSystemAccessRule>())
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && rule.IdentityReference is SecurityIdentifier ruleSid
                && ruleSid.Value == sid.Value
                && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify)
            {
                return true;
            }
        }
        return false;
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
        // 跨用户写权限由 EnsureSharedWritable 显式授予）
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData) && Directory.Exists(programData))
        {
            return Path.GetFullPath(Path.Combine(programData, DefaultDataFolderName));
        }

        // 优先级 4：exe 同目录（fallback，便携部署/异常环境）
        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}
