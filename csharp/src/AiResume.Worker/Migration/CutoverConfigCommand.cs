using AiResume.Core;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;
using AiResume.Wrapper;

namespace AiResume.Worker.Migration;

/// <summary>
/// 从产品配置的项目清单生成 cc-connect 的 <c>config.toml</c>。
/// 唯一调用方是 <see cref="CcConnectApplyCommand"/>(GUI「生成并重启」与 CLI <c>cc-connect-apply</c>
/// 共用),凭据只从 DPAPI 取;本类不再提供只写文件、不验证、不重启的独立 CLI。
/// </summary>
public static class CutoverConfigCommand
{
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
                    "尚未配置飞书凭据与授权名单。请在控制面「飞书接入」里填写。",
                    0, null, null);
            }

            allowFrom = storedAllow;
        }

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            if (!new FeishuCredentialStore().TryLoad(out string storedId, out string storedSecret, out _))
            {
                return new GenerateResult(false,
                    "尚未配置飞书凭据。请在控制面「飞书接入」里填写。",
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
        // codex 走官方登录(B1):只服务 codex 的第三方 provider 删除——上游会拿它 ensureCodexAuth 改写 auth.json。
        // 其它 agent 不动这些 provider:它们与 claudecode 的运行无关,删了只会让用户的配置凭空少一块。
        bool codexOfficial = agentType.Equals("codex", StringComparison.Ordinal);
        IReadOnlyList<string> removedProviders = Array.Empty<string>();
        string withoutCodexProviders = existingToml;
        if (codexOfficial)
        {
            withoutCodexProviders = CcConnectConfigGenerator.RemoveCodexOnlyProviders(existingToml, out removedProviders);
            if (removedProviders.Count > 0 && File.Exists(outPath) && !TryBackupBeforeProviderRemoval(outPath, existingToml))
            {
                return new GenerateResult(false,
                    "需要删除只服务 codex 的第三方 provider,但无法在 config.toml 同目录写入备份,已中止生成。", 0, null, null);
            }
        }
        IReadOnlyList<string> providerRefs = codexOfficial
            ? Array.Empty<string>()
            : CcConnectConfigGenerator.ReadGlobalProviderNames(withoutCodexProviders, agentType);
        (string selectedProvider, string selectedModel) = codexOfficial
            ? (string.Empty, string.Empty)
            : ResolveUnambiguousProviderSelection(
                withoutCodexProviders, agentType, providerRefs, preserveAgentSelection);
        string codexCmd;
        try
        {
            codexCmd = codexOfficial ? OfficialCodexCommand() : string.Empty;
        }
        catch (InvalidOperationException ex)
        {
            return new GenerateResult(false, ex.Message, 0, null, null);
        }

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
                SelectedModel: selectedModel,
                Cmd: codexCmd),
        };

        var config = new CcConnectConfig(projects, new CcConnectPlatformOptions(appId, appSecret, allowFrom));

        try
        {
            CcConnectConfigGenerator.Write(
                outPath,
                config,
                // codex 走官方时,旧配置里残留的 provider/model 选择(sub2api 及其模型)必须清掉,
                // 否则 cc-connect 会拿着一个已被删除的 provider 名启动。
                preserveAgentSelection && !codexOfficial,
                requireLoadable ? ValidateCandidateForActivation : null,
                preserveInlineAgentProviders: sameAgent,
                removeCodexOnlyProviders: codexOfficial);
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
        if (removedProviders.Count > 0)
        {
            resetNote += $" 已删除只服务 codex 的第三方 provider:{string.Join("、", removedProviders)}(Codex 改走官方登录;原文件已备份为 config.toml.bak-ai-resume-<时间>,其中含飞书密钥与管理令牌,请勿外传;飞书会话若曾选择这些 provider,请用 /provider switch 重选)。";
        }
        if (codexOfficial)
        {
            resetNote += " codex 通过 cmd 固定官方端点,凭据由 Codex 自身的 auth.json 提供。";
        }
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

    /// <summary>
    /// 让 cc-connect 的 codex agent 走官方连接。上游 v1.4.1 以 <c>strings.Fields</c> 切分 <c>cmd</c>,
    /// 所以每个 <c>-c</c> 覆盖值里不能有空白;<c>-c</c> 是 codex 根命令的全局选项,放在 <c>exec</c> 前有效
    /// (2026-10-12 本机 0.148.0 实测 <c>codex -c … exec --help</c> 正常)。
    /// 端点按 auth.json 的登录方式选:ChatGPT 登录走 chatgpt.com/backend-api/codex,API key 走 api.openai.com/v1;
    /// 显式写 <c>openai_base_url</c> 是因为本机桥接可能已把它改成 127.0.0.1,内置 openai provider 会照单全收。
    /// </summary>
    public static string OfficialCodexCommand(string? baseUrl = null, string? codexHome = null)
    {
        // 未登录时不猜端点:ChatGPT 登录与 API key 走的是两个不同的官方地址,
        // 写错一个会让 cc-connect 的 codex agent 把以后的 API key 发往 ChatGPT backend。
        CodexProviderCredentials official = CodexAuthProbe.ReadOfficialCredentials(codexHome);
        string endpoint = baseUrl ?? (official.Problem is null && official.BaseUrl is { Length: > 0 } url
            ? url
            : throw new InvalidOperationException("Codex 未登录(auth.json 无有效登录),拒绝生成官方 cmd;请先运行 codex login 再生成"));
        if (endpoint.Any(char.IsWhiteSpace) || endpoint.Contains('"'))
        {
            throw new InvalidOperationException("Codex 官方端点含空白或引号,无法写入 cc-connect cmd");
        }

        return "codex -c model_provider=\"openai\" -c openai_base_url=\"" + endpoint + "\"";
    }

    /// <summary>
    /// 删除 provider 前给生产文件留一份带时间戳的备份。同一份内容只备份一次——激活失败重试不该每秒堆一个含密钥的文件。
    /// 返回 false 表示备份没写成;调用方据此中止删除,而不是带着「尽力而为」继续。
    /// </summary>
    private static bool TryBackupBeforeProviderRemoval(string outPath, string existingToml)
    {
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? string.Empty;
            byte[] content = new System.Text.UTF8Encoding(false).GetBytes(existingToml);
            string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
            foreach (string existing in Directory.EnumerateFiles(directory, "config.toml.bak-ai-resume-*"))
            {
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(existing))) == digest)
                {
                    return true;
                }
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string backup = Path.Combine(directory, "config.toml.bak-ai-resume-" + stamp);
            for (int n = 2; File.Exists(backup) && n < 100; n++)
            {
                backup = Path.Combine(directory, "config.toml.bak-ai-resume-" + stamp + "-" + n);
            }

            using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
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
