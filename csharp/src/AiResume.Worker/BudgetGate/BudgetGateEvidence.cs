using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiResume.Worker.BudgetGate;

/// <summary>
/// 把 mod 写的心跳与 JSONL 日志汇总成界面要的证据(ADR-0008 §F):模式是否真的生效、mod 读数与 OAuth 读数对表、
/// 校准价格、最近判断、预测与实际(p80 覆盖率)、避免掉的中断、待补派。
///
/// 「生效中」是一句肯定句,必须能被外部证伪:settings.json 里恰好登记了安装目录、mod 文件齐全、组织策略允许旁加载、
/// 所有 3 分钟内的新鲜心跳都回报当前模式、配置版本与已装的 mod 版本。没跟上的会话分三种,界面分开说:
/// 设置刚更新、心跳还没轮到(mod 每分钟重读一次 config.json,会自己跟上)是「切换中」;设置更新一个心跳周期之后仍回报旧设置
/// 是没跟上;跑的不是已装版本的 mod 要重开会话才换。有会话没跟上或跑旧版时,一部分跟上是「部分生效」,一个都没有是「未跟上」;
/// 没有心跳是「等新会话」,读不出就是「未核实」。日志只读不改;半行、坏行、未知字段一律跳过。
/// 日志按文件增量解析(mod 每段不超过 512 KiB,只有变了的文件才重读),同一行出现在多个文件里只算一次。
/// </summary>
public static class BudgetGateEvidence
{
    public static readonly TimeSpan FreshBeat = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 设置更新后多久还回报旧设置才算没跟上:mod 每 60 秒先重读 config.json 再写心跳,读与写之间还有几步 IO,
    /// 留一个心跳周期再加余量;在这之前写的心跳回报旧设置是正常的。
    /// </summary>
    public static readonly TimeSpan ConfigFollowGrace = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan HeartbeatWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan LogWindow = TimeSpan.FromDays(7);
    private const int DecisionRows = 30;
    private const long MaxLogBytes = 64L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, CachedFile> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BudgetGateReport Build(BudgetGateInputs input)
    {
        DateTimeOffset now = input.Now;
        List<Heartbeat> beats = ReadHeartbeats(input.Paths.SessionsDir, now);
        List<Heartbeat> fresh = beats.Where(b => !b.Ended && now - b.BeatAt <= FreshBeat).ToList();
        (LogDigest logs, bool truncated) = ReadLogs(input.Paths.LogsDir, now);

        Judgement judged = Judge(input, fresh);

        var readingPairs = new List<ReadingPair>();
        foreach (string kind in new[] { "five_hour", "seven_day" })
        {
            (WindowReading? mod, DateTimeOffset? modAt) = MergedReading(beats, kind, now);
            WindowReading? oauth = input.OAuthWindows.FirstOrDefault(r => r.Kind == kind);
            double? delta = mod is not null && oauth is not null ? Math.Round(mod.PercentUsed - oauth.PercentUsed, 1) : null;
            bool? resetMatches = mod?.ResetsAt is { } a && oauth?.ResetsAt is { } b ? Math.Abs((a - b).TotalMinutes) <= 2 : null;
            readingPairs.Add(new ReadingPair(kind, mod?.PercentUsed, mod?.ResetsAt, modAt,
                oauth?.PercentUsed, oauth?.ResetsAt, oauth?.ObservedAt, delta, resetMatches));
        }

        Heartbeat? withPrices = beats.Where(b => b.Prices.Count > 0).MaxBy(b => b.BeatAt);
        var pending = fresh.SelectMany(b => b.Pending.Select(p => p with { SessionId = b.SessionId }))
                           .OrderBy(p => p.DeferUntil ?? DateTimeOffset.MaxValue).ToList();
        List<string> problems = fresh.SelectMany(b => b.ConfigProblems).Concat(fresh.Where(b => b.LastError is not null).Select(b => b.LastError!))
                                     .Distinct(StringComparer.Ordinal).Take(5).ToList();

        return new BudgetGateReport(
            Mode: input.Mode,
            ConfigVersion: input.ConfigVersion,
            Status: judged.Status,
            Reasons: judged.Reasons,
            Issue: judged.Issue,
            Behind: judged.Behind,
            FreshSessions: fresh.Count,
            ModVersions: fresh.Select(b => b.ModVersion).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList(),
            EngineVersions: fresh.Select(b => b.EngineVersion).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList(),
            Readings: readingPairs,
            Prices: withPrices?.Prices ?? new List<PriceRow>(),
            PricesAt: withPrices?.BeatAt,
            Decisions: logs.Decisions.OrderByDescending(d => d.At).Take(DecisionRows).ToList(),
            DecisionCounts: logs.Decisions.GroupBy(d => d.Kind).ToDictionary(g => g.Key, g => g.Count()),
            Coverage: logs.Coverage(),
            Avoided: logs.Avoided(),
            Pending: pending,
            LastErrors: problems,
            Truncated: truncated,
            GeneratedAt: now);
    }

