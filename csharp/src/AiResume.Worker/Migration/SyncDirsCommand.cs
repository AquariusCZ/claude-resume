using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiResume.Core;
using AiResume.Worker.Products;
using AiResume.Wrapper;

namespace AiResume.Worker.Migration;

/// <summary>
/// <c>AiResume.Worker.exe sync-dirs [--out &lt;path&gt;] [--current &lt;path&gt;]</c>(S10-P):
/// 把 AI Resume 的项目清单同步进 cc-connect 的 <c>dir_history.json</c>。
///
/// cc-connect 只有**一个** [[projects]],靠 <c>/dir &lt;路径&gt;</c> 切工作目录
/// (方案 A,2026-08-06 用户确认;同一飞书应用绑多个项目会导致一条消息被 N 个 agent
/// 各收一次,实测烧穿额度)。<c>/dir</c> 不带参数时列出历史访问过的目录并编号,
/// 该历史存在 <c>%USERPROFILE%\.cc-connect\dir_history.json</c>。
///
/// 问题:这份历史只有在用户**手动访问过**某个目录之后才会有该条目。新装或清空后,
/// <c>/dir</c> 是空的,用户看不到自己有哪些项目。AI Resume 本来就负责"动态项目发现"
/// (ADR-0003 §2.2 四项职责之一),把发现结果同步过去是自然延伸。
///
/// **为什么不用别的办法**(盘点结论,必须保留):
/// - 上游 cc-connect **没有**项目/目录选择卡片。<c>/model</c> 有卡片
///   (<c>select_static</c> 下拉 + <c>model_select</c> 回调),但二进制里不存在
///   <c>dir_select</c> / <c>project_select</c> 之类的 action,<c>/dir</c> 只有文本编号列表。
/// - 上游 [[commands]] 自定义命令的产出也是文本,做不出卡片。
/// - 由 AI Resume 自己渲染卡片需要消费飞书事件,违反 ADR-0003 划定的边界。
/// 因此当前最优解就是**把 cc-connect 自己的 <c>/dir</c> 历史喂饱**,用它原生的机制。
/// </summary>
public static class SyncDirsCommand
{
    /// <summary>dir_history.json 的默认位置。</summary>
    public static string DefaultHistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cc-connect", "dir_history.json");

    /// <summary>同步结果。</summary>
    public sealed record SyncResult(bool Ok, string Message, int DirCount, string? OutPath);

    public static int Run(string[] args)
    {
        try
        {
            string outPath = ReadOption(args, "--out") ?? DefaultHistoryPath;
            string? currentPath = ReadOption(args, "--current");
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                currentPath = null;
            }

            // 读 shadow 产品配置:候选路径 = Selected ∪ CustomProjects ∪ Discover。
            // 按此顺序拼接(已布防的排前面)。
            var configStore = new ProductConfigStore(ShadowPaths.Root);
            ProductConfig product = configStore.Load();

            var candidates = new List<string>();
            foreach (ProjectRef project in product.Selected)
            {
                if (!string.IsNullOrWhiteSpace(project.Path))
                {
                    candidates.Add(project.Path);
                }
            }

            foreach (ProjectRef project in product.CustomProjects)
            {
                if (!string.IsNullOrWhiteSpace(project.Path))
                {
                    candidates.Add(project.Path);
                }
            }

            var catalog = new ProjectCatalog(
                indexPath: Path.Combine(ShadowPaths.Root, "project-index.json"));
            foreach (ProjectEntry entry in catalog.Discover(product))
            {
                if (!string.IsNullOrWhiteSpace(entry.Path))
                {
                    candidates.Add(entry.Path);
                }
            }

            IReadOnlyList<string> dirs = BuildDirList(
                candidates,
                product.HiddenProjects,
                currentPath,
                Directory.Exists);

            // 读既有文件(读不到当空串),合并后原子写入。
            string existingJson = File.Exists(outPath) ? SafeRead(outPath) : string.Empty;
            string merged = MergeJson(existingJson, CutoverConfigCommand.ProjectName, dirs);

            string? outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            string tmpPath = outPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(merged);
                    writer.Flush();
                    fs.Flush(true); // 落盘,防断电半截文件。
                }

                File.Move(tmpPath, outPath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }
                catch
                {
                    // 清理失败不掩盖原始异常。
                }

