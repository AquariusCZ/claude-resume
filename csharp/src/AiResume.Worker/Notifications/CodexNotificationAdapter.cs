using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiResume.Worker.Probes;
using Tomlyn;
using Tomlyn.Model;

namespace AiResume.Worker.Notifications;

/// <summary>
/// Codex 通知适配器。
/// 管理活动 Codex home 下 config.toml 中的 notify 数组,
/// 支持链式包装既有 notify(--previous-notify)并安全还原。
/// </summary>
public sealed class CodexNotificationAdapter : INotificationAdapter
{
    /// <summary>标记文件名,用于识别我方命令。</summary>
    public const string MarkerFileName = "AiResume.Hook.exe";

    /// <summary>递归处理 --previous-notify 链的最大深度。</summary>
    private const int MaxChainDepth = 8;

    private const int WriteLockTimeoutSeconds = 10;
    private const int ConflictRestoreAttempts = 4;

    /// <summary>单行 notify 赋值前缀。数组边界必须由 TOML 字符串感知扫描器定位。</summary>
    private static readonly Regex NotifyPrefixRegex = new(
        @"^(?<prefix>[ \t]*(?:notify|""notify""|'notify')[ \t]*=[ \t]*)",
        RegexOptions.Compiled);

    /// <summary>
    /// notify 键的宽松匹配(不要求单行数组)。**定位必须用它,不能用 NotifyPrefixRegex**——
    /// 否则多行/非数组形式的 notify 会被判定为「不存在」,进而追加出第二个 notify 键,
    /// 使配置出现同名重复键、行为未定义。定位到之后再用 TrySplitNotifyLine 校验形态。
    /// </summary>
    private static readonly Regex NotifyKeyRegex = new(
        @"^[ \t]*(?:notify|""notify""|'notify')[ \t]*=",
        RegexOptions.Compiled);

    /// <summary>
    /// TOML 表头必须是合法 key path 的 <c>[table]</c> 或 <c>[[array.of.tables]]</c>。
    /// 不能只看首字符:顶层数组的续行也可能以 <c>[</c> 开头。
    /// </summary>
    private static readonly Regex TableHeaderRegex = new(
        @"^[ \t]*(?:\[[ \t]*(?:[A-Za-z0-9_-]+|""(?:\\.|[^""\\])*""|'[^']*')(?:[ \t]*\.[ \t]*(?:[A-Za-z0-9_-]+|""(?:\\.|[^""\\])*""|'[^']*'))*[ \t]*\]|\[\[[ \t]*(?:[A-Za-z0-9_-]+|""(?:\\.|[^""\\])*""|'[^']*')(?:[ \t]*\.[ \t]*(?:[A-Za-z0-9_-]+|""(?:\\.|[^""\\])*""|'[^']*'))*[ \t]*\]\])[ \t]*(?:#.*)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析 notify 数组文本。上游配置允许 TOML basic/literal string 混用,
    /// 因此不能用 JSON 解析器替代 TOML 解析器。
    /// </summary>
    private static string[] ParseNotifyArray(string arrayText)
    {
        try
        {
            TomlTable root = TomlSerializer.Deserialize<TomlTable>("notify = " + arrayText)
                ?? new TomlTable();
            if (!root.TryGetValue("notify", out object? value) || value is not TomlArray array)
            {
                throw new InvalidOperationException("notify 必须是 TOML 数组。");
            }

            var result = new string[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is not string item)
                {
                    throw new InvalidOperationException("notify 数组只能包含字符串。");
                }

                result[i] = item;
            }

            return result;
        }
        catch (TomlException ex)
        {
            throw new InvalidOperationException(
                "notify 数组无法解析为 TOML 字符串数组。", ex);
        }
    }

