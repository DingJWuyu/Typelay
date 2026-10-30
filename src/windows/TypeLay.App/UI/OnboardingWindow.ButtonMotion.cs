using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TypeLay.App;

internal sealed partial class OnboardingWindow
{
    // Only the model card's existing buttons. IsPressed remains the native input
    // source; this painter never invokes, captures, focuses, or authorizes action.
    private sealed class OnboardingButtonPress : IDisposable
    {
        private readonly Button _button;
        private readonly Func<bool> _page;
        private readonly DependencyPropertyDescriptor _pressed;
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly SolidColorBrush _brush = new();
        private ControlTemplate? _template;
        private Grid? _display;
        private Border? _surface;
        private Color _normal, _active;
        private double _shown, _from, _to, _duration;
        private TimeSpan? _started, _last;
        private Size _size;
        private long _version;
        private bool _loaded, _disposed, _inFrame;
        private static readonly KeySpline Ease = new(.25, .1, .25, 1);
        static OnboardingButtonPress() => Ease.Freeze();

        internal OnboardingButtonPress(Button button, Func<bool> page)
        {
            _button = button;
            _page = page;
            _pressed = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button));
            _pressed.AddValueChanged(button, PressedChanged);
            button.Loaded += Loaded;
            button.Unloaded += Unloaded;
            button.IsEnabledChanged += EnabledChanged;
            button.SizeChanged += GeometryChanged;
            SystemParameters.StaticPropertyChanged += SystemChanged;
            if (button.IsLoaded)
                Loaded(button, new RoutedEventArgs());
        }

        private bool Current() => !_disposed && _loaded && _page() && _button.IsLoaded && _button.IsVisible
            && ReferenceEquals(_button.Template, _template) && _display is not null && _surface is not null
            && _display.IsDescendantOf(_button) && _surface.IsDescendantOf(_display);
        private double Target => _button.IsPressed && _button.IsEnabled ? 1 : 0;
        private (Color Normal, Color Active) Palette() =>
            (((SolidColorBrush)_button.FindResource("Control")).Color, ((SolidColorBrush)_button.FindResource("Fill2")).Color);

        private void Loaded(object sender, RoutedEventArgs args)
        {
            if (_disposed || _loaded)
                return;
            _button.ApplyTemplate();
            if (_disposed || !_page())
            {
                Dispose();
                return;
            }
            _template = _button.Template;
            _display = _template.FindName("pressSurface", _button) as Grid;
            _surface = _template.FindName("surface", _button) as Border;
            _loaded = true;
            _size = _button.RenderSize;
            _shown = Target;
            if (!Current())
                Dispose();
        }

        private void PressedChanged(object? sender, EventArgs args)
        {
            if (!_loaded || _disposed)
                return;
            if (!Current())
            {
                Dispose();
                return;
            }
            if (!_button.IsEnabled || UiTheme.ReduceMotion)
            {
                Settle();
                return;
            }
            double target = Target;
            Stop();
            _from = _shown;
            _to = target;
            // For these fixed 0/1 endpoints, CSS reversing-shortening-factor
            // reduces to distance from the last actually displayed value.
            _duration = 120 * Math.Abs(_to - _from);
            if (_duration == 0)
            {
                Settle();
                return;
            }
            (_normal, _active) = Palette();
            _size = _button.RenderSize;
            long version = _version;
            CompositionTarget.Rendering += Frame;
            Paint(_shown, version);
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
                if (!_button.IsEnabled || UiTheme.ReduceMotion || _button.RenderSize != _size || Palette() != (_normal, _active))
                {
                    Settle();
                    return;
                }
                var now = ((RenderingEventArgs)args).RenderingTime;
                if (_last == now)
                    return;
                _last = now;
                _started ??= now;
                double fraction = Math.Clamp((now - _started.Value).TotalMilliseconds / _duration, 0, 1);
                long version = _version;
                if (Paint(_from + (_to - _from) * Ease.GetSplineProgress(fraction), version) && fraction >= 1 && version == _version)
                    Settle();
            }
            finally { _inFrame = false; }
        }

        private bool Paint(double value, long version)
        {
            bool Own() => Current() && version == _version;
            bool Set(DependencyObject target, DependencyProperty property, object painted)
            {
                if (!Own())
                    return false;
                if (!Equals(target.GetValue(property), painted))
                    target.SetValue(property, painted);
                return Own();
            }
            double scale = 1 - .03 * value;
            double alpha = _normal.A + (_active.A - _normal.A) * value;
            byte Channel(byte normal, byte active) => alpha <= 0 ? (byte)0
                : (byte)Math.Clamp(Math.Round((normal * _normal.A * (1 - value) + active * _active.A * value) / alpha), 0, 255);
            var color = Color.FromArgb((byte)Math.Round(alpha), Channel(_normal.R, _active.R), Channel(_normal.G, _active.G), Channel(_normal.B, _active.B));
            if (!Set(_scale, ScaleTransform.ScaleXProperty, scale) || !Set(_scale, ScaleTransform.ScaleYProperty, scale)
                || !Set(_display!, RenderTransformProperty, _scale) || !Set(_brush, SolidColorBrush.ColorProperty, color)
                || !Set(_surface!, Border.BackgroundProperty, _brush))
                return false;
            _shown = value;
            return true;
        }

        private void Stop()
        {
            _version++;
            CompositionTarget.Rendering -= Frame;
            _started = _last = null;
        }
        private void Settle()
        {
            Stop();
            long version = _version;
            _shown = Target;
            if (_display is not null && ReferenceEquals(_display.RenderTransform, _scale))
                _display.ClearValue(RenderTransformProperty);
            if (version != _version)
                return;
            if (_surface is not null && ReferenceEquals(_surface.Background, _brush))
                _surface.ClearValue(Border.BackgroundProperty);
        }
        private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (_loaded && !_disposed)
                Settle();
        }
        private void GeometryChanged(object sender, SizeChangedEventArgs args)
        {
            if (_loaded && !_disposed && _button.RenderSize != _size)
                Settle();
        }
        private void SystemChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (_loaded && !_disposed && (UiTheme.ReduceMotion || Palette() != (_normal, _active)))
                Settle();
        }
        private void Unloaded(object sender, RoutedEventArgs args) => Dispose();
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
            _pressed.RemoveValueChanged(_button, PressedChanged);
            _button.Loaded -= Loaded;
            _button.Unloaded -= Unloaded;
            _button.IsEnabledChanged -= EnabledChanged;
            _button.SizeChanged -= GeometryChanged;
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            Settle();
        }
    }
}
