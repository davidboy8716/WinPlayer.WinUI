using System;
using System.Threading.Tasks;
using System.Windows.Input;
using WinPlayer.WinUI.Services;

namespace WinPlayer.WinUI.Mvvm;

/// <summary>将无参数的视图模型操作封装为同步命令。</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

/// <summary>将异步操作封装为命令，并阻止同一操作并发执行。</summary>
public sealed class AsyncRelayCommand(Func<Task> execute) : ICommand
{
    private bool isRunning;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !isRunning;

    public async void Execute(object? parameter)
    {
        if (isRunning) return;
        isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute();
        }
        catch (Exception ex)
        {
            // async void 的异常没有调用方可以接住，会直接冒泡到 WinUI 的未处理异常处理并结束进程。
            // 这里记录后吞掉：单个命令失败不应拖垮整个播放器。
            AppLogService.Error("AsyncCommandFailed", "命令执行失败", exception: ex);
        }
        finally
        {
            isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
