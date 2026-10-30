using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private ApplicationSheet ApplicationDialog(string title, string id, double width) => new(this, title, id, width);

    // Applications has two real owned modal windows. This object only supplies
    // their presentation; the callers still own ShowDialog, choices and consent.
    private sealed class ApplicationSheet : IDisposable
    {
        private readonly SettingsWindow _owner;
        private readonly FrameworkElement _background;
        private readonly object _page;
        private readonly object? _navigation;
        private readonly Grid _frame, _material;
        private readonly Border _surface;
        private readonly Rectangle _backdrop, _backdropShade;
        private readonly VisualBrush _image;
        private readonly Grid _shadows;
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly TranslateTransform _translation = new();
        private readonly bool _dark;
        private readonly Border _scrim;
        private readonly SolidColorBrush _scrimBrush;
        private readonly Color _scrimColor;
        private TimeSpan? _started, _lastFrame;
        private bool _loaded, _disposed, _synchronizing, _geometryValid;
        private static readonly KeySpline Fade = new(.25, .1, .25, 1);
        public Window Dialog
        {
            get;
        }

        static ApplicationSheet() => Fade.Freeze();

        public ApplicationSheet(SettingsWindow owner, string title, string id, double width)
        {
            _owner = owner;
            _background = (FrameworkElement)owner.Content;
            _page = owner._pageIdentity;
            _navigation = owner._pageNavigation;
            // Read the already resolved application palette, including System.
            _dark = ((Color)owner.FindResource("SettingsPanelColor")).R < 128;
            _image = new VisualBrush(_background)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            // WPF provides the local blur here, not CSS saturate(180%). No
            // screen capture or shader dependency is used to approximate it.
            _backdrop = new Rectangle
            {
                Fill = _image,
                Margin = new(-28),
                IsHitTestVisible = false,
                Effect = owner._settings.Value.ReduceTransparency ? null : new BlurEffect { Radius = 28, KernelType = KernelType.Gaussian }
            };
            AutomationProperties.SetAutomationId(_backdrop, "ApplicationSheetBackdrop");
            _scrimColor = _dark ? Color.FromArgb(115, 0, 0, 0) : Color.FromArgb(56, 20, 20, 19);
            _scrimBrush = new SolidColorBrush(Color.FromArgb(1, _scrimColor.R, _scrimColor.G, _scrimColor.B));
            _scrim = new Border { Background = _scrimBrush, IsHitTestVisible = false, Focusable = false };
            AutomationProperties.SetAutomationId(_scrim, "ApplicationSheetScrim");
            _backdropShade = new Rectangle { Fill = _scrimBrush, IsHitTestVisible = false };
            _surface = new Border { Padding = new(22), CornerRadius = new(16) };
            _surface.SetResourceReference(Border.BackgroundProperty, "GlassStrong");
            AutomationProperties.SetAutomationId(_surface, "ApplicationDialogSurface");
            _material = new Grid();
            if (!owner._settings.Value.ReduceTransparency)
            {
                _material.Children.Add(_backdrop);
                // Match the sibling scrim without including it in the brush's
                // source subtree, which would make the backdrop recursive.
                _material.Children.Add(_backdropShade);
            }
            _material.Children.Add(_surface);
            var edge = new Border { CornerRadius = new(16), BorderThickness = new(.5), IsHitTestVisible = false };
            edge.SetResourceReference(Border.BorderBrushProperty, "GlassEdge");
            _material.Children.Add(edge);
            _shadows = new Grid { IsHitTestVisible = false };
            _shadows.Children.Add(Shadow(_dark ? 70 : 64, _dark ? 28 : 24, _dark ? .5 : .18));
            _shadows.Children.Add(Shadow(_dark ? 14 : 12, 4, _dark ? .3 : .08));
            _frame = new Grid { Width = width, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(_frame, "ApplicationSheetLayer");
            _frame.Children.Add(_shadows);
            _frame.Children.Add(_material);
            var transforms = new TransformGroup();
            transforms.Children.Add(_scale);
            transforms.Children.Add(_translation);
            _frame.RenderTransform = transforms;
            _frame.RenderTransformOrigin = new(.5, .5);
            var client = new Grid { ClipToBounds = true, Background = Brushes.Transparent };
            client.Children.Add(_scrim);
            client.Children.Add(_frame);
            Dialog = new Window
            {
                Owner = owner,
                Title = title,
                Content = client,
                Width = owner.ActualWidth,
                Height = owner.ActualHeight,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false
            };
            Dialog.Resources.MergedDictionaries.Add(UiTheme.ConfigurationScrollResources());
            Dialog.Resources.MergedDictionaries.Add(SheetButtonResources());
            Dialog.Resources["ApplicationSheetButtonShadowColor"] = _dark ? Colors.Black : Color.FromRgb(20, 20, 19);
            Dialog.Resources["ApplicationSheetButtonShadowOpacity"] = _dark ? .30 : .06;
            var smallEdge = new SolidColorBrush(_dark ? Color.FromArgb(15, 255, 255, 255) : Color.FromArgb(15, 20, 20, 19));
            smallEdge.Freeze();
            Dialog.Resources["ApplicationSheetButtonEdge"] = smallEdge;
            AutomationProperties.SetAutomationId(Dialog, id);
            Dialog.SourceInitialized += SourceInitialized;
            Dialog.Loaded += Loaded;
            Dialog.Closed += Closed;
        }

        public void SetContent(UIElement content) => _surface.Child = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };

        private Border Shadow(double blur, double offset, double opacity) => new()
        {
            Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), _dark ? (byte)0 : (byte)20, _dark ? (byte)0 : (byte)20, _dark ? (byte)0 : (byte)19)),
            CornerRadius = new(16),
            Effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian },
            RenderTransform = new TranslateTransform(0, offset),
            IsHitTestVisible = false
        };

        private bool Current() => !_disposed && _owner.IsVisible && !_owner._emergencyShutdown
            && ReferenceEquals(_page, _owner._pageIdentity) && ReferenceEquals(_navigation, _owner._pageNavigation)
            && ReferenceEquals(_owner._closeDialog, Dialog);

        private void SourceInitialized(object? sender, EventArgs args)
        {
            SynchronizeBounds();
            if (!_disposed && !_geometryValid)
            {
                // Fail before the first ShowWindow, not after a wrongly placed
                // transparent HWND has already become visible outside its owner.
                Dispose();
                Dialog.Close();
            }
        }

        private void Loaded(object sender, RoutedEventArgs args)
        {
            if (!Current() || !_geometryValid)
            {
                Retire();
                return;
            }
            _loaded = true;
            _owner.IsVisibleChanged += OwnerVisibilityChanged;
            _owner.Closed += OwnerClosed;
            _owner.LocationChanged += OwnerLocationChanged;
            _owner.SizeChanged += OwnerSizeChanged;
            _owner.DpiChanged += DpiChanged;
            _owner.LayoutUpdated += OwnerLayoutUpdated;
            Dialog.LocationChanged += DialogGeometryChanged;
            Dialog.SizeChanged += DialogSizeChanged;
            Dialog.DpiChanged += DpiChanged;
            Dialog.Deactivated += DialogDeactivated;
            _frame.SizeChanged += SurfaceSizeChanged;
            SynchronizeBounds();
            if (!Current() || !_geometryValid)
            {
                Retire();
                return;
            }
            UpdateBackground();
            if (UiTheme.ReduceMotion)
                Settle();
            else
            {
                CompositionTarget.Rendering += Frame;
                PaintEntrance(0);
                if (!Current())
                    Retire();
            }
        }

        private void SynchronizeBounds()
        {
            if (_disposed || _synchronizing)
                return;
            if (!Current())
            {
                Retire();
                return;
            }
            _synchronizing = true;
            _geometryValid = false;
            try
            {
                // Window.Height includes native chrome. Use the actual HWND
                // client rectangle, never the outer-window bounds or CenterOwner.
                nint owner = new WindowInteropHelper(_owner).Handle;
                nint modal = new WindowInteropHelper(Dialog).Handle;
                if (owner != 0 && modal != 0 && GetClientRect(owner, out var client))
                {
                    var first = new ClientPoint { X = client.Left, Y = client.Top };
                    var last = new ClientPoint { X = client.Right, Y = client.Bottom };
                    if (ClientToScreen(owner, ref first) && ClientToScreen(owner, ref last)
                        && last.X > first.X && last.Y > first.Y)
                    {
                        if (!Current())
                        {
                            Retire();
                            return;
                        }
                        bool positioned = Native.SetWindowPos(modal, 0, first.X, first.Y,
                            last.X - first.X, last.Y - first.Y, 0x0004 | 0x0010);
                        if (Current() && positioned && GetClientRect(owner, out var currentClient))
                        {
                            // SetWindowPos dispatches synchronous window messages.
                            // A nested owner resize cannot be accepted against the
                            // pre-call rectangle merely because our modal matches it.
                            var currentFirst = new ClientPoint { X = currentClient.Left, Y = currentClient.Top };
                            var currentLast = new ClientPoint { X = currentClient.Right, Y = currentClient.Bottom };
                            if (ClientToScreen(owner, ref currentFirst) && ClientToScreen(owner, ref currentLast)
                                && currentFirst.X == first.X && currentFirst.Y == first.Y
                                && currentLast.X == last.X && currentLast.Y == last.Y
                                && GetWindowRect(modal, out var actual)
                                && actual.Left == first.X && actual.Top == first.Y && actual.Right == last.X && actual.Bottom == last.Y
                                && Current())
                            {
                                var dpi = VisualTreeHelper.GetDpi(_owner);
                                _frame.MaxWidth = Math.Max(0, (last.X - first.X) / dpi.DpiScaleX - 32);
                                _frame.MaxHeight = Math.Max(0, (last.Y - first.Y) / dpi.DpiScaleY - 32);
                                _geometryValid = Current();
                            }
                        }
                    }
                }
            }
            finally
            {
                _synchronizing = false;
            }
            if (_loaded)
            {
                if (!Current() || !_geometryValid)
                    Retire();
                else
                    UpdateBackground();
            }
        }

        private void UpdateBackground()
        {
            if (!Current() || !_loaded || _frame.ActualWidth <= 0 || _frame.ActualHeight <= 0)
                return;
            double width = _frame.ActualWidth, height = _frame.ActualHeight;
            var round = new RectangleGeometry(new Rect(0, 0, width, height), 16, 16);
            _material.Clip = round;
            // CSS outer shadows do not darken the translucent sheet interior.
            _shadows.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-128, -128, width + 256, height + 256)), round);
            var first = _background.PointFromScreen(_material.PointToScreen(new Point(-28, -28)));
            var last = _background.PointFromScreen(_material.PointToScreen(new Point(width + 28, height + 28)));
            if (Current() && last.X > first.X && last.Y > first.Y)
                _image.Viewbox = new Rect(first, last);
        }

        private void Frame(object? sender, EventArgs args)
        {
            if (!Current() || !Dialog.IsVisible || !_geometryValid)
            {
                Retire();
                return;
            }
            if (UiTheme.ReduceMotion)
            {
                Settle();
                return;
            }
            var time = ((RenderingEventArgs)args).RenderingTime;
            if (_lastFrame == time)
                return;
            _lastFrame = time;
            _started ??= time;
            double elapsed = (time - _started.Value).TotalMilliseconds;
            PaintEntrance(elapsed);
            if (elapsed >= 380)
                Settle();
        }

        private void PaintEntrance(double elapsed)
        {
            double amount = EntranceProgress(Math.Clamp(elapsed / 380, 0, 1));
            _frame.Opacity = Math.Clamp(amount, 0, 1);
            _scale.ScaleX = _scale.ScaleY = .97 + .03 * amount;
            _translation.Y = -8 + 8 * amount;
            double dim = Fade.GetSplineProgress(Math.Clamp(elapsed / 200, 0, 1));
            // A fully transparent layered-window pixel is native click-through.
            // Keep the same scrim at one alpha byte even on its first frame; no
            // additional window, hit-test forwarding or confirmation action exists.
            _scrimBrush.Color = Color.FromArgb((byte)Math.Max(1, Math.Round(_scrimColor.A * dim)), _scrimColor.R, _scrimColor.G, _scrimColor.B);
            UpdateBackground();
        }

        private void Settle()
        {
            if (_disposed)
                return;
            CompositionTarget.Rendering -= Frame;
            _started = _lastFrame = null;
            _frame.Opacity = 1;
            _scale.ScaleX = _scale.ScaleY = 1;
            _translation.Y = 0;
            _scrimBrush.Color = _scrimColor;
            UpdateBackground();
        }

        private static double EntranceProgress(double time)
        {
            // CSS cubic-bezier(.3,1.25,.5,1) overshoots. Solve x for time;
            double low = 0, high = 1;
            for (int index = 0; index < 24; index++)
            {
                double t = (low + high) / 2, inverse = 1 - t;
                double x = 3 * inverse * inverse * t * .3 + 3 * inverse * t * t * .5 + t * t * t;
                if (x < time)
                    low = t;
                else
                    high = t;
            }
            double value = (low + high) / 2, remainder = 1 - value;
            return time is 0 or 1 ? time : 3 * remainder * remainder * value * 1.25 + 3 * remainder * value * value + value * value * value;
        }

        private void OwnerLocationChanged(object? sender, EventArgs args)
        {
            Settle();
            SynchronizeBounds();
        }
        private void OwnerSizeChanged(object sender, SizeChangedEventArgs args) => OwnerLocationChanged(sender, args);
        private void DpiChanged(object sender, DpiChangedEventArgs args) => OwnerLocationChanged(sender, args);
        private void DialogDeactivated(object? sender, EventArgs args) => Settle();
        private void DialogGeometryChanged(object? sender, EventArgs args) => UpdateBackground();
        private void DialogSizeChanged(object sender, SizeChangedEventArgs args) => UpdateBackground();
        private void SurfaceSizeChanged(object sender, SizeChangedEventArgs args) => UpdateBackground();
        private void OwnerLayoutUpdated(object? sender, EventArgs args)
        {
            if (!Current())
                Retire();
        }
        private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!_owner.IsVisible)
                Retire();
        }
        private void OwnerClosed(object? sender, EventArgs args) => Retire();
        private void Closed(object? sender, EventArgs args) => Dispose();

        private void Retire()
        {
            if (_disposed)
                return;
            Dispose();
            // SourceInitialized may retire before IsVisible becomes true.
            // Every caller owns this already-created HWND, never a new dialog.
            Dialog.Close();
        }

        private static ResourceDictionary SheetButtonResources() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Button">
            <Setter Property="Background" Value="{DynamicResource Control}"/><Setter Property="Foreground" Value="{DynamicResource Text}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource ApplicationSheetButtonEdge}"/><Setter Property="BorderThickness" Value="0.5"/>
            <Setter Property="Cursor" Value="Hand"/><Setter Property="FontFamily" Value="{DynamicResource UiFont}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Grid x:Name="pressSurface" Background="Transparent" RenderTransformOrigin="0.5,0.5">
                <Grid.RenderTransform><ScaleTransform/></Grid.RenderTransform>
                <Border x:Name="surface" CornerRadius="7" Background="{TemplateBinding Background}" IsHitTestVisible="False">
                  <Border.Effect><DropShadowEffect BlurRadius="2" ShadowDepth="1" Direction="270" Opacity="{DynamicResource ApplicationSheetButtonShadowOpacity}" Color="{DynamicResource ApplicationSheetButtonShadowColor}"/></Border.Effect>
                </Border>
                <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
                <Border x:Name="edge" CornerRadius="7" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" IsHitTestVisible="False"/>
                <Border x:Name="focus" CornerRadius="7" BorderBrush="{DynamicResource Accent}" BorderThickness="1" Visibility="Collapsed" IsHitTestVisible="False"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="focus" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="surface" Property="Background" Value="{DynamicResource Fill2}"/><Setter TargetName="pressSurface" Property="RenderTransform"><Setter.Value><ScaleTransform ScaleX="0.97" ScaleY="0.97"/></Setter.Value></Setter></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="ApplicationSheetPrimary" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Background" Value="{DynamicResource AccentStrong}"/><Setter Property="Foreground" Value="White"/>
            <Setter Property="BorderBrush" Value="#40FFFFFF"/><Setter Property="BorderThickness" Value="0,0.5,0,0"/>
          </Style>
        </ResourceDictionary>
        """);

        [StructLayout(LayoutKind.Sequential)]
        private struct ClientRect
        {
            public int Left, Top, Right, Bottom;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ClientPoint
        {
            public int X, Y;
        }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(nint window, out ClientRect rectangle);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(nint window, out ClientRect rectangle);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(nint window, ref ClientPoint point);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CompositionTarget.Rendering -= Frame;
            _started = _lastFrame = null;
            _image.Visual = null;
            _owner.IsVisibleChanged -= OwnerVisibilityChanged;
            _owner.Closed -= OwnerClosed;
            _owner.LocationChanged -= OwnerLocationChanged;
            _owner.SizeChanged -= OwnerSizeChanged;
            _owner.DpiChanged -= DpiChanged;
            _owner.LayoutUpdated -= OwnerLayoutUpdated;
            Dialog.SourceInitialized -= SourceInitialized;
            Dialog.Loaded -= Loaded;
            Dialog.Closed -= Closed;
            Dialog.LocationChanged -= DialogGeometryChanged;
            Dialog.SizeChanged -= DialogSizeChanged;
            Dialog.DpiChanged -= DpiChanged;
            Dialog.Deactivated -= DialogDeactivated;
            _frame.SizeChanged -= SurfaceSizeChanged;
        }

    }
}
