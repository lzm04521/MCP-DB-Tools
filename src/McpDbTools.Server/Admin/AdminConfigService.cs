using System.Globalization;
using System.Text.Json;
using McpDbTools.Server.Configuration;
using McpDbTools.Server.Database;
using Microsoft.Extensions.Options;

namespace McpDbTools.Server.Admin;

public sealed class AdminConfigService
{
    private static readonly DatabaseType[] SupportedDatabaseTypes =
    {
        DatabaseType.SqlServer,
        DatabaseType.MySql,
        DatabaseType.Oracle
    };

    private readonly ConfigStore _configStore;
    private readonly DatabaseProviderFactory _providerFactory;
    private readonly string _configPath;
    private readonly string _backupDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    public AdminConfigService(ConfigStore configStore, DatabaseProviderFactory providerFactory, IOptions<ConfigStoreOptions> options)
    {
        _configStore = configStore;
        _providerFactory = providerFactory;
        _configPath = Path.GetFullPath(options.Value.ConfigPath);
        string directory = Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory;
        _backupDirectory = Path.Combine(directory, "backups");
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }

    public AdminConfigResponse GetConfig()
        => ToResponse(_configStore.Current);

    /// <summary>
    /// 测试连接是否可用。用入参的连接字符串即时打开连接，不落盘、不影响当前配置。
    /// </summary>
    public async Task<TestConnectionResult> TestConnectionAsync(TestConnectionRequest request, CancellationToken cancellationToken)
    {
        string connectionString = request.ConnectionString?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new TestConnectionResult { Success = false, Error = "连接字符串不能为空。" };
        }
        if (!TryParseDatabaseType(request.DatabaseType, out DatabaseType type))
        {
            return new TestConnectionResult { Success = false, Error = $"数据库类型不支持: {request.DatabaseType}" };
        }

