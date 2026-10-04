using System.Windows.Input;

namespace KanamiReporter.App;

/// <summary>
/// H.NotifyIcon 的 TaskbarIcon（WinUI 版）没有 routed events，托盘单击只能通过
/// LeftClickCommand 等 ICommand 属性接线，这里提供最小可用的命令实现。
/// </summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}
