using System.Diagnostics;
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
        long started = Stopwatch.GetTimestamp();
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
        var travel = previous?.Begin(started);
        bool saved = await SaveAsync(proposed);
        // SaveAsync proves this synchronous Update committed (including a failed
        // observer). Only pure shadow values and the original start cross Refresh.
        if (saved && travel is not null && IsVisible && _page == "appearance" && !_emergencyShutdown && _closeDialog is null
            && visibility == _appearanceVisibility && geometry == _appearanceGeometry
            && ReferenceEquals(navigation, _pageNavigation) && _settings.Revision == revision + 1
            && !_settings.HasChanges(proposed))
            _appearanceTileRings?.Continue(travel, _settings.Value, _settings.Revision);
        else if (ReferenceEquals(previous, _appearanceTileRings))
            previous?.Settle();
    }

    private sealed record AppearanceShadow(double Spread, double Blur, double Offset, Color Color);
    private sealed record AppearanceTileLook(AppearanceShadow Outline, AppearanceShadow Soft, AppearanceShadow Edge);
    private sealed record AppearanceRingTravel(long Started, IReadOnlyDictionary<string, AppearanceTileLook> Looks, double ThemeWidth, double FontWidth);

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
        private TimeSpan? _lastFrame;
        private bool _disposed;
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

        public AppearanceRingTravel? Begin(long started)
        {
            if (_disposed || !_current())
                return null;
            _owner.UpdateLayout();
            if (_disposed || !_current())
                return null;
            if (_travel is { } active)
                Draw(active);
            else
            {
                _shown.Clear();
                foreach (var (key, look) in SavedLooks())
                    Paint(key, look);
            }
            var travel = new AppearanceRingTravel(started, new Dictionary<string, AppearanceTileLook>(_shown), _themes.ActualWidth, _fonts.ActualWidth);
            Stop();
            return travel;
        }

        public void Continue(AppearanceRingTravel travel, UserSettings committed, long revision)
        {
            Stop();
            if (_disposed || !_current())
                return;
            _owner.UpdateLayout();
            if (_disposed || !_current())
                return;
            if (UiTheme.ReduceMotion || Stopwatch.GetElapsedTime(travel.Started).TotalMilliseconds >= 200
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
            Draw(travel);
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
            Draw(travel);
        }

        private void Draw(AppearanceRingTravel travel)
        {
            if (_destination is null)
                return;
            double time = Math.Clamp(Stopwatch.GetElapsedTime(travel.Started).TotalMilliseconds / 200, 0, 1);
            double progress = Ease.GetSplineProgress(time);
            foreach (var (key, destination) in _destination)
            {
                var start = travel.Looks[key];
                Paint(key, new(Interpolate(start.Outline, destination.Outline, progress),
                    Interpolate(start.Soft, destination.Soft, progress), Interpolate(start.Edge, destination.Edge, progress)));
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

        private void Paint(string key, AppearanceTileLook look)
        {
            var choice = _choices[key];
            choice.ApplyTemplate();
            void Layer(string name, AppearanceShadow shadow, bool fill)
            {
                if (choice.Template.FindName(name, choice) is not Border border)
                    return;
                border.Margin = new(-shadow.Spread);
                border.CornerRadius = new(9 + shadow.Spread);
                border.Visibility = Visibility.Visible;
                var brush = new SolidColorBrush(shadow.Color);
                brush.Freeze();
                border.BorderThickness = fill ? new(0) : new(shadow.Spread);
                border.BorderBrush = brush;
                border.Background = fill ? brush : Brushes.Transparent;
                border.RenderTransform = new TranslateTransform(0, shadow.Offset);
                border.Effect = shadow.Blur > 0 ? new BlurEffect { Radius = shadow.Blur, KernelType = KernelType.Gaussian } : null;
            }
            Layer("edge", look.Edge, false);
            Layer("selectionSoft", look.Soft, true);
            Layer("selection", look.Outline, false);
            _shown[key] = look;
        }

        private void Stop()
        {
            CompositionTarget.Rendering -= Frame;
            _travel = null;
            _destination = null;
            _committed = null;
            _lastFrame = null;
        }

        public void Settle()
        {
            Stop();
            if (_disposed)
                return;
            foreach (var choice in _choices.Values)
            {
                choice.ApplyTemplate();
                foreach (string name in new[] { "edge", "selectionSoft", "selection" })
                {
                    if (choice.Template.FindName(name, choice) is not Border border)
                        continue;
                    // Restore the template's dynamic brushes and selected-state
                    // triggers, so later OS palette changes need no idle clock.
                    foreach (var property in new[] { MarginProperty, Border.CornerRadiusProperty, VisibilityProperty,
                        Border.BorderThicknessProperty, Border.BorderBrushProperty, Border.BackgroundProperty,
                        RenderTransformProperty, EffectProperty })
                        border.ClearValue(property);
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
