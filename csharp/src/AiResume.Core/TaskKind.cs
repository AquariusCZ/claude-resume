namespace AiResume.Core;

/// <summary>RunContract 的 taskKind。probe 也走同一 Start/Status/Cancel。</summary>
public enum TaskKind
{
    Chat,
    Query,
    Modify,
    Resume,
    Probe,
}

public static class TaskKindCodes
{
    public static string ToWireCode(this TaskKind taskKind) => taskKind switch
    {
        TaskKind.Chat => "chat",
        TaskKind.Query => "query",
        TaskKind.Modify => "modify",
        TaskKind.Resume => "resume",
        TaskKind.Probe => "probe",
        _ => throw new ArgumentOutOfRangeException(nameof(taskKind), taskKind, "未知 TaskKind。"),
    };
}
