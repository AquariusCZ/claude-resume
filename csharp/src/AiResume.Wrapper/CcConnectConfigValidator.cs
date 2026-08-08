using System.Diagnostics;
using System.Text;

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

/// <summary>校验结果。<paramref name="Problems"/> 逐条可读,直接给用户看。</summary>
public sealed record CcConnectConfigCheck(
    CcConnectConfigState State,
    string Summary,
    IReadOnlyList<string> Problems);

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
                CcConnectConfigState.Missing, "尚未生成 cc-connect 配置。", Array.Empty<string>());
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new CcConnectConfigCheck(
                CcConnectConfigState.Unknown, $"配置读不出来:{ex.Message}", Array.Empty<string>());
        }

        IReadOnlyList<string> semantic = CheckSemantics(text);

        Func<string, (int, string)>? run = runner ?? BuildDefaultRunner();
        if (run is null)
        {
            // 找不到 cc-connect:语义还能查,语法查不了。**这不是 ok。**
            return semantic.Count > 0
                ? new CcConnectConfigCheck(CcConnectConfigState.Invalid, "配置有问题(未能用 cc-connect 复核语法)。", semantic)
                : new CcConnectConfigCheck(
                    CcConnectConfigState.Unknown,
                    "配置已存在,但本机找不到 cc-connect,无法确认它能否加载。",
                    Array.Empty<string>());
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
                CcConnectConfigState.Unknown, $"未能调用 cc-connect 复核:{ex.Message}", semantic);
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
                CcConnectConfigState.Invalid, "cc-connect 无法加载这份配置。", problems);
        }

        return semantic.Count > 0
            ? new CcConnectConfigCheck(CcConnectConfigState.Invalid, "语法能解析,但配置不完整。", semantic)
            : new CcConnectConfigCheck(
                CcConnectConfigState.Ok, "cc-connect 能加载这份配置。", Array.Empty<string>());
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
