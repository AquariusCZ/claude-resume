using System.Text;

namespace AiResume.Worker.Notifications;

/// <summary>
/// 飞书通知收件人 open_id 的规整与校验。
///
/// <b>剥掉全部空白，不只是首尾。</b>open_id 里不可能有空白，而从飞书后台复制时中间夹进一个空格
/// (或不换行空格)是真实发生过的事:2026-08-08 白名单里的 open_id 被拆成两段，
/// 每条消息都匹配不上、日志一行报错都没有，所有健康检查还全绿。
/// </summary>
public static class NotifyRecipient
{
    /// <summary>去掉全部空白字符(含不换行空格、零宽字符)。null → 空串。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (!char.IsWhiteSpace(c) && c is not ('​' or '‌' or '‍' or '﻿'))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>规整后是否是一个 open_id:<c>ou_</c> 前缀加字母数字。不硬编码长度，飞书没有承诺过。</summary>
    public static bool IsValid(string? value)
    {
        string normalized = Normalize(value);
        return normalized.Length > 3 &&
               normalized.StartsWith("ou_", StringComparison.Ordinal) &&
               normalized.AsSpan(3).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789") < 0;
    }
}
