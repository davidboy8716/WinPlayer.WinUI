namespace WinPlayer.WinUI.Models;

/// <summary>已经解码为预乘 BGRA8 的单帧 PGS 字幕及其在原视频坐标中的位置。</summary>
public sealed record PgsSubtitleImage(
    byte[] Pixels, int Width, int Height, int X, int Y, int CanvasWidth, int CanvasHeight);
