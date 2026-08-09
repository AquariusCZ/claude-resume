using AiResume.Core;
using AiResume.Worker.Products;
using AiResume.Wrapper;

namespace AiResume.Worker.Migration;

/// <summary>
/// <c>AiResume.Worker.exe cutover-config [--out &lt;path&gt;] [--work-dir &lt;path&gt;]</c>(S10):
/// 从 shadow 产品配置的项目清单生成 cc-connect 的 <c>config.toml</c>。
///
/// **飞书凭据只从环境变量取,绝不作为命令行参数**:命令行会进入进程列表、
/// PowerShell 历史与本工具自己的 CIM 枚举结果,等于把 app_secret 广播出去。
/// 环境变量由用户在自己的会话里设置,值全程不经过日志、不进仓库、不打印——
/// 终端上只回显脱敏版本。
/// </summary>
public static class CutoverConfigCommand
{
    private const string AppIdEnv = "FEISHU_APP_ID";
    private const string AppSecretEnv = "FEISHU_APP_SECRET";

    /// <summary>
    /// cc-connect 侧的项目名是**身份键**:会话、dir 历史、provider 引用、
    /// projects/*.state.json 全部挂在它上面。因此固定不变,不从任何目录名派生。
    /// 实际工作目录由 work_dir 与运行时 /dir 决定,与这个名字无关。
    /// </summary>
    public const string ProjectName = "ai-resume";

    /// <summary>cc-connect 侧的默认 agent 类型。实际值由 shadow 配置的 CcConnectAgent 决定。</summary>
    public static string DefaultAgentType => CcConnectAgents.Default;

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cc-connect", "config.toml");

    /// <summary>生成结果。<see cref="SanitizedToml"/> 已脱敏,可安全回显给界面与日志。</summary>
    public sealed record GenerateResult(bool Ok, string Message, int ProjectCount, string? SanitizedToml, string? OutPath);

    public static int Run(string[] args)
    {
        string outPath = ReadOption(args, "--out") ?? DefaultConfigPath;
        string? workDir = ReadOption(args, "--work-dir");

        // 命令行场景仍支持环境变量;为空则回落到 GUI 存进 DPAPI 的凭据。
        string? appId = Environment.GetEnvironmentVariable(AppIdEnv);
        string? appSecret = Environment.GetEnvironmentVariable(AppSecretEnv);

        GenerateResult result = Generate(outPath, appId, appSecret, workDir);
        if (!result.Ok)
        {
            Console.Error.WriteLine(result.Message);
            return 1;
        }

        Console.WriteLine(result.Message);
        Console.WriteLine();
        Console.WriteLine(result.SanitizedToml);
        Console.WriteLine("下一步:`AiResume.Worker.exe preflight` 必须返回 Clear 之后才能启动 cc-connect。");
        return 0;
    }

    /// <summary>
    /// 生成 cc-connect 配置。凭据留空时从 DPAPI 取(GUI 存的那份)。
    ///
    /// **返回值里绝不含 app_secret 实值**:只有脱敏 TOML。
    /// GUI 与 CLI 共用这一条路径,避免两处逻辑各自漂移。
    /// </summary>
    public static GenerateResult Generate(
        string outPath,
        string? appId,
        string? appSecret,
        string? workDir = null,
        bool requireLoadable = false)
    {
        // 授权名单只从 DPAPI 取:它是安全边界,不接受命令行/环境变量覆盖。
        string allowFrom;
        {
            if (!new FeishuCredentialStore().TryLoad(out string _, out string _, out string storedAllow))
            {
                return new GenerateResult(false,
                    "尚未配置飞书凭据与授权名单。请在控制面「飞书接入」里填写,或先运行 import-feishu。",
                    0, null, null);
            }

            allowFrom = storedAllow;
        }

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            if (!new FeishuCredentialStore().TryLoad(out string storedId, out string storedSecret, out _))
            {
                return new GenerateResult(false,
                    $"尚未配置飞书凭据。请在控制面「飞书接入」里填写,或设置 {AppIdEnv} / {AppSecretEnv} 环境变量。",
                    0, null, null);
            }

            appId = storedId;
            appSecret = storedSecret;
        }

        var configStore = new ProductConfigStore(ShadowPaths.Root);
        ProductConfig product = configStore.Load();

        // agent 类型从 shadow 配置读取并规范化:白名单外的值写进 config.toml 会让 cc-connect 启动失败。
        string agentType = CcConnectAgents.Normalize(product.CcConnectAgent);

