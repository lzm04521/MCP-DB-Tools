using McpDbTools.Server.Admin;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Tests;

// 验证保存配置遇到写权限/IO 阻塞时：不假成功、返回明确中文提示（供 Admin UI 展示）。
public class ConfigSavePermissionTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigSavePermissionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcpdbsaveperm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SaveConfigAsync_WhenWriteBlocked_ReturnsExplicitError()
    {
        // 用只读 config.json 制造写入阻塞：File.Replace 对只读目标失败（UnauthorizedAccessException / IOException）
        string configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, "{\"databases\":{}}");
        File.SetAttributes(configPath, FileAttributes.ReadOnly);
        try
        {
            var (_, service) = BuildService(configPath);

            var request = new AdminConfigRequest { Projects = new List<AdminProjectDto>() };
            AdminSaveResult result = await service.SaveConfigAsync(request, CancellationToken.None);

            Assert.False(result.Success);
            Assert.NotEmpty(result.Errors);
            Assert.StartsWith("保存失败", result.Errors[0]);
            // 提示必须带上配置文件路径，帮助用户定位被占用/拒绝的文件
            Assert.Contains(configPath, result.Errors[0]);
        }
        finally
        {
            File.SetAttributes(configPath, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "没有写权限")]
    [InlineData(typeof(IOException), "I/O 错误")]
    public void TryConfigWriteErrorMessage_MapsKnownExceptions(Type exType, string expectedFragment)
    {
        const string configPath = @"C:\data\McpDbTools\config.json";
        var ex = (Exception)Activator.CreateInstance(exType)!;
        string? msg = AdminConfigService.TryConfigWriteErrorMessage(ex, configPath);

        Assert.NotNull(msg);
        Assert.Contains(expectedFragment, msg);
        // 两类提示都拼入配置文件路径，便于定位
        Assert.Contains(configPath, msg);
    }

    [Fact]
    public void TryConfigWriteErrorMessage_UnmappedException_ReturnsNull()
    {
        // 非权限/IO 异常不处理，继续上冒（由上层 API 路由兜底为 500 + 结构化提示）
        Assert.Null(AdminConfigService.TryConfigWriteErrorMessage(new InvalidOperationException(), "config.json"));
    }

    private static (ConfigStore store, AdminConfigService service) BuildService(string configPath)
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var options = Options.Create(new ConfigStoreOptions { ConfigPath = configPath });
        var store = new ConfigStore(loggerFactory.CreateLogger<ConfigStore>(), options);
        var service = new AdminConfigService(store, new DatabaseProviderFactory(), options);
        return (store, service);
    }
}
