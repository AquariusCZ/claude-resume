using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 控制面点阵字体随包契约(2026-11-16 由 Ark Pixel 换成 Fusion Pixel 12px 等宽 zh_hans)。防四种回退:
/// <list type="bullet">
/// <item><c>@font-face</c> 指向不存在的文件:整页静默落回微软雅黑,测试与截图之外没人会发现;</item>
/// <item>换成度量不在 12px 网格上的字体:<c>1rem = 12px</c> 的点阵字纪律随之失效,字发糊;</item>
/// <item>页面里出现字体没有的汉字或全角标点:浏览器逐字回退,点阵字与非点阵字混排(换字体的原因);</item>
/// <item>上游许可没随字体分发,或构建输出里没有字体(安装包把构建输出整目录搬走)。</item>
/// </list>
/// 字体只读不改:WOFF2 的 cmap / head / hhea / OS/2 不做变换,解 Brotli 数据流后按表目录顺序切出即可。
/// </summary>
public sealed class GuiFontAssetTests
{
    private const string FontFile = "fusion-pixel-12px-monospaced-zh_hans.woff2";

    private static readonly string[] LicenseFiles =
    [
        "OFL.txt",
        Path.Combine("LICENSES", "ark-pixel", "OFL.txt"),
        Path.Combine("LICENSES", "cubic-11", "OFL.txt"),
        Path.Combine("LICENSES", "galmuri", "LICENSE.txt"),
    ];

    [Fact]
    public void 页面字体声明指向随包字体且排在字体栈第一位()
    {
        string html = File.ReadAllText(Wwwroot("index.html"));

        Assert.Single(Regex.Matches(html, "@font-face"));
        Match face = Regex.Match(html,
            @"@font-face\{\s*font-family:""(?<family>[^""]+)"";\s*src:url\(""(?<src>[^""]+)""\) format\(""woff2""\);");
        Assert.True(face.Success, "index.html 里找不到 woff2 的 @font-face");
        Assert.Equal("fonts/" + FontFile, face.Groups["src"].Value);
        Assert.True(File.Exists(Wwwroot("fonts", FontFile)), $"字体文件不在仓库里:{FontFile}");
        // 系统字体只在栈尾兜底;点阵字不排第一位,整页都不是点阵字。
        Assert.Contains($"--ui:\"{face.Groups["family"].Value}\",", html, StringComparison.Ordinal);

        // 字体目录只放页面引用的那一份,换字体时旧文件不得留在包里。
        string[] fonts = Directory.GetFiles(Wwwroot("fonts"), "*", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(path, @"\.(woff2?|ttf|otf)$", RegexOptions.IgnoreCase))
            .Select(path => Path.GetFileName(path))
            .ToArray();
        Assert.Equal(new[] { FontFile }, fonts);
    }

