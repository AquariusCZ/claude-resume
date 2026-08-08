namespace AiResume.Worker.Migration;

/// <summary>
/// <c>AiResume.Worker.exe import-feishu</c>(S10):把现役 AppDir 里的
/// <c>feishuAppId</c> / <c>feishuAppSecret</c> 搬进 DPAPI。
///
/// 切换用的是同一个飞书应用,凭据本来就在本机。机器到机器搬运比让用户去开放平台
/// 重抄一遍少一次经手。**只读这两个键**,其余键(含 openaiApiKey 等)一律不取值;
/// 值不打印、不入日志,只回显遮蔽后的 app_id 供确认搬对了。
/// </summary>
public static class ImportFeishuCommand
{
    public static int Run()
    {
        string legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeResume", "config.json");

        var store = new FeishuCredentialStore();
        try
        {
            store.ImportFromLegacy(legacy);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"导入失败:{ex.Message}");
            return 1;
        }

        FeishuCredentialStatus status = store.Describe();
        Console.WriteLine($"已从现役配置导入并经 DPAPI 加密保存:{status.AppIdMasked}");
        Console.WriteLine("下一步:`AiResume.Worker.exe cutover-config` 生成 cc-connect 配置。");
        return 0;
    }
}
