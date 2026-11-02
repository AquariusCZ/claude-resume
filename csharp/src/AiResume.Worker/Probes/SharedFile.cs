using System.Text;

namespace AiResume.Worker.Probes;

/// <summary>
/// 读别人(Claude Code、Codex、OpenCode)的凭据与配置文件:共享读写与删除打开。
/// <see cref="File.ReadAllText(string)"/> 以 <c>FileShare.Read</c> 打开，句柄存续期间对方写不进、也换不掉自己的文件——
/// 恰好撞上它刷新令牌时，新令牌落不了盘，用户在那个客户端里被登出。
/// </summary>
internal static class SharedFile
{
    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
