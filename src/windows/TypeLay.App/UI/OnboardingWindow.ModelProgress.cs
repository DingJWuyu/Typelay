using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TypeLay.App;

internal sealed partial class OnboardingWindow
{
    // One page's decoration. The native ProgressBar and its RangeValue peer
    // continue to receive the actual download value without animation.
    private sealed class OnboardingProgressFill : IDisposable
    {
        private static readonly KeySpline Ease = new(.22, 1, .36, 1);
        private readonly ProgressBar _bar;
        private readonly Func<bool> _current;
        private FrameworkElement? _track;
        private Border? _fill;
        private bool _disposed, _determinate, _inFrame;
        private double _shown, _target;
        private long _paintVersion;
        private Travel? _travel;

        private sealed class Travel(double from, double to)
        {
            internal double From { get; } = from;
            internal double To { get; } = to;
            internal TimeSpan? Started
            {
                get; set;
            }
        }

        internal OnboardingProgressFill(ProgressBar bar, Func<bool> current)
        {
            _bar = bar;
            _current = current;
            bar.Loaded += Loaded;
            bar.Unloaded += Unloaded;
        }

        private bool Current() => !_disposed && _current() && _bar.IsVisible && _bar.IsLoaded;
        private double Target() => _track is null || _bar.Maximum <= _bar.Minimum ? 0
            : _track.ActualWidth * Math.Clamp((_bar.Value - _bar.Minimum) / (_bar.Maximum - _bar.Minimum), 0, 1);

        private void Loaded(object sender, RoutedEventArgs args)
        {
            if (!Current())
            {
                Dispose();
                return;
            }
            _track = _bar.Template.FindName("PART_Track", _bar) as FrameworkElement;
            _fill = _bar.Template.FindName("displayFill", _bar) as Border;
            if (_track is null || _fill is null)
            {
                Dispose();
                return;
            }
            _track.SizeChanged += GeometryChanged;
            Settle();
        }

        internal void Update()
        {
            if (!Current() || _fill is null)
                return;
            if (_bar.IsIndeterminate || !_determinate || UiTheme.ReduceMotion)
            {
                Settle();
                return;
            }
            double target = Target();
            if (target == _target)
                return;
            _target = target;
            _paintVersion++;
            _travel = new(_shown, target);
            CompositionTarget.Rendering -= Frame;
            CompositionTarget.Rendering += Frame;
        }

        private void GeometryChanged(object sender, SizeChangedEventArgs args)
        {
            if (Current())
                Settle();
        }

        private void Settle()
        {
            Stop();
            if (!Current() || _fill is null)
                return;
            _determinate = !_bar.IsIndeterminate;
            _target = _determinate ? Target() : 0;
            Paint(_target, _paintVersion);
        }

        private void Frame(object? sender, EventArgs args)
        {
            if (_inFrame)
                return;
            _inFrame = true;
            try
            {
                if (!Current())
                {
                    Dispose();
                    return;
                }
                if (UiTheme.ReduceMotion || _bar.IsIndeterminate)
                {
                    Settle();
                    return;
                }
                if (_travel is not { } travel)
                    return;
                var time = ((RenderingEventArgs)args).RenderingTime;
                travel.Started ??= time;
                double fraction = Math.Clamp((time - travel.Started.Value).TotalMilliseconds / 400, 0, 1);
                long version = _paintVersion;
                Paint(travel.From + (travel.To - travel.From) * Ease.GetSplineProgress(fraction), version);
                if (Current() && ReferenceEquals(_travel, travel) && fraction >= 1)
                    Stop();
            }
            finally { _inFrame = false; }
        }

        private void Paint(double width, long version)
        {
            if (!Current() || _fill is null)
                return;
            _fill.Width = width;
            // Public DP notifications may synchronously retire this page or
            // start a newer decoration. Never overwrite either continuation.
            if (Current() && version == _paintVersion)
                _shown = width;
        }

        private void Stop()
        {
            CompositionTarget.Rendering -= Frame;
            _travel = null;
            _paintVersion++;
        }

        private void Unloaded(object sender, RoutedEventArgs args) => Dispose();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
            _bar.Loaded -= Loaded;
            _bar.Unloaded -= Unloaded;
            if (_track is not null)
                _track.SizeChanged -= GeometryChanged;
            _track = null;
            var fill = _fill;
            _fill = null;
            fill?.ClearValue(WidthProperty);
        }
    }
}
