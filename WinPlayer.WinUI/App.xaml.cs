using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using WinPlayer.WinUI.Services;

// WinUI 项目结构和模板说明：http://aka.ms/winui-project-info。

namespace WinPlayer.WinUI
{
    /// <summary>
    /// 为默认 Application 类补充本程序所需的启动和生命周期行为。
    /// </summary>
    public partial class App : Application
    {
        // 模式化命名：普通模式无后缀，隐私模式带 ".b"（见 AppMode）。
        // 两种模式因此拥有各自的单例与激活通道，互不激活、互不转发。
        private static string InstanceMutexName => "Local\\WinPlayer.WinUI.SingleInstance" + AppMode.Suffix;
        private static string InstancePipeName => "WinPlayer.WinUI.Activation" + AppMode.Suffix;
        private MainWindow? _window;
        private Mutex? _instanceMutex;

        /// <summary>任务栏应用标识（AUMID）按模式设置，须在任何窗口创建之前调用。</summary>
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);

        /// <summary>
        /// 初始化应用程序单例，相当于传统桌面程序中的 main() 或 WinMain() 入口。
        /// </summary>
        public App()
        {
            // 任务栏身份：两模式使用不同 AUMID，任务栏显示为两个独立按钮，
            // 跳转列表/最近使用项也不会互相混入。必须在创建窗口之前设置。
            int aumidResult = -1;
            try { aumidResult = SetCurrentProcessExplicitAppUserModelID(AppMode.AppUserModelId); }
            catch { /* 任务栏身份设置失败不影响播放功能 */ }
            InitializeComponent();
            UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            // 隐私模式不把媒体路径写进日志。
            if (AppMode.IsPrivacy)
                AppLogService.Information("ApplicationStarted", "程序开始启动",
                    new { Mode = "privacy", ArgumentCount = Environment.GetCommandLineArgs().Length - 1, AppUserModelId = AppMode.AppUserModelId, AumidResult = aumidResult });
            else
                AppLogService.Information("ApplicationStarted", "程序开始启动",
                    new { Arguments = Environment.GetCommandLineArgs(), AppUserModelId = AppMode.AppUserModelId, AumidResult = aumidResult });
        }

        private static void CurrentDomain_UnhandledException(
            object sender, System.UnhandledExceptionEventArgs e)
        {
            AppLogService.Error("AppDomainUnhandledException", "发生未处理的运行时异常",
                new { e.IsTerminating }, e.ExceptionObject as Exception);
        }

        private static void TaskScheduler_UnobservedTaskException(
            object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLogService.Error("UnobservedTaskException", "后台任务发生未观察异常",
                exception: e.Exception);
            e.SetObserved();
        }

        private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            AppLogService.Error("UnhandledException", "发生未处理的 WinUI 异常", exception: e.Exception);
            // 发布环境没有附加调试器时，将启动异常写入日志以便定位问题。
            try
            {
                string directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinPlayer.WinUI");
                Directory.CreateDirectory(directory);
                File.WriteAllText(System.IO.Path.Combine(directory, $"startup-error{AppMode.Suffix}.log"),
                    $"{DateTime.Now:O}\r\n{e.Exception}");
            }
            catch (Exception ex)
            {
                AppLogService.Error("StartupErrorFallbackFailed", "写入备用启动错误日志失败",
                    exception: ex);
            }
        }

        /// <summary>
        /// 在应用程序启动时调用。
        /// </summary>
        /// <param name="args">本次启动请求的参数。</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            string[] commandLineArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var settings = new WinPlayer.WinUI.Services.SettingsService().Load();
            if (settings.SingleInstance)
            {
                _instanceMutex = new Mutex(true, InstanceMutexName, out bool isPrimaryInstance);
                if (!isPrimaryInstance)
                {
                    await SendArgumentsToPrimaryInstanceAsync(commandLineArguments);
                    Exit();
                    return;
                }
            }

            _window = new MainWindow(commandLineArguments);
            _window.Activate();
            if (settings.SingleInstance) _ = ListenForSecondaryInstancesAsync();
            // 若此前注册过文件关联但程序目录被移动过，静默把注册表命令更新为当前路径。
            // 隐私模式不触碰注册表。
            if (AppMode.IsPrivacy) return;
            _ = Task.Run(() =>
            {
                try
                {
                    new WinPlayer.WinUI.Services.FileAssociationService().RefreshCommandPathsIfRegistered();
                }
                catch (Exception ex)
                {
                    AppLogService.Warning("FileAssociationRefreshFailed", "刷新文件关联路径失败", exception: ex);
                }
            });
        }

        private static async Task SendArgumentsToPrimaryInstanceAsync(string[] arguments)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", InstancePipeName, PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2500);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                await writer.WriteAsync(JsonSerializer.Serialize(arguments));
            }
            catch (Exception ex)
            {
                AppLogService.Error("ActivationForwardFailed", "向主程序转发启动参数失败",
                    new { Arguments = arguments }, ex);
            }
        }

        private async Task ListenForSecondaryInstancesAsync()
        {
            // 每次激活都创建一个只接收单个客户端的管道服务，并通过主窗口的
            // DispatcherQueue 将界面操作切回 UI 线程。
            while (_window is not null)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(InstancePipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync();
                    using var reader = new StreamReader(pipe);
                    string json = await reader.ReadToEndAsync();
                    string[] arguments = JsonSerializer.Deserialize<string[]>(json) ?? [];
                    _window.DispatcherQueue.TryEnqueue(async () =>
                    {
                        _window?.Activate();
                        if (_window is not null && arguments.Length > 0)
                            await _window.ViewModel.LoadStartupArgumentsAsync(arguments);
                    });
                }
                catch (Exception ex)
                {
                    AppLogService.Warning("ActivationPipeFailed", "单例通信管道发生错误，稍后重试",
                        exception: ex);
                    await Task.Delay(250);
                }
            }
        }
    }
}