        // **只生成一个 [[projects]]**(方案 A,2026-08-06 用户确认)。
        //
        // 此前给全部 N 个项目都写了**同一个**飞书 app_id/app_secret,于是一条入站消息
        // 被 N 个 platform 同时收到、各起一个 agent——实测 9 个 claude.exe、9 条回复卡、
        // 36 条 invalid receive_id,还把模型额度烧穿。上游 INSTALL.md 的多项目示例里
        // 每个项目用的是**不同平台**,从没有"同一个飞书应用绑多个项目"的用法。
        //
        // 单项目 + `/dir <path>` 切工作目录才是 cc-connect 的原意
        // (docs/usage.md §Work Directory Switching)。给其余项目留空 platform 没有意义:
        // 够不着的项目条目只是死配置。
        // AI Resume 自己的续跑队列与此无关,仍然按 shadow 配置管理全部项目。
        // **项目名是身份键**,不能每次生成都变:会话与 provider 引用都挂在它上面。
        // 此前取"发现结果的第一个",于是哪个目录最后被碰过项目就叫什么——
        // 实测从 claude-resume-migration 漂成了 _smoke-cutover,
        // 直接导致 provider list --project 报 project not found。
        // 现在固定为 ProjectName,不再从任何目录名派生。
        string existingToml = File.Exists(outPath) ? SafeRead(outPath) : string.Empty;
        (string existingAgent, string existingProvider, string existingModel) =
            CcConnectConfigValidator.ReadProjectAgentTriple(existingToml, ProjectName);
        bool sameAgent = existingAgent.Length > 0 &&
            existingAgent.Equals(agentType, StringComparison.Ordinal);
        IReadOnlyList<string> existingCoherence = CcConnectConfigValidator.CheckAgentCoherence(
            existingToml, ProjectName);
        bool hasAgentSelection = existingProvider.Length > 0 || existingModel.Length > 0;
        bool preserveAgentSelection = sameAgent && hasAgentSelection && existingCoherence.Count == 0;
        bool projectSelectionReset = existingAgent.Length > 0 && hasAgentSelection && !preserveAgentSelection;
        string? existingWorkDir = CcConnectConfigGenerator.TryReadExistingWorkDir(existingToml, ProjectName);
        // 顶层 [[providers]] 的全局服务商必须经 provider_refs 引用,否则项目用不到它们。
        IReadOnlyList<string> providerRefs = CcConnectConfigGenerator.ReadGlobalProviderNames(existingToml, agentType);
        (string selectedProvider, string selectedModel) = ResolveUnambiguousProviderSelection(
            existingToml, agentType, providerRefs, preserveAgentSelection);

        // 解析 work_dir:显式参数 > 既有配置(目录仍存在) > 已布防项目 > 发现结果(按名排序)。
        // 发现结果要扫盘,所以只在前三级全部落空时才求值——先不传,拿到 null 再补一次。
        string? resolvedWorkDir = ResolveWorkDir(
            workDir,
            existingWorkDir,
            product.Selected,
            discovered: null,
            directoryExists: Directory.Exists);

        if (resolvedWorkDir is null)
        {
            var catalog = new ProjectCatalog(
                indexPath: Path.Combine(ShadowPaths.Root, "project-index.json"));
            resolvedWorkDir = ResolveWorkDir(
                explicitWorkDir: null,
                existingConfigWorkDir: null,
                selected: null,
                discovered: catalog.Discover(product),
                directoryExists: Directory.Exists);
        }

        if (resolvedWorkDir is null)
        {
            return new GenerateResult(false,
                "没有可写入的项目:shadow 配置里既没有已布防项目,也没有发现任何项目。", 0, null, null);
        }

        var projects = new List<CcConnectProject>
        {
            new(
                Name: ProjectName,
                Agent: agentType,
                WorkDir: resolvedWorkDir,
                AdminFrom: allowFrom,
                ProviderRefs: providerRefs,
                SelectedProvider: selectedProvider,
                SelectedModel: selectedModel),
        };

        var config = new CcConnectConfig(projects, new CcConnectPlatformOptions(appId, appSecret, allowFrom));

        try
        {
            CcConnectConfigGenerator.Write(
                outPath,
                config,
                preserveAgentSelection,
                requireLoadable ? ValidateCandidateForActivation : null,
                preserveInlineAgentProviders: sameAgent);
        }
        catch (Exception ex)
        {
            return new GenerateResult(false, $"写入失败:{ex.Message}", 0, null, null);
        }

        // 保留下来的非飞书平台(如 `cc-connect weixin setup` 扫码绑的微信)要在回显里报数,
        // 否则用户看不出它还在,会以为被这次生成抹掉了。
        int foreignCount = CcConnectConfigGenerator.ExtractForeignPlatforms(existingToml, ProjectName).Count;