    /// <summary>判定结果:状态、给详情看的原因、卡片用的问题码(未生效、未核实、已关闭却仍登记时)与没跟上的会话分类。</summary>
    private sealed record Judgement(string Status, List<string> Reasons, string Issue, BehindStats Behind);

    private static readonly BehindStats NoneBehind = new(0, 0, 0, false);

    private static Judgement Judge(BudgetGateInputs input, List<Heartbeat> fresh)
    {
        var reasons = new List<string>();
        if (input.Mode == "off")
        {
            // settings.json 读不出时撤没撤下登记核实不了(切到已关闭时撤登记失败会被吞掉),不能只写「已关闭」了事。
            if (input.PluginDirs.Unverified)
            {
                reasons.Add($"已关闭,但{input.PluginDirs.Detail}:登记撤没撤下核实不了");
                return new("disabled", reasons, "settings-unreadable", NoneBehind);
            }
            if (input.PluginDirs.Ours.Count == 0)
                return new("disabled", reasons, string.Empty, NoneBehind);
            reasons.Add("已关闭,但 settings.json 里还登记着:新开的会话仍会加载它(只写心跳、不判断);再选一次「已关闭」或重新安装会撤下");
            return new("disabled", reasons, "still-registered", NoneBehind);
        }
        if (input.PluginDirs.Unverified)
        {
            reasons.Add(input.PluginDirs.Detail);
            return new("unverified", reasons, "settings-unreadable", NoneBehind);
        }

        // 未生效的原因可能不止一个:卡片只说第一个(按排查顺序),详情里全列。
        string issue = string.Empty;
        void Problem(string code, string text)
        {
            if (issue.Length == 0)
                issue = code;
            reasons.Add(text);
        }

        if (!input.Policy.CanRegister)
            Problem(input.Policy.Blocked ? "policy-blocked" : "policy-unknown", input.Policy.Detail);
        if (!input.PluginDirs.IsExact)
            Problem(input.PluginDirs.Ours.Count == 0 ? "unregistered" : "wrong-path", $"settings.json:{input.PluginDirs.Detail}");
        if (!input.ModFilesOk)
            Problem("mod-files", input.ModFilesDetail);
        if (input.ConfigVersion is null)
            Problem("config", "config.json 不存在或读不出");
        else if (input.ConfigMode is not null && input.ConfigMode != input.Mode)
        {
            // 会话照 config.json 走:它和当前设置不一致时,跟着它的会话并没有「没跟上」,重开也没用。
            Problem("config-mismatch", $"config.json 里是{Setting(input.ConfigMode, input.ConfigVersion)},当前设置是「{BudgetGateControl.ModeLabel(input.Mode)}」:" +
                                       "会话照 config.json 走;重新安装会按当前设置重写");
        }
        if (reasons.Count > 0)
            return new("broken", reasons, issue, NoneBehind);
        // 已装 mod 的版本读不出(plugin.json 坏了,或正在覆盖安装时读到半截):分不清会话跑的是不是已装版本,不能说生效。
        if (input.InstalledModVersion is null)
        {
            reasons.Add("读不出已装 mod 的版本(plugin.json):分不清会话跑的是不是已装版本;正在安装时稍后会好,否则重新安装可修复");
            return new("unverified", reasons, "mod-version", NoneBehind);
        }
        if (fresh.Count == 0)
        {
            reasons.Add("最近 3 分钟没有会话在跑预检:只有新开的 Claude Code 会话才会加载它");
            return new("waiting", reasons, string.Empty, NoneBehind);
        }

        bool ModOk(Heartbeat b) => b.ModVersion == input.InstalledModVersion;
        bool ConfigOk(Heartbeat b) => b.Mode == input.Mode && b.ConfigVersion == input.ConfigVersion;
        // 设置刚更新、这个会话还没轮到重读:更新之后不到一个心跳周期写的心跳回报旧设置是正常的,会自己跟上。
        // 更新时刻读不出(或在未来,时钟回拨留下的)时分不清是哪种,按没跟上处理。
        DateTimeOffset? writtenAt = input.ConfigWrittenAt is { } at && at <= input.Now + TimeSpan.FromMinutes(1) ? at : null;
        bool Settling(Heartbeat b) => writtenAt is { } w && b.BeatAt - w <= ConfigFollowGrace;

        List<Heartbeat> otherMod = fresh.Where(b => !ModOk(b)).ToList();
        List<Heartbeat> settling = fresh.Where(b => ModOk(b) && !ConfigOk(b) && Settling(b)).ToList();
        List<Heartbeat> stuck = fresh.Where(b => ModOk(b) && !ConfigOk(b) && !Settling(b)).ToList();
        int matching = fresh.Count - otherMod.Count - settling.Count - stuck.Count;
        bool allOlder = otherMod.Count > 0 && otherMod.All(b => IsOlder(b.ModVersion, input.InstalledModVersion));

        if (otherMod.Count > 0)
        {
            string versions = string.Join("、", otherMod.Select(b => b.ModVersion).Distinct().OrderBy(v => v, StringComparer.Ordinal));
            reasons.Add($"{otherMod.Count} 个会话跑的是{(allOlder ? "旧版" : "别的版本的")} mod {versions}(已装 {input.InstalledModVersion}):" +
                        "会话只在启动时加载 mod,重开这些会话后生效");
        }
        if (stuck.Count > 0)
        {
            string seen = string.Join("、", stuck.Select(b => Setting(b.Mode, b.ConfigVersion)).Distinct());
            string target = Setting(input.Mode, input.ConfigVersion);
            reasons.Add(writtenAt is null
                ? $"{stuck.Count} 个会话回报的是{seen},当前设置是{target}:读不出设置的更新时刻,分不清是不是刚改的,按没跟上新设置算;重开这些会话试试"
                : $"{stuck.Count} 个会话没跟上新设置:设置改成{target}之后它们又报过心跳,仍是{seen};" +
                  "可能读不到 config.json(见下方 mod 报错),重开这些会话试试");
        }
        if (settling.Count > 0)
            reasons.Add($"{settling.Count} 个会话还没轮到重读刚更新的设置,约 1 分钟内跟上");

        string status = otherMod.Count + stuck.Count + settling.Count == 0 ? "effective"
            : otherMod.Count + stuck.Count == 0 ? "switching"
            : matching > 0 ? "partial"
            : "stale";
        return new(status, reasons, string.Empty, new BehindStats(settling.Count, stuck.Count, otherMod.Count, allOlder));
    }

