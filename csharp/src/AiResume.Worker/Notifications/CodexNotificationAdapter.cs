using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiResume.Worker.Notifications;

/// <summary>
/// Codex 通知适配器。
/// 管理 %USERPROFILE%\.codex\config.toml 中的 notify 数组,
/// 支持链式包装既有 notify(--previous-notify)并安全还原。
/// </summary>
public sealed class CodexNotificationAdapter : INotificationAdapter
{
    /// <summary>标记文件名,用于识别我方命令。</summary>
    public const string MarkerFileName = "AiResume.Hook.exe";

    /// <summary>递归处理 --previous-notify 链的最大深度。</summary>
    private const int MaxChainDepth = 8;

    /// <summary>单行 notify 数组匹配正则:捕获前缀、数组文本、行尾后缀。</summary>
    private static readonly Regex NotifyLineRegex = new(
        @"^(?<prefix>[ \t]*(?:notify|""notify""|'notify')[ \t]*=[ \t]*)(?<array>\[.*?\])(?<suffix>[ \t]*(?:#.*)?)$",
        RegexOptions.Compiled);

    /// <summary>
    /// notify 键的宽松匹配(不要求单行数组)。**定位必须用它,不能用 NotifyLineRegex**——
    /// 否则多行/非数组形式的 notify 会被判定为「不存在」,进而追加出第二个 notify 键,
    /// 使配置出现同名重复键、行为未定义。定位到之后再用 NotifyLineRegex 校验形态。
    /// </summary>
    private static readonly Regex NotifyKeyRegex = new(
        @"^[ \t]*(?:notify|""notify""|'notify')[ \t]*=",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析 notify 数组文本。TOML 基本字符串与 JSON 转义规则一致(反斜杠须写成 \\),
    /// 遇到非法转义等情况时把底层 JsonException 转成说明性异常,避免把实现细节泄露给调用方。
    /// </summary>
    private static string[] ParseNotifyArray(string arrayText)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(arrayText) ?? Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"notify 数组无法解析(TOML 基本字符串需按 JSON 规则转义,如路径中的反斜杠须写成 \\\\):{ex.Message}", ex);
        }
    }

    private readonly string _configPath;
    private readonly string _configDir;

    /// <summary>使用默认配置路径(%USERPROFILE%\.codex\config.toml)。</summary>
    public CodexNotificationAdapter()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "config.toml"))
    {
    }

    /// <summary>使用指定配置路径(测试用)。</summary>
    public CodexNotificationAdapter(string configPath)
    {
        _configPath = configPath;
        _configDir = Path.GetDirectoryName(configPath) ?? string.Empty;
    }

    public NotificationProviderKind Kind => NotificationProviderKind.Codex;

    public string DisplayName => "Codex";

    /// <inheritdoc />
    public NotificationProviderStatus Probe()
    {
        try
        {
            // 目录不存在即视为未安装
            if (!Directory.Exists(_configDir))
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, IsInstalled: false, IsEnabled: false,
                    ConfigPath: null, Detail: "Codex 配置目录不存在");
            }

            // 文件不存在视为已安装但未启用
            if (!File.Exists(_configPath))
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, IsInstalled: true, IsEnabled: false,
                    ConfigPath: _configPath, Detail: "config.toml 不存在,未启用");
            }

            // 解析顶部区 notify
            var lines = File.ReadAllLines(_configPath);
            var topSection = ExtractTopSection(lines);
            var notifyLine = FindNotifyLine(topSection);

            if (notifyLine == null)
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, IsInstalled: true, IsEnabled: false,
                    ConfigPath: _configPath, Detail: "顶部区无 notify 配置");
            }

            var match = NotifyLineRegex.Match(notifyLine);
            if (!match.Success)
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, IsInstalled: true, IsEnabled: false,
                    ConfigPath: _configPath, Detail: "notify 不是单行数组形式");
            }

            var arrayText = match.Groups["array"].Value;
            var existing = ParseNotifyArray(arrayText);
            string? ownCommand = FindOwnCommand(existing, 0);
            bool isEnabled = ownCommand is not null;

            return new NotificationProviderStatus(
                Kind, DisplayName, IsInstalled: true, IsEnabled: isEnabled,
                ConfigPath: _configPath, Detail: isEnabled ? "已安装 AI Resume 通知钩子" : "未安装 AI Resume 通知钩子",
                HookCommand: ownCommand);
        }
        catch (Exception ex)
        {
            // 任何异常都不抛出,记录到 Detail
            return new NotificationProviderStatus(
                Kind, DisplayName, IsInstalled: false, IsEnabled: false,
                ConfigPath: _configPath, Detail: $"探测异常: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Enable(string hookCommand)
    {
        // **不能按空格切。** hookCommand 是一条**路径**,不是命令行。
        //
        // 2026-08-08 实测事故:安装目录从 ClaudeResume 改名成含空格的 "AI Resume" 后,
        // 原来的 `Split(' ')[0]` 把
        //   C:\Users\…\AppData\Local\AI Resume\AiResume.Hook.exe
        // 截成
        //   C:\Users\…\AppData\Local\AI
        // 写进了用户的 config.toml。截断后的条目不含标记 AiResume.Hook.exe,
        // 于是下一次启用认不出它是自己写的,**再套一层而不是替换** ——
        // 套到第 8 层撞上 MaxChainDepth,适配器彻底罢工,
        // 界面只显示「notify 链深度超过上限」。整行长到 9909 字符。
        // 这行代码写于目录名还没有空格的年代,改名那天它就静默失效了。
        //
        // 改按 **.exe 边界**切,不按空格、也不碰磁盘:
        //   "C:\A B\hook.exe codex"  → "C:\A B\hook.exe"   (路径含空格 + 有参数)
        //   "C:\A B\hook.exe"        → "C:\A B\hook.exe"   (路径含空格 + 无参数)
        //   "C:\t\hook.exe codex"    → "C:\t\hook.exe"
        // 确定性、不依赖文件是否存在,所以离线测试与真机行为一致。
        var hookExe = ExtractHookExe(hookCommand);

        // 确保目录存在
        Directory.CreateDirectory(_configDir);

        // 读取现有内容
        string[] lines;
        if (File.Exists(_configPath))
        {
            lines = File.ReadAllLines(_configPath);
        }
        else
        {
            lines = Array.Empty<string>();
        }

        // 提取顶部区
        var topSection = ExtractTopSection(lines);
        var notifyLineIndex = FindNotifyLineIndex(topSection);

        // 构造新数组
        string[] newArray;
        if (notifyLineIndex >= 0)
        {
            var match = NotifyLineRegex.Match(topSection[notifyLineIndex]);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    "notify 配置不是单行数组形式,无法安全修改。请手动编辑 config.toml 或删除该行后重试。");
            }

            var arrayText = match.Groups["array"].Value;
            var existing = ParseNotifyArray(arrayText);
            newArray = MergeNotify(existing, hookExe);
        }
        else
        {
            // 无既有 notify,直接创建我方命令
            newArray = new[] { hookExe, "codex" };
        }

        // 构造新文件内容
        var newLines = new StringBuilder();
        var topLineCount = topSection.Length;

        for (int i = 0; i < lines.Length; i++)
        {
            if (i < topLineCount)
            {
                if (i == notifyLineIndex)
                {
                    // 替换 notify 行,保留前缀和后缀
                    var match = NotifyLineRegex.Match(lines[i]);
                    var prefix = match.Groups["prefix"].Value;
                    var suffix = match.Groups["suffix"].Value;
                    var newArrayText = JsonSerializer.Serialize(newArray);
                    newLines.Append(prefix).Append(newArrayText).Append(suffix).Append("\r\n");
                }
                else
                {
                    newLines.Append(lines[i]).Append("\r\n");
                }
            }
            else
            {
                newLines.Append(lines[i]).Append("\r\n");
            }
        }

        // 如果顶部区没有 notify 行,追加
        if (notifyLineIndex < 0)
        {
            var newArrayText = JsonSerializer.Serialize(newArray);
            newLines.Append("notify = ").Append(newArrayText).Append("\r\n");
        }

        // 写入前备份
        if (File.Exists(_configPath))
        {
            File.Copy(_configPath, _configPath + ".bak", overwrite: true);
        }

        // 原子替换写入
        var tempFile = _configPath + ".tmp";
        File.WriteAllText(tempFile, newLines.ToString(), new UTF8Encoding(false));
        File.Move(tempFile, _configPath, overwrite: true);
    }

    /// <inheritdoc />
    public void Disable()
    {
        if (!File.Exists(_configPath))
        {
            return; // 文件不存在,无事可做
        }

        var lines = File.ReadAllLines(_configPath);
        var topSection = ExtractTopSection(lines);
        var notifyLineIndex = FindNotifyLineIndex(topSection);

        if (notifyLineIndex < 0)
        {
            return; // 无 notify 行
        }

        var match = NotifyLineRegex.Match(topSection[notifyLineIndex]);
        if (!match.Success)
        {
            return; // 非单行数组,不处理
        }

        var arrayText = match.Groups["array"].Value;
        var existing = ParseNotifyArray(arrayText);

        // 只有可验证的我方命令层才允许摘除;参数里碰巧出现文件名不构成所有权。
        if (!HasOwnInChain(existing, 0))
        {
            return; // 不含我方标记,不做任何事
        }

        // 摘除我方层,提升 previous
        var restored = RemoveOwnLayer(existing);

        // 构造新文件内容
        var newLines = new StringBuilder();
        var topLineCount = topSection.Length;

        for (int i = 0; i < lines.Length; i++)
        {
            if (i < topLineCount)
            {
                if (i == notifyLineIndex)
                {
                    if (restored == null)
                    {
                        // 无 previous,删除整行
                        continue;
                    }
                    else
                    {
                        // 替换为还原后的数组
                        var prefix = match.Groups["prefix"].Value;
                        var suffix = match.Groups["suffix"].Value;
                        var newArrayText = JsonSerializer.Serialize(restored);
                        newLines.Append(prefix).Append(newArrayText).Append(suffix).Append("\r\n");
                    }
                }
                else
                {
                    newLines.Append(lines[i]).Append("\r\n");
                }
            }
            else
            {
                newLines.Append(lines[i]).Append("\r\n");
            }
        }

        // 写入前备份
        File.Copy(_configPath, _configPath + ".bak", overwrite: true);

        // 原子替换写入
        var tempFile = _configPath + ".tmp";
        File.WriteAllText(tempFile, newLines.ToString(), new UTF8Encoding(false));
        File.Move(tempFile, _configPath, overwrite: true);
    }

    /// <summary>
    /// 合并 notify 数组(§3 合并算法)。
    /// </summary>
    private static string[] MergeNotify(string[] existing, string hookExe)
    {
        // 情况 1:刷新已托管链
        if (HasOwnInChain(existing, 0))
        {
            return RefreshChain(existing, hookExe, 0);
        }

        // 情况 2:Codex Desktop wrapper 特判
        if (existing.Length > 0)
        {
            var firstExe = existing[0];
            var fileName = Path.GetFileName(firstExe).ToLowerInvariant();
            if (fileName == "codex-computer-use.exe" || fileName == "cod-use.exe")
            {
                return WrapDesktopWrapper(existing, hookExe);
            }
        }

        // 情况 3:批处理拒绝
        if (existing.Length > 0)
        {
            var firstExe = existing[0];
            if (firstExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                firstExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "批处理 notify 链无法安全包装,请保留既有 notify 或改用可执行文件。");
            }
        }

        // 其余情况:用我方命令包装 existing。即使用户参数文本中出现
        // AiResume.Hook.exe 也只作为普通 previous 保留,不会被误删或误刷新。
        var previousJson = JsonSerializer.Serialize(existing);
        return new[] { hookExe, "codex", "--previous-notify", previousJson };
    }

    /// <summary>
    /// 从 hookCommand 里取出可执行文件路径。**按 <c>.exe</c> 边界切,不按空格。**
    ///
    /// 2026-08-08 实测事故:原实现是 <c>hookCommand.Split(' ')[0]</c>。
    /// 安装目录从 <c>ClaudeResume</c> 改名成含空格的 <c>AI Resume</c> 之后,
    /// <c>C:\Users\…\Local\AI Resume\AiResume.Hook.exe</c> 被截成
    /// <c>C:\Users\…\Local\AI</c> 写进用户配置。截断后的条目不含标记,
    /// 下一次启用认不出是自己写的,**再套一层而不是替换**,
    /// 套到第 8 层撞上 MaxChainDepth 后适配器彻底罢工(实测那一行 9909 字符)。
    /// 那行代码写于目录名还没有空格的年代,改名当天它就静默失效了。
    ///
    /// 不用 File.Exists 判断,是为了让离线测试与真机走同一条分支。
    /// </summary>
    /// <summary>
    /// 从 notify 数组里含标记的那一项,取出我方那条命令的**路径原文**。
    ///
    /// 不能直接把那一项当路径用:**我方条目可能被别人的 wrapper 包住**。
    /// 实测本机(2026-08-08)是 Codex 自己的 <c>codex-computer-use.exe</c> 占了第 0 位,
    /// 把我们整个塞进它的 <c>--previous-notify</c> 里;于是含标记的那一项
    /// 是一段 <b>JSON 数组文本</b>,首元素才是我方 exe。
    /// 直接拿它去 File.Exists,得到的是 <c>["C:\…\AiResume.Hook.exe</c> 这种带方括号的残缺路径,
    /// 面板会红着说"钩子断链" —— 而钩子其实好好的。
    /// **误判比漏判更糟:它让人去修一个没坏的东西。**
    ///
    /// 所以这里逐层往里剥,直到拿到不是数组的那一层。剥不动就返回 null
    /// (交给上层按「核对不了」处理,不按「坏了」处理)。
    /// </summary>
    public static string? ResolveOwnCommand(string? element, int depth = 0)
    {
        string s = element?.Trim() ?? string.Empty;
        if (s.Length == 0)
        {
            return null;
        }

        // 不是数组形状时也必须验证“首个 exe + codex 参数”的完整命令形状。
        if (!s.StartsWith('['))
        {
            return HookCommand.IsManaged(s, MarkerFileName, "codex")
                ? HookCommand.ExtractExecutable(s)
                : null;
        }

        // MaxChainDepth 同源的保险:链子理论上可以套很深,但不该无限递归。
        if (depth >= MaxChainDepth)
        {
            return null;
        }

        try
        {
            string[]? inner = JsonSerializer.Deserialize<string[]>(s);
            return inner is null ? null : FindOwnCommand(inner, depth + 1);
        }
        catch (JsonException)
        {
            // 形状认不出来:说"核对不了",不说"坏了"。
            return null;
        }
    }

    public static string ExtractHookExe(string? hookCommand)
    {
        string s = hookCommand?.Trim() ?? string.Empty;
        if (s.Length == 0)
        {
            throw new ArgumentException("hookCommand 不能为空", nameof(hookCommand));
        }

        return HookCommand.ExtractExecutable(s) ?? s;
    }

    /// <summary>
    /// 兼容旧测试/调用入口。没有不可伪造的所有权证据时,任何 notify 命令都必须保留。
    /// 历史上 <c>["%LOCALAPPDATA%\\AI", "codex"]</c> 可能是旧版本写坏的路径,
    /// 也可能是用户自己的离线命令;仅凭形状无法区分,因此不再自动删除。
    /// </summary>
    public static string[] PruneDeadLinks(string[] array, Func<string, bool>? fileExists = null)
    {
        _ = fileExists;
        return (string[])array.Clone();
    }

    /// <summary>判断当前数组层是否为我方命令。</summary>
    private static bool IsOwnCommand(string[] array)
    {
        return array.Length >= 2 &&
               HookCommand.IsManaged($"\"{array[0]}\" {array[1]}", MarkerFileName, "codex");
    }

    private static string? FindOwnCommand(string[] array, int depth)
    {
        if (depth >= MaxChainDepth)
        {
            return null;
        }

        if (IsOwnCommand(array))
        {
            return HookCommand.ExtractExecutable(array[0]);
        }

        for (int i = 0; i < array.Length - 1; i++)
        {
            if (!string.Equals(array[i], "--previous-notify", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                string[]? previous = JsonSerializer.Deserialize<string[]>(array[i + 1]);
                string? own = previous is null ? null : FindOwnCommand(previous, depth + 1);
                if (own is not null)
                {
                    return own;
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    /// <summary>递归检查链中是否含我方命令。</summary>
    private static bool HasOwnInChain(string[] array, int depth)
    {
        if (depth >= MaxChainDepth)
        {
            throw new InvalidOperationException("notify 链深度超过上限,无法安全处理。");
        }

        if (IsOwnCommand(array))
        {
            return true;
        }

        // 查找 --previous-notify
        for (int i = 0; i < array.Length - 1; i++)
        {
            if (array[i] == "--previous-notify")
            {
                try
                {
                    var previous = JsonSerializer.Deserialize<string[]>(array[i + 1]);
                    if (previous != null && HasOwnInChain(previous, depth + 1))
                    {
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // 无法解析的 previous,视为不含我方命令
                }
            }
        }

        return false;
    }

    /// <summary>刷新已托管链中的 exe 路径。</summary>
    private static string[] RefreshChain(string[] array, string hookExe, int depth)
    {
        if (depth >= MaxChainDepth)
        {
            throw new InvalidOperationException("notify 链深度超过上限,无法安全处理。");
        }

        // 如果当前层是我方命令,更新 exe 路径
        if (IsOwnCommand(array))
        {
            var result = (string[])array.Clone();
            result[0] = hookExe;
            return result;
        }

        // 递归下探 --previous-notify
        for (int i = 0; i < array.Length - 1; i++)
        {
            if (array[i] == "--previous-notify")
            {
                try
                {
                    var previous = JsonSerializer.Deserialize<string[]>(array[i + 1]);
                    if (previous != null && HasOwnInChain(previous, depth + 1))
                    {
                        var refreshed = RefreshChain(previous, hookExe, depth + 1);
                        var result = (string[])array.Clone();
                        result[i + 1] = JsonSerializer.Serialize(refreshed);
                        return result;
                    }
                }
                catch (JsonException)
                {
                    // 无法解析,继续
                }
            }
        }

        return array;
    }

    /// <summary>包装 Codex Desktop wrapper。</summary>
    private static string[] WrapDesktopWrapper(string[] existing, string hookExe)
    {
        var result = (string[])existing.Clone();

        // 查找 --previous-notify
        for (int i = 0; i < result.Length - 1; i++)
        {
            if (result[i] == "--previous-notify")
            {
                // 把我们的命令包到那一层的内部
                try
                {
                    var previous = JsonSerializer.Deserialize<string[]>(result[i + 1]);
                    if (previous != null)
                    {
                        var wrapped = MergeNotify(previous, hookExe);
                        result[i + 1] = JsonSerializer.Serialize(wrapped);
                        return result;
                    }
                }
                catch (JsonException)
                {
                    // 无法解析,继续
                }
            }
        }

        // 没有 --previous-notify,在末尾追加
        var myCommand = JsonSerializer.Serialize(new[] { hookExe, "codex" });
        var newResult = new string[result.Length + 2];
        Array.Copy(result, newResult, result.Length);
        newResult[result.Length] = "--previous-notify";
        newResult[result.Length + 1] = myCommand;
        return newResult;
    }

    /// <summary>摘除我方层,提升 previous。</summary>
    private static string[]? RemoveOwnLayer(string[] array)
    {
        // 如果当前层是我方命令
        if (IsOwnCommand(array))
        {
            // 查找 --previous-notify
            for (int i = 0; i < array.Length - 1; i++)
            {
                if (array[i] == "--previous-notify")
                {
                    try
                    {
                        return JsonSerializer.Deserialize<string[]>(array[i + 1]);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
            }
            return null; // 无 previous,删除整行
        }

        // 递归下探 --previous-notify
        for (int i = 0; i < array.Length - 1; i++)
        {
            if (array[i] == "--previous-notify")
            {
                try
                {
                    var previous = JsonSerializer.Deserialize<string[]>(array[i + 1]);
                    if (previous != null && HasOwnInChain(previous, 0))
                    {
                        var restored = RemoveOwnLayer(previous);
                        if (restored == null)
                        {
                            // 移除 --previous-notify 参数
                            var result = new string[array.Length - 2];
                            Array.Copy(array, 0, result, 0, i);
                            Array.Copy(array, i + 2, result, i, array.Length - i - 2);
                            return result;
                        }
                        else
                        {
                            var result = (string[])array.Clone();
                            result[i + 1] = JsonSerializer.Serialize(restored);
                            return result;
                        }
                    }
                }
                catch (JsonException)
                {
                    // 无法解析,继续
                }
            }
        }

        return array;
    }

    /// <summary>提取顶部区(首个 [section] 之前的内容)。</summary>
    private static string[] ExtractTopSection(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return lines.Take(i).ToArray();
            }
        }
        return lines;
    }

    /// <summary>在顶部区查找 notify 行索引。</summary>
    private static int FindNotifyLineIndex(string[] topSection)
    {
        for (int i = 0; i < topSection.Length; i++)
        {
            // 用宽松正则定位:多行或非数组形式的 notify 也必须被发现,
            // 由调用方用 NotifyLineRegex 判定形态并在不可安全处理时拒绝。
            if (NotifyKeyRegex.IsMatch(topSection[i]))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>在顶部区查找 notify 行(返回行内容)。</summary>
    private static string? FindNotifyLine(string[] topSection)
    {
        var index = FindNotifyLineIndex(topSection);
        return index >= 0 ? topSection[index] : null;
    }
}