        IDatabaseProvider provider = _providerFactory.Get(type);
        int timeout = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 5;
        (bool success, long elapsedMs, string? error) = await provider.TestConnectionAsync(connectionString, timeout, cancellationToken);
        return new TestConnectionResult { Success = success, ElapsedMs = elapsedMs, Error = error };
    }

    public async Task<AdminSaveResult> SaveConfigAsync(AdminConfigRequest request, CancellationToken cancellationToken)
    {
        DatabasesConfig current = _configStore.Current;
        var errors = new List<string>();
        DatabasesConfig next = ToConfig(request, current, errors);
        if (errors.Count > 0)
        {
            return new AdminSaveResult { Success = false, Errors = errors };
        }

        string backupName = await WriteConfigAtomicallyAsync(next, cancellationToken);
        return new AdminSaveResult
        {
            Success = true,
            BackupName = backupName,
            Config = ToResponse(next)
        };
    }

    private AdminConfigResponse ToResponse(DatabasesConfig config)
    {
        var projects = config.Projects
            .Select(project => new AdminProjectDto
            {
                Name = project.Key,
                OriginalName = project.Key,
                DisplayName = project.Value.DisplayName,
                DefaultEnvironment = project.Value.DefaultEnvironment,
                Environments = project.Value.Environments
                    .Select(env => new AdminEnvironmentDto
                    {
                        Name = env.Key,
                        OriginalName = env.Key,
                        DisplayName = env.Value.DisplayName,
                        IsProduction = env.Value.IsProduction,
                        Type = ToConfigType(env.Value.Type),
                        ConnectionString = env.Value.ConnectionString,
                        ConnectionStringMasked = string.Empty,
                        MaxRows = env.Value.MaxRows,
                        CommandTimeout = env.Value.CommandTimeout,
                        DisabledKeywords = env.Value.DisabledKeywords.ToList()
                    })
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AdminConfigResponse
        {
            ConfigPath = _configPath,
            DefaultDisabledKeywords = NormalizeKeywords(config.DefaultDisabledKeywords is { Count: > 0 }
                ? config.DefaultDisabledKeywords
                : DefaultDisabledKeywords.BuiltIn),
            DefaultDisabledKeywordsByType = ToResponseKeywordsByType(config),
            Projects = projects
        };
    }

    private static DatabasesConfig ToConfig(AdminConfigRequest request, DatabasesConfig current, List<string> errors)
    {
        var projects = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (AdminProjectDto project in request.Projects)
        {
            string projectName = project.Name.Trim();
            if (string.IsNullOrWhiteSpace(projectName))
            {
                errors.Add("项目 key 不能为空。");
                continue;
            }
            if (ContainsControlOrPathSeparator(projectName))
            {
                errors.Add($"项目 key 不建议包含控制字符或路径分隔符: {projectName}");
            }
            if (projects.ContainsKey(projectName))
            {
                errors.Add($"项目 key 重复: {projectName}");
                continue;
            }

            ProjectConfig? currentProject = FindCurrentProject(current, project);
            // 项目 key 创建后不可修改：携带 originalName（表示已存在的项目）时，name 必须与之相同
            string? originalProjectName = NullIfWhiteSpace(project.OriginalName);
            if (originalProjectName is not null && !string.Equals(originalProjectName, projectName, StringComparison.Ordinal))
            {
                errors.Add($"项目 key 创建后不可修改：原 “{originalProjectName}” 不能改为 “{projectName}”。");
            }

            var environments = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (AdminEnvironmentDto env in project.Environments)
            {
                string envName = env.Name.Trim();
                if (string.IsNullOrWhiteSpace(envName))
                {
                    errors.Add($"项目 {projectName} 的环境 key 不能为空。");
                    continue;
                }
                if (ContainsControlOrPathSeparator(envName))
                {
                    errors.Add($"环境 key 不建议包含控制字符或路径分隔符: {projectName}/{envName}");
                }
                if (environments.ContainsKey(envName))
                {
                    errors.Add($"项目 {projectName} 下环境 key 重复: {envName}");
                    continue;
                }

                if (!TryParseDatabaseType(env.Type, out DatabaseType type))
                {
                    errors.Add($"项目 {projectName} / 环境 {envName} 的数据库类型不支持: {env.Type}");
                    continue;
                }

                DatabaseConfig? currentEnv = FindCurrentEnvironment(currentProject, env);
                // 环境 key 创建后不可修改：同项目 key 规则
                string? originalEnvName = NullIfWhiteSpace(env.OriginalName);
                if (currentProject is not null && originalEnvName is not null && !string.Equals(originalEnvName, envName, StringComparison.Ordinal))
                {
                    errors.Add($"环境 key 创建后不可修改：项目 {projectName} 下原 “{originalEnvName}” 不能改为 “{envName}”。");
                }
                string connectionString = env.ConnectionString?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(connectionString) && currentEnv is not null)
                {
                    connectionString = currentEnv.ConnectionString;
                }
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    errors.Add($"项目 {projectName} / 环境 {envName} 的连接字符串不能为空。");
                }
                if (env.MaxRows <= 0)
                {
                    errors.Add($"项目 {projectName} / 环境 {envName} 的 maxRows 必须大于 0。");
                }
                if (env.CommandTimeout <= 0)
                {
                    errors.Add($"项目 {projectName} / 环境 {envName} 的 commandTimeout 必须大于 0。");
                }

                environments[envName] = new DatabaseConfig
                {
                    DisplayName = NullIfWhiteSpace(env.DisplayName),
                    IsProduction = env.IsProduction,
                    Type = type,
                    ConnectionString = connectionString,
                    MaxRows = env.MaxRows,
                    CommandTimeout = env.CommandTimeout,
                    DisabledKeywords = env.DisabledKeywords
                        .Select(k => k.Trim())
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            }

            string? defaultEnvironment = NullIfWhiteSpace(project.DefaultEnvironment);
            if (defaultEnvironment is not null && !environments.ContainsKey(defaultEnvironment))
            {
                errors.Add($"项目 {projectName} 的默认环境不存在: {defaultEnvironment}");
            }

            projects[projectName] = new ProjectConfig
            {
                DisplayName = NullIfWhiteSpace(project.DisplayName),
                DefaultEnvironment = defaultEnvironment,
                Environments = environments
            };
        }

        return new DatabasesConfig
        {
            DefaultDisabledKeywords = request.DefaultDisabledKeywords is null
                ? current.DefaultDisabledKeywords?.ToList()
                : NormalizeKeywords(request.DefaultDisabledKeywords),
            DefaultDisabledKeywordsByType = request.DefaultDisabledKeywordsByType is null
                ? current.DefaultDisabledKeywordsByType?.ToDictionary(
                    item => item.Key,
                    item => item.Value.ToList())
                : ToConfigKeywordsByType(request.DefaultDisabledKeywordsByType, errors),
            Projects = projects
        };
    }

    private static Dictionary<string, List<string>> ToResponseKeywordsByType(DatabasesConfig config)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (DatabaseType type in SupportedDatabaseTypes)
        {
            IReadOnlyList<string> keywords =
                config.DefaultDisabledKeywordsByType is not null &&
                config.DefaultDisabledKeywordsByType.TryGetValue(type, out List<string>? configured) &&
                configured is { Count: > 0 }
                    ? configured
                    : DefaultDisabledKeywords.BuiltInByType.TryGetValue(type, out IReadOnlyList<string>? builtIn)
                        ? builtIn
                        : Array.Empty<string>();

            result[ToConfigType(type)] = NormalizeKeywords(keywords);
        }

        return result;
    }

    private static Dictionary<DatabaseType, List<string>> ToConfigKeywordsByType(
        Dictionary<string, List<string>> request,
        List<string> errors)
    {
        var result = new Dictionary<DatabaseType, List<string>>();
        foreach ((string rawType, List<string> keywords) in request)
        {
            if (!TryParseDatabaseType(rawType, out DatabaseType type))
            {
                errors.Add($"数据库类型阻止关键字不支持: {rawType}");
                continue;
            }
            if (result.ContainsKey(type))
            {
                errors.Add($"数据库类型阻止关键字重复: {rawType}");
                continue;
            }

            result[type] = NormalizeKeywords(keywords);
        }

        foreach (DatabaseType type in SupportedDatabaseTypes)
        {
            if (!result.ContainsKey(type))
            {
                result[type] = new List<string>();
            }
        }

        return result;
    }

    private static List<string> NormalizeKeywords(IEnumerable<string>? keywords)
        => keywords?
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

    private async Task<string> WriteConfigAtomicallyAsync(DatabasesConfig config, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, "config.tmp.json");
        string json = JsonSerializer.Serialize(config, _jsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);

        string verifyJson = await File.ReadAllTextAsync(tempPath, cancellationToken);
        DatabasesConfig? verified = JsonSerializer.Deserialize<DatabasesConfig>(verifyJson, _jsonOptions);
        if (verified is null)
        {
            throw new InvalidDataException("临时配置文件反序列化结果为空。");
        }

        string backupDirectory = Path.Combine(directory, "backups");
        Directory.CreateDirectory(backupDirectory);
        string backupName = $"config.{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json";
        string backupPath = Path.Combine(backupDirectory, backupName);
        if (File.Exists(_configPath))
        {
            File.Copy(_configPath, backupPath, overwrite: false);
            File.Replace(tempPath, _configPath, null);
        }
        else
        {
            await File.WriteAllTextAsync(backupPath, "{}", cancellationToken);
            File.Move(tempPath, _configPath);
        }

        return backupName;
    }

    /// <summary>列出所有备份（按时间倒序）。</summary>
    public BackupListResponse ListBackups()
    {
        var items = new List<BackupItem>();
        if (Directory.Exists(_backupDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_backupDirectory, "config.*.json"))
            {
                var info = new FileInfo(file);
                items.Add(new BackupItem
                {
                    Name = info.Name,
                    Time = info.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    SizeBytes = info.Length
                });
            }
        }

        // 按时间倒序（文件名含时间戳，名字字典序与时间序一致，倒序即最新在前）
        items.Sort((a, b) => string.Compare(b.Name, a.Name, StringComparison.Ordinal));
        return new BackupListResponse { Items = items, Directory = _backupDirectory };
    }

    /// <summary>返回备份文件物理路径与内容类型，供下载。文件不存在或非法名返回 null。</summary>
    public string? GetBackupPath(string name)
    {
        if (!IsSafeBackupName(name))
        {
            return null;
        }
        string path = Path.Combine(_backupDirectory, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 将指定备份恢复为当前 config.json。
    /// <para>安全策略：先把当前 config.json 复制为一份新备份（恢复前快照，可撤销），再用备份覆盖。</para>
    /// <para>返回新产生的「恢复前快照」备份名，便于撤销提示。</para>
    /// </summary>
    public RestoreResult RestoreBackup(string name)
    {
        string? backupPath = GetBackupPath(name);
        if (backupPath is null)
        {
            return new RestoreResult { Success = false, Error = "备份不存在或文件名非法。" };
        }

        try
        {
            Directory.CreateDirectory(_backupDirectory);

            // 恢复前先把当前配置存为快照（可撤销）
            string snapshotName = $"config.{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json";
            string snapshotPath = Path.Combine(_backupDirectory, snapshotName);
            if (File.Exists(_configPath))
            {
                File.Copy(_configPath, snapshotPath, overwrite: false);
            }
            else
            {
                // 当前无配置文件：写一个空快照占位
                File.WriteAllText(snapshotPath, "{}");
            }

            // 用备份内容覆盖当前配置（先读到内存再写，避免 File.Replace 的目标文件限制）
            string content = File.ReadAllText(backupPath);
            File.WriteAllText(_configPath, content);

            return new RestoreResult { Success = true, SnapshotName = snapshotName, RestoredName = name };
        }
        catch (Exception ex)
        {
            return new RestoreResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>删除指定备份文件。</summary>
    public DeleteBackupResult DeleteBackup(string name)
    {
        string? path = GetBackupPath(name);
        if (path is null)
        {
            return new DeleteBackupResult { Success = false, Error = "备份不存在或文件名非法。" };
        }
        try
        {
            File.Delete(path);
            return new DeleteBackupResult { Success = true, Name = name };
        }
        catch (Exception ex)
        {
            return new DeleteBackupResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>备份文件名安全校验：必须形如 config.{时间戳}.json，禁止路径穿越。</summary>
    private static bool IsSafeBackupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        // 禁止任何路径分隔或父目录引用
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            return false;
        }
        return name.StartsWith("config.", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectConfig? FindCurrentProject(DatabasesConfig current, AdminProjectDto project)
    {
        string? originalName = NullIfWhiteSpace(project.OriginalName);
        if (originalName is not null && current.Projects.TryGetValue(originalName, out ProjectConfig? byOriginal))
        {
            return byOriginal;
        }

        string name = project.Name.Trim();
        return current.Projects.TryGetValue(name, out ProjectConfig? byName) ? byName : null;
    }

    private static DatabaseConfig? FindCurrentEnvironment(ProjectConfig? currentProject, AdminEnvironmentDto env)
    {
        if (currentProject is null)
        {
            return null;
        }

        string? originalName = NullIfWhiteSpace(env.OriginalName);
        if (originalName is not null && currentProject.Environments.TryGetValue(originalName, out DatabaseConfig? byOriginal))
        {
            return byOriginal;
        }

        string name = env.Name.Trim();
        return currentProject.Environments.TryGetValue(name, out DatabaseConfig? byName) ? byName : null;
    }

    private static bool TryParseDatabaseType(string value, out DatabaseType type)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "sqlserver":
                type = DatabaseType.SqlServer;
                return true;
            case "mysql":
                type = DatabaseType.MySql;
                return true;
            case "oracle":
                type = DatabaseType.Oracle;
                return true;
            default:
                type = default;
                return false;
        }
    }

    private static string ToConfigType(DatabaseType type) => type switch
    {
        DatabaseType.SqlServer => "sqlserver",
        DatabaseType.MySql => "mysql",
        DatabaseType.Oracle => "oracle",
        _ => throw new NotSupportedException($"不支持的数据库类型: {type}")
    };

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ContainsControlOrPathSeparator(string value)
        => value.Any(char.IsControl) || value.Contains('/') || value.Contains('\\');
}
