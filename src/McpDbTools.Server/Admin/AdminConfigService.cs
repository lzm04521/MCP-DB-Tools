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
        DatabaseType.Oracle,
        DatabaseType.PostgreSql
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
        // backups 目录放在集中解析的数据目录下，尊重 DI 中 ConfigPath 的目录（测试/显式覆盖场景）
        string dataDir = DataDirectoryResolver.Resolve(options.Value.ConfigPath);
        _backupDirectory = Path.Combine(dataDir, "backups");
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

        try
        {
            string backupName = await WriteAtomicallyAsync(next, cancellationToken);
            return new AdminSaveResult
            {
                Success = true,
                BackupName = backupName,
                Config = ToResponse(next)
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
        {
            // 权限/IO 阻塞：不假成功，返回明确中文提示（路由 BadRequest，前端展示 Errors）
            return new AdminSaveResult { Success = false, Errors = new List<string> { TryConfigWriteErrorMessage(ex)! } };
        }
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
                        AllowWrite = env.Value.AllowWrite,
                        Type = ToConfigType(env.Value.Type),
                        ConnectionString = env.Value.ConnectionString,
                        ConnectionStringMasked = string.Empty,
                        MaxRows = env.Value.MaxRows,
                        CommandTimeout = env.Value.CommandTimeout,
                        MaxPoolSize = env.Value.MaxPoolSize,
                        ConnectTimeoutSeconds = env.Value.ConnectTimeoutSeconds,
                        MaxConcurrency = env.Value.MaxConcurrency,
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
            // 透传配置原值：空就是空（编辑框留空 = 使用系统默认，运行时由 ResolvedConfig.Build 回退内置）。
            // 内置全集通过下方 BuiltIn*Keywords 字段单独暴露，不在展示值里回退，
            // 避免用户清空保存后重新加载时被内置列表填回、并被当成用户配置写回 config.json。
            DefaultDisabledKeywords = NormalizeKeywords(config.DefaultDisabledKeywords),
            DefaultWriteDisabledKeywords = NormalizeKeywords(config.DefaultWriteDisabledKeywords),
            // 内置关键字只读暴露（单一真源 = 后端）
            BuiltInReadOnlyKeywords = DefaultDisabledKeywords.BuiltInReadOnly.ToList(),
            BuiltInWriteKeywords = DefaultDisabledKeywords.BuiltInWrite.ToList(),
            BuiltInDisabledKeywordsByType = DefaultDisabledKeywords.BuiltInByType
                .ToDictionary(kv => kv.Key.ToString().ToLowerInvariant(), kv => kv.Value.ToList()),
            DefaultDisabledKeywordsByType = ToResponseKeywordsByType(config),
            DefaultMaxConcurrency = config.DefaultMaxConcurrency ?? 0,
            DefaultMaxConcurrencyWaitSeconds = config.DefaultMaxConcurrencyWaitSeconds ?? 0,
            DefaultMaxPoolSize = config.DefaultMaxPoolSize ?? 0,
            DefaultConnectTimeoutSeconds = config.DefaultConnectTimeoutSeconds ?? 0,
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

            // originalName = 改名前旧身份定位器（FindCurrentProject 先按它查找，空连接串回填依赖）
            ProjectConfig? currentProject = FindCurrentProject(current, project);

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

                // originalName = 改名前旧身份定位器（空连接串回填依赖），改名不再报错
                DatabaseConfig? currentEnv = FindCurrentEnvironment(currentProject, env);
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
                // 互斥校验：生产环境与 DB 写不能同时开启
                if (env.AllowWrite && env.IsProduction)
                {
                    errors.Add($"项目 {projectName} / 环境 {envName}：生产环境与 DB 写互斥，不能同时开启。");
                }

                environments[envName] = new DatabaseConfig
                {
                    DisplayName = NullIfWhiteSpace(env.DisplayName),
                    IsProduction = env.IsProduction,
                    AllowWrite = env.AllowWrite,
                    Type = type,
                    ConnectionString = connectionString,
                    MaxRows = env.MaxRows,
                    CommandTimeout = env.CommandTimeout,
                    MaxPoolSize = env.MaxPoolSize,
                    ConnectTimeoutSeconds = env.ConnectTimeoutSeconds,
                    MaxConcurrency = env.MaxConcurrency,
                    DisabledKeywords = env.DisabledKeywords
                        .Select(k => k.Trim())
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            }

            string? defaultEnvironment = NullIfWhiteSpace(project.DefaultEnvironment);
            // 环境改名跟随：defaultEnvironment 命中某环境的旧名（originalName）时重映射为新名。
            // 按旧身份定位（Ordinal）：Test↔Prod 互换时默认环境跟随原环境，而非字面同名。
            if (defaultEnvironment is not null)
            {
                var renamed = project.Environments.FirstOrDefault(e =>
                    !string.IsNullOrWhiteSpace(e.OriginalName) &&
                    string.Equals(e.OriginalName.Trim(), defaultEnvironment, StringComparison.Ordinal));
                if (renamed is not null)
                {
                    defaultEnvironment = renamed.Name.Trim();
                }
            }
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
            // 写池：null 保持当前；非 null 按 NormalizeKeywords 归一化（含 trim/distinct）
            DefaultWriteDisabledKeywords = request.DefaultWriteDisabledKeywords is null
                ? current.DefaultWriteDisabledKeywords?.ToList()
                : NormalizeKeywords(request.DefaultWriteDisabledKeywords),
            DefaultDisabledKeywordsByType = request.DefaultDisabledKeywordsByType is null
                ? current.DefaultDisabledKeywordsByType?.ToDictionary(
                    item => item.Key,
                    item => item.Value.ToList())
                : ToConfigKeywordsByType(request.DefaultDisabledKeywordsByType, errors),
            DefaultMaxConcurrency = request.DefaultMaxConcurrency ?? current.DefaultMaxConcurrency,
            DefaultMaxConcurrencyWaitSeconds = request.DefaultMaxConcurrencyWaitSeconds ?? current.DefaultMaxConcurrencyWaitSeconds,
            DefaultMaxPoolSize = request.DefaultMaxPoolSize ?? current.DefaultMaxPoolSize,
            DefaultConnectTimeoutSeconds = request.DefaultConnectTimeoutSeconds ?? current.DefaultConnectTimeoutSeconds,
            // port/maintenance 独立于 projects/keywords：保存 projects 时原样透传，避免全量替换丢失
            Port = current.Port,
            Maintenance = current.Maintenance,
            Projects = projects
        };
    }

    private static Dictionary<string, List<string>> ToResponseKeywordsByType(DatabasesConfig config)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (DatabaseType type in SupportedDatabaseTypes)
        {
            // 透传配置原值：空就是空（编辑框留空 = 使用系统默认，运行时由 ResolvedConfig.Build 回退内置）
            IReadOnlyList<string> keywords =
                config.DefaultDisabledKeywordsByType is not null &&
                config.DefaultDisabledKeywordsByType.TryGetValue(type, out List<string>? configured)
                    ? configured
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

    // ============ 项目配置导入（环境级合并）============

    /// <summary>
    /// 预览导入：解析 JSON → 与 current 环境级合并 → 复用 ToConfig 校验。不落盘。
    /// 返回合并计划与校验错误列表（不论是否有错误都返回，前端展示后决定）。
    /// </summary>
    public ImportPreviewResponse GetImportPreview(string json)
        => BuildMergedConfig(json).Preview;

    /// <summary>
    /// 应用导入：重新合并+校验（不信任前端缓存，防预览后 config 被改），全过则原子落盘。
    /// 任一校验失败：不落盘，返回 errors。
    /// </summary>
    public async Task<ImportApplyResult> ApplyImportAsync(string json, CancellationToken cancellationToken)
    {
        (DatabasesConfig? next, ImportPreviewResponse preview) = BuildMergedConfig(json);
        if (preview.Errors.Count > 0 || next is null)
        {
            return new ImportApplyResult
            {
                Success = false,
                Errors = preview.Errors,
                Plan = preview.Plan
            };
        }

        string backupName = await WriteAtomicallyAsync(next, cancellationToken);
        return new ImportApplyResult
        {
            Success = true,
            BackupName = backupName,
            Plan = preview.Plan
        };
    }

    /// <summary>
    /// 合并核心：解析（宽容）→ 环境级合并 → 复用 ToConfig 校验。
    /// 产出最终 DTO 列表时按「current 是否已存在」设置 originalName，
    /// 使 ToConfig 既能校验又能转换，且不误报「key 创建后不可修改」。
    /// </summary>
    private (DatabasesConfig? Next, ImportPreviewResponse Preview) BuildMergedConfig(string json)
    {
        var plan = new ImportPlan();
        var errors = new List<string>();

        // 1. 宽容解析：接受 ① 完整 config.json（取 databases）② 纯 databases 对象 ③ 单项目片段
        Dictionary<string, ProjectConfig> imported;
        try
        {
            JsonElement root = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("未识别的 JSON 结构：顶层不是 JSON 对象。");
                return (null, BuildPreview(plan, errors, 0));
            }
            JsonElement databasesEl = root.TryGetProperty("databases", out JsonElement dbEl) ? dbEl : root;
            if (databasesEl.ValueKind != JsonValueKind.Object)
            {
                errors.Add("未识别的 JSON 结构：databases 不是 JSON 对象。");
                return (null, BuildPreview(plan, errors, 0));
            }
            imported = JsonSerializer.Deserialize<Dictionary<string, ProjectConfig>>(databasesEl.GetRawText(), _jsonOptions)
                ?? new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase);
            // System.Text.Json 反序列化 Dictionary<string,T> 用 Ordinal 比较器（PropertyNameCaseInsensitive
            // 只影响属性绑定，不影响字典 key 比较器）。不重建会导致 mixed-case key（如 current "ERP" vs
            // imported "erp"）被当成两个不同项目，imported 静默丢失。此处显式重建为 OrdinalIgnoreCase。
            imported = RebuildImportedCaseInsensitive(imported);
        }
        catch (JsonException ex)
        {
            errors.Add($"JSON 解析失败：{ex.Message}");
            return (null, BuildPreview(plan, errors, 0));
        }

        DatabasesConfig current = _configStore.Current;
        var result = new List<AdminProjectDto>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 2a. current 已有项目：与 imported 同名则环境级合并，否则原样保留
        foreach ((string key, ProjectConfig currentProj) in current.Projects)
        {
            if (imported.TryGetValue(key, out ProjectConfig? importedProj))
            {
                plan.UpdatedProjects.Add(key);
                var envDtos = new List<AdminEnvironmentDto>();
                var envHandled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach ((string envKey, DatabaseConfig currentEnv) in currentProj.Environments)
                {
                    if (importedProj.Environments.TryGetValue(envKey, out DatabaseConfig? importedEnv))
                    {
                        plan.UpdatedEnvironments.Add($"{key}/{envKey}");
                        envDtos.Add(ToImportEnvDto(envKey, originalName: envKey, importedEnv));
                    }
                    else
                    {
                        // current 有、imported 没有：保留（originalName=envKey 表示已存在）
                        envDtos.Add(ToImportEnvDto(envKey, originalName: envKey, currentEnv));
                    }
                    envHandled.Add(envKey);
                }
                foreach ((string envKey, DatabaseConfig importedEnv) in importedProj.Environments)
                {
                    if (!envHandled.Contains(envKey))
                    {
                        plan.AddedEnvironments.Add($"{key}/{envKey}");
                        envDtos.Add(ToImportEnvDto(envKey, originalName: null, importedEnv));
                    }
                }

                result.Add(new AdminProjectDto
                {
                    Name = key,
                    OriginalName = key,                       // 项目已存在，不可改名
                    DisplayName = importedProj.DisplayName,   // 项目级字段用 imported 覆盖
                    DefaultEnvironment = importedProj.DefaultEnvironment,
                    Environments = envDtos
                });
            }
            else
            {
                // current 有、imported 没有：完全保留
                result.Add(ToImportProjectDto(key, originalName: key, currentProj));
            }
            handled.Add(key);
        }

        // 2b. imported 有、current 没有：整体新增
        foreach ((string key, ProjectConfig importedProj) in imported)
        {
            if (handled.Contains(key))
            {
                continue;
            }
            plan.AddedProjects.Add(key);
            foreach (string envKey in importedProj.Environments.Keys)
            {
                plan.AddedEnvironments.Add($"{key}/{envKey}");
            }
            result.Add(ToImportProjectDto(key, originalName: null, importedProj));
        }

        // 3. 复用 ToConfig 做校验 + 转换（全局字段 null → 保持 current；Maintenance 透传）。
        //    errors 与 plan 为引用类型，ToConfig 往 errors 追加的校验错误会反映到下方 BuildPreview 返回的对象。
        var request = new AdminConfigRequest { Projects = result };
        DatabasesConfig next = ToConfig(request, current, errors);

        return (errors.Count > 0 ? null : next, BuildPreview(plan, errors, imported.Count));
    }

    /// <summary>构造预览响应。plan 与 errors 为引用类型，合并/校验阶段追加的项会反映到返回对象。</summary>
    private static ImportPreviewResponse BuildPreview(ImportPlan plan, List<string> errors, int parsedProjectCount) => new()
    {
        Plan = plan,
        Errors = errors,
        ParsedProjectCount = parsedProjectCount
    };

    /// <summary>ProjectConfig → AdminProjectDto 映射（导入专用，按是否已存在设 originalName）。</summary>
    private static AdminProjectDto ToImportProjectDto(string name, string? originalName, ProjectConfig p)
    {
        List<AdminEnvironmentDto> envs = p.Environments
            .Select(kv => ToImportEnvDto(kv.Key, originalName: originalName is not null ? kv.Key : null, kv.Value))
            .ToList();
        return new AdminProjectDto
        {
            Name = name,
            OriginalName = originalName,
            DisplayName = p.DisplayName,
            DefaultEnvironment = p.DefaultEnvironment,
            Environments = envs
        };
    }

    /// <summary>DatabaseConfig → AdminEnvironmentDto 映射（剥离无 originalName 的语义由调用方决定）。</summary>
    private static AdminEnvironmentDto ToImportEnvDto(string name, string? originalName, DatabaseConfig env) => new()
    {
        Name = name,
        OriginalName = originalName,
        DisplayName = env.DisplayName,
        IsProduction = env.IsProduction,
        AllowWrite = env.AllowWrite,
        Type = ToConfigType(env.Type),
        ConnectionString = env.ConnectionString,
        MaxRows = env.MaxRows,
        CommandTimeout = env.CommandTimeout,
        MaxPoolSize = env.MaxPoolSize,
        ConnectTimeoutSeconds = env.ConnectTimeoutSeconds,
        MaxConcurrency = env.MaxConcurrency,
        DisabledKeywords = env.DisabledKeywords.ToList()
    };

    /// <summary>
    /// 重建导入字典为 OrdinalIgnoreCase 比较器（含嵌套 Environments 字典）。
    /// </summary>
    private static Dictionary<string, ProjectConfig> RebuildImportedCaseInsensitive(
        Dictionary<string, ProjectConfig> raw)
    {
        var result = new Dictionary<string, ProjectConfig>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, ProjectConfig proj) in raw)
        {
            var envs = new Dictionary<string, DatabaseConfig>(StringComparer.OrdinalIgnoreCase);
            foreach ((string envKey, DatabaseConfig env) in proj.Environments)
            {
                envs[envKey] = env;
            }
            result[key] = new ProjectConfig
            {
                DisplayName = proj.DisplayName,
                DefaultEnvironment = proj.DefaultEnvironment,
                Environments = envs
            };
        }
        return result;
    }

    /// <summary>
    /// 原子写入 config.json：写临时文件 → 校验 → 备份当前配置 → 替换。
    /// 供 SaveConfigAsync 与 SaveMaintenanceAsync 共用，保证两处落盘一致。
    /// </summary>
    private async Task<string> WriteAtomicallyAsync(DatabasesConfig config, CancellationToken cancellationToken)
    {
        // 临时文件、备份目录都放在集中解析的数据目录下，尊重 _configPath 的目录
        string directory = DataDirectoryResolver.EnsureExists(_configPath);

        string tempPath = Path.Combine(directory, "config.tmp.json");
        string json = JsonSerializer.Serialize(config, _jsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);

        string verifyJson = await File.ReadAllTextAsync(tempPath, cancellationToken);
        DatabasesConfig? verified = JsonSerializer.Deserialize<DatabasesConfig>(verifyJson, _jsonOptions);
        if (verified is null)
        {
            throw new InvalidDataException("临时配置文件反序列化结果为空。");
        }

        string backupDirectory = _backupDirectory;
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

    /// <summary>
    /// 把配置落盘环节的权限/IO 异常映射为面向用户的中文提示。
    /// 仅识别 UnauthorizedAccessException（权限阻塞）与 IOException（I/O 错误），其他异常返回 null（不处理，继续上冒）。
    /// 供 SaveConfigAsync 与 Admin API 路由（maintenance / port）复用。
    /// </summary>
    internal static string? TryConfigWriteErrorMessage(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return "保存失败：对配置文件或数据目录没有写权限。数据目录可能由其他用户创建，且共享写权限未就绪。"
                + "请以该目录所有者或管理员身份启动一次本应用以完成共享授权，然后重试。";
        }
        if (ex is IOException)
        {
            return $"保存失败（文件 I/O 错误）：{ex.Message}";
        }
        return null;
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

    // ============ 全局设置（maintenance 节点）============

    /// <summary>
    /// 读取当前 maintenance 配置。节点缺失（null）时返回内置默认（全部关闭、保留 30 天）。
    /// </summary>
    public MaintenanceSettingsResponse GetMaintenance()
    {
        MaintenanceConfig m = _configStore.Current.Maintenance ?? MaintenanceConfig.Default;
        return ToMaintenanceResponse(m);
    }

    /// <summary>
    /// 保存 maintenance 配置（仅替换 maintenance 节点，不动 projects/keywords）。
    /// <para>校验：任一开关开启时对应天数必须 &gt; 0，否则抛 ArgumentException（由调用方包装为 400）。</para>
    /// </summary>
    public async Task<MaintenanceSettingsResponse> SaveMaintenanceAsync(
        MaintenanceSettingsRequest request, CancellationToken cancellationToken)
    {
        ValidateRetentionDays(request.AuditLogAutoCleanup, request.AuditLogRetentionDays, "审计日志");
        ValidateRetentionDays(request.BackupAutoCleanup, request.BackupRetentionDays, "备份");

        DatabasesConfig current = _configStore.Current;
        // DatabasesConfig 是 sealed class（非 record），手动重建：除 maintenance 外其余字段从 current 透传
        DatabasesConfig next = new()
        {
            DefaultDisabledKeywords = current.DefaultDisabledKeywords,
            // 关键防回归：写池必须透传，否则保存 maintenance 会静默丢写池（生产数据风险）
            DefaultWriteDisabledKeywords = current.DefaultWriteDisabledKeywords,
            DefaultDisabledKeywordsByType = current.DefaultDisabledKeywordsByType,
            DefaultMaxConcurrency = current.DefaultMaxConcurrency,
            DefaultMaxConcurrencyWaitSeconds = current.DefaultMaxConcurrencyWaitSeconds,
            DefaultMaxPoolSize = current.DefaultMaxPoolSize,
            DefaultConnectTimeoutSeconds = current.DefaultConnectTimeoutSeconds,
            // 关键防回归：port 必须透传，否则保存 maintenance 会把端口写丢（重启后回退默认）
            Port = current.Port,
            Maintenance = new MaintenanceConfig
            {
                AuditLogAutoCleanup = request.AuditLogAutoCleanup,
                AuditLogRetentionDays = NormalizeRetentionDays(request.AuditLogRetentionDays),
                BackupAutoCleanup = request.BackupAutoCleanup,
                BackupRetentionDays = NormalizeRetentionDays(request.BackupRetentionDays),
                AuditRecordResults = request.AuditRecordResults
            },
            Projects = current.Projects
        };
        await WriteAtomicallyAsync(next, cancellationToken);
        return ToMaintenanceResponse(next.Maintenance!);
    }

    // ============ 端口配置（独立读写，仅改 port 字段，透传其余）============

    /// <summary>读取 config.json 的 port 字段（null 表示未配置，启动回退默认 61123）。</summary>
    public int? GetConfigPort() => _configStore.Current.Port;

    /// <summary>
    /// 保存端口到 config.json（仅改 port，透传其余字段）。校验 1-65535，否则抛 ArgumentException。
    /// <para>注意：写入后需重启进程才生效（Kestrel 端口启动时绑定）。</para>
    /// </summary>
    public async Task<int> SavePortAsync(int port, CancellationToken cancellationToken)
    {
        if (port <= 0 || port > 65535)
        {
            throw new ArgumentException("端口必须在 1-65535 之间。");
        }

        DatabasesConfig current = _configStore.Current;
        // 与 SaveMaintenanceAsync 同模式：手动重建透传所有字段，仅 Port 用入参
        DatabasesConfig next = new()
        {
            DefaultDisabledKeywords = current.DefaultDisabledKeywords,
            DefaultWriteDisabledKeywords = current.DefaultWriteDisabledKeywords,
            DefaultDisabledKeywordsByType = current.DefaultDisabledKeywordsByType,
            DefaultMaxConcurrency = current.DefaultMaxConcurrency,
            DefaultMaxConcurrencyWaitSeconds = current.DefaultMaxConcurrencyWaitSeconds,
            DefaultMaxPoolSize = current.DefaultMaxPoolSize,
            DefaultConnectTimeoutSeconds = current.DefaultConnectTimeoutSeconds,
            Port = port,
            Maintenance = current.Maintenance,
            Projects = current.Projects
        };
        await WriteAtomicallyAsync(next, cancellationToken);
        return port;
    }

    /// <summary>开关开启时校验天数 &gt; 0；关闭时不校验（天数可能任意值，忽略不生效）。</summary>
    private static void ValidateRetentionDays(bool enabled, int days, string label)
    {
        if (enabled && days <= 0)
        {
            throw new ArgumentException($"{label}自动清理已启用，保留天数必须大于 0。");
        }
    }

    /// <summary>归一化天数：非法（&lt;=0）值回退到内置默认 30。</summary>
    private static int NormalizeRetentionDays(int days)
        => days > 0 ? days : MaintenanceConfig.DefaultRetentionDays;

    private static MaintenanceSettingsResponse ToMaintenanceResponse(MaintenanceConfig m) => new()
    {
        AuditLogAutoCleanup = m.AuditLogAutoCleanup,
        AuditLogRetentionDays = m.AuditLogRetentionDays > 0 ? m.AuditLogRetentionDays : MaintenanceConfig.DefaultRetentionDays,
        BackupAutoCleanup = m.BackupAutoCleanup,
        BackupRetentionDays = m.BackupRetentionDays > 0 ? m.BackupRetentionDays : MaintenanceConfig.DefaultRetentionDays,
        AuditRecordResults = m.AuditRecordResults
    };

    // ============ 备份自动清理（供 MaintenanceHostedService 调用）============

    /// <summary>
    /// 删除早于指定天数的备份文件，返回删除数量。
    /// <para>按文件 LastWriteTimeUtc 判断；单文件删除失败跳过（记录但不中断），保证整体清理完成。</para>
    /// <para>与手动 DeleteBackup 不同，这里不返回每个文件的结果，仅供后台服务批量清理。</para>
    /// </summary>
    public int DeleteBackupsOlderThan(int days)
    {
        if (days <= 0)
        {
            throw new ArgumentException("保留天数必须大于 0。", nameof(days));
        }
        if (!Directory.Exists(_backupDirectory))
        {
            return 0;
        }

        DateTime cutoff = DateTime.UtcNow.AddDays(-days);
        int deleted = 0;
        foreach (string file in Directory.EnumerateFiles(_backupDirectory, "config.*.json"))
        {
            string name = Path.GetFileName(file);
            if (!IsSafeBackupName(name))
            {
                continue;
            }
            try
            {
                FileInfo info = new(file);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (IOException)
            {
                // 单文件并发占用（如正在下载/恢复），跳过本次，下次清理周期重试
            }
            catch (UnauthorizedAccessException)
            {
                // 权限问题跳过，不影响其它文件
            }
        }
        return deleted;
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
            case "postgresql":
                type = DatabaseType.PostgreSql;
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
        DatabaseType.PostgreSql => "postgresql",
        _ => throw new NotSupportedException($"不支持的数据库类型: {type}")
    };

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ContainsControlOrPathSeparator(string value)
        => value.Any(char.IsControl) || value.Contains('/') || value.Contains('\\');
}