    /// <summary>
    /// 安全切分顶层单行 notify。不能用 <c>\[.*?\]</c>:previous-notify 的 JSON 文本
    /// 本身含有方括号,且 TOML literal string 不使用 JSON 转义。
    /// </summary>
    private static bool TrySplitNotifyLine(
        string line,
        out string prefix,
        out string arrayText,
        out string suffix)
    {
        prefix = string.Empty;
        arrayText = string.Empty;
        suffix = string.Empty;

        Match prefixMatch = NotifyPrefixRegex.Match(line);
        if (!prefixMatch.Success)
        {
            return false;
        }

        int arrayStart = prefixMatch.Length;
        if (arrayStart >= line.Length || line[arrayStart] != '[')
        {
            return false;
        }

        int depth = 0;
        TomlStringMode stringMode = TomlStringMode.None;
        bool escaped = false;
        for (int i = arrayStart; i < line.Length; i++)
        {
            char c = line[i];
            switch (stringMode)
            {
                case TomlStringMode.Basic:
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        stringMode = TomlStringMode.None;
                    }
                    continue;
                case TomlStringMode.Literal:
                    if (c == '\'')
                    {
                        stringMode = TomlStringMode.None;
                    }
                    continue;
                case TomlStringMode.MultilineBasic:
                case TomlStringMode.MultilineLiteral:
                    return false;
            }

            if (c == '#')
            {
                return false;
            }

            if (c == '"')
            {
                if (i + 2 < line.Length && line[i + 1] == '"' && line[i + 2] == '"')
                {
                    return false;
                }

                stringMode = TomlStringMode.Basic;
                escaped = false;
                continue;
            }

            if (c == '\'')
            {
                if (i + 2 < line.Length && line[i + 1] == '\'' && line[i + 2] == '\'')
                {
                    return false;
                }

                stringMode = TomlStringMode.Literal;
                continue;
            }

            if (c == '[')
            {
                depth++;
                continue;
            }

            if (c != ']')
            {
                continue;
            }

            depth--;
            if (depth < 0)
            {
                return false;
            }

            if (depth != 0)
            {
                continue;
            }

            string tail = line[(i + 1)..];
            int commentIndex = tail.IndexOf('#');
            string whitespace = commentIndex >= 0 ? tail[..commentIndex] : tail;
            if (whitespace.Any(c2 => c2 is not (' ' or '\t')))
            {
                return false;
            }

            prefix = prefixMatch.Groups["prefix"].Value;
            arrayText = line[arrayStart..(i + 1)];
            suffix = tail;
            return true;
        }

