using System.Security.Cryptography;
using AiResume.Wrapper;

namespace AiResume.Worker.Migration;

/// <summary>一次 cc-connect 配置激活的结果。<see cref="SanitizedToml"/> 已脱敏,可回显。</summary>
public sealed record CcConnectApplyResult(
    bool Ok,
    string Message,
    int ProjectCount,
    string? SanitizedToml,
    string? OutPath,
    bool ConfigWritten,
    bool RestartVerified,
    string? Agent,
    int? Pid,
    string Phase);

/// <summary>
/// 生成候选配置、经 cc-connect 自己的解析器验证、原子提交并通过管理 API 自重启,最后核验新进程换代。
///
/// GUI 的「生成并重启 cc-connect」与 CLI <c>AiResume.Worker.exe cc-connect-apply</c> 共用这一条路径,
/// 避免两处逻辑各自漂移;跨窗口/跨进程由仓库外的 <see cref="CcConnectApplyLock"/> 串行化。
/// 全部判据与回滚规则见项目 <c>CLAUDE.md</c>「cc-connect 配置激活契约」。
/// </summary>
public static class CcConnectApplyCommand
{
    public static CcConnectApplyResult Apply(
        string outPath,
        Func<string, CutoverConfigCommand.GenerateResult> generate,
        Func<CcConnectDaemonController> controllerFactory)
    {
        bool configWritten = false;
        string? candidatePath = null;
        try
        {
            using CcConnectApplyLock applyLock = CcConnectApplyLock.Acquire(outPath);
            string directory = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
            Directory.CreateDirectory(directory);
            candidatePath = Path.Combine(directory, ".config.toml.ai-resume-candidate-" + Guid.NewGuid().ToString("N"));
            byte[]? original = File.Exists(outPath) ? File.ReadAllBytes(outPath) : null;
            byte[]? originalHash = original is null ? null : SHA256.HashData(original);
            if (original is not null)
            {
                File.WriteAllBytes(candidatePath, original);
            }

            CutoverConfigCommand.GenerateResult r = generate(candidatePath);
            if (!r.Ok || string.IsNullOrWhiteSpace(r.OutPath))
            {
                return new CcConnectApplyResult(
                    false, r.Message, r.ProjectCount, r.SanitizedToml, outPath,
                    ConfigWritten: false, RestartVerified: false, Agent: null, Pid: null, Phase: "generate");
            }

            string candidateToml = File.ReadAllText(candidatePath);
            byte[] candidateHash = SHA256.HashData(File.ReadAllBytes(candidatePath));
            string expectedAgent = CcConnectConfigValidator
                .ReadProjectAgentTriple(candidateToml, CutoverConfigCommand.ProjectName).Agent;
            if (expectedAgent.Length == 0)
            {
                return new CcConnectApplyResult(
                    false, "候选配置无法读出 ai-resume 项目的 agent,生产配置未改动。",
                    r.ProjectCount, r.SanitizedToml, outPath,
                    ConfigWritten: false, RestartVerified: false, Agent: null, Pid: null, Phase: "validate");
            }

            string candidate = candidatePath;
            CcConnectDaemonRestartResult restart = controllerFactory().ActivateAndVerify(
                outPath,
                candidatePath,
                CutoverConfigCommand.ProjectName,
                expectedAgent,
                commitConfiguration: () =>
                {
                    EnsureProductionConfigUnchanged(outPath, originalHash);
                    File.Move(candidate, outPath, overwrite: true);
                    configWritten = true;
                },
                rollbackConfiguration: () =>
                {
                    EnsureProductionConfigMatches(outPath, candidateHash);
                    if (original is null)
                    {
                        File.Delete(outPath);
                    }
                    else
                    {
                        WriteBytesAtomically(outPath, original);
                    }
                    configWritten = false;
                });

            // SanitizedToml 里的 app_secret 已是 [REDACTED],可以安全展示给用户复核。
            return new CcConnectApplyResult(
                restart.Ok,
                restart.Message,
                r.ProjectCount,
                r.SanitizedToml,
                outPath,
                ConfigWritten: restart.ConfigWritten,
                RestartVerified: restart.Ok,
                Agent: expectedAgent,
                Pid: restart.CurrentPid,
                Phase: restart.Phase);
        }
        catch (Exception ex)
        {
            return new CcConnectApplyResult(
                false, AiResume.Secrets.SecretRedactor.RedactText(ex.Message), 0, null, outPath,
                ConfigWritten: configWritten, RestartVerified: false, Agent: null, Pid: null, Phase: "exception");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(candidatePath) && File.Exists(candidatePath))
            {
                try { File.Delete(candidatePath); } catch (Exception) { }
            }
        }
    }

    /// <summary><c>AiResume.Worker.exe cc-connect-apply [--out &lt;config.toml&gt;]</c>。退出码 0 = 新进程换代已验证。</summary>
    public static int Run(string[] args)
    {
        string outPath = ReadOption(args, "--out") ?? CutoverConfigCommand.DefaultConfigPath;
        CcConnectApplyResult result = Apply(
            outPath,
            path => CutoverConfigCommand.Generate(path, appId: null, appSecret: null, requireLoadable: true),
            () => new CcConnectDaemonController());

        Console.WriteLine($"阶段:{result.Phase} · 配置:{(result.ConfigWritten ? "已提交" : "未提交")} · 新进程:{(result.RestartVerified ? "已验证" : "未验证")}");
        Console.WriteLine(result.Message);
        if (!string.IsNullOrWhiteSpace(result.SanitizedToml))
        {
            Console.WriteLine();
            Console.WriteLine(result.SanitizedToml);
        }

        return result.Ok ? 0 : 1;
    }

    private static void EnsureProductionConfigUnchanged(string path, byte[]? expectedHash)
    {
        if (expectedHash is null)
        {
            if (File.Exists(path))
            {
                throw new InvalidOperationException("候选校验期间生产 config.toml 被外部创建,拒绝覆盖。请重新生成。");
            }

            return;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException("候选校验期间生产 config.toml 被外部删除,拒绝提交。请重新生成。");
        }

        byte[] currentHash = SHA256.HashData(File.ReadAllBytes(path));
        if (!CryptographicOperations.FixedTimeEquals(currentHash, expectedHash))
        {
            throw new InvalidOperationException("候选校验期间生产 config.toml 已被外部修改,拒绝覆盖。请重新生成。");
        }
    }

    private static void EnsureProductionConfigMatches(string path, byte[] expectedHash)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("准备回滚时生产 config.toml 已被外部删除,拒绝覆盖。请人工核对。");
        }

        byte[] currentHash = SHA256.HashData(File.ReadAllBytes(path));
        if (!CryptographicOperations.FixedTimeEquals(currentHash, expectedHash))
        {
            throw new InvalidOperationException("准备回滚时生产 config.toml 已被外部修改,拒绝覆盖。请人工核对。");
        }
    }

    private static void WriteBytesAtomically(string path, byte[] content)
    {
        string temp = path + ".rollback-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (Exception) { }
            }
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
