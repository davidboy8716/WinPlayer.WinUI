using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinPlayer.WinUI.Models;

namespace WinPlayer.WinUI.Services;

/// <summary>
/// 在当前用户注册表（HKCU）中维护媒体文件关联，无需管理员权限。
/// 只写入 OpenWithProgids 和能力声明，不改写各扩展名的默认值；
/// Windows 的默认程序（决定双击行为）必须由用户在系统设置中确认。
/// </summary>
public sealed class FileAssociationService
{
    private const string ProgId = "WinPlayer.WinUI.media";
    private const string ProgIdDisplayName = "WinPlayer.WinUI 媒体文件";
    private const string AppName = "WinPlayer.WinUI";
    private const string AppDescription = "WinPlayer.WinUI 媒体播放器";
    private const string CapabilitiesPath = @"Software\WinPlayer.WinUI\Capabilities";
    private const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
    private const string RegisteredApplicationsValueName = "WinPlayer.WinUI";

    public string ExePath { get; } = Environment.ProcessPath ?? string.Empty;
    private string ApplicationClassPath => $@"Software\Classes\Applications\{Path.GetFileName(ExePath)}";
    private string ProgIdClassPath => $@"Software\Classes\{ProgId}";
    private string ExpectedCommand => $"\"{ExePath}\" \"%1\"";

    /// <summary>
    /// 按用户选择的扩展名注册 ProgID、打开方式和能力声明。
    /// 受支持但未选中的扩展名会被同步清理，重复调用会用当前程序路径覆盖旧值。
    /// </summary>
    public void Register(IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(ExePath))
            throw new InvalidOperationException("无法确定当前程序的可执行文件路径。");

        var selected = new HashSet<string>(
            extensions.Where(item => !string.IsNullOrWhiteSpace(item)),
            StringComparer.OrdinalIgnoreCase);
        string command = ExpectedCommand;
        string icon = $"\"{ExePath}\",0";

        using (RegistryKey progId = Registry.CurrentUser.CreateSubKey(ProgIdClassPath, true))
        {
            progId.SetValue(null, ProgIdDisplayName);
            using RegistryKey iconKey = progId.CreateSubKey("DefaultIcon", true);
            iconKey.SetValue(null, icon);
            using RegistryKey commandKey = progId.CreateSubKey(@"shell\open\command", true);
            commandKey.SetValue(null, command);
        }

        // OpenWithProgids 只把本程序加入“打开方式”候选，不改变用户现有默认程序。
        SyncExtensionValues(selected, @"Software\Classes\{0}\OpenWithProgids", ProgId);

        // Applications\<exe 文件名> 让“打开方式”列表显示友好名称而不是裸路径。
        using (RegistryKey application = Registry.CurrentUser.CreateSubKey(ApplicationClassPath, true))
        {
            application.SetValue("FriendlyAppName", AppName);
            using (RegistryKey supportedTypes = application.CreateSubKey("SupportedTypes", true))
            {
                foreach (string extension in SupportedMedia.MediaExtensions)
                {
                    if (selected.Contains(extension))
                        supportedTypes.SetValue(extension, string.Empty);
                    else if (supportedTypes.GetValue(extension) is not null)
                        supportedTypes.DeleteValue(extension, false);
                }
            }
            using (RegistryKey iconKey = application.CreateSubKey("DefaultIcon", true),
                   commandKey = application.CreateSubKey(@"shell\open\command", true))
            {
                iconKey.SetValue(null, icon);
                commandKey.SetValue(null, command);
            }
        }

