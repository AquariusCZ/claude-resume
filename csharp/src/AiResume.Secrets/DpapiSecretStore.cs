using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AiResume.Secrets;

/// <summary>
/// DPAPI(CurrentUser)机密存储(S2-F 实质实现)。
///
/// 契约(规格 §3.7):credential_ref → &lt;root&gt;\secrets\&lt;ref&gt;.bin,密文由
/// CryptProtectData(CurrentUser)保护,仅当前 Windows 用户可解密;
/// 日志/事件/异常绝不含明文。
///
/// 实现要点:
/// 1. 不新增 NuGet 依赖:DPAPI 直接 P/Invoke crypt32.dll(CryptProtectData/
///    CryptUnprotectData/LocalFree),与 ProcessSupervisor 的 Job Object P/Invoke 同风格。
/// 2. credentialRef 白名单校验([A-Za-z0-9._-]),杜绝路径穿越与目录注入。
/// 3. 原子写:先写 .tmp 再替换(崩溃不产生半截密文文件);替换前 fsync 落盘。
/// 4. Load 解密失败(非本用户/密文损坏)抛 CryptographicException,不落任何明文。
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
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        cancellationToken.ThrowIfCancellationRequested();
        string path = SecretPath(credentialRef);
        Directory.CreateDirectory(_secretsDirectory);

        byte[] cipher = Protect(plaintext);
        try
        {
            string tmpPath = path + ".tmp";
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(cipher, 0, cipher.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmpPath, path, overwrite: true);
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

    private static byte[] Protect(byte[] plaintext)
    {
        var input = new DataBlob(plaintext);
        try
        {
            // ref 不能指向属性,先取局部副本(ref struct 语义:仅本次调用有效)。
            NativeBlob blob = input.Blob;
            if (!CryptProtectData(ref blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out NativeBlob output))
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
