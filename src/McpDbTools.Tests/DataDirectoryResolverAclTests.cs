using System.Security.AccessControl;
using System.Security.Principal;
using McpDbTools.Server.Configuration;

namespace McpDbTools.Tests;

// 验证 %ProgramData% 共享目录的 ACL 补授权：确保同机任意本地用户可读写 config.json / audit.db。
// 依赖真实文件系统（ACL 无法可靠 mock）；测试以当前用户身份创建临时目录，自身即 owner 可改 DACL。
public class DataDirectoryResolverAclTests : IDisposable
{
    private readonly string _tempRoot;

    public DataDirectoryResolverAclTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mcpdbacl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void ApplyUsersModify_AddsInheritableUsersModifyAce()
    {
        string dir = Path.Combine(_tempRoot, "add");
        Directory.CreateDirectory(dir);

        DataDirectoryResolver.ApplyUsersModify(dir);

        Assert.True(HasUsersModify(dir));
    }

    [Fact]
    public void ApplyUsersModify_Idempotent_DoesNotDuplicate()
    {
        string dir = Path.Combine(_tempRoot, "idem");
        Directory.CreateDirectory(dir);

        DataDirectoryResolver.ApplyUsersModify(dir);
        DataDirectoryResolver.ApplyUsersModify(dir); // 再调一次不抛

        Assert.Equal(1, CountUsersModifyAces(dir));
    }

    [Fact]
    public void EnsureSharedWritable_NonProgramDataPath_IsNoOp()
    {
        // 临时目录不在 %ProgramData% 下，EnsureSharedWritable 应跳过、不改 ACL
        string dir = Path.Combine(_tempRoot, "noop");
        Directory.CreateDirectory(dir);
        int before = CountAllAces(dir);

        DataDirectoryResolver.EnsureSharedWritable(dir);

        Assert.Equal(before, CountAllAces(dir));
    }

    [Fact]
    public void ApplyUsersModify_PropagatesToExistingChildFile()
    {
        // 已存在的子文件应通过 NTFS 继承传播获得 Users:Modify（无需手动递归）
        string dir = Path.Combine(_tempRoot, "withfile");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "config.json");
        File.WriteAllText(file, "{}");

        DataDirectoryResolver.ApplyUsersModify(dir);

        Assert.True(HasUsersModify(file));
    }

    private static bool HasUsersModify(string path)
        => GetRules(path).Any(IsUsersAllowModify);

    private static int CountUsersModifyAces(string dir)
        => GetRules(dir).Count(IsUsersAllowModify);

    private static int CountAllAces(string dir)
        => GetRules(dir).Count;

    private static bool IsUsersAllowModify(FileSystemAccessRule rule)
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        return rule.AccessControlType == AccessControlType.Allow
            && rule.IdentityReference is SecurityIdentifier sid
            && sid.Value == users.Value
            && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify;
    }

    private static List<FileSystemAccessRule> GetRules(string path)
    {
        AuthorizationRuleCollection collection = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            : new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        return collection.OfType<FileSystemAccessRule>().ToList();
    }
}
