using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinPlayer.WinUI.Effects;

/// <summary>
/// 采样控件后方已经渲染的内容，应用高斯模糊后再叠加颜色蒙层。
/// 此画刷用于模糊窗口内部的视频画面，而不是窗口外部的桌面内容。
/// </summary>
public sealed class BackdropBlurBrush : XamlCompositionBrushBase
{
    public static readonly DependencyProperty BlurAmountProperty = DependencyProperty.Register(
        nameof(BlurAmount), typeof(double), typeof(BackdropBlurBrush),
        new PropertyMetadata(28d, OnBlurAmountChanged));

    public static readonly DependencyProperty TintColorProperty = DependencyProperty.Register(
        nameof(TintColor), typeof(Color), typeof(BackdropBlurBrush),
        new PropertyMetadata(Color.FromArgb(255, 20, 22, 28), OnTintChanged));

    public static readonly DependencyProperty TintOpacityProperty = DependencyProperty.Register(
        nameof(TintOpacity), typeof(double), typeof(BackdropBlurBrush),
        new PropertyMetadata(0.42d, OnTintChanged));

    public double BlurAmount
    {
        get => (double)GetValue(BlurAmountProperty);
        set => SetValue(BlurAmountProperty, value);
    }

    public Color TintColor
    {
        get => (Color)GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    public double TintOpacity
    {
        get => (double)GetValue(TintOpacityProperty);
        set => SetValue(TintOpacityProperty, value);
    }

    public BackdropBlurBrush() => FallbackColor = Color.FromArgb(224, 20, 22, 28);

    protected override void OnConnected()
    {
        // 所属控件进入或离开可视化树时，XamlCompositionBrushBase 可能被重复连接。
        if (CompositionBrush is not null) return;
        Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
        var blur = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = (float)BlurAmount,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Balanced,
            Source = new CompositionEffectSourceParameter("backdrop")
        };
        var tint = new ColorSourceEffect
        {
            Name = "Tint",
            Color = GetEffectiveTintColor()
        };
        var effect = new CompositeEffect
        {
            Mode = CanvasComposite.SourceOver,
            Sources = { blur, tint }
        };
        var factory = compositor.CreateEffectFactory(effect,
            new[] { "Blur.BlurAmount", "Tint.Color" });
        var brush = factory.CreateBrush();
        brush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());
        CompositionBrush = brush;
    }

    protected override void OnDisconnected()
    {
        // Composition 画刷持有本机资源，断开连接时必须主动释放。
        CompositionBrush?.Dispose();
        CompositionBrush = null;
    }

    private static void OnBlurAmountChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is BackdropBlurBrush brush && brush.CompositionBrush is CompositionEffectBrush effectBrush)
            effectBrush.Properties.InsertScalar("Blur.BlurAmount", (float)(double)args.NewValue);
    }

    private static void OnTintChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is BackdropBlurBrush brush && brush.CompositionBrush is CompositionEffectBrush effectBrush)
            effectBrush.Properties.InsertColor("Tint.Color", brush.GetEffectiveTintColor());
    }

    private Color GetEffectiveTintColor()
    {
        Color color = TintColor;
        double opacity = System.Math.Clamp(TintOpacity, 0d, 1d);
        return Color.FromArgb((byte)System.Math.Round(color.A * opacity), color.R, color.G, color.B);
    }
}
