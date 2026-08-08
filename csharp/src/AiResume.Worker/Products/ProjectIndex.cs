using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace AiResume.Worker.Products;

/// <summary>
/// 项目发现索引条目,以会话目录为 key,缓存「目录 → (jsonl mtime, cwd)」这一层昂贵的 I/O 结果。
/// </summary>
public sealed record ProjectIndexEntry(
    string SessionDir,
    DateTimeOffset DirWriteUtc,
    string? JsonlPath,
    DateTimeOffset JsonlWriteUtc,
    string? Cwd);

/// <summary>
/// 项目发现索引,用于消除冷调用时对全部历史会话目录的重复扫描。
/// 仅缓存「目录 → (jsonl mtime, cwd)」,不缓存策略结果(排除、去重、排序、custom 追加),
/// 因此 hiddenProjects 等配置变化无需失效索引。
/// 线程安全:内部锁保护所有读写操作。
/// </summary>
public sealed class ProjectIndex
{
    private const int CurrentVersion = 1;
    private const string TempSuffix = ".tmp";

    private readonly object _lock = new();
    private readonly Dictionary<string, ProjectIndexEntry> _entries;
    private readonly HashSet<string> _seen;
    private bool _dirty;

    /// <summary>
    /// 创建一个空索引。
    /// </summary>
    public ProjectIndex()
    {
        _entries = new Dictionary<string, ProjectIndexEntry>(StringComparer.OrdinalIgnoreCase);
        _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _dirty = false;
    }

    /// <summary>
    /// 从指定路径加载索引。文件不存在、JSON 损坏、版本号不匹配时静默降级为空索引,不抛异常。
    /// </summary>
    public static ProjectIndex Load(string indexPath)
    {
        try
        {
            if (!File.Exists(indexPath))
            {
                return new ProjectIndex();
            }

            string json = File.ReadAllText(indexPath);
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // 版本号不匹配则视为无效,返回空索引
            if (!root.TryGetProperty("Version", out JsonElement versionElement) ||
                versionElement.GetInt32() != CurrentVersion)
            {
                return new ProjectIndex();
            }

            if (!root.TryGetProperty("Entries", out JsonElement entriesElement) ||
                entriesElement.ValueKind != JsonValueKind.Array)
            {
                return new ProjectIndex();
            }

            var index = new ProjectIndex();
            foreach (JsonElement entryElement in entriesElement.EnumerateArray())
            {
                try
                {
                    string? sessionDir = entryElement.TryGetProperty("SessionDir", out JsonElement sd) ? sd.GetString() : null;
                    if (string.IsNullOrEmpty(sessionDir))
                    {
                        continue;
                    }

                    DateTimeOffset dirWriteUtc = entryElement.TryGetProperty("DirWriteUtc", out JsonElement dw) ? dw.GetDateTimeOffset() : DateTimeOffset.MinValue;
                    string? jsonlPath = entryElement.TryGetProperty("JsonlPath", out JsonElement jp) ? jp.GetString() : null;
                    DateTimeOffset jsonlWriteUtc = entryElement.TryGetProperty("JsonlWriteUtc", out JsonElement jw) ? jw.GetDateTimeOffset() : DateTimeOffset.MinValue;
                    string? cwd = entryElement.TryGetProperty("Cwd", out JsonElement cw) ? cw.GetString() : null;

                    var entry = new ProjectIndexEntry(sessionDir, dirWriteUtc, jsonlPath, jsonlWriteUtc, cwd);
                    index._entries[sessionDir] = entry;
                }
                catch (Exception)
                {
                    // 单条损坏则跳过该条,继续解析其余条目
                    continue;
                }
            }

            return index;
        }
        catch (Exception)
        {
            // 任何读取/解析异常都静默降级为空索引
            return new ProjectIndex();
        }
    }

    /// <summary>
    /// 尝试获取指定会话目录的索引条目。
    /// 仅当命中且 DirWriteUtc 相等才返回 true;命中即自动 MarkSeen。
    /// </summary>
    public bool TryGet(string sessionDir, DateTimeOffset dirWriteUtc, out ProjectIndexEntry entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(sessionDir, out ProjectIndexEntry? existing) &&
                existing.DirWriteUtc == dirWriteUtc)
            {
                entry = existing;
                _seen.Add(sessionDir);
                return true;
            }

            entry = null!;
            return false;
        }
    }

    /// <summary>
    /// 新增或更新索引条目,并标记为已见。
    /// </summary>
    public void Put(ProjectIndexEntry entry)
    {
        lock (_lock)
        {
            _entries[entry.SessionDir] = entry;
            _seen.Add(entry.SessionDir);
            _dirty = true;
        }
    }

    /// <summary>
    /// 标记指定会话目录为已见(本轮枚举中出现)。
    /// </summary>
    public void MarkSeen(string sessionDir)
    {
        lock (_lock)
        {
            _seen.Add(sessionDir);
        }
    }

    /// <summary>
    /// 移除本轮未 MarkSeen 的条目(目录已删除)。
    /// </summary>
    public void RemoveUnseen()
    {
        lock (_lock)
        {
            // 注意:_seen 为空是合法状态(例如发现根被清空、本轮一个目录都没枚举到),
            // 此时应当把索引清空而不是保留死条目——保留会让已删除的会话目录永久残留。
            var toRemove = new List<string>();
            foreach (string key in _entries.Keys)
            {
                if (!_seen.Contains(key))
                {
                    toRemove.Add(key);
                }
            }

            foreach (string key in toRemove)
            {
                _entries.Remove(key);
                _dirty = true;
            }

            _seen.Clear();
        }
    }

    /// <summary>
    /// 有变更才写盘;临时文件 + flush + 原子替换;返回是否真的写了。
    /// </summary>
    public bool SaveIfChanged(string indexPath)
    {
        lock (_lock)
        {
            if (!_dirty)
            {
                return false;
            }

            try
            {
                string? directory = Path.GetDirectoryName(indexPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string tempPath = indexPath + TempSuffix;
                string json = Serialize();

                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, System.Text.Encoding.UTF8))
                {
                    writer.Write(json);
                    writer.Flush();
                    fs.Flush(flushToDisk: true);
                }

                // 原子替换
                File.Move(tempPath, indexPath, overwrite: true);

                _dirty = false;
                return true;
            }
            catch (Exception)
            {
                // 写盘失败静默忽略,不阻断发现流程
                return false;
            }
        }
    }

    /// <summary>
    /// 序列化为 JSON 字符串。
    /// </summary>
    private string Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", CurrentVersion);
            writer.WriteStartArray("Entries");

            foreach (ProjectIndexEntry entry in _entries.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("SessionDir", entry.SessionDir);
                writer.WriteString("DirWriteUtc", entry.DirWriteUtc);
                writer.WriteString("JsonlPath", entry.JsonlPath);
                writer.WriteString("JsonlWriteUtc", entry.JsonlWriteUtc);
                writer.WriteString("Cwd", entry.Cwd);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}