    /// <summary>「试运行」(v3) 这样的说法:模式用界面上的名字,读不出的版本写 ?。</summary>
    private static string Setting(string mode, int? version) =>
        $"「{BudgetGateControl.ModeLabel(mode)}」(v{version?.ToString(CultureInfo.InvariantCulture) ?? "?"})";

    /// <summary>心跳里的 mod 版本是否比已装的旧;任一边认不出就不算(卡片说「版本不同」,不断言是旧版)。</summary>
    private static bool IsOlder(string running, string? installed) =>
        installed is not null && Version.TryParse(running, out Version? r) && Version.TryParse(installed, out Version? i) && r < i;

    /// <summary>对表用的 mod 读数,只看 30 分钟内观测到的。</summary>
    public static readonly TimeSpan ReadingWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// mod 读数与 mod 自己合并读数同一套规则:丢掉已过重置时刻的,取最新周期(重置时刻最晚)里的最大值。
    /// 同一账号的各会话共用一个窗口,周期内读数只增不减;空闲会话的 usage() 停在它最后一次请求时的旧值,取最大才是现值。
    /// </summary>
    private static (WindowReading? Reading, DateTimeOffset? At) MergedReading(List<Heartbeat> beats, string kind, DateTimeOffset now)
    {
        var candidates = beats
            .Where(b => b.ReadingsAt is { } at && now - at <= ReadingWindow)
            .SelectMany(b => b.Readings
                .Where(r => r.Kind == kind && (r.ResetsAt is null || r.ResetsAt > now))
                .Select(r => (Reading: r, At: b.ReadingsAt!.Value)))
            .ToList();
        if (candidates.Count == 0)
            return (null, null);
        DateTimeOffset? cycle = candidates.Max(c => c.Reading.ResetsAt);
        var best = candidates
            .Where(c => c.Reading.ResetsAt is { } r && cycle is { } k ? Math.Abs((r - k).TotalMinutes) <= 2 : c.Reading.ResetsAt == cycle)
            .OrderByDescending(c => c.Reading.PercentUsed)
            .ThenByDescending(c => c.At)
            .First();
        return (best.Reading, best.At);
    }

