using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TypeLay.Core;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private GeneralModeThumb? _generalModeThumb;
    private long _generalModeVisibility, _generalModeWindowGeometry;

    private void GeneralModeWindowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        _generalModeWindowGeometry++;
        _generalModeThumb?.Settle();
    }

    private void RetireGeneralModeThumb()
    {
        _generalModeThumb?.Dispose();
        _generalModeThumb = null;
    }

    private FrameworkElement GeneralModeSurface(StackPanel choices, IReadOnlyList<RadioButton> buttons, Func<bool> current)
    {
        var surface = new Grid();
        var decoration = new Canvas { IsHitTestVisible = false, Focusable = false };
        var thumb = new Border { Height = 24, CornerRadius = new(6), BorderThickness = new(.5), IsHitTestVisible = false, Focusable = false };
        AutomationProperties.SetAutomationId(thumb, "GeneralModeThumb");
        thumb.SetResourceReference(BackgroundProperty, "SettingsLanguageThumb");
        thumb.SetResourceReference(Border.BorderBrushProperty, "SettingsModeThumbEdge");
        thumb.SetResourceReference(EffectProperty, "SettingsModeThumbShadow");
        decoration.Children.Add(thumb);
        surface.Children.Add(decoration);
        surface.Children.Add(choices); // The existing RadioButtons alone receive pointer, keyboard and UIA input.
        _generalModeThumb = new(surface, choices, buttons, thumb, current, () => _settings.Value.Mode);
        return surface;
    }

    private async Task ChangeGeneralModeAsync(WorkMode mode, Func<bool> current)
    {
        if (!current() || _savingSettings)
            return;
        var proposed = _settings.Value with
        {
            Mode = mode
        };
        long revision = _settings.Revision, visibility = _generalModeVisibility, geometry = _generalModeWindowGeometry;
        object? navigation = _pageNavigation;
        string? appearance = _appearanceStamp;
        var originalThumb = _generalModeThumb;
        var travel = originalThumb?.Begin(mode);
        // The current control authorized this ordinary preference synchronously.
        // Navigation may retire its peer and animation, but not an accepted save.
        bool saved = await SaveAsync(proposed);
        // SaveAsync still retires and rebuilds the real RadioButtons. Only numbers
        // and the original start instant cross that rebuild, never an old peer or permission.
        if (saved && travel is not null && IsVisible && _page == "general" && !_emergencyShutdown && _closeDialog is null
            && visibility == _generalModeVisibility && geometry == _generalModeWindowGeometry
            && ReferenceEquals(navigation, _pageNavigation) && appearance == _appearanceStamp
            && _settings.Revision == revision + 1 && !_settings.HasChanges(proposed))
            _generalModeThumb?.Continue(travel);
        else if (ReferenceEquals(originalThumb, _generalModeThumb))
            originalThumb?.Settle();
    }

    private sealed record GeneralModeTravel(long Started, double Left, double Width, double TargetLeft, double TargetWidth, double TrackWidth);

    // One bounded decoration on this page, not an animation/interaction framework.
    private sealed class GeneralModeThumb : IDisposable
    {
        private readonly Grid _surface;
        private readonly StackPanel _choices;
        private readonly IReadOnlyList<RadioButton> _buttons;
        private readonly Border _thumb;
        private readonly Func<bool> _current;
        private readonly Func<WorkMode> _savedMode;
        private GeneralModeTravel? _travel;
        private TimeSpan? _lastFrame;
        private bool _disposed;

        public GeneralModeThumb(Grid surface, StackPanel choices, IReadOnlyList<RadioButton> buttons,
            Border thumb, Func<bool> current, Func<WorkMode> savedMode)
        {
            _surface = surface;
            _choices = choices;
            _buttons = buttons;
            _thumb = thumb;
            _current = current;
            _savedMode = savedMode;
            surface.Loaded += Loaded;
            surface.Unloaded += Unloaded;
            choices.SizeChanged += SizeChanged;
        }

        private (double Left, double Width) Geometry(WorkMode mode)
        {
            var button = _buttons[mode == WorkMode.Automatic ? 0 : 1];
            return (button.TranslatePoint(new Point(), _choices).X, button.ActualWidth);
        }

        private void Position(double left, double width)
        {
            Canvas.SetLeft(_thumb, left);
            _thumb.Width = Math.Max(0, width);
        }

        private void Stop()
        {
            CompositionTarget.Rendering -= Frame;
            _travel = null;
            _lastFrame = null;
        }

        public void Settle()
        {
            Stop();
            if (_disposed || _choices.ActualWidth <= 0)
                return;
            var saved = Geometry(_savedMode());
            Position(saved.Left, saved.Width);
        }

        public GeneralModeTravel? Begin(WorkMode mode)
        {
            if (_disposed || !_current())
                return null;
            _surface.UpdateLayout();
            var target = Geometry(mode);
            double left = Canvas.GetLeft(_thumb);
            if (!double.IsFinite(left) || _thumb.ActualWidth <= 0)
            {
                Settle();
                left = Canvas.GetLeft(_thumb);
            }
            var travel = new GeneralModeTravel(Stopwatch.GetTimestamp(), left, _thumb.Width,
                target.Left, target.Width, _choices.ActualWidth);
            Continue(travel);
            return travel;
        }

        public void Continue(GeneralModeTravel travel)
        {
            Stop();
            if (_disposed || !_current())
                return;
            _surface.UpdateLayout();
            // Reflow (including translated labels) cancels carry-over rather than
            // animating stale geometry over the new controls.
            if (Math.Abs(_choices.ActualWidth - travel.TrackWidth) > .01)
            {
                Settle();
                return;
            }
            if (UiTheme.ReduceMotion || Stopwatch.GetElapsedTime(travel.Started).TotalMilliseconds >= 300)
            {
                Position(travel.TargetLeft, travel.TargetWidth);
                return;
            }
            _travel = travel;
            CompositionTarget.Rendering += Frame;
            Draw(travel);
        }

        private void Frame(object? sender, EventArgs args)
        {
            if (_travel is not { } travel)
                return;
            if (_disposed || !_current() || !_surface.IsVisible
                || Math.Abs(_choices.ActualWidth - travel.TrackWidth) > .01)
            {
                Settle();
                return;
            }
            if (UiTheme.ReduceMotion)
            {
                Stop();
                Position(travel.TargetLeft, travel.TargetWidth);
                return;
            }
            var frame = ((RenderingEventArgs)args).RenderingTime;
            if (_lastFrame == frame)
                return;
            _lastFrame = frame;
            Draw(travel);
        }

        private void Draw(GeneralModeTravel travel)
        {
            double progress = Math.Clamp(Stopwatch.GetElapsedTime(travel.Started).TotalMilliseconds / 300, 0, 1);
            double eased = Ease(progress);
            Position(travel.Left + (travel.TargetLeft - travel.Left) * eased,
                travel.Width + (travel.TargetWidth - travel.Width) * eased);
            if (progress >= 1)
                Stop();
        }

        private static double Ease(double time)
        {
            // CSS cubic-bezier(.3,1.3,.5,1): time is x, not the curve parameter.
            double low = 0, high = 1;
            for (int i = 0; i < 24; i++)
            {
                double u = (low + high) / 2, v = 1 - u;
                double x = 3 * v * v * u * .3 + 3 * v * u * u * .5 + u * u * u;
                if (x < time)
                    low = u;
                else
                    high = u;
            }
            double t = (low + high) / 2, inverse = 1 - t;
            return time is 0 or 1 ? time : 3 * inverse * inverse * t * 1.3 + 3 * inverse * t * t + t * t * t;
        }

        private void Loaded(object sender, RoutedEventArgs args)
        {
            if (_travel is null)
                Settle();
        }
        private void Unloaded(object sender, RoutedEventArgs args) => Dispose();
        private void SizeChanged(object sender, SizeChangedEventArgs args) => Settle();

        public void Dispose()
        {
            if (_disposed)
                return;
            Settle();
            _disposed = true;
            _surface.Loaded -= Loaded;
            _surface.Unloaded -= Unloaded;
            _choices.SizeChanged -= SizeChanged;
        }
    }
}