                throw;
            }

            // 写入前检测 cc-connect 是否在运行:在跑时照常写入,但追加警告。
            // 实测 cc-connect 退出时可能用内存快照覆盖磁盘上的 dir_history.json。
            string warning = string.Empty;
            if (Process.GetProcessesByName("cc-connect").Length > 0)
            {
                warning = Environment.NewLine +
                    "警告:cc-connect 正在运行,它可能在退出时用内存快照覆盖本次写入。请重启 cc-connect 使其生效。";
            }

            Console.WriteLine($"已同步 {dirs.Count} 个目录到 {outPath}。{warning}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"同步失败:{ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 纯函数:计算最终要写入的目录列表。
    ///
    /// 规则(从上到下依次执行):
    /// 1. candidatePaths 为 null 视同空集合;逐项 Trim(),空白项丢弃。
    /// 2. 排除隐藏项:与 hiddenPaths 任一项相等则丢弃。比较忽略大小写、忽略结尾 \ 与 /。
    /// 3. 排除不存在的目录:directoryExists(path) 为 false 时丢弃;null 视同恒 true。
    /// 4. 去重:忽略大小写 + 忽略尾部分隔符;保留首次出现的原始写法。
    /// 5. 当前目录置顶:currentPath 非空白且通过 2/3 检查时,在结果里就移到第 0 位,
    ///    不在结果里就插入第 0 位;否则不置顶也不插入。
    /// 6. 其余元素保持候选入参的相对顺序(稳定排序)。
    ///
    /// 顺序必须确定:不得依赖字典/哈希集合的枚举顺序。
    /// </summary>
    /// <remarks>仅为可测性公开:测试项目未配 InternalsVisibleTo。</remarks>
    public static IReadOnlyList<string> BuildDirList(
        IReadOnlyList<string>? candidatePaths,
        IReadOnlyList<string>? hiddenPaths,
        string? currentPath,
        Func<string, bool>? directoryExists)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hiddenPaths is not null)
        {
            foreach (string h in hiddenPaths)
            {
                if (!string.IsNullOrWhiteSpace(h))
                {
                    hidden.Add(NormalizePath(h));
                }
            }
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (candidatePaths is not null)
        {
            foreach (string candidate in candidatePaths)
            {
                string trimmed = candidate?.Trim() ?? string.Empty;
                if (trimmed.Length == 0)
                {
                    continue;
                }

                string normalized = NormalizePath(trimmed);
                if (hidden.Contains(normalized))
                {
                    continue;
                }

                bool exists = directoryExists?.Invoke(trimmed) ?? true;
                if (!exists)
                {
                    continue;
                }

                if (seen.Add(normalized))
                {
                    result.Add(trimmed);
                }
            }
        }

        // 当前目录置顶/插入。
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            string current = currentPath.Trim();
            string normalizedCurrent = NormalizePath(current);
            bool currentHidden = hidden.Contains(normalizedCurrent);
            bool currentExists = directoryExists?.Invoke(current) ?? true;

            if (!currentHidden && currentExists)
            {
                int existingIndex = result.FindIndex(p => NormalizePath(p) == normalizedCurrent);
                if (existingIndex >= 0)
                {
                    string item = result[existingIndex];
                    result.RemoveAt(existingIndex);
                    result.Insert(0, item);
                }
                else
                {
                    result.Insert(0, current);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 纯函数:把本项目的目录列表并进既有 JSON,保留其它项目名的条目。
    ///
    /// - existingJson 为 null/空/无法解析时,视同空对象 {{}},不得抛异常。
    /// - 结果 = 既有对象的全部其它键原样保留 + 本 projectName 键被替换成 dirs。
    ///   这条是硬要求:本项目已经因为"重新生成时整份覆盖"出过事故,
    ///   生成 cc-connect 配置时整份重写,把 [management] 抹掉,admin 页直接消失。
    /// - dirs 为空集合时,该键写成空数组 []。
    /// - 输出为缩进 2 空格、末尾不加换行;反斜杠按 JSON 规则转义;
    ///   不对非 ASCII 字符转义(项目路径里有中文目录名,转成 \uXXXX 人不可读)。
    /// </summary>
    /// <remarks>仅为可测性公开:测试项目未配 InternalsVisibleTo。</remarks>
    public static string MergeJson(string? existingJson, string projectName, IReadOnlyList<string> dirs)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        Dictionary<string, List<string>> merged;
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(existingJson);
                merged = parsed ?? new Dictionary<string, List<string>>();
            }
            catch (JsonException)
            {
                // 无法解析时视同空对象:无从保留,如实当空对象处理。
                merged = new Dictionary<string, List<string>>();
            }
        }
        else
        {
            merged = new Dictionary<string, List<string>>();
        }

        merged[projectName] = dirs.ToList();

        return JsonSerializer.Serialize(merged, options);
    }

    private static string NormalizePath(string path)
    {
        return path.TrimEnd('\\', '/');
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
        catch (UnauthorizedAccessException)
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
