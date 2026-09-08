namespace AiResume.Core;

/// <summary>
/// Claude Code 续跑可绑定的模型族。OAuth 的 <c>weekly_scoped</c> 名称与 CLI 的
/// <c>--model</c> 参数形状不同，二者只能在明确识别为同一已知模型族时配对。
/// </summary>
public static class ClaudeModelFamilies
{
    private static readonly string[] Families = ["fable", "opus", "sonnet", "haiku"];

    public static IReadOnlyList<string> Supported { get; } = Array.AsReadOnly(Families);

    /// <summary>
    /// 校验并规范化用户配置的 CLI 模型。只接受已知短别名或以 <c>claude-</c>
    /// 开头、且只包含一个已知模型族 token 的官方完整 id。
    /// </summary>
    public static bool TryNormalizeConfiguredModel(string? value, out string family)
    {
        family = string.Empty;
        if (string.IsNullOrEmpty(value) ||
            value.Any(character => character is not (>= 'a' and <= 'z')
                and not (>= 'A' and <= 'Z')
                and not (>= '0' and <= '9')
                and not '-'))
        {
            return false;
        }

        string? alias = Families.FirstOrDefault(candidate =>
            string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
        if (alias is not null)
        {
            family = alias;
            return true;
        }

        string[] parts = value.Split('-', StringSplitOptions.None);
        if (parts.Length < 3 ||
            !parts[0].Equals("claude", StringComparison.OrdinalIgnoreCase) ||
            parts.Skip(1).Any(string.IsNullOrEmpty))
        {
            return false;
        }

        int[] familyIndexes = parts
            .Select((part, index) => (part, index))
            .Where(item => item.index > 0 &&
                Families.Contains(item.part, StringComparer.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        if (familyIndexes.Length != 1 || familyIndexes[0] == parts.Length - 1)
        {
            return false;
        }

        int familyIndex = familyIndexes[0];
        // 兼容 claude-3-5-sonnet-* 等旧式官方 id；模型族之前只允许版本数字，
        // 避免把 claude-preview-fable-* 这类任意字符串静默改写成有效别名。
        if (familyIndex > 1 && parts[1..familyIndex].Any(part => !part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        family = Families.Single(candidate =>
            candidate.Equals(parts[familyIndex], StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>从 OAuth scoped 窗口的显示名中提取已知模型族。</summary>
    public static bool TryNormalizeScopeName(string? value, out string family)
    {
        family = string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int identitySuffix = value.LastIndexOf('#');
        if (identitySuffix > 0 &&
            value.Length - identitySuffix == 7 &&
            value[(identitySuffix + 1)..].All(Uri.IsHexDigit))
        {
            value = value[..identitySuffix];
        }

        // 小数点只能分隔版本数字，不能把 Fable..5 或 Fable.5 误认成正常名称。
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '.' && (i == 0 || i == value.Length - 1 ||
                !char.IsAsciiDigit(value[i - 1]) || !char.IsAsciiDigit(value[i + 1]))) return false;
        string[] parts = value.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        int familyIndex = parts.Length > 0 &&
            parts[0].Equals("claude", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
        if (parts.Length <= familyIndex ||
            !Families.Contains(parts[familyIndex], StringComparer.OrdinalIgnoreCase) ||
            parts.Skip(familyIndex + 1).Any(part =>
                Families.Contains(part, StringComparer.OrdinalIgnoreCase)) ||
            parts.Skip(familyIndex + 1).Any(part => part.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9'))))
        {
            return false;
        }

        family = Families.Single(candidate =>
            candidate.Equals(parts[familyIndex], StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public static bool ScopeMatchesModel(string scope, string model)
    {
        if (!TryNormalizeScopeName(scope, out string family) ||
            !TryNormalizeConfiguredModel(model, out string configured) || family != configured) return false;
        string label = scope.Split('#')[0];
        string[] parts = label.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        int index = Array.FindIndex(parts, p => p.Equals(family, StringComparison.OrdinalIgnoreCase));
        string[] scopeVersion = parts.Skip(index + 1).ToArray();
        if (scopeVersion.Length == 0 || model.Equals(family, StringComparison.OrdinalIgnoreCase)) return true;
        if (scopeVersion.Any(p => !p.All(char.IsAsciiDigit))) return false;
        string[] modelParts = model.Split('-');
        string[] modelVersion = modelParts.Skip(1).Where(p => p.All(char.IsAsciiDigit) && p.Length < 8).ToArray();
        return scopeVersion.SequenceEqual(modelVersion, StringComparer.Ordinal);
    }
}
