using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace AiResume.Secrets;

/// <summary>DPAPI 加密范围。解密不需要指定:密文自带范围信息。</summary>
public enum DpapiScope
{
    /// <summary>仅当前 Windows 用户可解(默认)。</summary>
    CurrentUser,

    /// <summary>本机任何账号可解;只用于必须在 S4U 会话里解密的材料,见 SaveAsync 重载说明。</summary>
    LocalMachine,
}

/// <summary>
/// DPAPI 机密存储(S2-F 实质实现)。默认 CurrentUser 范围;LocalMachine 范围只给 S4U 会话必须解密的材料,
/// 且保存时把文件 ACL 收紧到当前用户 + SYSTEM + Administrators。
///
/// 契约(规格 §3.7):credential_ref → &lt;root&gt;\secrets\&lt;ref&gt;.bin,密文由
/// CryptProtectData 保护;日志/事件/异常绝不含明文。
///
/// 实现要点:
/// 1. 不新增 NuGet 依赖:DPAPI 直接 P/Invoke crypt32.dll(CryptProtectData/
///    CryptUnprotectData/LocalFree),与 ProcessSupervisor 的 Job Object P/Invoke 同风格。
/// 2. credentialRef 白名单校验([A-Za-z0-9._-]),杜绝路径穿越与目录注入。
/// 3. 原子写:先写 .tmp 再替换(崩溃不产生半截密文文件);替换前 fsync 落盘。
/// 4. Load 解密失败(非本用户/密文损坏)抛 Win32Exception,不落任何明文。
/// 5. 明文仅存在于本次调用栈内,不做任何日志/异常参数传递。
/// </summary>
public sealed class DpapiSecretStore
{
    /// <summary>UI 提示禁止标志:纯后台解密,绝不弹出系统对话框。</summary>
    private const uint CryptProtectUiForbidden = 0x1;

    private readonly string _secretsDirectory;

    public DpapiSecretStore(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _secretsDirectory = Path.Combine(root, "secrets");
    }

    public Task SaveAsync(string credentialRef, byte[] plaintext, CancellationToken cancellationToken)
        => SaveAsync(credentialRef, plaintext, DpapiScope.CurrentUser, cancellationToken);

