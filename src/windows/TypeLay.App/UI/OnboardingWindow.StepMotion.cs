using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace TypeLay.App;

internal sealed partial class OnboardingWindow
{
    private object _stepOpening = new();
    private object? _showRequest;
    private StepEntranceRequest? _stepEntrance;
    private StepPainter? _stepPainter;
    private long _stepPaintGeneration;

    private sealed class StepEntranceRequest(object opening, int step)
    {
        internal object Opening { get; } = opening;
        internal int Step { get; } = step;
        internal bool Consumed
        {
            get; set;
        }
        internal bool Queued
        {
            get; set;
        }
    }

    private void RequestStepEntrance()
    {
        if (IsVisible && !_closed)
            _stepEntrance = new(_stepOpening, _step);
    }

    private bool CurrentStepRequest(StepEntranceRequest request) => !_closed && IsVisible
        && ReferenceEquals(_stepEntrance, request) && ReferenceEquals(_stepOpening, request.Opening) && _step == request.Step;

    private void CompleteStepEntrance(object opening, object page, int step, StepEntranceRequest? accepted)
    {
        var request = _stepEntrance is { Consumed: false } pending ? pending : accepted;
        if (request is null || !CurrentStepRequest(request))
            return;
        if (ReferenceEquals(opening, _stepOpening) && ReferenceEquals(page, _pageIdentity) && step == _step)
        {
            request.Consumed = true;
            RetireStepMotion();
            if (!CurrentStepRequest(request) || !ReferenceEquals(page, _pageIdentity))
                return;
            var painter = new StepPainter(this, opening, page, step, ++_stepPaintGeneration);
            _stepPainter = painter;
            painter.Begin();
        }
        else if (!request.Consumed && !request.Queued)
        {
            // A Show during the old Refresh may only request one fresh build.
            // Do not animate the mixed old subtree or renew this queue ticket.
            request.Queued = true;
            object queuedPage = _pageIdentity;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_rendering || request.Consumed || !CurrentStepRequest(request)
                    || !ReferenceEquals(_pageIdentity, queuedPage))
                    return;
                request.Consumed = true;
                RefreshStep(request);
            }), DispatcherPriority.Loaded);
        }
    }

    private void RetireStepMotion(StepPainter? expected = null)
    {
        if (expected is not null && !ReferenceEquals(_stepPainter, expected))
        {
            expected.Detach();
            return;
        }
        var painter = _stepPainter;
        _stepPainter = null;
        long generation = ++_stepPaintGeneration;
        painter?.Retire(generation);
    }

    // Only this opening's body decoration. No input, focus, model or saved state
    // is derived from these values; the real controls remain the only inputs.
    private sealed class StepPainter(OnboardingWindow owner, object opening, object page, int step, long generation)
    {
        private static readonly KeySpline Ease = new(.22, 1, .36, 1);
        private readonly TranslateTransform _translation = new();
        // WPF's kernel uses sigma = radius / 3. This is the sigma-4 candidate,
        // not a claim of CSS raster equivalence; actual edges remain to verify.
        private readonly BlurEffect _blur = new() { KernelType = KernelType.Gaussian, Radius = 12 };
        private TimeSpan? _started, _last;
        private Size _size;
        private bool _retired, _painted, _inFrame;
        static StepPainter() => Ease.Freeze();
        private bool Current() => !_retired && !owner._closed && owner.IsVisible
            && ReferenceEquals(owner._stepPainter, this) && owner._stepPaintGeneration == generation
            && ReferenceEquals(owner._stepOpening, opening) && ReferenceEquals(owner._pageIdentity, page) && owner._step == step;

        internal void Begin()
        {
            if (!Current())
                return;
            owner._body.Loaded += Loaded;
            owner._body.Unloaded += Unloaded;
            owner._body.SizeChanged += GeometryChanged;
            owner.SizeChanged += WindowGeometryChanged;
            SystemParameters.StaticPropertyChanged += MotionChanged;
            if (owner._body.IsLoaded)
                Start();
        }

        private void Loaded(object sender, RoutedEventArgs args) => Start();
        private void Start()
        {
            if (_painted)
                return;
            if (!Current())
            {
                owner.RetireStepMotion(this);
                return;
            }
            if (UiTheme.ReduceMotion)
            {
                // This new current opening owns its opacity even when no
                // animation starts; do not inherit A's partially faded value.
                _painted = true;
                owner.RetireStepMotion(this);
                return;
            }
            _size = owner._body.RenderSize;
            _painted = true;
            owner._body.Opacity = 0;
            if (!Current())
                return;
            _translation.X = 18;
            owner._body.RenderTransform = _translation;
            if (!Current())
                return;
            owner._body.Effect = _blur;
            if (!Current())
                return;
            CompositionTarget.Rendering -= Frame;
            CompositionTarget.Rendering += Frame;
        }

        private void Frame(object? sender, EventArgs args)
        {
            if (_inFrame)
                return;
            _inFrame = true;
            try
            {
                if (!Current() || UiTheme.ReduceMotion || owner._body.RenderSize != _size)
                {
                    owner.RetireStepMotion(this);
                    return;
                }
                var now = ((RenderingEventArgs)args).RenderingTime;
                if (_last == now)
                    return;
                _last = now;
                _started ??= now;
                double fraction = Math.Clamp((now - _started.Value).TotalMilliseconds / 480, 0, 1);
                double progress = Ease.GetSplineProgress(fraction);
                owner._body.Opacity = progress;
                if (!Current())
                    return;
                _translation.X = 18 * (1 - progress);
                if (!Current())
                    return;
                _blur.Radius = 12 * (1 - progress);
                if (Current() && fraction >= 1)
                    owner.RetireStepMotion(this);
            }
            finally { _inFrame = false; }
        }

        private void GeometryChanged(object sender, SizeChangedEventArgs args)
        {
            if (!Current())
                return;
            if (_started is null)
                _size = owner._body.RenderSize;
            else if (owner._body.RenderSize != _size)
                owner.RetireStepMotion(this);
        }
        private void WindowGeometryChanged(object sender, SizeChangedEventArgs args)
        {
            if (Current() && _started is not null)
                owner.RetireStepMotion(this);
        }
        private void MotionChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation) && Current() && UiTheme.ReduceMotion)
                owner.RetireStepMotion(this);
        }
        private void Unloaded(object sender, RoutedEventArgs args) => owner.RetireStepMotion(this);

        internal void Detach()
        {
            _retired = true;
            CompositionTarget.Rendering -= Frame;
            owner._body.Loaded -= Loaded;
            owner._body.Unloaded -= Unloaded;
            owner._body.SizeChanged -= GeometryChanged;
            owner.SizeChanged -= WindowGeometryChanged;
            SystemParameters.StaticPropertyChanged -= MotionChanged;
            _started = _last = null;
        }
        internal void Retire(long retirementGeneration)
        {
            Detach();
            bool OwnCleanup() => owner._stepPaintGeneration == retirementGeneration && owner._stepPainter is null;
            if (!_painted)
                return;
            if (OwnCleanup())
                owner._body.ClearValue(OpacityProperty);
            // The shared body may have acquired a new opening synchronously.
            // Clear only this painter's exact private objects, never B's values;
            // a reduced B may deliberately leave no replacement object behind.
            if (ReferenceEquals(owner._body.RenderTransform, _translation))
                owner._body.ClearValue(RenderTransformProperty);
            if (ReferenceEquals(owner._body.Effect, _blur))
                owner._body.ClearValue(EffectProperty);
        }
    }
}