    [Fact]
    public void 字体度量落在12px网格上()
    {
        Woff2Font font = Woff2Font.Read(Wwwroot("fonts", FontFile));

        // UPM 1200 = 每个设计像素 100 单位;升 1000 + 降 200 = 一行正好 12 个设计像素,与 Ark Pixel 12px 相同。
        byte[] head = font.Table("head");
        Assert.Equal(1200, BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18)));
        byte[] hhea = font.Table("hhea");
        Assert.Equal(1000, BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(4)));
        Assert.Equal(-200, BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(6)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(8)));
        byte[] os2 = font.Table("OS/2");
        Assert.Equal(1000, BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(74)));
        Assert.Equal(200, BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(76)));
    }

    [Fact]
    public void 页面里的汉字与全角标点字体里都有()
    {
        HashSet<int> cmap = Woff2Font.Read(Wwwroot("fonts", FontFile)).CodePoints();

        // 换字体的直接原因:Ark Pixel 12px 缺这些一级汉字,界面上逐字回退成微软雅黑。
        foreach (char c in "执窗即骤然避紧热警")
        {
            Assert.Contains((int)c, cmap);
        }

        // 页面源文件(含注释与视图模型里的文案)出现的每个汉字、CJK 标点与全角字符都必须在字体里;
        // 字体缺的少数半角符号(≈ ✓ ≠ › 等)不在此列,出现时由字体栈尾的系统字体回退。
        // 失败时列出的字就是会回退成非点阵字的那些:换个说法,或换覆盖更全的字体。
        string html = File.ReadAllText(Wwwroot("index.html"));
        string[] missing = html.EnumerateRunes()
            .Select(rune => rune.Value)
            .Where(IsCjk)
            .Where(cp => !cmap.Contains(cp))
            .Distinct()
            .Select(cp => $"{char.ConvertFromUtf32(cp)}(U+{cp:X4})")
            .ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public void 上游许可随字体分发()
    {
        Assert.Contains("Fusion Pixel Font", File.ReadAllText(Wwwroot("fonts", "OFL.txt")), StringComparison.Ordinal);
        Assert.Contains("Ark Pixel Font",
            File.ReadAllText(Wwwroot("fonts", "LICENSES", "ark-pixel", "OFL.txt")), StringComparison.Ordinal);
        Assert.Contains("Cubic 11",
            File.ReadAllText(Wwwroot("fonts", "LICENSES", "cubic-11", "OFL.txt")), StringComparison.Ordinal);
        Assert.Contains("Lee Minseo",
            File.ReadAllText(Wwwroot("fonts", "LICENSES", "galmuri", "LICENSE.txt")), StringComparison.Ordinal);
        foreach (string license in LicenseFiles)
        {
            Assert.Contains("SIL Open Font License", File.ReadAllText(Wwwroot("fonts", license)),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void 构建输出带着字体与许可()
    {
        // 前端资源按 wwwroot\**\* 随 Gui 输出复制,install 再把输出目录整个搬进安装目录;
        // 测试项目引用 Gui,同一批文件也会出现在测试输出里。
        string csproj = File.ReadAllText(RepositoryFile("csharp", "src", "AiResume.Gui", "AiResume.Gui.csproj"));
        Assert.Contains(@"<None Include=""wwwroot\**\*"" CopyToOutputDirectory=""PreserveNewest"" />", csproj,
            StringComparison.Ordinal);

        string output = Path.Combine(AppContext.BaseDirectory, "wwwroot", "fonts");
        Assert.Equal(File.ReadAllBytes(Wwwroot("fonts", FontFile)), File.ReadAllBytes(Path.Combine(output, FontFile)));
        foreach (string license in LicenseFiles)
        {
            Assert.True(File.Exists(Path.Combine(output, license)), $"构建输出缺许可文件:{license}");
        }
    }

    private static bool IsCjk(int cp) =>
        cp is >= 0x3000 and <= 0x303F   // CJK 符号与标点
            or >= 0x3400 and <= 0x4DBF  // 扩展 A
            or >= 0x4E00 and <= 0x9FFF  // 基本区
            or >= 0xF900 and <= 0xFAFF  // 兼容汉字
            or >= 0xFF00 and <= 0xFFEF; // 半角与全角形式

    private static string Wwwroot(params string[] parts) =>
        RepositoryFile(new[] { "csharp", "src", "AiResume.Gui", "wwwroot" }.Concat(parts).ToArray());

    private static string RepositoryFile(params string[] parts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "CLAUDE.md")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine(new[] { current!.FullName }.Concat(parts).ToArray());
    }

    /// <summary>只读 WOFF2 里不做变换的表(cmap、head、hhea、OS/2 等);glyf/loca/hmtx 可能被变换,不在用途内。</summary>
    private sealed class Woff2Font
    {
        // WOFF2 规范 §5 的已知表标签,标志字节低 6 位是它的下标,63 表示后面跟 4 字节标签。
        private static readonly string[] KnownTags =
        [
            "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep",
            "CFF ", "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE",
            "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt",
            "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar",
            "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
        ];

        private readonly Dictionary<string, (int Offset, int Length, bool Transformed)> _tables = new(StringComparer.Ordinal);
        private byte[] _data = [];

        public static Woff2Font Read(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            Assert.Equal("wOF2", Encoding.ASCII.GetString(file, 0, 4));
            int numTables = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(12));
            int totalCompressedSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(20)));

            var font = new Woff2Font();
            int position = 48;
            int offset = 0;
            for (int i = 0; i < numTables; i++)
            {
                byte flags = file[position++];
                string tag;
                if ((flags & 0x3F) == 63)
                {
                    tag = Encoding.ASCII.GetString(file, position, 4);
                    position += 4;
                }
                else
                {
                    tag = KnownTags[flags & 0x3F];
                }

                int transformVersion = flags >> 6;
                int length = ReadBase128(file, ref position);
                bool transformed = tag is "glyf" or "loca" ? transformVersion != 3 : transformVersion != 0;
                if (transformed)
                {
                    length = ReadBase128(file, ref position);
                }

                font._tables[tag] = (offset, length, transformed);
                offset += length;
            }

            using var compressed = new MemoryStream(file, position, totalCompressedSize);
            using var brotli = new BrotliStream(compressed, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            brotli.CopyTo(decompressed);
            font._data = decompressed.ToArray();
            Assert.Equal(offset, font._data.Length);
            return font;
        }

        public byte[] Table(string tag)
        {
            Assert.True(_tables.TryGetValue(tag, out var entry), $"字体里没有 {tag} 表");
            Assert.False(entry.Transformed, $"{tag} 表被变换过,不能直接读");
            return _data.AsSpan(entry.Offset, entry.Length).ToArray();
        }

        /// <summary>cmap 里映射到非 0 字形的全部码位(格式 4 与格式 12)。</summary>
        public HashSet<int> CodePoints()
        {
            byte[] cmap = Table("cmap");
            var set = new HashSet<int>();
            int subtables = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(2));
            for (int i = 0; i < subtables; i++)
            {
                int at = checked((int)BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(4 + i * 8 + 4)));
                int format = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(at));
                if (format == 12)
                {
                    long groups = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(at + 12));
                    for (int g = 0; g < groups; g++)
                    {
                        int group = at + 16 + g * 12;
                        uint start = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(group));
                        uint end = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(group + 4));
                        for (uint cp = start; cp <= end; cp++)
                        {
                            set.Add((int)cp);
                        }
                    }
                }
                else if (format == 4)
                {
                    int segX2 = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(at + 6));
                    int ends = at + 14;
                    int starts = ends + segX2 + 2;
                    int deltas = starts + segX2;
                    int rangeOffsets = deltas + segX2;
                    for (int s = 0; s < segX2 / 2; s++)
                    {
                        int end = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(ends + s * 2));
                        int start = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(starts + s * 2));
                        int delta = BinaryPrimitives.ReadInt16BigEndian(cmap.AsSpan(deltas + s * 2));
                        int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(rangeOffsets + s * 2));
                        for (int cp = start; cp <= end && cp != 0xFFFF; cp++)
                        {
                            int glyph;
                            if (rangeOffset == 0)
                            {
                                glyph = (cp + delta) & 0xFFFF;
                            }
                            else
                            {
                                int address = rangeOffsets + s * 2 + rangeOffset + (cp - start) * 2;
                                glyph = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(address));
                                glyph = glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
                            }

                            if (glyph != 0)
                            {
                                set.Add(cp);
                            }
                        }
                    }
                }
            }

            return set;
        }

        private static int ReadBase128(byte[] buffer, ref int position)
        {
            long value = 0;
            for (int i = 0; i < 5; i++)
            {
                byte b = buffer[position++];
                value = (value << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    return checked((int)value);
                }
            }

            throw new InvalidDataException("WOFF2 UIntBase128 超过 5 字节");
        }
    }
}
