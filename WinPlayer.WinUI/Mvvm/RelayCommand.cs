using System;
using System.Threading.Tasks;
using System.Windows.Input;

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
        try { await execute(); }
        finally
        {
            isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
