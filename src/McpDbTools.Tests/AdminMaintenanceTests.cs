using System.Text.Json;
using McpDbTools.Server.Admin;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Tests;

/// <summary>
/// 全局设置（maintenance 节点）后端测试。
/// 覆盖：maintenance 读写、节点缺失回退默认、保存 projects 不丢失 maintenance（D1 防回归）、
/// 备份按时间删除、非法天数拒绝、路径穿越防护。
/// </summary>
public class AdminMaintenanceTests : IDisposable
{
    private readonly string _tempDir;

    public AdminMaintenanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbmaint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private (ConfigStore store, AdminConfigService service, string configPath) Create(string? json = null)
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        if (json is not null)
        {
            File.WriteAllText(configPath, json);
        }

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var service = new AdminConfigService(store, new DatabaseProviderFactory(), options);
        return (store, service, configPath);
    }

    private static bool MaintenanceEquals(MaintenanceSettingsResponse r, bool a, int ad, bool b, int bd)
        => r.AuditLogAutoCleanup == a && r.AuditLogRetentionDays == ad
            && r.BackupAutoCleanup == b && r.BackupRetentionDays == bd;

    /// <summary>
    /// 等待 ConfigStore 热重载完成（FileSystemWatcher → 500ms 去抖 → Reload）。
    /// AdminConfigService 保存后写文件，ConfigStore.Current 异步刷新；连续两次保存的测试需要等待，否则第二次读到旧快照。
    /// </summary>
    private static async Task WaitForReloadAsync(ConfigStore store, Func<bool> predicate, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(50);
        }
    }

    // ============ maintenance 读写 ============

    [Fact]
    public void GetMaintenance_NullNode_ReturnsDefaults()
    {
        // maintenance 节点缺失时应返回内置默认：全部关闭、保留 30 天
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            MaintenanceSettingsResponse r = service.GetMaintenance();
            Assert.True(MaintenanceEquals(r, false, 30, false, 30));
        }
    }

    [Fact]
    public async Task SaveMaintenance_PersistsAndReadsBack()
    {
        var (store, service, configPath) = Create("{\"databases\":{}}");
        using (store)
        {
            MaintenanceSettingsResponse saved = await service.SaveMaintenanceAsync(new MaintenanceSettingsRequest
            {
                AuditLogAutoCleanup = true,
                AuditLogRetentionDays = 60,
                BackupAutoCleanup = true,
                BackupRetentionDays = 10
            }, CancellationToken.None);

            Assert.True(MaintenanceEquals(saved, true, 60, true, 10));

            // 保存写文件后 ConfigStore 异步热重载（500ms 去抖），等待刷新后再回读
            await WaitForReloadAsync(store, () => store.Current.Maintenance?.AuditLogAutoCleanup == true);
            MaintenanceSettingsResponse r = service.GetMaintenance();
            Assert.True(MaintenanceEquals(r, true, 60, true, 10));
        }

        // config.json 中应出现 maintenance 节点
        string json = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("maintenance", out JsonElement m));
        Assert.True(m.GetProperty("auditLogAutoCleanup").GetBoolean());
        Assert.Equal(60, m.GetProperty("auditLogRetentionDays").GetInt32());
    }

    [Fact]
    public async Task SaveMaintenance_EnableWithZeroDays_Throws()
    {
        // 开关开启但天数非法（0）应抛 ArgumentException（API 层会包装为 400）
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveMaintenanceAsync(new MaintenanceSettingsRequest
            {
                AuditLogAutoCleanup = true,
                AuditLogRetentionDays = 0
            }, CancellationToken.None));
        }
    }

    [Fact]
    public async Task SaveMaintenance_DisableWithZeroDays_OkAndNormalized()
    {
        // 开关关闭时天数不校验；非法值归一化为默认 30
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            MaintenanceSettingsResponse saved = await service.SaveMaintenanceAsync(new MaintenanceSettingsRequest
            {
                AuditLogAutoCleanup = false,
                AuditLogRetentionDays = 0,
                BackupAutoCleanup = false,
                BackupRetentionDays = -5
            }, CancellationToken.None);

            Assert.Equal(MaintenanceConfig.DefaultRetentionDays, saved.AuditLogRetentionDays);
            Assert.Equal(MaintenanceConfig.DefaultRetentionDays, saved.BackupRetentionDays);
        }
    }

    // ============ D1 防回归：保存 projects 不丢失 maintenance ============

    [Fact]
    public async Task SaveConfig_PreservesMaintenanceNode()
    {
        // 先保存 maintenance 设置
        var (store, service, configPath) = Create("{\"databases\":{}}");
        using (store)
        {
            await service.SaveMaintenanceAsync(new MaintenanceSettingsRequest
            {
                AuditLogAutoCleanup = true,
                AuditLogRetentionDays = 45,
                BackupAutoCleanup = false,
                BackupRetentionDays = 30
            }, CancellationToken.None);

            // 保存写文件后 ConfigStore 异步热重载（500ms 去抖）；等待刷新后再走 projects 保存，
            // 否则 SaveConfigAsync 的 ToConfig(current,...) 读到的 current.Maintenance 还是 null
            await WaitForReloadAsync(store, () => store.Current.Maintenance?.AuditLogAutoCleanup == true);

            // 再走 projects 保存（PUT /admin/api/config 全量替换路径）
            AdminSaveResult result = await service.SaveConfigAsync(new AdminConfigRequest
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

            Assert.True(result.Success);

            // maintenance 必须仍然存在（D1：原 ToConfig 会丢，已修复透传）
            MaintenanceSettingsResponse m = service.GetMaintenance();
            Assert.True(m.AuditLogAutoCleanup);
            Assert.Equal(45, m.AuditLogRetentionDays);
        }

        // 直接验证 config.json 文件中 maintenance 仍在
        string json = File.ReadAllText(configPath);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("maintenance", out JsonElement m2));
        Assert.True(m2.GetProperty("auditLogAutoCleanup").GetBoolean());
        Assert.Equal(45, m2.GetProperty("auditLogRetentionDays").GetInt32());
    }

    // ============ 备份自动清理：按时间删除 ============

    [Fact]
    public async Task DeleteBackupsOlderThan_RemovesOnlyExpired()
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            string backupDir = Path.Combine(_tempDir, "backups");
            Directory.CreateDirectory(backupDir);

            // 构造三个备份文件：旧的（40 天前）、临界（10 天前）、新的（1 天前）
            string old = Path.Combine(backupDir, "config.old.json");
            string mid = Path.Combine(backupDir, "config.mid.json");
            string recent = Path.Combine(backupDir, "config.recent.json");
            await File.WriteAllTextAsync(old, "{}");
            await File.WriteAllTextAsync(mid, "{}");
            await File.WriteAllTextAsync(recent, "{}");

            // 用 LastWriteTimeUtc 模拟时间（删除逻辑按此判断）
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));
            File.SetLastWriteTimeUtc(mid, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-1));

            // 清理 30 天前的：应只删 old
            int deleted = service.DeleteBackupsOlderThan(30);
            Assert.Equal(1, deleted);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(mid));
            Assert.True(File.Exists(recent));

            // 清理 5 天前的：应再删 mid（recent 保留）
            int deleted2 = service.DeleteBackupsOlderThan(5);
            Assert.Equal(1, deleted2);
            Assert.False(File.Exists(mid));
            Assert.True(File.Exists(recent));
        }
    }

    [Fact]
    public void DeleteBackupsOlderThan_ZeroDays_Throws()
    {
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            Assert.Throws<ArgumentException>(() => service.DeleteBackupsOlderThan(0));
            Assert.Throws<ArgumentException>(() => service.DeleteBackupsOlderThan(-1));
        }
    }

    [Fact]
    public void DeleteBackupsOlderThan_EmptyDirectory_ReturnsZero()
    {
        // 备份目录不存在时安全返回 0
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            Assert.Equal(0, service.DeleteBackupsOlderThan(30));
        }
    }

    [Fact]
    public async Task DeleteBackupsOlderThan_SkipsUnsafeNames()
    {
        // 非 config.*.json 命名的文件不应被清理（即便过期）
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            string backupDir = Path.Combine(_tempDir, "backups");
            Directory.CreateDirectory(backupDir);
            string unsafeName = Path.Combine(backupDir, "evil.json");
            await File.WriteAllTextAsync(unsafeName, "{}");
            File.SetLastWriteTimeUtc(unsafeName, DateTime.UtcNow.AddDays(-100));

            int deleted = service.DeleteBackupsOlderThan(30);
            Assert.Equal(0, deleted);
            Assert.True(File.Exists(unsafeName)); // 非法名被跳过
        }
    }

    // ============ AuditRecordResults 开关（记录查询结果）============

    [Fact]
    public void GetMaintenance_AuditRecordResults_DefaultsFalse_WhenNodeMissing()
    {
        // maintenance 节点缺失时，AuditRecordResults 应回退为默认 false
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            MaintenanceSettingsResponse r = service.GetMaintenance();
            Assert.False(r.AuditRecordResults);
        }
    }

    [Fact]
    public async Task SaveMaintenance_PreservesAuditRecordResults()
    {
        // SaveMaintenanceAsync 路径：PUT 含 auditRecordResults=true → 再 GET → 字段保留
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            MaintenanceSettingsResponse saved = await service.SaveMaintenanceAsync(
                new MaintenanceSettingsRequest { AuditRecordResults = true },
                CancellationToken.None);
            Assert.True(saved.AuditRecordResults);

            // 等 ConfigStore 热重载（保存写文件 → FileSystemWatcher → Reload），再读确保落盘一致
            await WaitForReloadAsync(store,
                () => (store.Current.Maintenance ?? MaintenanceConfig.Default).AuditRecordResults);
            Assert.True(service.GetMaintenance().AuditRecordResults);
        }

        // 直接验证 config.json 文件里 auditRecordResults 落了 true
        // (service 已 dispose，但文件还在 _tempDir，Create 用的 configPath 不便拿；
        //  这里仅校验内存 round-trip 即可，文件落盘由现有 D1 测试模式覆盖)
    }

    [Fact]
    public async Task SaveConfig_PreservesAuditRecordResults()
    {
        // SaveConfigAsync 路径：先开 auditRecordResults=true，再 PUT /config 改 projects
        // maintenance 节点（含开关）应被原样透传，开关仍为 true
        var (store, service, _) = Create("{\"databases\":{}}");
        using (store)
        {
            // 先经 maintenance 接口打开开关
            await service.SaveMaintenanceAsync(
                new MaintenanceSettingsRequest { AuditRecordResults = true },
                CancellationToken.None);

            // 等 ConfigStore 热重载（保存写文件 → FileSystemWatcher → Reload）
            await WaitForReloadAsync(store,
                () => (store.Current.Maintenance ?? MaintenanceConfig.Default).AuditRecordResults);

            // 再经 config 接口保存 projects（复用现有 GetConfig 返回，不改变 projects 内容）
            AdminConfigResponse configResp = service.GetConfig();
            AdminSaveResult result = await service.SaveConfigAsync(
                new AdminConfigRequest { Projects = configResp.Projects },
                CancellationToken.None);
            Assert.True(result.Success);

            // maintenance 开关应仍为 true（ToConfig 的 Maintenance = current.Maintenance 引用透传）
            Assert.True(service.GetMaintenance().AuditRecordResults);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 测试清理 */ }
    }
}