        string resetNote = projectSelectionReset
            ? $" 已为 {agentType} 清除项目配置中不一致的默认 provider/model 选择。"
            : string.Empty;
        string selectionNote = selectedProvider.Length > 0
            ? $" 已自动选择唯一兼容 provider「{selectedProvider}」"
              + (selectedModel.Length > 0 ? $"及模型「{selectedModel}」。" : "。")
            : string.Empty;

        return new GenerateResult(true,
            $"已写入 {outPath}({projects.Count} 个项目)。{resetNote}{selectionNote} 以下为**脱敏**回显:",
            projects.Count,
            CcConnectConfigGenerator.RenderSanitized(config, foreignCount),
            outPath);
    }

    private static string? ValidateCandidateForActivation(string candidatePath)
    {
        CcConnectConfigCheck check = CcConnectConfigValidator.CheckFile(candidatePath);
        if (check.State != CcConnectConfigState.Ok)
        {
            string details = string.Join("; ", check.Problems);
            return details.Length > 0
                ? $"候选配置未通过 cc-connect 解析验证:{check.Summary} {details}"
                : $"候选配置未通过 cc-connect 解析验证:{check.Summary}";
        }

        if (check.Warnings.Count > 0)
        {
            return "候选配置存在 agent/provider/model 不一致:" + string.Join("; ", check.Warnings);
        }

        return null;
    }

    /// <summary>
    /// 按确定性优先级解析 work_dir。全部候选都不可用时返回 null。
    ///
    /// 优先级:
    /// 1. explicitWorkDir 非空白 → 直接返回(不校验目录存在:用户显式指定的意图,
    ///    不存在也如实写进配置,由 preflight 报错);
    /// 2. existingConfigWorkDir 非空白且目录存在 → 返回(保护用户已有选择);
    /// 3. selected 里第一个 Path 非空白的元素 → 返回其 Path;
    /// 4. discovered 按 Name 用 Ordinal 升序排序后第一个 Path 非空白的元素 → 返回;
    /// 5. 都没有 → null。
    ///
    /// 第 4 级是本次修复的核心:调用方传进来的 discovered 是 mtime 序,
    /// 必须在这里重排,不得依赖入参顺序。
    /// </summary>
    /// <remarks>仅为可测性公开:测试项目未配 InternalsVisibleTo。</remarks>
    public static string? ResolveWorkDir(
        string? explicitWorkDir,
        string? existingConfigWorkDir,
        IReadOnlyList<ProjectRef>? selected,
        IReadOnlyList<ProjectEntry>? discovered,
        Func<string, bool>? directoryExists)
    {
        if (!string.IsNullOrWhiteSpace(explicitWorkDir))
        {
            return explicitWorkDir;
        }

        if (!string.IsNullOrWhiteSpace(existingConfigWorkDir))
        {
            bool exists = directoryExists?.Invoke(existingConfigWorkDir) ?? true;
            if (exists)
            {
                return existingConfigWorkDir;
            }
        }

        if (selected is not null)
        {
            foreach (ProjectRef project in selected)
            {
                if (!string.IsNullOrWhiteSpace(project.Path))
                {
                    return project.Path;
                }
            }
        }

        if (discovered is not null)
        {
            ProjectEntry? first = discovered
                .Where(e => !string.IsNullOrWhiteSpace(e.Path))
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (first is not null)
            {
                return first.Path;
            }
        }

        return null;
    }

    /// <summary>
    /// 上游只有在 <c>agent.options.provider</c> 明确存在时才激活 provider 并读取模型表。
    /// 唯一兼容候选没有歧义,可以安全自动选;零个或多个候选都返回空,不得猜第一个。
    /// </summary>
    /// <remarks>仅为可测性公开:测试项目未配 InternalsVisibleTo。</remarks>
    public static (string Provider, string Model) ResolveUnambiguousProviderSelection(
        string existingToml,
        string agentType,
        IReadOnlyList<string> providerRefs,
        bool preserveAgentSelection)
    {
        if (preserveAgentSelection)
        {
            return (string.Empty, string.Empty);
        }

        CcConnectProviderDescriptor[] compatible = CcConnectProviderCatalog.Parse(existingToml).Providers
            .Where(provider => providerRefs.Contains(provider.Name, StringComparer.Ordinal))
            .ToArray();
        return compatible.Length == 1
            ? (compatible[0].Name, compatible[0].EffectiveModel(agentType))
            : (string.Empty, string.Empty);
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