    /// <summary>
    /// 按指定范围加密保存。
    ///
    /// <see cref="DpapiScope.LocalMachine"/> 只给**必须在 S4U 计划任务里解密**的材料用:
    /// 2026-09/10 真机日志证明,会话 0 的 S4U Worker 解 CurrentUser 密文会间歇失败
    /// (<c>CryptUnprotectData</c> 0x8009000B,一次持续数小时),而机器范围不依赖用户主密钥。
    /// 代价是本机任何能读到该文件的账号都能解,所以保存后显式把文件 ACL 收紧到
    /// 当前用户 + SYSTEM + Administrators(断开继承)并读回核验;收不紧就删掉文件并抛错,不留一份谁都能解的密文。
    /// </summary>
    public Task SaveAsync(string credentialRef, byte[] plaintext, DpapiScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        cancellationToken.ThrowIfCancellationRequested();
        string path = SecretPath(credentialRef);
        Directory.CreateDirectory(_secretsDirectory);

        byte[] cipher = Protect(plaintext, scope);
        string tmpPath = scope == DpapiScope.LocalMachine ? path + ".tmp-" + Guid.NewGuid().ToString("N") : path + ".tmp";
        try
        {
            if (scope == DpapiScope.LocalMachine)
            {
                // 机器范围密文从创建那一刻起就用受限 DACL,在临时文件上核验通过后才替换正式文件(同卷改名保留 DACL):
                // 收不紧时只丢掉临时文件,上一份可用凭据原样保留;也不存在「已落盘、还没收紧」的时间窗。
                FileSecurity security = RestrictedFileSecurity(out SecurityIdentifier[] allowed);
                using (FileStream stream = new FileInfo(tmpPath).Create(
                           FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
                {
                    stream.Write(cipher, 0, cipher.Length);
                    stream.Flush(flushToDisk: true);
                }

                VerifyRestrictedAcl(tmpPath, allowed);
                File.Move(tmpPath, path, overwrite: true);
            }
            else
            {
                using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(cipher, 0, cipher.Length);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tmpPath, path, overwrite: true);
            }
        }
        catch (Exception ex) when (scope == DpapiScope.LocalMachine && ex is not OutOfMemoryException)
        {
            TryDelete(tmpPath);
            throw new InvalidOperationException("无法以受限权限保存机器范围密文,已放弃保存(原有凭据未改动):" + ex.Message, ex);
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
        }

        return Task.CompletedTask;
    }

    public Task<byte[]> LoadAsync(string credentialRef, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = SecretPath(credentialRef);
        if (!File.Exists(path))
        {
            throw new KeyNotFoundException($"credential '{credentialRef}' 不存在。");
        }

        byte[] cipher = File.ReadAllBytes(path);
        try
        {
            return Task.FromResult(Unprotect(cipher));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    public Task DeleteAsync(string credentialRef, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = SecretPath(credentialRef);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// credentialRef 白名单:[A-Za-z0-9._-],返回安全路径。
    /// 任何目录分隔符/相对路径片段直接拒绝(防路径穿越)。
    /// </summary>
    private string SecretPath(string credentialRef)
    {
        if (string.IsNullOrWhiteSpace(credentialRef))
        {
            throw new ArgumentException("credentialRef 不能为空。", nameof(credentialRef));
        }

        foreach (char c in credentialRef)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                throw new ArgumentException(
                    $"credentialRef 只允许字母数字与 ._- ,收到非法字符 '{c}'。", nameof(credentialRef));
            }
        }

        if (credentialRef is "." or "..")
        {
            throw new ArgumentException("credentialRef 不能是路径片段。", nameof(credentialRef));
        }

        return Path.Combine(_secretsDirectory, credentialRef + ".bin");
    }

    /// <summary>
    /// 机器范围密文的文件 DACL:断开继承,只留当前用户、SYSTEM、Administrators 的完全控制。
    /// </summary>
    private static FileSecurity RestrictedFileSecurity(out SecurityIdentifier[] allowed)
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("无法取得当前用户 SID");
        allowed =
        [
            user,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        ];

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier sid in allowed)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>读回核验:任何其它 SID 仍有 Allow 规则就抛错——宁可保存失败,也不留一份全机可解的密文。</summary>
    private static void VerifyRestrictedAcl(string path, SecurityIdentifier[] allowed)
    {
        FileSecurity applied = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        foreach (FileSystemAccessRule rule in applied.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference is SecurityIdentifier sid && !allowed.Contains(sid))
            {
                throw new InvalidOperationException("机器范围密文的文件权限收紧后仍有其它账号可读");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>CRYPTPROTECT_LOCAL_MACHINE:密文不绑定用户主密钥,本机任何账号可解。</summary>
    private const uint CryptProtectLocalMachine = 0x4;

    private static byte[] Protect(byte[] plaintext, DpapiScope scope = DpapiScope.CurrentUser)
    {
        var input = new DataBlob(plaintext);
        try
        {
            // ref 不能指向属性,先取局部副本(ref struct 语义:仅本次调用有效)。
            NativeBlob blob = input.Blob;
            uint flags = CryptProtectUiForbidden |
                         (scope == DpapiScope.LocalMachine ? CryptProtectLocalMachine : 0);
            if (!CryptProtectData(ref blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    flags, out NativeBlob output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptProtectData 失败。");
            }

            try
            {
                return output.ToBytes();
            }
            finally
            {
                LocalFree(output.PbData);
            }
        }
        finally
        {
            input.Free();
        }
    }

    private static byte[] Unprotect(byte[] cipher)
    {
        var input = new DataBlob(cipher);
        try
        {
            NativeBlob blob = input.Blob;
            if (!CryptUnprotectData(ref blob, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out NativeBlob output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptUnprotectData 失败(非本用户密文或已损坏)。");
            }

            try
            {
                return output.ToBytes();
            }
            finally
            {
                LocalFree(output.PbData);
            }
        }
        finally
        {
            input.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBlob
    {
        public uint CbData;
        public IntPtr PbData;

        public byte[] ToBytes()
        {
            byte[] result = new byte[CbData];
            Marshal.Copy(PbData, result, 0, (int)CbData);
            return result;
        }
    }

    /// <summary>托管字节 → DPAPI blob 的临时封装;Free 释放非托管缓冲。</summary>
    private sealed class DataBlob
    {
        public DataBlob(byte[] bytes)
        {
            Blob = new NativeBlob
            {
                CbData = (uint)bytes.Length,
                PbData = Marshal.AllocHGlobal(bytes.Length),
            };
            Marshal.Copy(bytes, 0, Blob.PbData, bytes.Length);
        }

        public NativeBlob Blob { get; }

        public void Free()
        {
            if (Blob.PbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Blob.PbData);
            }
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref NativeBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out NativeBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref NativeBlob pDataIn, out string? ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out NativeBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