    // ---------- 心跳 ----------

    private sealed record Heartbeat(
        string SessionId, string ModVersion, string EngineVersion, string Mode, int? ConfigVersion,
        DateTimeOffset BeatAt, bool Ended, List<WindowReading> Readings, DateTimeOffset? ReadingsAt,
        List<PriceRow> Prices, List<PendingRow> Pending, List<string> ConfigProblems, string? LastError);

    private static List<Heartbeat> ReadHeartbeats(string dir, DateTimeOffset now)
    {
        var list = new List<Heartbeat>();
        if (!Directory.Exists(dir))
            return list;
        foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles("*.json"))
        {
            if (now - file.LastWriteTimeUtc > HeartbeatWindow)
                continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(BudgetGateControl.ReadShared(file.FullName));
                JsonElement h = doc.RootElement;
                DateTimeOffset? beat = Time(h, "beatAt");
                if (beat is null || Str(h, "sessionId") is not { } sid)
                    continue;
                var configProblems = new List<string>();
                if (h.TryGetProperty("configProblems", out JsonElement cp) && cp.ValueKind == JsonValueKind.Array)
                    configProblems.AddRange(cp.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => "config.json:" + p.GetString()));
                list.Add(new Heartbeat(
                    sid, Str(h, "mod") ?? "?", Str(h, "engine") ?? "?", Str(h, "mode") ?? "?", Int(h, "configVersion"),
                    beat.Value, Bool(h, "ended") == true,
                    Readings(h, "readings"), Time(h, "readingsAt"),
                    Prices(h), PendingRows(h), configProblems, Str(h, "lastError")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 正在被 mod 改写的心跳下一轮再读
            }
        }

