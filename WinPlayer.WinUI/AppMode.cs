using System;
using System.Collections.Generic;

namespace WinPlayer.WinUI;

/// <summary>
/// 当前进程的运行模式（普通 / 隐私）。
///
/// <para>
/// 模式由启动参数唯一决定，在进程启动那一刻确定、整个会话内不可变。
/// 之所以必须是“由参数推导的纯函数”而不是“先构造服务再设置的变量”：
/// <see cref="Services.AppLogService.LogPath"/>、<see cref="Services.SettingsService"/> 的路径
/// 都在静态/字段初始化时求值，任何“后设模式”的写法都会踩初始化顺序陷阱。
/// </para>
///
/// <para>
/// 隔离层级：记录文件、设置文件、日志文件、单例互斥体、激活管道、任务栏图标与应用标识（AUMID）
/// 全部按模式分开；普通模式的后缀为空，隐私模式为 <c>.b</c>（刻意使用中性后缀，
/// 避免从进程对象名就能看出“存在隐私实例”）。
/// </para>
/// </summary>
internal static class AppMode
{
    private static readonly Lazy<bool> privacy = new(() => Detect(Environment.GetCommandLineArgs()));

    /// <summary>当前进程是否为隐私模式。</summary>
    public static bool IsPrivacy => privacy.Value;

    /// <summary>文件名 / 资源名后缀：普通为空字符串，隐私为 <c>.b</c>。</summary>
    public static string Suffix => IsPrivacy ? ".b" : string.Empty;

    /// <summary>窗口与任务栏图标文件名。</summary>
    public static string IconFileName => IsPrivacy ? "player.b.ico" : "player.ico";

    /// <summary>
    /// 任务栏应用标识（AppUserModelID）。两模式使用不同标识，
    /// 以便任务栏显示为两个独立按钮，并顺带隔离跳转列表/最近使用项。
    /// </summary>
    public static string AppUserModelId => IsPrivacy ? "WinPlayer.WinUI.b" : "WinPlayer.WinUI";

    /// <summary>
    /// 纯函数：判断参数中是否出现隐私开关或 <c>Privacy:</c> 前缀。
    /// （逐文件的参数解析仍在视图模型里完成，这里只回答“本进程是不是隐私模式”。）
    /// </summary>
    public static bool Detect(IReadOnlyList<string> arguments)
    {
        if (arguments is null) return false;
        foreach (string rawArgument in arguments)
        {
            if (string.IsNullOrWhiteSpace(rawArgument)) continue;
            string argument = rawArgument.Trim().Trim('"');
            if (argument.Equals("--privacy", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-privacy", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/privacy", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("Privacy:", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
