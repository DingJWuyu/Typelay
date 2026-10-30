using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private AppearanceTileRings? _appearanceTileRings;
    private long _appearanceVisibility, _appearanceGeometry;

    private void AppearanceWindowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        _appearanceGeometry++;
        _appearanceTileRings?.Settle();
    }

    private void RetireAppearanceTileRings()
    {
        _appearanceTileRings?.Dispose();
        _appearanceTileRings = null;
    }

    private async Task ChangeAppearanceTileAsync(string kind, string value, Func<bool> current)
    {
        if (!current() || _savingSettings)
            return;
        var proposed = kind == "theme" ? _settings.Value with
        {
            Theme = value
        } : _settings.Value with
        {
            Font = value
        };
        long revision = _settings.Revision, visibility = _appearanceVisibility, geometry = _appearanceGeometry;
        object? navigation = _pageNavigation;
        var previous = _appearanceTileRings;
        var travel = previous?.Begin();
        var preparation = new AppearancePreparationTiming();
        bool saved = await SaveAsync(proposed, appearancePreparation: preparation);
        // SaveAsync proves this synchronous Update committed (including a failed
        // observer). Only pure displayed shadow values cross Refresh.
        if (saved && travel is not null && IsVisible && _page == "appearance" && !_emergencyShutdown && _closeDialog is null
            && visibility == _appearanceVisibility && geometry == _appearanceGeometry
            && ReferenceEquals(navigation, _pageNavigation) && _settings.Revision == revision + 1
            && !_settings.HasChanges(proposed))
            _appearanceTileRings?.Continue(travel, _settings.Value, _settings.Revision, preparation.WaitMilliseconds >= 200);
        else if (ReferenceEquals(previous, _appearanceTileRings))
            previous?.Settle();
    }

    // Observation only: SaveAsync fills this within its existing ACK await.
    // It cannot authorize a save or preserve an old interactive control.
    private sealed class AppearancePreparationTiming
    {
        public double WaitMilliseconds
        {
            get; set;
        }
    }

    private sealed record AppearanceShadow(double Spread, double Blur, double Offset, Color Color);
    private sealed record AppearanceTileLook(AppearanceShadow Outline, AppearanceShadow Soft, AppearanceShadow Edge);
    private sealed record AppearanceRingTravel(IReadOnlyDictionary<string, AppearanceTileLook> Looks, double ThemeWidth, double FontWidth);

    // The two CSS box-shadow lists are interpolated on this page only. These
    // borders never own focus, hit testing, selection, or permission to save.
    private sealed class AppearanceTileRings : IDisposable
    {
        private readonly SettingsWindow _owner;
        private readonly StackPanel _themes, _fonts;
        private readonly IReadOnlyDictionary<string, Control> _choices;
        private readonly Func<bool> _current;
        private readonly Dictionary<string, AppearanceTileLook> _shown = [];
        private AppearanceRingTravel? _travel;
        private Dictionary<string, AppearanceTileLook>? _destination;
        private UserSettings? _committed;
        private long _revision;
        private TimeSpan? _lastFrame, _presented;
        private bool _disposed;
        private long _paintVersion;
        private readonly Dictionary<(string Key, string Name), LayerPaint> _layers = [];
        private sealed class LayerPaint(ControlTemplate template, Border border)
        {
            internal ControlTemplate Template { get; } = template;
            internal Border Border { get; } = border;
            internal SolidColorBrush Brush { get; } = new();
            internal TranslateTransform Offset { get; } = new();
            internal BlurEffect Blur { get; } = new() { KernelType = KernelType.Gaussian };
            internal AppearanceShadow? Shown
            {
                get; set;
            }
        }
        private static readonly KeySpline Ease = new(.25, .1, .25, 1);

        static AppearanceTileRings() => Ease.Freeze();

        public AppearanceTileRings(SettingsWindow owner, StackPanel themes, StackPanel fonts,
            IReadOnlyDictionary<string, Control> choices, Func<bool> current)
        {
            _owner = owner;
            _themes = themes;
            _fonts = fonts;
            _choices = choices;
            _current = current;
            themes.Loaded += Loaded;
            themes.Unloaded += Unloaded;
            themes.SizeChanged += GeometryChanged;
            fonts.SizeChanged += GeometryChanged;
        }

        private Dictionary<string, AppearanceTileLook> SavedLooks()
        {
            Color ColorOf(string name) => ((SolidColorBrush)_owner.FindResource(name)).Color;
            var accent = ColorOf("Accent");
            var soft = ColorOf("AppearanceTileSoft");
            var drop = ColorOf("AppearanceTileDrop");
            var normal = new AppearanceTileLook(new(.5, 0, 0, ColorOf("SettingsControlLine")), new(0, 2, 1, drop), new(.5, 0, 0, ColorOf("SettingsModeThumbEdge")));
            var selected = new AppearanceTileLook(new(2.5, 0, 0, accent), new(5, 0, 0, soft), new(0, 0, 0, Colors.Transparent));
            string theme = "theme:" + _owner._settings.Value.Theme, font = "font:" + _owner._settings.Value.Font;
            return _choices.Keys.ToDictionary(key => key, key => key == theme || key == font ? selected : normal);
        }

        public AppearanceRingTravel? Begin()
        {
            if (_disposed || !_current())
                return null;
            _owner.UpdateLayout();
            if (_disposed || !_current())
                return null;
            // Retarget from what was actually painted. Advancing the old clock
            // here would capture a value that no frame has presented.
            if (_travel is null)
            {
                _shown.Clear();
                long version = ++_paintVersion;
                foreach (var (key, look) in SavedLooks())
                    if (!Paint(key, look, version))
                        return null;
            }
            var travel = new AppearanceRingTravel(new Dictionary<string, AppearanceTileLook>(_shown), _themes.ActualWidth, _fonts.ActualWidth);
            Stop();
            return travel;
        }

        public void Continue(AppearanceRingTravel travel, UserSettings committed, long revision, bool latePreparation)
        {
            Stop();
            if (_disposed || !_current())
                return;
            _owner.UpdateLayout();
            if (_disposed || !_current())
                return;
            if (UiTheme.ReduceMotion || latePreparation
                || !SameGeometry(travel))
            {
                Settle();
                return;
            }
            _committed = committed;
            _revision = revision;
            _destination = SavedLooks();
            _travel = travel;
            CompositionTarget.Rendering += Frame;
            // Hold the old displayed look until the first real rendering frame.
            // Only that frame starts the one 200 ms presentation interval.
            Draw(travel, 0);
        }

        private bool SameGeometry(AppearanceRingTravel travel) => Math.Abs(_themes.ActualWidth - travel.ThemeWidth) < .01
            && Math.Abs(_fonts.ActualWidth - travel.FontWidth) < .01;

        private void Frame(object? sender, EventArgs args)
        {
            if (_travel is not { } travel)
                return;
            if (_disposed || !_current() || !_themes.IsVisible || UiTheme.ReduceMotion || !SameGeometry(travel)
                || !ReferenceEquals(_committed, _owner._settings.Value) || _revision != _owner._settings.Revision
                || _destination is null || SavedLooks().Any(pair => _destination[pair.Key] != pair.Value))
            {
                Settle();
                return;
            }
            var frame = ((RenderingEventArgs)args).RenderingTime;
            if (_lastFrame == frame)
                return;
            _lastFrame = frame;
            _presented ??= frame;
            Draw(travel, (frame - _presented.Value).TotalMilliseconds);
        }

        private void Draw(AppearanceRingTravel travel, double elapsedMilliseconds)
        {
            if (_destination is null)
                return;
            double time = Math.Clamp(elapsedMilliseconds / 200, 0, 1);
            double progress = Ease.GetSplineProgress(time);
            long version = ++_paintVersion;
            foreach (var (key, destination) in _destination)
            {
                var start = travel.Looks[key];
                if (!Paint(key, new(Interpolate(start.Outline, destination.Outline, progress),
                    Interpolate(start.Soft, destination.Soft, progress), Interpolate(start.Edge, destination.Edge, progress)), version))
                    return;
            }
            if (time >= 1)
                Settle();
        }

        private static AppearanceShadow Interpolate(AppearanceShadow from, AppearanceShadow to, double progress)
        {
            double Mix(double a, double b) => a + (b - a) * progress;
            double alpha = Mix(from.Color.A, to.Color.A);
            byte Channel(byte a, byte b) => alpha <= 0 ? (byte)0 : (byte)Math.Clamp(Math.Round(Mix(a * from.Color.A, b * to.Color.A) / alpha), 0, 255);
            // CSS shadow colors interpolate with premultiplied alpha.
            var color = Color.FromArgb((byte)Math.Round(alpha), Channel(from.Color.R, to.Color.R), Channel(from.Color.G, to.Color.G), Channel(from.Color.B, to.Color.B));
            return new(Mix(from.Spread, to.Spread), Mix(from.Blur, to.Blur), Mix(from.Offset, to.Offset), color);
        }

        private bool Paint(string key, AppearanceTileLook look, long version)
        {
            bool Current() => !_disposed && version == _paintVersion && _current();
            if (!Current())
                return false;
            var choice = _choices[key];
            bool Layer(string name, AppearanceShadow shadow, bool fill)
            {
                if (!Current())
                    return false;
                var id = (key, name);
                if (!_layers.TryGetValue(id, out var layer) || !ReferenceEquals(layer.Template, choice.Template)
                    || !layer.Border.IsDescendantOf(choice))
                {
                    choice.ApplyTemplate();
                    if (!Current() || choice.Template.FindName(name, choice) is not Border border)
                        return false;
                    layer = new(choice.Template, border);
                    _layers[id] = layer;
                }
                bool LayerCurrent() => Current() && ReferenceEquals(layer.Template, choice.Template)
                    && layer.Border.IsDescendantOf(choice);
                if (layer.Shown == shadow)
                    return LayerCurrent();
                bool Set(DependencyObject target, DependencyProperty property, object? value)
                {
                    if (!LayerCurrent())
                        return false;
                    if (!Equals(target.GetValue(property), value))
                        target.SetValue(property, value);
                    // A public DP notification may synchronously retire or
                    // retarget this painter; it must not resume on the old tree.
                    return LayerCurrent();
                }
                if (!Set(layer.Border, MarginProperty, new Thickness(-shadow.Spread))
                    || !Set(layer.Border, Border.CornerRadiusProperty, new CornerRadius(9 + shadow.Spread))
                    || !Set(layer.Border, VisibilityProperty, Visibility.Visible)
                    || !Set(layer.Brush, SolidColorBrush.ColorProperty, shadow.Color)
                    || !Set(layer.Border, Border.BorderThicknessProperty, fill ? new Thickness(0) : new Thickness(shadow.Spread))
                    || !Set(layer.Border, Border.BorderBrushProperty, layer.Brush)
                    || !Set(layer.Border, Border.BackgroundProperty, fill ? layer.Brush : Brushes.Transparent)
                    || !Set(layer.Offset, TranslateTransform.YProperty, shadow.Offset)
                    || !Set(layer.Border, RenderTransformProperty, layer.Offset)
                    || !Set(layer.Blur, BlurEffect.RadiusProperty, shadow.Blur)
                    || !Set(layer.Border, EffectProperty, shadow.Blur > 0 ? layer.Blur : null))
                    return false;
                layer.Shown = shadow;
                return true;
            }
            if (!Layer("edge", look.Edge, false) || !Layer("selectionSoft", look.Soft, true) || !Layer("selection", look.Outline, false))
                return false;
            _shown[key] = look;
            return true;
        }

        private void Stop()
        {
            _paintVersion++;
            CompositionTarget.Rendering -= Frame;
            _travel = null;
            _destination = null;
            _committed = null;
            _lastFrame = null;
            _presented = null;
        }

        public void Settle()
        {
            Stop();
            if (_disposed)
                return;
            long version = _paintVersion;
            _layers.Clear();
            foreach (var choice in _choices.Values)
            {
                choice.ApplyTemplate();
                if (_disposed || version != _paintVersion)
                    return;
                foreach (string name in new[] { "edge", "selectionSoft", "selection" })
                {
                    if (choice.Template.FindName(name, choice) is not Border border)
                        continue;
                    // Restore the template's dynamic brushes and selected-state
                    // triggers, so later OS palette changes need no idle clock.
                    foreach (var property in new[] { MarginProperty, Border.CornerRadiusProperty, VisibilityProperty,
                        Border.BorderThicknessProperty, Border.BorderBrushProperty, Border.BackgroundProperty,
                        RenderTransformProperty, EffectProperty })
                    {
                        border.ClearValue(property);
                        if (_disposed || version != _paintVersion)
                            return;
                    }
                }
            }
            _shown.Clear();
            foreach (var (key, look) in SavedLooks())
                _shown[key] = look;
        }

        private void Loaded(object sender, RoutedEventArgs args)
        {
            if (_travel is null)
                Settle();
        }
        private void Unloaded(object sender, RoutedEventArgs args) => Dispose();
        private void GeometryChanged(object sender, SizeChangedEventArgs args)
        {
            if (_travel is { } travel && !SameGeometry(travel))
                Settle();
        }
        public void Dispose()
        {
            if (_disposed)
                return;
            Settle();
            _disposed = true;
            _themes.Loaded -= Loaded;
            _themes.Unloaded -= Unloaded;
            _themes.SizeChanged -= GeometryChanged;
            _fonts.SizeChanged -= GeometryChanged;
        }
    }
}
