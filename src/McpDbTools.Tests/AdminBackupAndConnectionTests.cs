using System.Text.Json;
using McpDbTools.Server.Admin;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Tests;

/// <summary>
/// 备份管理 + 测试连接的后端测试（不连真实数据库：测试连接只覆盖参数校验路径，
/// 备份测试覆盖列表/恢复/删除/恢复前快照/路径穿越防护）。
/// </summary>
public class AdminBackupAndConnectionTests : IDisposable
{
    private readonly string _tempDir;

    public AdminBackupAndConnectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbbk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private (ConfigStore store, AdminConfigService service, string configPath) Create(string configJson)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, configJson);

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var service = new AdminConfigService(store, new DatabaseProviderFactory(), options);
        return (store, service, configPath);
    }

    private (ConfigStore store, AdminConfigService service, string configPath) CreateMissing()
    {
        string configPath = Path.Combine(_tempDir, "config.json");

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var service = new AdminConfigService(store, new DatabaseProviderFactory(), options);
        return (store, service, configPath);
    }

    // ============ 测试连接（参数校验，不连真实库） ============

    [Fact]
    public async Task TestConnection_EmptyConnectionString_ReturnsError()
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            TestConnectionResult result = await service.TestConnectionAsync(new TestConnectionRequest
            {
                DatabaseType = "sqlserver",
                ConnectionString = "   "
            }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("连接字符串", result.Error ?? string.Empty);
        }
    }

    [Fact]
    public async Task TestConnection_UnknownDatabaseType_ReturnsError()
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            TestConnectionResult result = await service.TestConnectionAsync(new TestConnectionRequest
            {
                DatabaseType = "postgres",
                ConnectionString = "Server=x;"
            }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("数据库类型", result.Error ?? string.Empty);
        }
    }

    // ============ 备份管理 ============

    [Fact]
    public async Task SaveConfig_GeneratesBackup_And_ListBackupsShowsIt()
    {
        var (store, service, _) = CreateMissing();

        using (store)
        {
            // 首次保存：config.json 不存在，会写一份占位备份 + 真实配置
            await service.SaveConfigAsync(new AdminConfigRequest
            {
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "erp",
                        DefaultEnvironment = "test",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new() { Name = "test", Type = "sqlserver", ConnectionString = "Server=.;", MaxRows = 100, CommandTimeout = 30 }
                        }
                    }
                }
            }, CancellationToken.None);

            BackupListResponse list = service.ListBackups();
            Assert.True(list.Items.Count >= 1);
            Assert.All(list.Items, item =>
            {
                Assert.StartsWith("config.", item.Name);
                Assert.EndsWith(".json", item.Name);
                Assert.True(item.SizeBytes >= 0);
            });
        }
    }

    [Fact]
    public async Task ListBackups_OrderedNewestFirst()
    {
        var (store, service, configPath) = CreateMissing();
        using (store)
        {
            // 连续保存多次，产生多份备份
            for (int i = 0; i < 3; i++)
            {
                await service.SaveConfigAsync(new AdminConfigRequest
                {
                    Projects = new List<AdminProjectDto>
                    {
                        new()
                        {
                            Name = "erp",
                            DefaultEnvironment = "test",
                            Environments = new List<AdminEnvironmentDto>
                            {
                                new() { Name = "test", Type = "sqlserver", ConnectionString = $"Server=.{i};", MaxRows = 100, CommandTimeout = 30 }
                            }
                        }
                    }
                }, CancellationToken.None);
                await Task.Delay(20); // 错开时间戳
            }

            BackupListResponse list = service.ListBackups();
            Assert.True(list.Items.Count >= 3);
            // 时间戳字典序倒序 = 最新在前
            for (int i = 1; i < list.Items.Count; i++)
            {
                Assert.True(string.Compare(list.Items[i - 1].Name, list.Items[i].Name, StringComparison.Ordinal) >= 0,
                    "备份应按时间倒序");
            }
        }
    }

    [Fact]
    public async Task RestoreBackup_OverwritesCurrent_AndCreatesSnapshot()
    {
        var (store, service, configPath) = CreateMissing();
        using (store)
        {
            // 保存 v1：connectionString = "Server=v1;"
            await service.SaveConfigAsync(new AdminConfigRequest
            {
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "erp",
                        DefaultEnvironment = "test",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new() { Name = "test", Type = "sqlserver", ConnectionString = "Server=v1;", MaxRows = 100, CommandTimeout = 30 }
                        }
                    }
                }
            }, CancellationToken.None);

            string firstBackup = service.ListBackups().Items[0].Name; // 包含 v1 之后的状态（首次保存时为占位空）

            // 保存 v2：connectionString = "Server=v2;"
            await service.SaveConfigAsync(new AdminConfigRequest
            {
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "erp",
                        DefaultEnvironment = "test",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new() { Name = "test", Type = "sqlserver", ConnectionString = "Server=v2;", MaxRows = 100, CommandTimeout = 30 }
                        }
                    }
                }
            }, CancellationToken.None);

            // 找到 v1 时期的备份（恢复后会还原那时的连接串）
            BackupListResponse list = service.ListBackups();
            string v2Backup = list.Items[0].Name; // 最新一份（保存 v2 前的快照，内容应是 v1）

            // 当前应是 v2
            string before = File.ReadAllText(configPath);
            Assert.Contains("Server=v2;", before);

            // 恢复到 v2Backup（恢复后当前配置应变成 v1）
            RestoreResult restoreResult = service.RestoreBackup(v2Backup);
            Assert.True(restoreResult.Success, restoreResult.Error ?? "恢复失败");
            Assert.NotNull(restoreResult.SnapshotName);

            // 恢复后当前 config.json 应是 v1
            string after = File.ReadAllText(configPath);
            Assert.Contains("Server=v1;", after);

            // 快照应出现在列表里（恢复前自动存的当前配置 = v2，可撤销）
            BackupListResponse afterList = service.ListBackups();
            Assert.Contains(afterList.Items, i => i.Name == restoreResult.SnapshotName);
        }
    }

    [Fact]
    public async Task DeleteBackup_RemovesFile()
    {
        var (store, service, _) = CreateMissing();
        using (store)
        {
            await service.SaveConfigAsync(new AdminConfigRequest
            {
                Projects = new List<AdminProjectDto>
                {
                    new()
                    {
                        Name = "erp",
                        DefaultEnvironment = "test",
                        Environments = new List<AdminEnvironmentDto>
                        {
                            new() { Name = "test", Type = "sqlserver", ConnectionString = "Server=.;", MaxRows = 100, CommandTimeout = 30 }
                        }
                    }
                }
            }, CancellationToken.None);

            string name = service.ListBackups().Items[0].Name;
            DeleteBackupResult result = service.DeleteBackup(name);
            Assert.True(result.Success);

            BackupListResponse after = service.ListBackups();
            Assert.DoesNotContain(after.Items, i => i.Name == name);
        }
    }

    [Fact]
    public void RestoreBackup_RejectsPathTraversal()
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            // 路径穿越 / 父目录引用 / 非法名都应被拒
            RestoreResult r1 = service.RestoreBackup("../../etc/passwd");
            Assert.False(r1.Success);

            RestoreResult r2 = service.RestoreBackup("config.evil.json");
            Assert.False(r2.Success); // 文件不存在

            DeleteBackupResult r3 = service.DeleteBackup("../../../config.json");
            Assert.False(r3.Success);
        }
    }

    [Fact]
    public void GetBackupPath_ReturnsNull_ForUnsafeName()
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            Assert.Null(service.GetBackupPath("normal.json")); // 不以 config. 开头
            Assert.Null(service.GetBackupPath("config.x.txt")); // 不以 .json 结尾
            Assert.Null(service.GetBackupPath("config.x.json")); // 不存在
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理 */ }
    }
}
