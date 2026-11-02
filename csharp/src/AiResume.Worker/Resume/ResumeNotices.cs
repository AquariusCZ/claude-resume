using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiResume.Worker.Resume;

/// <summary>
/// 续跑引擎要告诉用户的一件事。<see cref="DedupeKey"/> 决定事件 ID:同一件事无论写几次、
/// Worker 重启几次，投递端都只发一次(文件名去重 + 7 天已发送表 + 飞书 uuid 幂等)。
/// </summary>
/// <param name="Type">
/// session-open(额度已恢复但会话仍开在客户端)/ resume-started / resume-succeeded / resume-failed /
/// credentials-unavailable(登录令牌过期，读不到额度)/ provider-changed(账号变化，需重新布防)。
/// </param>
public sealed record ResumeNotice(string Type, string ProjectPath, string DedupeKey, string? ModelId = null, string? Detail = null);

public interface IResumeNotices
{
    void Post(ResumeNotice notice);
}

/// <summary>
/// 把续跑提醒写进完成通知队列(<c>completion-events</c>),与钩子事件同格式、同一个投递端。
/// 只写项目路径、模型 ID 与状态码，不写令牌、账号或会话正文。
/// </summary>
public sealed class ResumeNoticeWriter(string eventsDirectory, Func<DateTimeOffset>? clock = null) : IResumeNotices
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public static string EventIdFor(ResumeNotice notice) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ai-resume|" + notice.Type + "|" + notice.DedupeKey)))[..16]
            .ToLowerInvariant();

    public void Post(ResumeNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        string eventId = EventIdFor(notice);
        string target = Path.Combine(eventsDirectory, eventId + ".json");
        if (File.Exists(target))
        {
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["eventId"] = eventId,
            ["source"] = "ai-resume",
            ["kind"] = "resume",
            ["notificationType"] = notice.Type,
            ["cwd"] = notice.ProjectPath,
            ["model"] = notice.ModelId,
            ["detail"] = notice.Detail,
            ["atUtc"] = _clock().ToString("o", CultureInfo.InvariantCulture),
        };

        Directory.CreateDirectory(eventsDirectory);
        string temp = Path.Combine(eventsDirectory, eventId + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temp, target, overwrite: false);
        }
        catch (IOException) when (File.Exists(target))
        {
            // 并发写了同一件事：已经在队列里，正是想要的结果。
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
