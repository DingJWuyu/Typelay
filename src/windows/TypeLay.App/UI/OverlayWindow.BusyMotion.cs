using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TypeLay.Core;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private readonly ScaleTransform _busyDotScale = new(1, 1), _busyHaloScale = new(1, 1);
    private readonly Border _busyHalo = new() { Width = 7, Height = 7, CornerRadius = new(3.5), IsHitTestVisible = false };
    private readonly KeySpline _busyEase = new(.42, 0, .58, 1);
    private readonly List<(Border Line, LinearGradientBrush Sweep)> _busyLines = [];
    private SolidColorBrush? _busyFill;
    private bool _busyAnimating;
    private long _busyStarted;
    private TimeSpan? _busyRenderingTime;

    private Grid CreateBusyDot()
    {
        var host = new Grid
        {
            Width = 7,
            Height = 7,
            Margin = _dot.Margin,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _busyDotScale,
            RenderTransformOrigin = new(.5, .5),
            IsHitTestVisible = false
        };
        AutomationProperties.SetAutomationId(host, "TranslationBusyDot");
        _dot.Margin = new(0);
        _busyHalo.RenderTransform = _busyHaloScale;
        _busyHalo.RenderTransformOrigin = new(.5, .5);
        _busyHalo.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        _busyHalo.Visibility = Visibility.Hidden;
        host.Children.Add(_busyHalo);
        host.Children.Add(_dot);
        return host;
    }

    private void InitializeBusyMotion()
    {
        foreach (Border line in _skeleton.Children)
            _busyLines.Add((line, new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute }));
        RefreshBusyPalette();
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopBusyMotion(); };
        Closed += (_, _) => StopBusyMotion();
    }

    private void RefreshBusyPalette()
    {
        if (_busyLines.Count == 0)
            return;
        // Existing shared tokens are the prototype's fill-2 and fill-1. This
        // decoration does not alter the real editor or the status accessibility text.
        Color fill = (Color)FindResource("SettingsControlFillColor");
        Color light = ((SolidColorBrush)FindResource("SettingsLanguageFill")).Color;
        _busyFill = new(fill);
        _busyFill.Freeze();
        foreach (var (line, sweep) in _busyLines)
        {
            sweep.GradientStops = new GradientStopCollection { new(fill, .3), new(light, .5), new(fill, .7) };
            line.Height = UiTheme.ContentSize * .62;
            line.CornerRadius = new(6);
            line.Background = _busyAnimating && _skeleton.Visibility == Visibility.Visible ? sweep : _busyFill;
        }
    }

    private void RefreshBusyMotion()
    {
        if (!IsVisible || _sessionState != SessionState.Translating || _settings.ShouldReduceMotion(!_clientAreaAnimation()))
        {
            StopBusyMotion();
            return;
        }
        foreach (var (line, sweep) in _busyLines)
            line.Background = _skeleton.Visibility == Visibility.Visible ? sweep : _busyFill;
        if (_busyAnimating)
            return; // A repeated render does not restart a real in-flight task's decoration.
        _busyAnimating = true;
        _busyStarted = Stopwatch.GetTimestamp();
        _busyRenderingTime = null;
        _busyHalo.Visibility = Visibility.Visible;
        PaintBusyMotion(0);
        CompositionTarget.Rendering += AdvanceBusyMotion;
    }

    private void AdvanceBusyMotion(object? sender, EventArgs args)
    {
        if (!IsVisible || _sessionState != SessionState.Translating || _settings.ShouldReduceMotion(!_clientAreaAnimation()))
        {
            StopBusyMotion();
            return;
        }
        TimeSpan frame = ((RenderingEventArgs)args).RenderingTime;
        if (_busyRenderingTime == frame)
            return;
        _busyRenderingTime = frame;
        PaintBusyMotion(Stopwatch.GetElapsedTime(_busyStarted).TotalSeconds);
    }

    private void PaintBusyMotion(double elapsed)
    {
        // Prototype breathe: 1.2 s, ease-in-out, scale .6 and a 5 px spread at 50%.
        double phase = elapsed % 1.2 / .6;
        double eased = _busyEase.GetSplineProgress(phase <= 1 ? phase : 2 - phase);
        _busyDotScale.ScaleX = _busyDotScale.ScaleY = 1 - .4 * eased;
        _busyHaloScale.ScaleX = _busyHaloScale.ScaleY = 1 + 10d / 7 * eased;
        if (_skeleton.Visibility != Visibility.Visible)
            return;
        // CSS 100deg/250% gradient with background-position 120% -> -120%.
        // Position is based only on each inert line's actual size, never the source.
        double offset = -1.8 + 3.6 * (elapsed % 1.3 / 1.3);
        const double dx = .984807753012208, dy = .173648177666930;
        foreach (var (line, sweep) in _busyLines)
        {
            double width = line.ActualWidth, height = line.ActualHeight;
            double length = 2.5 * width * dx + height * dy;
            double centerX = (1.25 + offset) * width;
            sweep.StartPoint = new(centerX - dx * length / 2, height / 2 - dy * length / 2);
            sweep.EndPoint = new(centerX + dx * length / 2, height / 2 + dy * length / 2);
        }
    }

    private void StopBusyMotion()
    {
        CompositionTarget.Rendering -= AdvanceBusyMotion;
        _busyAnimating = false;
        _busyRenderingTime = null;
        _busyDotScale.ScaleX = _busyDotScale.ScaleY = 1;
        _busyHaloScale.ScaleX = _busyHaloScale.ScaleY = 1;
        _busyHalo.Visibility = Visibility.Hidden;
        foreach (var (line, _) in _busyLines)
            line.Background = _busyFill;
    }
}
