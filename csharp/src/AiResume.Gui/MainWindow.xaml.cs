using System.IO;
using System.Windows;
using AiResume.Secrets;
using AiResume.Worker.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace AiResume.Gui;

/// <summary>
/// S7-B 控制面宿主窗口(ADR-0003 §5 选型 D:WPF 壳 + WebView2 + Web 前端)。
///
/// 启动时序(§5.4 硬约束——首帧不得依赖 I/O):
///   窗口与骨架立即可见 → 异步初始化 WebView2 → 导航到虚拟主机 → 页面自行拉数据填充。
/// 初始化失败不弹异常,降级为骨架上的可读错误文本。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>前端资源的虚拟主机名;用 https 而非 file:// 以获得正常的同源与 fetch 行为。</summary>
    private const string VirtualHost = "controlplane.airesume.local";
    private const string DevToolsEnvironmentVariable = "AI_RESUME_ENABLE_WEBVIEW_DEVTOOLS";

    private readonly ControlPlaneBridge _bridge;
    private readonly DailyJsonFileLoggerProvider _loggerProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _screenshotMode;

    public MainWindow()
    {
        InitializeComponent();
        _screenshotMode = Environment.GetCommandLineArgs().Any(
            arg => string.Equals(arg, "--screenshot", StringComparison.OrdinalIgnoreCase));
        _loggerProvider = new DailyJsonFileLoggerProvider(AiResume.Worker.ShadowPaths.LogsDirectory, "gui");
        _logger = _loggerProvider.CreateLogger(typeof(MainWindow).FullName!);
        // 公开截图必须使用合成数据，不能读取或展示真实项目、用户名、凭据和本机路径。
        _bridge = new ControlPlaneBridge(
            folderPicker: PickFolderAsync,
            probeFailureReporter: (probe, exceptionType) => _logger.LogWarning(
                "gui.provider_probe.failed probe={Probe} exception_type={ExceptionType}",
                probe,
                exceptionType),
            requestFailureReporter: (requestType, exceptionType) => _logger.LogError(
                "gui.control_plane.request_failed request_type={RequestType} exception_type={ExceptionType}",
                requestType,
                exceptionType),
            demoMode: _screenshotMode);
        Loaded += async (_, _) => await InitializeHostAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _loggerProvider.Dispose();
        };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_bridge.IsCutoverInProgress)
        {
            return;
        }

        e.Cancel = true;
        MessageBox.Show(
            this,
            "cc-connect 正在切换配置并验证新进程。为保证失败时可以完成回滚,请等待操作结束后再关闭窗口。",
            "正在切换 cc-connect",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// 「添加项目」的原生选目录对话框。桥接层的请求处理跑在线程池上,
    /// 而 WPF 对话框必须在 UI 线程弹出,所以这里显式切回 Dispatcher。
    /// 用户取消返回 null,由桥接层转成 <c>{path:null}</c>——取消不是错误。
    /// </summary>
    private Task<string?> PickFolderAsync(CancellationToken cancellationToken)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择要加入续跑队列的项目目录",
                Multiselect = false,
            };

            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }).Task;
    }

    private async Task InitializeHostAsync()
    {
        try
        {
            // EnsureRoot 建目录并把旧 ClaudeResumeShadow 的内容搬到
            // %LOCALAPPDATA%\AI Resume\state —— GUI 常常是先被打开的那一个,
            // 所以迁移必须在这里也触发,不能只挂在 Worker 上。
            string userData = Path.Combine(AiResume.Worker.ShadowPaths.EnsureRoot(), "webview2");
            Directory.CreateDirectory(userData);

            CoreWebView2Environment env = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: userData)
                .ConfigureAwait(true);

            await Host.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            CoreWebView2 core = Host.CoreWebView2;

            // 控制面是本机工具,不需要这些浏览器能力;关掉以缩小攻击面。
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = ShouldEnableDevTools();
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                {
                    Skeleton.Visibility = Visibility.Collapsed;
                    Host.Visibility = Visibility.Visible;
                    await CaptureIfRequestedAsync();
                }
                else
                {
                    SkeletonText.Text = $"控制面加载失败(导航错误 {e.WebErrorStatus})。";
                }
            };

            // WebView2 的用户数据目录是持久的。固定 URL 会在重新安装 HTML 后仍命中旧缓存，
            // 表现成“源码和安装哈希已更新，但界面还是上一版”。文件时间戳只用于换缓存键，
            // 页面仍由本机虚拟主机加载，不引入任何网络请求。
            string indexPath = Path.Combine(wwwroot, "index.html");
            long cacheVersion = File.GetLastWriteTimeUtc(indexPath).Ticks;
            core.Navigate($"https://{VirtualHost}/index.html?v={cacheVersion}");
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException)
        {
            SkeletonText.Text = "未检测到 WebView2 运行时。请安装 Microsoft Edge WebView2 Runtime 后重开。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gui.webview.initialize_failed");
            SkeletonText.Text = $"控制面初始化失败:{SecretRedactor.RedactText(ex.Message)}";
        }
    }

    internal static bool ShouldEnableDevTools(Func<string, string?>? environmentVariable = null)
    {
        environmentVariable ??= Environment.GetEnvironmentVariable;
        string? value;
        try
        {
            value = environmentVariable(DevToolsEnvironmentVariable);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException)
        {
            return false;
        }

        return string.Equals(value, "1", StringComparison.Ordinal) ||
               bool.TryParse(value, out bool enabled) && enabled;
    }

    /// <summary>
    /// 自测支持:`--screenshot &lt;png 路径&gt;` 时,等页面数据填充后截取 WebView 内容并退出。
    /// 经 CapturePreviewAsync 截图,不依赖窗口是否在前台(Windows 前台锁定会让屏幕抓取失效),
    /// 因此可在无人值守下产出可复核的界面证据(替代 v1 picker.ps1 的 -RenderTo)。
    /// </summary>
    private async Task CaptureIfRequestedAsync()
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.FindIndex(args, a => string.Equals(a, "--screenshot", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length)
        {
            return;
        }

        string target = args[i + 1];
        try
        {
            // 给前端时间完成首次数据拉取与渲染,否则截到的是骨架态。
            // 12 秒是被额度探测抬上来的:它要拉起 claude 子进程,冷缓存实测约 7 秒
            // (项目发现只要几十毫秒)。截图是自测通道,宁可慢也不要截出"正在探测"。
            await Task.Delay(_screenshotMode ? 2000 : 20000).ConfigureAwait(true);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            await using var fs = new FileStream(target, FileMode.Create, FileAccess.Write);
            await Host.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gui.screenshot.capture_failed");
            Console.Error.WriteLine($"截图失败:{SecretRedactor.RedactText(ex.Message)}");
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// 前端 → 宿主。处理全程在线程池,完成后回到 UI 线程投递应答,
    /// 保证任何一次数据拉取都不阻塞窗口。
    /// </summary>
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string request;
        try
        {
            request = e.WebMessageAsJson;
        }
        catch (Exception)
        {
            return;
        }

        string response;
        try
        {
            response = await _bridge.HandleAsync(request, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            Host.CoreWebView2?.PostWebMessageAsJson(response);
        }
        catch (Exception)
        {
            // 窗口关闭途中投递失败:忽略。
        }
    }
}
