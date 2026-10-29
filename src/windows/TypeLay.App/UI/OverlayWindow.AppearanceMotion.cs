using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private readonly ScaleTransform _appearanceScale = new(1, 1);
    private SpringAxis? _appearanceSpring;
    private long _appearanceFrame;
    private TimeSpan? _appearanceRenderingTime;

    private void BeginAppearance(bool above)
    {
        ResetAppearance();
        _shell.RenderTransformOrigin = new(1, above ? 1 : 0);
        _appearanceSpring = new(.9)
        {
            Target = 1
        };
        _appearanceScale.ScaleX = _appearanceScale.ScaleY = .9;
        _appearanceFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += AdvanceAppearance;
    }

    private void AdvanceAppearance(object? sender, EventArgs args)
    {
        if (_appearanceSpring is not { } spring)
            return;
        if (!IsVisible || !_clientAreaAnimation() || _settings.ReduceMotion)
        {
            ResetFade();
            return;
        }
        TimeSpan frame = ((RenderingEventArgs)args).RenderingTime;
        if (_appearanceRenderingTime == frame)
            return;
        _appearanceRenderingTime = frame;
        double elapsed = Math.Clamp(Stopwatch.GetElapsedTime(_appearanceFrame).TotalSeconds, 0, 1d / 30);
        _appearanceFrame = Stopwatch.GetTimestamp();
        spring.Step(elapsed, 320, 22);
        if (spring.Settled)
        {
            ResetAppearance();
            return;
        }
        // A render transform only decorates the existing controls. It neither
        // resizes the native source nor acquires focus, a permit or an input key.
        _appearanceScale.ScaleX = _appearanceScale.ScaleY = spring.Value;
    }

    private void ResetAppearance()
    {
        CompositionTarget.Rendering -= AdvanceAppearance;
        _appearanceSpring = null;
        _appearanceRenderingTime = null;
        _appearanceScale.ScaleX = _appearanceScale.ScaleY = 1;
    }
}