        // Capabilities + RegisteredApplications 让程序出现在“设置 → 默认应用”中，
        // 用户可以按文件类型批量指定本播放器为默认程序。
        using (RegistryKey capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath, true))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", AppDescription);
            using RegistryKey associations = capabilities.CreateSubKey("FileAssociations", true);
            foreach (string extension in SupportedMedia.MediaExtensions)
            {
                if (selected.Contains(extension))
                    associations.SetValue(extension, ProgId);
                else if (associations.GetValue(extension) is not null)
                    associations.DeleteValue(extension, false);
            }
        }

        using RegistryKey? registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, true);
        registered?.SetValue(RegisteredApplicationsValueName, CapabilitiesPath);
    }

    /// <summary>
    /// 把各受支持扩展名下的指定值同步为选中集合：选中的写入，取消勾选的删除。
    /// 仅操作本程序自己的值，不影响其他程序写入的内容。
    /// </summary>
    private static void SyncExtensionValues(
        HashSet<string> selected, string subKeyFormat, string valueName)
    {
        foreach (string extension in SupportedMedia.MediaExtensions)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    string.Format(subKeyFormat, extension), true);
                bool exists = key.GetValue(valueName) is not null;
                if (selected.Contains(extension))
                {
                    if (!exists)
                        key.SetValue(valueName, Array.Empty<byte>(), RegistryValueKind.None);
                }
                else if (exists)
                {
                    key.DeleteValue(valueName, false);
                }
            }
            catch (Exception ex)
            {
                AppLogService.Warning("FileAssociationSyncExtensionFailed",
                    "同步单个扩展名的打开方式失败", new { Extension = extension }, ex);
            }
        }
    }

    /// <summary>移除本程序写入的关联项；只删除本程序自己的值，不影响其他程序。</summary>
    public void Unregister()
    {
        foreach (string extension in SupportedMedia.MediaExtensions)
        {
            try
            {
                using RegistryKey? openWith = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\{extension}\OpenWithProgids", true);
                if (openWith?.GetValue(ProgId) is not null)
                    openWith.DeleteValue(ProgId, false);
            }
            catch (Exception ex)
            {
                AppLogService.Warning("FileAssociationRemoveExtensionFailed",
                    "移除单个扩展名的打开方式失败", new { Extension = extension }, ex);
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(ProgIdClassPath, false);
        if (!string.IsNullOrWhiteSpace(ExePath))
            Registry.CurrentUser.DeleteSubKeyTree(ApplicationClassPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(CapabilitiesPath, false);

        using RegistryKey? registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, true);
        if (registered?.GetValue(RegisteredApplicationsValueName) is not null)
            registered.DeleteValue(RegisteredApplicationsValueName, false);
    }

    /// <summary>关联命令是否已指向当前程序路径。</summary>
    public bool IsRegistered()
    {
        if (string.IsNullOrWhiteSpace(ExePath)) return false;
        using RegistryKey? command = Registry.CurrentUser.OpenSubKey($@"{ProgIdClassPath}\shell\open\command");
        return string.Equals(command?.GetValue(null) as string, ExpectedCommand,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>统计“打开方式”中实际登记了本程序的扩展名数量。</summary>
    public int CountRegisteredExtensions()
    {
        int count = 0;
        foreach (string extension in SupportedMedia.MediaExtensions)
        {
            using RegistryKey? openWith = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{extension}\OpenWithProgids");
            if (openWith?.GetValue(ProgId) is not null) count++;
        }
        return count;
    }

    /// <summary>
    /// 之前注册过关联但程序被移动或重命名时，把命令路径静默更新为当前位置，
    /// 避免资源管理器按失效路径启动旧位置（或已不存在）的程序。
    /// 按用户保存的扩展名选择重新写入。
    /// </summary>
    public void RefreshCommandPathsIfRegistered()
    {
        if (string.IsNullOrWhiteSpace(ExePath)) return;
        using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(ProgIdClassPath);
        if (existing is null) return;

        IReadOnlyList<string> extensions;
        try
        {
            extensions = new SettingsService().Load().AssociatedExtensions;
        }
        catch
        {
            extensions = SupportedMedia.MediaExtensions;
        }
        Register(extensions.Count > 0 ? extensions : SupportedMedia.MediaExtensions);
    }
}
