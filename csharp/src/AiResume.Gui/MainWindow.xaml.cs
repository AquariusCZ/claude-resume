using System.IO;
using System.Windows;
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

    private readonly ControlPlaneBridge _bridge;
    private readonly CancellationTokenSource _cts = new();

    public MainWindow()
    {
        InitializeComponent();
        _bridge = new ControlPlaneBridge(folderPicker: PickFolderAsync);
        Loaded += async (_, _) => await InitializeHostAsync();
        Closed += (_, _) => _cts.Cancel();
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
            // 用户数据目录落 shadow 目录,绝不写生产 AppDir。
            string userData = Path.Combine(AiResume.Worker.ShadowPaths.Root, "webview2");
            Directory.CreateDirectory(userData);

            CoreWebView2Environment env = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: userData)
                .ConfigureAwait(true);

            await Host.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            CoreWebView2 core = Host.CoreWebView2;

            // 控制面是本机工具,不需要这些浏览器能力;关掉以缩小攻击面。
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true; // 开发期保留;发布前由配置关闭
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

            core.Navigate($"https://{VirtualHost}/index.html");
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException)
        {
            SkeletonText.Text = "未检测到 WebView2 运行时。请安装 Microsoft Edge WebView2 Runtime 后重开。";
        }
        catch (Exception ex)
        {
            SkeletonText.Text = $"控制面初始化失败:{ex.Message}";
        }
    }

    /// <summary>
    /// 自测支持:`--screenshot &lt;png 路径&gt;` 时,等页面数据填充后截取 WebView 内容并退出。
    /// 经 CapturePreviewAsync 截图,不依赖窗口是否在前台(Windows 前台锁定会让屏幕抓取失效),
    /// 因此可在无人值守下产出可复核的界面证据(对应现役 picker.ps1 的 -RenderTo)。
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
            await Task.Delay(20000).ConfigureAwait(true);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            await using var fs = new FileStream(target, FileMode.Create, FileAccess.Write);
            await Host.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"截图失败:{ex.Message}");
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