        return false;
    }

    private readonly string _configPath;
    private readonly string _configDir;
    private readonly string _writeLockName;
    private readonly Action? _beforeAtomicReplace;
    private readonly Action<int>? _beforeConflictRestore;

    /// <summary>使用默认配置路径(%USERPROFILE%\.codex\config.toml)。</summary>
    public CodexNotificationAdapter()
        : this(Path.Combine(CodexAuthProbe.ResolveCodexHome(), "config.toml"))
    {
    }

    /// <summary>使用指定配置路径(测试用)。</summary>
    public CodexNotificationAdapter(string configPath)
        : this(configPath, beforeAtomicReplace: null, beforeConflictRestore: null)
    {
    }

    /// <summary>使用指定配置路径，并可注入原子替换前动作以验证竞态恢复。</summary>
    public CodexNotificationAdapter(string configPath, Action? beforeAtomicReplace)
        : this(configPath, beforeAtomicReplace, beforeConflictRestore: null)
    {
    }

    /// <summary>使用指定配置路径，并可注入两个替换窗口的动作以验证竞态恢复。</summary>
    public CodexNotificationAdapter(
        string configPath,
        Action? beforeAtomicReplace,
        Action<int>? beforeConflictRestore)
    {
        _configPath = configPath;
        _configDir = Path.GetDirectoryName(configPath) ?? string.Empty;
        string normalizedPath = Path.GetFullPath(configPath).ToUpperInvariant();
        string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        _writeLockName = @"Local\AIResume.CodexNotify." + pathHash;
        _beforeAtomicReplace = beforeAtomicReplace;
        _beforeConflictRestore = beforeConflictRestore;
    }

    public NotificationProviderKind Kind => NotificationProviderKind.Codex;

    public string DisplayName => "Codex";

    /// <inheritdoc />
    public NotificationProviderStatus Probe()
    {
        bool isInstalled = false;
        try
        {
            // 目录不存在即视为未安装
            if (!Directory.Exists(_configDir))
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, IsInstalled: false, IsEnabled: false,
                    ConfigPath: null, Detail: "Codex 配置目录不存在");
            }
            isInstalled = true;

            // 文件不存在视为已安装但未启用
            if (!File.Exists(_configPath))
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, IsInstalled: true, IsEnabled: false,
                    ConfigPath: _configPath, Detail: "config.toml 不存在,未启用");
            }

            // 解析顶部区 notify
            var lines = File.ReadAllLines(_configPath);
            string? misplacedOwnCommand = FindMisplacedOwnCommand(lines);
            var topSection = ExtractTopSection(lines);
            var notifyLine = FindNotifyLine(topSection);

            if (notifyLine == null)
            {
                if (misplacedOwnCommand is not null)
                {
                    return MisplacedOwnedNotifyStatus(misplacedOwnCommand);
                }

                return new NotificationProviderStatus(
                    Kind, DisplayName, IsInstalled: true, IsEnabled: false,
                    ConfigPath: _configPath, Detail: "顶部区无 notify 配置");
            }

            if (!TrySplitNotifyLine(notifyLine, out _, out string arrayText, out _))
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, IsInstalled: true, IsEnabled: false,
                    ConfigPath: _configPath, Detail: "notify 不是单行数组形式");
            }

            var existing = ParseNotifyArray(arrayText);
            string? ownCommand = FindOwnCommand(existing, 0);
            bool isEnabled = ownCommand is not null;

            if (misplacedOwnCommand is not null)
            {
                return MisplacedOwnedNotifyStatus(ownCommand ?? misplacedOwnCommand);
            }

            return new NotificationProviderStatus(
                Kind, DisplayName, IsInstalled: true, IsEnabled: isEnabled,
                ConfigPath: _configPath,
                Detail: isEnabled
                    ? "已安装 AI Resume 通知钩子;配置变更后需重启已运行的 Codex 客户端"
                    : "未安装 AI Resume 通知钩子",
                HookCommand: ownCommand);
        }
        catch (Exception ex)
        {
            // 任何异常都不抛出,记录到 Detail
            return new NotificationProviderStatus(
                Kind, DisplayName, IsInstalled: isInstalled, IsEnabled: false,
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
        using IDisposable writeLease = AcquireWriteLease();

        // 读取现有内容
        string? originalContent = ReadConfigContent();
        string[] lines = SplitLines(originalContent);
        lines = NormalizeMisplacedOwnedNotifyLines(lines);

        // 提取顶部区
        var topSection = ExtractTopSection(lines);
        var notifyLineIndex = FindNotifyLineIndex(topSection);

        // 构造新数组
        string[] newArray;
        string? notifyPrefix = null;
        string? notifySuffix = null;
        if (notifyLineIndex >= 0)
        {
            if (!TrySplitNotifyLine(
                    topSection[notifyLineIndex],
                    out notifyPrefix,
                    out string arrayText,
                    out notifySuffix))
            {
                throw new InvalidOperationException(
                    "notify 配置不是单行数组形式,无法安全修改。请手动编辑 config.toml 或删除该行后重试。");
            }

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
                    var newArrayText = JsonSerializer.Serialize(newArray);
                    newLines.Append(notifyPrefix).Append(newArrayText).Append(notifySuffix).Append("\r\n");
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

        // 如果顶部区没有 notify 行,必须插在首个表头之前。TOML 没有“离开当前表”语法,
        // 追加到文件末尾会把 notify 写进最后一个 [projects.*] / provider 表。
        if (notifyLineIndex < 0)
        {
            var newArrayText = JsonSerializer.Serialize(newArray);
            var inserted = new StringBuilder();
            inserted.Append("notify = ").Append(newArrayText).Append("\r\n");
            inserted.Append(newLines);
            newLines = inserted;
        }

        WriteConfig(newLines.ToString(), originalContent);
    }

    /// <summary>
    /// 修复旧版把我方 notify 追加进最后一个表的事故。只删除可完整解析、且命令形状能证明
    /// 属于 AI Resume 的非顶层行；用户或第三方 notify 一律保留并由后续解析失败关闭。
    /// </summary>
    private static string[] NormalizeMisplacedOwnedNotifyLines(string[] lines)
    {
        int firstSection = FindFirstTableHeaderIndex(lines);
        if (firstSection < 0)
        {
            return lines;
        }

        bool[] statementStarts = FindStatementStartLines(lines);
        var normalized = new List<string>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i >= firstSection && statementStarts[i] &&
                TryParseNotifyLine(lines[i], out string[]? command) &&
                command is not null && IsOwnCommand(command))
            {
                continue;
            }

            normalized.Add(lines[i]);
        }

        return normalized.ToArray();
    }

    private static string? FindMisplacedOwnCommand(string[] lines)
    {
        int firstSection = FindFirstTableHeaderIndex(lines);
        if (firstSection < 0)
        {
            return null;
        }

        bool[] statementStarts = FindStatementStartLines(lines);
        for (int i = firstSection; i < lines.Length; i++)
        {
            if (statementStarts[i] &&
                TryParseNotifyLine(lines[i], out string[]? command) && command is not null)
            {
                if (IsOwnCommand(command))
                {
                    return HookCommand.ExtractExecutable(command[0]);
                }
            }
        }

        return null;
    }

    private NotificationProviderStatus MisplacedOwnedNotifyStatus(string ownCommand) =>
        new(
            Kind,
            DisplayName,
            IsInstalled: true,
            IsEnabled: true,
            ConfigPath: _configPath,
            Detail: "检测到旧版 AI Resume notify 被写入 TOML 子表,Codex 配置可能无法加载;重新安装可自动修复",
            HookCommand: ownCommand,
            HookBroken: true);

    private static bool TryParseNotifyLine(string line, out string[]? command)
    {
        command = null;
        if (!TrySplitNotifyLine(line, out _, out string arrayText, out _))
        {
            return false;
        }

        try
        {
            command = ParseNotifyArray(arrayText);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Disable()
    {
        if (!File.Exists(_configPath))
        {
            return; // 文件不存在,无事可做
        }

        using IDisposable writeLease = AcquireWriteLease();
        string? originalContent = ReadConfigContent();
        if (originalContent is null)
        {
            return;
        }

        string[] originalLines = SplitLines(originalContent);
        var lines = NormalizeMisplacedOwnedNotifyLines(originalLines);
        bool removedMisplaced = lines.Length != originalLines.Length;
        var topSection = ExtractTopSection(lines);
        var notifyLineIndex = FindNotifyLineIndex(topSection);

        if (notifyLineIndex < 0)
        {
            if (removedMisplaced)
            {
                WriteConfig(lines, originalContent);
            }
            return; // 无 notify 行
        }

        if (!TrySplitNotifyLine(
                topSection[notifyLineIndex],
                out string notifyPrefix,
                out string arrayText,
                out string notifySuffix))
        {
            if (removedMisplaced)
            {
                WriteConfig(lines, originalContent);
            }
            return; // 非单行数组,不处理
        }

        var existing = ParseNotifyArray(arrayText);

        // 只有可验证的我方命令层才允许摘除;参数里碰巧出现文件名不构成所有权。
        if (!HasOwnInChain(existing, 0))
        {
            if (removedMisplaced)
            {
                WriteConfig(lines, originalContent);
            }
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
                        var newArrayText = JsonSerializer.Serialize(restored);
                        newLines.Append(notifyPrefix).Append(newArrayText).Append(notifySuffix).Append("\r\n");
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

        WriteConfig(newLines.ToString(), originalContent);
    }

    private void WriteConfig(IEnumerable<string> lines, string? expectedContent)
    {
        var content = new StringBuilder();
        foreach (string line in lines)
        {
            content.Append(line).Append("\r\n");
        }

        WriteConfig(content.ToString(), expectedContent);
    }

    private void WriteConfig(string content, string? expectedContent)
    {
        EnsureConfigUnchanged(expectedContent);

        string tempFile = _configPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(content);
            using (var stream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            EnsureConfigUnchanged(expectedContent);
            _beforeAtomicReplace?.Invoke();
            if (expectedContent is not null)
            {
                // 同目录 Replace 同时完成旧文件备份与候选提交,避免 File.Copy 在最终
                // 内容比对之后再拉长竞态窗口。备份保留的是被替换文件的精确字节。
                string backupFile = _configPath + ".bak";
                File.Replace(tempFile, _configPath, backupFile, ignoreMetadataErrors: true);
                VerifyReplacedVersion(expectedContent, content, backupFile);
            }
            else
            {
                File.Move(tempFile, _configPath);
            }
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
            }
        }
    }

    private void VerifyReplacedVersion(string expectedContent, string candidateContent, string backupFile)
    {
        string replacedContent = File.ReadAllText(backupFile);
        if (string.Equals(replacedContent, expectedContent, StringComparison.Ordinal))
        {
            return;
        }

        // File.Replace 的备份是“实际被替换版本”的提交见证。若它不等于读取快照，
        // 说明外部写入落在最终检查与替换之间；不能把我方候选留在生产路径。
        // 恢复本身也可能与外部编辑器竞争，因此每轮先留副本，再以被替换内容判断
        // 是否出现了更晚版本；更晚版本始终在下一轮被提升回活动路径。
        string candidateSnapshot = WriteRecoverySnapshot("candidate", candidateContent);
        string desiredPath = backupFile;
        string desiredContent = replacedContent;
        string expectedActiveContent = candidateContent;
        string? lastConflictFile = null;

        for (int attempt = 0; attempt < ConflictRestoreAttempts; attempt++)
        {
            string recoverySnapshot;
            try
            {
                recoverySnapshot = CopyRecoverySnapshot("recovery", desiredPath);
                _beforeConflictRestore?.Invoke(attempt);
                string conflictFile = _configPath + ".conflict-" + Guid.NewGuid().ToString("N");
                File.Replace(desiredPath, _configPath, conflictFile, ignoreMetadataErrors: true);
                lastConflictFile = conflictFile;

                string displacedContent = File.ReadAllText(conflictFile);
                if (string.Equals(displacedContent, expectedActiveContent, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Codex config.toml 在原子提交期间被外部更新；外部版本已恢复，" +
                        $"冲突版本保留在 {conflictFile}，恢复快照保留在 {recoverySnapshot}。");
                }

                // conflictFile 是刚刚被我方替换掉的、更晚外部版本；下一轮恢复它。
                desiredPath = conflictFile;
                expectedActiveContent = desiredContent;
                desiredContent = displacedContent;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("外部版本已恢复", StringComparison.Ordinal))
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Codex config.toml 在原子提交期间被外部更新；恢复未完成，" +
                    $"材料保留在 {candidateSnapshot}、{desiredPath}。",
                    ex);
            }
        }

        throw new InvalidOperationException(
            $"Codex config.toml 在恢复期间持续被外部更新；已停止覆盖。" +
            $"最新检测版本保留在 {desiredPath}，候选版本保留在 {candidateSnapshot}，" +
            $"上一冲突文件为 {lastConflictFile ?? "无"}。");
    }

    private string WriteRecoverySnapshot(string kind, string content)
    {
        string path = _configPath + "." + kind + "-" + Guid.NewGuid().ToString("N");
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return path;
    }

    private string CopyRecoverySnapshot(string kind, string sourcePath)
    {
        string path = _configPath + "." + kind + "-" + Guid.NewGuid().ToString("N");
        File.Copy(sourcePath, path, overwrite: false);
        return path;
    }

    private string? ReadConfigContent() =>
        File.Exists(_configPath) ? File.ReadAllText(_configPath) : null;

    private static string[] SplitLines(string? content)
    {
        if (content is null)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    private void EnsureConfigUnchanged(string? expectedContent)
    {
        string? currentContent = ReadConfigContent();
        if (!string.Equals(currentContent, expectedContent, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex config.toml 在修改期间被外部更新,已拒绝覆盖该改动。");
        }
    }

    private IDisposable AcquireWriteLease()
    {
        var mutex = new Mutex(initiallyOwned: false, _writeLockName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(WriteLockTimeoutSeconds));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new TimeoutException("等待 Codex 通知配置写锁超时,未修改 config.toml。");
        }

        return new MutexLease(mutex);
    }

    private sealed class MutexLease : IDisposable
    {
        private Mutex? _mutex;

        public MutexLease(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            Mutex? mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex is null)
            {
                return;
            }

            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    /// <summary>
    /// 合并 notify 数组(§3 合并算法)。
    /// </summary>
    private static string[] MergeNotify(string[] existing, string hookExe)
    {
        if (IsDesktopWrapper(existing))
        {
            ValidatePreviousNotifyChain(existing, 0);
        }

        // 情况 1:刷新已托管链
        if (HasOwnInChain(existing, 0))
        {
            return RefreshChain(existing, hookExe, 0);
        }

        // 情况 2:Codex Desktop wrapper 特判
        if (IsDesktopWrapper(existing))
        {
            return WrapDesktopWrapper(existing, hookExe);
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

        string unquoted = s.Length > 1 && s[0] == '"' && s[^1] == '"'
            ? s[1..^1]
            : s;
        if (unquoted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return unquoted;
        }

        int markerStart = 0;
        while (markerStart < s.Length)
        {
            markerStart = s.IndexOf(MarkerFileName, markerStart, StringComparison.OrdinalIgnoreCase);
            if (markerStart < 0)
            {
                break;
            }

            int markerEnd = markerStart + MarkerFileName.Length;
            if ((markerEnd == s.Length || char.IsWhiteSpace(s[markerEnd]) || s[markerEnd] == '"') &&
                string.Equals(
                    Path.GetFileName(s[..markerEnd].Trim().Trim('"')),
                    MarkerFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return s[..markerEnd].Trim().Trim('"');
            }

            markerStart = markerEnd;
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
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] == "--previous-notify")
            {
                string[] previous = ParsePreviousNotifyOrThrow(result, i, "Codex Desktop wrapper");
                var wrapped = MergeNotify(previous, hookExe);
                result[i + 1] = JsonSerializer.Serialize(wrapped);
                return result;
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

    private static bool IsDesktopWrapper(string[] command)
    {
        if (command.Length == 0)
        {
            return false;
        }

        string fileName = Path.GetFileName(command[0]);
        return fileName.Equals("codex-computer-use.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cod-use.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidatePreviousNotifyChain(string[] command, int depth)
    {
        if (depth >= MaxChainDepth)
        {
            throw new InvalidOperationException("notify 链深度超过上限,无法安全处理。");
        }

        for (int i = 0; i < command.Length; i++)
        {
            if (!string.Equals(command[i], "--previous-notify", StringComparison.Ordinal))
            {
                continue;
            }

            string[] previous = ParsePreviousNotifyOrThrow(command, i, "notify 链");
            ValidatePreviousNotifyChain(previous, depth + 1);
            i++;
        }
    }

    private static string[] ParsePreviousNotifyOrThrow(
        string[] command,
        int markerIndex,
        string owner)
    {
        if (markerIndex + 1 >= command.Length)
        {
            throw new InvalidOperationException(
                $"{owner} 的 --previous-notify 缺少值，已拒绝修改配置。");
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(command[markerIndex + 1])
                ?? throw new InvalidOperationException(
                    $"{owner} 的 --previous-notify 不是字符串数组，已拒绝修改配置。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{owner} 的 --previous-notify 无法解析，已拒绝修改配置。",
                ex);
        }
    }

    /// <summary>摘除我方层,提升 previous。</summary>
    private static string[]? RemoveOwnLayer(string[] array)
    {
        // 如果当前层是我方命令
        if (IsOwnCommand(array))
        {
            // 查找 --previous-notify
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] == "--previous-notify")
                {
                    if (i + 1 >= array.Length)
                    {
                        throw new InvalidOperationException(
                            "AI Resume notify 层的 --previous-notify 缺少值，已拒绝删除配置。");
                    }

                    try
                    {
                        return JsonSerializer.Deserialize<string[]>(array[i + 1])
                            ?? throw new InvalidOperationException(
                                "AI Resume notify 层的 --previous-notify 不是数组，已拒绝删除配置。");
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidOperationException(
                            "AI Resume notify 层的 --previous-notify 无法解析，已拒绝删除配置。",
                            ex);
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
        int firstTable = FindFirstTableHeaderIndex(lines);
        return firstTable >= 0 ? lines.Take(firstTable).ToArray() : lines;
    }

    private static bool IsTableHeader(string line) => TableHeaderRegex.IsMatch(line);

    private enum TomlStringMode
    {
        None,
        Basic,
        Literal,
        MultilineBasic,
        MultilineLiteral,
    }

    private static int FindFirstTableHeaderIndex(string[] lines)
    {
        bool[] statementStarts = FindStatementStartLines(lines);
        for (int i = 0; i < lines.Length; i++)
        {
            if (statementStarts[i] && IsTableHeader(lines[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 标出每一行是否从 TOML 语句边界开始。多行字符串、数组和内联表内部即使出现
    /// 形似 <c>notify = [...]</c> 的文本,也只是值内容,不得作为可删除的配置键处理。
    /// </summary>
    private static bool[] FindStatementStartLines(string[] lines)
    {
        var starts = new bool[lines.Length];
        int arrayDepth = 0;
        int inlineTableDepth = 0;
        TomlStringMode stringMode = TomlStringMode.None;
        bool escaped = false;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            starts[lineIndex] = arrayDepth == 0 && inlineTableDepth == 0 &&
                                stringMode == TomlStringMode.None;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                switch (stringMode)
                {
                    case TomlStringMode.Basic:
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (c == '\\')
                        {
                            escaped = true;
                        }
                        else if (c == '"')
                        {
                            stringMode = TomlStringMode.None;
                        }
                        continue;
                    case TomlStringMode.Literal:
                        if (c == '\'')
                        {
                            stringMode = TomlStringMode.None;
                        }
                        continue;
                    case TomlStringMode.MultilineBasic:
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (c == '\\')
                        {
                            escaped = true;
                        }
                        else if (c == '"' && i + 2 < line.Length &&
                                 line[i + 1] == '"' && line[i + 2] == '"')
                        {
                            stringMode = TomlStringMode.None;
                            i += 2;
                        }
                        continue;
                    case TomlStringMode.MultilineLiteral:
                        if (c == '\'' && i + 2 < line.Length && line[i + 1] == '\'' && line[i + 2] == '\'')
                        {
                            stringMode = TomlStringMode.None;
                            i += 2;
                        }
                        continue;
                }

                if (c == '#')
                {
                    break;
                }

                if (c == '"')
                {
                    if (i + 2 < line.Length && line[i + 1] == '"' && line[i + 2] == '"')
                    {
                        stringMode = TomlStringMode.MultilineBasic;
                        escaped = false;
                        i += 2;
                    }
                    else
                    {
                        stringMode = TomlStringMode.Basic;
                        escaped = false;
                    }
                    continue;
                }

                if (c == '\'')
                {
                    if (i + 2 < line.Length && line[i + 1] == '\'' && line[i + 2] == '\'')
                    {
                        stringMode = TomlStringMode.MultilineLiteral;
                        i += 2;
                    }
                    else
                    {
                        stringMode = TomlStringMode.Literal;
                    }
                    continue;
                }

                if (c == '[')
                {
                    arrayDepth++;
                }
                else if (c == ']' && arrayDepth > 0)
                {
                    arrayDepth--;
                }
                else if (c == '{')
                {
                    inlineTableDepth++;
                }
                else if (c == '}' && inlineTableDepth > 0)
                {
                    inlineTableDepth--;
                }
            }

            // 多行 basic string 的续行反斜杠只折叠换行,不转义下一物理行首字符。
            if (stringMode == TomlStringMode.MultilineBasic)
            {
                escaped = false;
            }
        }

        return starts;
    }

    /// <summary>在顶部区查找 notify 行索引。</summary>
    private static int FindNotifyLineIndex(string[] topSection)
    {
        bool[] statementStarts = FindStatementStartLines(topSection);
        for (int i = 0; i < topSection.Length; i++)
        {
            // 用宽松正则定位:多行或非数组形式的 notify 也必须被发现,
            // 由调用方用 TrySplitNotifyLine 判定形态并在不可安全处理时拒绝。
            if (statementStarts[i] && NotifyKeyRegex.IsMatch(topSection[i]))
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
