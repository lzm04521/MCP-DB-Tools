using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDbTools.Server.Configuration;

/// <summary>
/// 配置文件的加载与热重载。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>启动时从磁盘读取 config.json，文件缺失时使用空配置快照，解析失败则抛异常阻止启动（错误必须暴露，见全局规则）。</item>
/// <item>FileSystemWatcher 监听变更，去抖后重新读取并原子替换内存快照。</item>
/// <item>正在执行的查询使用变更前的旧快照完成，互不干扰。</item>
/// </list>
/// </para>
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private readonly ILogger<ConfigStore> _logger;
    private readonly string _configPath;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private DatabasesConfig _current;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ConfigStore(ILogger<ConfigStore> logger, IOptions<ConfigStoreOptions> options)
    {
        _logger = logger;
        _configPath = Path.GetFullPath(options.Value.ConfigPath);

        // 启动时即加载——文件缺失时使用空配置；解析失败仍必须暴露，不吞错
        _current = Load();

        // FileSystemWatcher 监听数据目录下的 config.json
        _watcher = new FileSystemWatcher(DataDirectoryResolver.Resolve(_configPath), Path.GetFileName(_configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;

        // 500ms 去抖，避免编辑器多次保存触发多次重载
        _debounceTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);

        _logger.LogInformation("配置已加载: {Path}，项目数: {Count}", _configPath, _current.Projects.Count);
    }

    /// <summary>获取当前配置快照。调用方持有引用期间不受热重载影响。</summary>
    public DatabasesConfig Current => _current;

    /// <summary>加载配置并合并三层阻止关键字，返回不可变快照。</summary>
    public ResolvedConfig GetResolved() => ResolvedConfigBuilder.Build(_current);

    private DatabasesConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            _logger.LogWarning("配置文件不存在，使用空配置等待后续保存或热重载创建: {Path}", _configPath);
            return new DatabasesConfig();
        }

        string json = File.ReadAllText(_configPath);
        DatabasesConfig? config = JsonSerializer.Deserialize<DatabasesConfig>(json, JsonOptions);
        if (config is null)
        {
            throw new InvalidDataException($"配置文件解析结果为空: {_configPath}");
        }
        return config;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // 去抖：重置计时器，500ms 内无新事件才真正重载
        _debounceTimer.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
    }

    private void Reload()
    {
        try
        {
            DatabasesConfig fresh = Load();
            // 原子替换：赋值引用是原子的，旧查询仍持有旧引用
            _current = fresh;
            _logger.LogInformation("配置已热重载: 项目数 {Count}", fresh.Projects.Count);
        }
        catch (Exception ex)
        {
            // 重载失败不终止进程，保留旧配置继续服务，但必须上报
            _logger.LogError(ex, "配置热重载失败，保留旧配置继续运行");
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounceTimer.Dispose();
    }
}

/// <summary>ConfigStore 的初始化选项。</summary>
public sealed class ConfigStoreOptions
{
    public string ConfigPath { get; set; } = "config.json";
}
