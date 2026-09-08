using System.Text.Json;

namespace AiResume.Worker.Resume;

/// <summary>官方 exec JSONL 不含结构化限流码；错误只能阻断，不能按文案授权重放。</summary>
public sealed class CodexResumeEvidence
{
    public bool Completed { get; private set; }
    public bool Failed { get; private set; }
    public bool SideEffects { get; private set; }
    public bool ThreadMatched { get; private set; }
    public bool TurnStarted { get; private set; }
    public bool Success => Completed && ThreadMatched && TurnStarted && !Failed;

    public void Inspect(string line, string sessionId)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            string? type = root.GetProperty("type").GetString();
            if (Completed) { Failed = true; return; }
            switch (type)
            {
                case "thread.started":
                    ThreadMatched = root.GetProperty("thread_id").GetString() == sessionId;
                    Failed |= !ThreadMatched;
                    break;
                case "turn.started": TurnStarted = true; break;
                case "turn.completed": Completed = true; break;
                case "turn.failed":
                case "error": Failed = true; break;
                case "item.started":
                case "item.updated":
                case "item.completed":
                    string? item = root.GetProperty("item").GetProperty("type").GetString();
                    switch (item)
                    {
                        case "agent_message": case "reasoning": case "todo_list": case "web_search": break;
                        case "command_execution": case "file_change": case "mcp_tool_call": case "collab_tool_call":
                            SideEffects = true; break;
                        default: Failed = true; SideEffects = true; break;
                    }
                    break;
                default: Failed = true; SideEffects = true; break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Failed = true;
            SideEffects = true;
        }
    }
}
