using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private readonly KeySpline _actionEase = new(.22, 1, .36, 1);
    private double _actionOpacity, _actionFrom, _actionTarget;
    private long _actionFadeStarted;
    private bool _actionFading;
    private TimeSpan? _actionRenderingTime;

    private void InitializeActionMotion()
    {
        PaintActionOpacity();
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopActionFade(); };
        _moreButton.IsVisibleChanged += (_, _) => { if (!_moreButton.IsVisible) StopActionFade(); };
        Closed += (_, _) => StopActionFade();
    }

    private void UpdateActionOpacity(bool visible)
    {
        if (!IsVisible || !_moreButton.IsVisible)
        {
            StopActionFade();
            return;
        }
        double target = visible ? 1 : 0;
        if (_settings.ShouldReduceMotion(!_clientAreaAnimation()))
        {
            FinishActionFade(target);
            return;
        }
        if (_actionTarget == target)
        {
            PaintActionOpacity();
            return; // Lease/source renders do not restart a visual transition.
        }
        _actionFrom = _actionOpacity;
        _actionTarget = target;
        _actionFadeStarted = Stopwatch.GetTimestamp();
        _actionRenderingTime = null;
        if (!_actionFading)
        {
            _actionFading = true;
            CompositionTarget.Rendering += AdvanceActionFade;
        }
    }

    private void AdvanceActionFade(object? sender, EventArgs args)
    {
        if (!IsVisible || !_moreButton.IsVisible)
        {
            StopActionFade();
            return;
        }
        if (_settings.ShouldReduceMotion(!_clientAreaAnimation()))
        {
            FinishActionFade(_actionTarget);
            return;
        }
        TimeSpan frame = ((RenderingEventArgs)args).RenderingTime;
        if (_actionRenderingTime == frame)
            return;
        _actionRenderingTime = frame;
        double progress = Stopwatch.GetElapsedTime(_actionFadeStarted).TotalMilliseconds / 200;
        if (progress >= 1)
        {
            FinishActionFade(_actionTarget);
            return;
        }
        _actionOpacity = _actionFrom + (_actionTarget - _actionFrom) * _actionEase.GetSplineProgress(Math.Max(0, progress));
        PaintActionOpacity();
    }

    private void PaintActionOpacity()
    {
        // Opacity decorates the existing controls only. Their hit testing,
        // enabled state, focusability and command admission remain unchanged.
        _expandButton.Opacity = _actionOpacity * (_expandButton.IsEnabled ? 1 : .35);
        _moreButton.Opacity = _actionOpacity * (_moreButton.IsEnabled ? 1 : .35);
    }

    private void StopActionFade() => FinishActionFade(0);

    private void FinishActionFade(double opacity)
    {
        CompositionTarget.Rendering -= AdvanceActionFade;
        _actionFading = false;
        _actionRenderingTime = null;
        _actionOpacity = _actionTarget = opacity;
        PaintActionOpacity();
    }
}
