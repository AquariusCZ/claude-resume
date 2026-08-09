using System.Diagnostics;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace AiResume.Wrapper;

/// <summary>配置文件此刻的可信状态。</summary>
public enum CcConnectConfigState
{
    /// <summary>文件不在。</summary>
    Missing,

    /// <summary>cc-connect 自己的解析器接受它,我们的语义不变量也满足。</summary>
    Ok,

    /// <summary>确证坏了:解析失败,或缺少必需的段/键。cc-connect 起不来或会放行所有人。</summary>
    Invalid,

    /// <summary>核对不了(找不到 cc-connect、调用失败)。**不是 ok**,只是没结论。</summary>
    Unknown,
}

/// <summary>
/// 校验结果。<paramref name="Problems"/> 与 <paramref name="Warnings"/> 都逐条可读,直接给用户看。
///
/// 两者**必须分开**:语法错/缺键会让 cc-connect 起不来(Problems);
/// 而 agent 与 provider 对不上是一份**完全合法、照常加载**的配置,只是行为不是用户以为的那样
/// (Warnings)。把后者也说成"配置无法加载"是另一种谎——用户会去查一个根本没坏的东西。
/// </summary>
public sealed record CcConnectConfigCheck(
    CcConnectConfigState State,
    string Summary,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 「cc-connect 配置已生成」凭什么这么说。
///
/// 原来的依据是 <c>File.Exists</c> 与写入没抛异常 —— 两者都只证明**我们做完了自己那步**,
/// 证明不了对面收得下。2026-08-08 第二轮审计把配置改坏,界面照旧显示"配置已生成"(A3)。
///
/// 契约要以对方的解析器为准,这在本项目已经付过一次学费:S6-A 时我们把 agent 写成字符串,
/// 自测全绿,真机上 cc-connect 直接拒绝加载(expected table but found string),
/// 因为当时的测试只断言了我们自己臆想的输出格式。
///
/// 所以这里**调 cc-connect 自己的 <c>config format</c>** 来判 TOML 是否成立,
/// 而不是手写一个 TOML 检查器去猜别人的解析器。手写的检查器有两种错法:
/// 漏判(等于没做)和误判(告诉用户一份能用的配置坏了)——后者更糟。
///
/// 三条纪律:
/// <list type="number">
/// <item><b>只校验副本。</b> <c>config format</c> 会重写文件;
///       生产配置里有用户手工维护的段落与注释,不能被一次"检查"改写。</item>
/// <item><b>不带 <c>--config</c> 前置全局标志。</b> 实测
///       <c>cc-connect --config X config format</c> 会先获取实例锁(走的是启动路径);
///       而飞书长连接是集群模式,多起一个消费者会把事件随机截走。
///       必须写成 <c>config format --config X</c>,标志在子命令之后。</item>
/// <item><b>核对不了就说核对不了。</b> 找不到 cc-connect 时返回 Unknown,不返回 Ok。</item>
/// </list>
/// </summary>
public static class CcConnectConfigValidator
{
    /// <summary>子进程超时。解析一份几十 KB 的 TOML 用不了这么久,超时即视为核对不了。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 我们自己的语义不变量。cc-connect 的解析器不会为这些抱怨 ——
    /// 一份语法完美但 <c>allow_from</c> 为空的配置能正常加载,然后放行所有飞书用户。
    /// 纯函数,离线可测。
    /// </summary>
    public static IReadOnlyList<string> CheckSemantics(string? toml)
    {
        var problems = new List<string>();
        string text = toml ?? string.Empty;

        if (!text.Contains("[[projects]]", StringComparison.Ordinal))
        {
            problems.Add("没有任何 [[projects]] 段:cc-connect 起来后一个项目都不认。");
        }

        if (!text.Contains("[projects.platforms.options]", StringComparison.Ordinal))
        {
            problems.Add("没有 [projects.platforms.options] 段:飞书凭据没有落点。");
        }

        foreach (string key in new[] { "app_id", "app_secret", "allow_from" })
        {
            string? value = ReadFirstStringValue(text, key);
            if (value is null)
            {
                problems.Add($"缺少 {key}。");
            }
            else if (value.Length == 0)
            {
                // allow_from 为空是**安全问题**而不是配置瑕疵,措辞要说出后果。
                problems.Add(key == "allow_from"
                    ? "allow_from 为空:cc-connect 会放行所有飞书用户,任何人都能驱动本机 AI 改你的项目。"
                    : $"{key} 为空。");
            }
        }

        return problems;
    }

    /// <summary>Claude Code 自己的模型别名。出现在非 claudecode 的项目里就是配错了。</summary>
    private static readonly HashSet<string> ClaudeAliases =
        new(StringComparer.OrdinalIgnoreCase) { "opus", "sonnet", "haiku", "fable" };

    /// <summary>
    /// agent、provider、model 三者对不对得上。
    ///
    /// 这三个是**同一条链上的三段**,任何一段错位,表现都是"回复还是原来那个模型",
    /// 而配置文件本身完全合法、cc-connect 也照常启动 —— 又一个静默失败:
    ///
    /// <list type="number">
    /// <item><b>agent</b> 决定说哪种 API 方言:claudecode 说 Anthropic 的形状,codex 说 OpenAI 的;</item>
    /// <item><b>provider</b> 是那个 agent 去连的端点 + 密钥,**必须和方言对得上**;</item>
    /// <item><b>model</b> 是发给那个端点的名字,必须是**那个端点认识的名字**。</item>
    /// </list>
    ///
    /// 2026-08-08 用户实测踩到的正是这个:把 agent 换成 codex、也重新生成了配置,
    /// 但 <c>provider = "deepseek"</c>(base_url 是 <c>…/anthropic</c>,给 claudecode 用的)
    /// 和 <c>model = "opus"</c>(Claude 的别名)都是当初 agent=claudecode 时留下的,
    /// **换 agent 不会重置它们**。于是三段里有两段还指着 Claude。
    ///
    /// 判据都取"确凿冲突"这一档,不猜:没写 agent_types、base_url 看不出方言的,一律不报。
    /// </summary>
    public static IReadOnlyList<string> CheckAgentCoherence(string? toml, string projectName = "")
    {
        var problems = new List<string>();
        string text = toml ?? string.Empty;
        if (text.Length == 0)
        {
            return problems;
        }

        // **必须按段取,不能取全文第一个。** 实测踩到:`[speech]` 段里有一行
        // `provider = ""`,而它在文件里排在项目区之前 —— 取全文第一个会拿到空串,
        // 于是整段 provider 判断被静默跳过,一条都不报。
        // 判据自己出这种错,比不做检查更糟:它看起来在检查。
        (string agent, string provider, string model) = ReadProjectAgentTriple(text, projectName);

        if (agent.Length == 0)
        {
            return problems;
        }

        if (provider.Length > 0)
        {
            CcConnectProviderDescriptor? descriptor;
            try
            {
                descriptor = CcConnectProviderCatalog.Parse(text).Find(provider);
            }
            catch (Exception)
            {
                return problems;
            }
            if (descriptor is null)
            {
                problems.Add(
                    $"项目选择了 provider「{provider}」,但全局 [[providers]] 中找不到它。");
            }
            else if (!descriptor.SupportsAgent(agent))
            {
                string endpoint = descriptor.Endpoints.TryGetValue(agent, out string? overrideEndpoint) &&
                    overrideEndpoint.Length > 0
                    ? overrideEndpoint
                    : descriptor.BaseUrl;
                string reason = descriptor.AgentTypes.Count > 0 &&
                    !descriptor.AgentTypes.Contains(agent, StringComparer.Ordinal)
                    ? $"它声明只支持 {string.Join(", ", descriptor.AgentTypes)}"
                    : $"它对 {agent} 的有效端点是 Anthropic 形状({endpoint})";
                problems.Add(
                    $"provider「{provider}」与当前 agent「{agent}」对不上:{reason}。");
            }
        }

        if (model.Length > 0 &&
            ClaudeAliases.Contains(model) &&
            !agent.Equals("claudecode", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                $"当前模型是「{model}」——这是 Claude Code 的别名,而 agent 是「{agent}」。" +
                "换 agent 不会重置模型;这是上一 agent 的残留选择,生成配置时应清除后再激活。");
        }

        return problems;
    }

    /// <summary>
    /// 从项目区读出 (agent 类型, 当前 provider, 当前 model)。
    ///
    /// 三个键分别落在不同的地方,只能逐段扫:
    /// <c>type</c> 紧跟在 <c>[projects.agent]</c> 之后(平台块里也有 <c>type</c>,不能混);
    /// <c>provider</c> / <c>model</c> 是 cc-connect 自己写回项目区的顶格键。
    /// 取最后一次出现:同名键重复时,后写的才是当前值。
    /// </summary>
    public static (string Agent, string Provider, string Model) ReadProjectAgentTriple(
        string toml,
        string projectName = "")
    {
        try
        {
            TomlTable root = TomlSerializer.Deserialize<TomlTable>(toml ?? string.Empty)
                ?? new TomlTable();
            if (!root.TryGetValue("projects", out object? rawProjects) ||
                rawProjects is not TomlTableArray projects)
            {
                return (string.Empty, string.Empty, string.Empty);
            }

            TomlTable? selected = projects.OfType<TomlTable>()
                .FirstOrDefault(project => projectName.Length == 0 ||
                    ReadTableString(project, "name").Equals(projectName, StringComparison.Ordinal));
            if (selected is null)
            {
                return (string.Empty, string.Empty, string.Empty);
            }

            TomlTable? agentTable = selected.TryGetValue("agent", out object? rawAgent) && rawAgent is TomlTable parsedAgent
                ? parsedAgent
                : null;
            TomlTable? options = agentTable is not null &&
                agentTable.TryGetValue("options", out object? rawOptions) && rawOptions is TomlTable parsedOptions
                ? parsedOptions
                : null;
            string provider = options is null ? string.Empty : ReadTableString(options, "provider");
            string model = options is null ? string.Empty : ReadTableString(options, "model");

            return (agentTable is null ? string.Empty : ReadTableString(agentTable, "type"), provider, model);
        }
        catch (Exception)
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    private static string ReadTableString(TomlTable table, string key) =>
        table.TryGetValue(key, out object? value) && value is string text ? text : string.Empty;

    /// <summary>
    /// 校验磁盘上的配置。<paramref name="runner"/> 供测试注入(返回退出码与合并输出)。
    /// </summary>
    public static CcConnectConfigCheck CheckFile(
        string path,
        Func<string, (int ExitCode, string Output)>? runner = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new CcConnectConfigCheck(
                CcConnectConfigState.Missing, "尚未生成 cc-connect 配置。", Array.Empty<string>(), Array.Empty<string>());
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new CcConnectConfigCheck(
                CcConnectConfigState.Unknown, $"配置读不出来:{ex.Message}", Array.Empty<string>(), Array.Empty<string>());
        }

        IReadOnlyList<string> semantic = CheckSemantics(text);
        // 一致性是**告警**不是错误:配置照常加载,只是行为不是用户以为的那样。
        IReadOnlyList<string> warnings = CheckAgentCoherence(text);

        Func<string, (int, string)>? run = runner ?? BuildDefaultRunner();
        if (run is null)
        {
            // 找不到 cc-connect:语义还能查,语法查不了。**这不是 ok。**
            return semantic.Count > 0
                ? new CcConnectConfigCheck(CcConnectConfigState.Invalid, "配置有问题(未能用 cc-connect 复核语法)。", semantic, warnings)
                : new CcConnectConfigCheck(
                    CcConnectConfigState.Unknown,
                    "配置已存在,但本机找不到 cc-connect,无法确认它能否加载。",
                    Array.Empty<string>(), warnings);
        }

        (int exitCode, string output) = (0, string.Empty);
        string? copy = null;
        try
        {
            // **只校验副本。** config format 会重写文件,而这份配置里有用户手工维护的
            // 段落与注释([management]、[log]、扫码绑定的微信平台……)。
            copy = Path.Combine(Path.GetTempPath(), "airesume-ccconfig-" + Guid.NewGuid().ToString("N") + ".toml");
            File.WriteAllText(copy, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            (exitCode, output) = run(copy);
        }
        catch (Exception ex)
        {
            return new CcConnectConfigCheck(
                CcConnectConfigState.Unknown, $"未能调用 cc-connect 复核:{ex.Message}", semantic, warnings);
        }
        finally
        {
            TryDelete(copy);
            // 校验副本时 cc-connect 可能在同目录留下锁文件,一并收走,不给别人留垃圾。
            if (copy is not null)
            {
                TryDelete(Path.Combine(
                    Path.GetDirectoryName(copy) ?? string.Empty, "." + Path.GetFileName(copy) + ".lock"));
            }
        }

        if (exitCode != 0)
        {
            var problems = new List<string> { FirstMeaningfulLine(output) };
            problems.AddRange(semantic);
            return new CcConnectConfigCheck(
                CcConnectConfigState.Invalid, "cc-connect 无法加载这份配置。", problems, warnings);
        }

        if (semantic.Count > 0)
        {
            return new CcConnectConfigCheck(
                CcConnectConfigState.Invalid, "语法能解析,但配置不完整。", semantic, warnings);
        }

        return new CcConnectConfigCheck(
            CcConnectConfigState.Ok,
            warnings.Count > 0 ? "配置能加载,但 agent 与 provider 对不上。" : "cc-connect 能加载这份配置。",
            Array.Empty<string>(), warnings);
    }

    /// <summary>定位 cc-connect 可执行文件;找不到返回 null。</summary>
    public static string? TryResolveExe()
    {
        foreach (string candidate in ExeCandidates())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 非法路径(PATH 里常有):跳过,继续找下一个。
            }
        }

        return null;
    }

    private static IEnumerable<string> ExeCandidates()
    {
        // npm 全局安装是本机的实际形态。放最前,命中率最高。
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "npm", "node_modules", "cc-connect", "bin", "cc-connect.exe");
        yield return Path.Combine(appData, "npm", "cc-connect.exe");

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        foreach (string p in (pathVar ?? string.Empty).Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                yield return Path.Combine(p.Trim(), "cc-connect.exe");
            }
        }
    }

    private static Func<string, (int, string)>? BuildDefaultRunner()
    {
        string? exe = TryResolveExe();
        if (exe is null)
        {
            return null;
        }

        return configPath =>
        {
            var psi = new ProcessStartInfo(exe)
            {
                // **标志必须在子命令之后。** 前置的 --config 会走启动路径并获取实例锁。
                ArgumentList = { "config", "format", "--config", configPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 临时目录当工作目录:不在用户的仓库里留下任何足迹
                // (探测足迹的教训见 docs/LESSONS.md §十)。
                WorkingDirectory = Path.GetTempPath(),
            };

            using Process p = Process.Start(psi)
                ?? throw new InvalidOperationException("cc-connect 未能启动。");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                throw new TimeoutException("cc-connect 复核超时。");
            }

            return (p.ExitCode, (stdout + "\n" + stderr).Trim());
        };
    }

    /// <summary>取输出里第一条像样的错误行。cc-connect 的错误信息本身已经很准确,原样转给用户。</summary>
    public static string FirstMeaningfulLine(string? output)
    {
        foreach (string line in (output ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (t.Length > 0)
            {
                return t;
            }
        }

        return "cc-connect 拒绝加载该配置(未给出原因)。";
    }

    /// <summary>
    /// 取某个键的第一个字符串值;键不存在返回 null,值为空串返回空串。
    /// 只认 <c>key = "…"</c> 这一种形状 —— 我们自己生成的就是这种,
    /// 认不出的形状交给 cc-connect 的解析器去判,这里不猜。
    /// </summary>
    public static string? ReadFirstStringValue(string toml, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            toml ?? string.Empty,
            @"^\s*" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*=\s*""(?<v>[^""]*)""",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响结论。
        }
    }
}