        return list;
    }

    private static List<WindowReading> Readings(JsonElement h, string name)
    {
        var list = new List<WindowReading>();
        if (!h.TryGetProperty(name, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (JsonElement r in arr.EnumerateArray())
        {
            if (Str(r, "kind") is { } kind && Num(r, "percentUsed") is { } p)
                list.Add(new WindowReading(kind, p, Time(r, "resetsAt"), null));
        }

        return list;
    }

    private static List<PriceRow> Prices(JsonElement h)
    {
        var list = new List<PriceRow>();
        if (!h.TryGetProperty("prices", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (JsonElement r in arr.EnumerateArray())
        {
            if (Str(r, "window") is { } w && Str(r, "model") is { } m && Num(r, "k") is { } k)
                list.Add(new PriceRow(w, m, Str(r, "period") ?? "?", k, Str(r, "source") ?? "?", Int(r, "samples") ?? 0, Num(r, "spread")));
        }

        return list;
    }

    private static List<PendingRow> PendingRows(JsonElement h)
    {
        var list = new List<PendingRow>();
        if (!h.TryGetProperty("pending", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (JsonElement r in arr.EnumerateArray())
        {
            list.Add(new PendingRow(string.Empty, Int(r, "seq") ?? 0, Str(r, "runId"), Int(r, "agentIndex"),
                Str(r, "type") ?? "?", Str(r, "model") ?? "?", Str(r, "rule") ?? "?", Time(r, "deferUntil"), Bool(r, "applied") == true));
        }

        return list;
    }

    // ---------- 日志 ----------

    /// <summary>一行日志里汇总用得到的部分;<c>Hash</c> 是原文哈希,跨文件去重用。</summary>
    private sealed record LogRow(string Hash, DateTimeOffset At, string ProcessKey, string Ev, JsonElement Row);

    private sealed record CachedFile(long Length, DateTime WriteTimeUtc, List<LogRow> Rows);

    private sealed class LogDigest
    {
        public readonly List<DecisionRow> Decisions = new();
        /// <summary>进程键 + seq → 启动后的 agentId。</summary>
        public readonly Dictionary<string, string> AgentBySeq = new(StringComparer.Ordinal);
        /// <summary>有录到的限额拒绝(stop_failure rate_limit)的 agent:只认这一种证据,不把任意报错当成被限额打断。</summary>
        public readonly HashSet<string> RateLimitedAgents = new(StringComparer.Ordinal);
        public readonly List<(bool Covered, double Ratio)> Outcomes = new();
        public readonly List<(DateTimeOffset At, string Kind, double Percent)> Readings = new();
        public int LandingsApplied;
        public int LandingsShadow;

        public CoverageStats Coverage()
        {
            if (Outcomes.Count == 0)
                return new CoverageStats(0, 0, null, null);
            int covered = Outcomes.Count(o => o.Covered);
            double[] ratios = Outcomes.Select(o => o.Ratio).Where(r => r > 0).OrderBy(r => r).ToArray();
            double? median = ratios.Length == 0 ? null : ratios[ratios.Length / 2];
            return new CoverageStats(Outcomes.Count, covered, Math.Round((double)covered / Outcomes.Count, 3), median is null ? null : Math.Round(median.Value, 2));
        }

        /// <summary>
        /// 避免掉的中断。试运行(shadow):本来会推迟的 agent 后来真的被限额拒绝(有 stop_failure rate_limit 为证);
        /// 启用:真推迟的 agent(同一工作流 agent 多次被拒只算一次),其卡住的窗口在推迟期内读数到过 ≥ 99%,加上真执行的软着陆。
        /// </summary>
        public AvoidedStats Avoided()
        {
            int shadow = 0;
            var enforced = new HashSet<string>(StringComparer.Ordinal);
            foreach (DecisionRow d in Decisions)
            {
                if (d.Verdict != "defer")
                    continue;
                if (!d.Applied)
                {
                    if (AgentBySeq.TryGetValue(d.ProcessKey + "#" + d.Seq, out string? agent) && RateLimitedAgents.Contains(agent))
                        shadow += 1;
                    continue;
                }
                DateTimeOffset until = d.DeferUntil ?? d.At.AddHours(5);
                string? window = d.Binding is "five_hour" or "seven_day" ? d.Binding : null;
                if (window is not null && Readings.Any(r => r.Kind == window && r.At >= d.At && r.At <= until && r.Percent >= 99))
                    enforced.Add(d.RunId is not null && d.AgentIndex is not null ? $"{d.RunId}#{d.AgentIndex}" : $"{d.ProcessKey}#{d.Seq}");
            }

            return new AvoidedStats(shadow, enforced.Count, LandingsApplied, LandingsShadow);
        }
    }

    private static (LogDigest Digest, bool Truncated) ReadLogs(string dir, DateTimeOffset now)
    {
        var digest = new LogDigest();
        if (!Directory.Exists(dir))
            return (digest, false);
        long budget = MaxLogBytes;
        bool truncated = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<LogRow>();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles("*.jsonl")
                     .Where(f => now - f.LastWriteTimeUtc <= LogWindow)
                     .OrderByDescending(f => f.LastWriteTimeUtc))
        {
            budget -= file.Length;
            if (budget < 0)
            {
                truncated = true;
                break;
            }

            live.Add(file.FullName);
            List<LogRow>? parsed = Parse(file);
            if (parsed is null)
                continue;
            foreach (LogRow row in parsed)
            {
                if (now - row.At <= LogWindow && seen.Add(row.Hash))
                    rows.Add(row);
            }
        }

        foreach (string stale in Cache.Keys.Where(k => k.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && !live.Contains(k)).ToList())
            Cache.TryRemove(stale, out _);
        foreach (LogRow row in rows.OrderBy(r => r.At))
            Absorb(digest, row);
        return (digest, truncated);
    }

    /// <summary>解析一个日志文件里汇总用得到的行;长度与修改时间没变就用缓存。读不出返回 null。</summary>
    private static List<LogRow>? Parse(FileInfo file)
    {
        if (Cache.TryGetValue(file.FullName, out CachedFile? cached) && cached.Length == file.Length && cached.WriteTimeUtc == file.LastWriteTimeUtc)
            return cached.Rows;
        string text;
        try
        {
            text = BudgetGateControl.ReadShared(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string processKey = ProcessKey(file.Name);
        var rows = new List<LogRow>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            // 绝大多数行是逐次请求计量(step),汇总用不到:先用子串筛掉,省掉建 JsonDocument 的开销。
            if (line.Length < 2 || line.Contains("\"ev\":\"step\"", StringComparison.Ordinal) || line.Contains("\"ev\":\"measure\"", StringComparison.Ordinal))
                continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement row = doc.RootElement;
                if (Time(row, "ts") is not { } at || Str(row, "ev") is not { } ev)
                    continue;
                rows.Add(new LogRow(Hash(line), at, processKey, ev, row.Clone()));
            }
            catch (JsonException)
            {
                // 半行(mod 正在写)或坏行跳过
            }
        }

        Cache[file.FullName] = new CachedFile(file.Length, file.LastWriteTimeUtc, rows);
        return rows;
    }

    private static string Hash(string line) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(line)), 0, 12);

    /// <summary>日志文件名 = 启动时刻-启动时会话前 8 位-随机-段号.jsonl;同一进程的各段共享前三段。</summary>
    private static string ProcessKey(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int dash = stem.LastIndexOf('-');
        return dash > 0 ? stem[..dash] : stem;
    }

    private static void Absorb(LogDigest d, LogRow log)
    {
        JsonElement row = log.Row;
        string processKey = log.ProcessKey;
        switch (log.Ev)
        {
            case "decision":
            {
                string verdict = Str(row, "verdict") ?? "?";
                bool applied = Bool(row, "applied") == true;
                string kind = verdict == "allow" ? "allow" : applied ? "deferred" : "would-defer";
                (double? need, double? margin, double? percent) = BindingWindow(row);
                JsonElement wf = row.TryGetProperty("wf", out JsonElement w) && w.ValueKind == JsonValueKind.Object ? w : default;
                d.Decisions.Add(new DecisionRow(
                    log.At, processKey, Int(row, "seq") ?? 0, Str(row, "mode") ?? "?", verdict, applied, kind,
                    Str(row, "rule") ?? "?", Str(row, "model") ?? "?", Str(row, "type") ?? "?",
                    wf.ValueKind == JsonValueKind.Object ? Str(wf, "run") : null,
                    wf.ValueKind == JsonValueKind.Object ? Int(wf, "idx") : null,
                    Str(row, "binding"), need, margin, percent, Time(row, "until"),
                    row.TryGetProperty("cost", out JsonElement c) ? Num(c, "p80") : null));
                break;
            }
            case "spawn" when Str(row, "phase") == "post" && Str(row, "agentId") is { } agent:
                d.AgentBySeq[processKey + "#" + (Int(row, "seq") ?? 0)] = agent;
                break;
            case "stop_failure" when Str(row, "error") == "rate_limit" && Str(row, "agentId") is { } agent:
                d.RateLimitedAgents.Add(agent);
                break;
            case "agent_done" when Bool(row, "covered") is { } covered && Bool(row, "complete") != false:
            {
                double eq = Num(row, "eq") ?? 0;
                double median = Num(row, "predictedMedianEq") ?? 0;
                d.Outcomes.Add((covered, median > 0 ? eq / median : 0));
                break;
            }
            case "landing":
                if (Bool(row, "applied") == true)
                    d.LandingsApplied += 1;
                else
                    d.LandingsShadow += 1;
                break;
            case "rl" when row.TryGetProperty("rl", out JsonElement list) && list.ValueKind == JsonValueKind.Array:
                foreach (JsonElement r in list.EnumerateArray())
                {
                    if (Str(r, "k") is { } k && Num(r, "p") is { } p)
                        d.Readings.Add((log.At, k, p));
                }

                break;
        }
    }

    /// <summary>判断行里最紧的那个窗口(binding,否则余量最小的)的需求、余量与读数。</summary>
    private static (double? Need, double? Margin, double? Percent) BindingWindow(JsonElement row)
    {
        if (!row.TryGetProperty("win", out JsonElement win) || win.ValueKind != JsonValueKind.Array)
            return (null, null, null);
        string? binding = Str(row, "binding");
        JsonElement? pick = null;
        double best = double.MaxValue;
        foreach (JsonElement w in win.EnumerateArray())
        {
            if (binding is not null && Str(w, "kind") == binding)
            {
                pick = w;
                break;
            }
            double margin = (Num(w, "margin") ?? 0) - (Num(w, "need") ?? 0);
            if (margin < best)
            {
                best = margin;
                pick = w;
            }
        }

        return pick is { } p ? (Num(p, "need"), Num(p, "margin"), Num(p, "percentUsed")) : (null, null, null);
    }

    // ---------- 读取工具 ----------

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;

    private static int? Int(JsonElement e, string name) =>
        Num(e, name) is { } d && d >= int.MinValue && d <= int.MaxValue ? (int)d : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t) ? t : null;
}

public sealed record BudgetGateInputs(
    BudgetGatePaths Paths,
    string Mode,
    int? ConfigVersion,
    PluginDirsStatus PluginDirs,
    SideloadVerdict Policy,
    bool ModFilesOk,
    string ModFilesDetail,
    string? InstalledModVersion,
    IReadOnlyList<WindowReading> OAuthWindows,
    DateTimeOffset Now,
    DateTimeOffset? ConfigWrittenAt = null,
    string? ConfigMode = null);

/// <summary>
/// 没跟上当前设置的新鲜心跳:<c>Settling</c> 设置刚更新、还没轮到重读(约 1 分钟内自己跟上);<c>Stuck</c> 更新一个心跳周期之后仍回报旧设置;
/// <c>ModMismatch</c> 跑的不是已装版本的 mod(重开会话才换),<c>ModAllOlder</c> 为这些全是更旧的版本。
/// </summary>
public sealed record BehindStats(int Settling, int Stuck, int ModMismatch, bool ModAllOlder);

public sealed record WindowReading(string Kind, double PercentUsed, DateTimeOffset? ResetsAt, DateTimeOffset? ObservedAt);

public sealed record ReadingPair(
    string Kind, double? ModPercent, DateTimeOffset? ModResetsAt, DateTimeOffset? ModObservedAt,
    double? OAuthPercent, DateTimeOffset? OAuthResetsAt, DateTimeOffset? OAuthObservedAt, double? Delta, bool? ResetMatches);

public sealed record PriceRow(string Window, string Model, string Period, double K, string Source, int Samples, double? Spread);

public sealed record DecisionRow(
    DateTimeOffset At, string ProcessKey, int Seq, string Mode, string Verdict, bool Applied, string Kind, string Rule,
    string Model, string Type, string? RunId, int? AgentIndex, string? Binding, double? Need, double? Margin, double? Percent,
    DateTimeOffset? DeferUntil, double? CostP80);

public sealed record PendingRow(
    string SessionId, int Seq, string? RunId, int? AgentIndex, string Type, string Model, string Rule, DateTimeOffset? DeferUntil, bool Applied);

public sealed record CoverageStats(int Total, int Covered, double? Ratio, double? MedianActualOverPredicted);

public sealed record AvoidedStats(int ShadowWouldAvoid, int EnforceAvoided, int LandingsApplied, int LandingsShadow);

public sealed record BudgetGateReport(
    string Mode, int? ConfigVersion, string Status, IReadOnlyList<string> Reasons, string Issue, BehindStats Behind, int FreshSessions,
    IReadOnlyList<string> ModVersions, IReadOnlyList<string> EngineVersions, IReadOnlyList<ReadingPair> Readings,
    IReadOnlyList<PriceRow> Prices, DateTimeOffset? PricesAt, IReadOnlyList<DecisionRow> Decisions,
    IReadOnlyDictionary<string, int> DecisionCounts, CoverageStats Coverage, AvoidedStats Avoided,
    IReadOnlyList<PendingRow> Pending, IReadOnlyList<string> LastErrors, bool Truncated, DateTimeOffset GeneratedAt);
