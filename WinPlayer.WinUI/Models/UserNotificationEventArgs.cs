using System;

namespace WinPlayer.WinUI.Models;

public sealed class UserNotificationEventArgs(string message, bool isError = false) : EventArgs
{
    public string Message { get; } = message;
    public bool IsError { get; } = isError;
}
