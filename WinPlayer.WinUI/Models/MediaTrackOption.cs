namespace WinPlayer.WinUI.Models;

/// <summary>与具体界面无关的音轨或定时元数据轨道描述。</summary>
public sealed record MediaTrackOption(int Index, string DisplayName, bool IsSelected);
