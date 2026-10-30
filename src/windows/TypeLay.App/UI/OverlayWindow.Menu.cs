using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Shapes;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private sealed class EditorMenuButton(OverlayWindow owner) : Button
    {
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            // ButtonBase normally focuses itself on mouse-down. Keep an already
            // focused ordinary editor as the menu's natural restoration target;
            // keyboard navigation and every owned-source handoff stay unchanged.
            if (owner._ownedEditor is not null && !owner.BeginOwnedLocalMenu())
            {
                e.Handled = true;
                return;
            }
            bool focusable = Focusable;
            bool preserve = owner._ownedLocalOpening is not null && owner.LocalDraftEditorFocused(owner._ownedLocalOpening)
                || owner._editing && owner._editor.IsKeyboardFocused && !owner.EditingComposition
                && owner._ownedEditor is null && owner._viewContext is { CanEdit: true } context
                && owner._ownedEditorHost?.IsOwnedEditorTarget(context.Token.Target) != true;
            try
            {
                if (preserve)
                    SetCurrentValue(FocusableProperty, false);
                base.OnMouseLeftButtonDown(e);
            }
            finally
            {
                if (preserve)
                    SetCurrentValue(FocusableProperty, focusable);
            }
        }
    }

    private sealed class MoreContextMenu : ContextMenu
    {
        private bool _opened;
        public bool Retired
        {
            get; private set;
        }
        public bool Reopened
        {
            get; private set;
        }
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            if (e.Property == IsOpenProperty)
            {
                if ((bool)e.NewValue)
                {
                    Reopened |= Retired;
                    _opened = true;
                }
                else if (_opened)
                    Retired = true;
            }
            // Track the actual IsOpen transition before WPF's popup callbacks.
            // Routed Closed can arrive after the deferred MenuItem.Click.
            base.OnPropertyChanged(e);
        }
    }

    private sealed class MoreMenuItem(Func<bool> current) : MenuItem
    {
        private bool _clickPending;
        protected override void OnClick()
        {
            if (!IsEnabled || !current())
                return;
            // WPF closes the menu before dispatching its public Click at Render
            // priority. Admit only this existing UI command, not a new input
            // lease; the handler still checks the retained menu/view/IME state.
            _clickPending = true;
            try
            {
                base.OnClick();
            }
            catch { _clickPending = false; throw; }
        }
        internal bool TakeClick()
        {
            bool pending = _clickPending;
            _clickPending = false;
            return pending;
        }
    }

    // Semantic roles belong only to this menu's presentation. Every segment
    // remains an ordinary direct MenuItem child of the native ContextMenu.
    private enum MoreRole
    {
        Action, ToneLabel, ToneSegment
    }
    private static readonly DependencyProperty MoreRoleProperty = DependencyProperty.RegisterAttached(
        "MoreRole", typeof(MoreRole), typeof(OverlayWindow), new FrameworkPropertyMetadata(MoreRole.Action,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));
    private sealed class MoreMenuPanel : Panel
    {
        private Rect _segments;
        public MoreMenuPanel()
        {
        }
        private IEnumerable<UIElement> Visible => InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed);
        private static MoreRole Role(UIElement child) => (MoreRole)child.GetValue(MoreRoleProperty);
        protected override Size MeasureOverride(Size available)
        {
            double width = 0, height = 0, toneWidth = 0;
            bool hasTone = false;
            foreach (var child in Visible)
            {
                child.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
                if (Role(child) is MoreRole.ToneLabel or MoreRole.ToneSegment)
                {
                    hasTone = true;
                    toneWidth += child.DesiredSize.Width;
                }
                else
                {
                    width = Math.Max(width, child.DesiredSize.Width);
                    height += child.DesiredSize.Height;
                }
            }
            // Row padding 10, icon-label/segment gap 10, segment inset 2.
            return new(Math.Max(width, toneWidth + (hasTone ? 34 : 0)), height + (hasTone ? 40 : 0));
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            double y = 0;
            bool toneArranged = false;
            _segments = Rect.Empty;
            var segments = Visible.Where(child => Role(child) == MoreRole.ToneSegment).ToArray();
            foreach (var child in Visible)
            {
                if (Role(child) is MoreRole.ToneLabel or MoreRole.ToneSegment)
                {
                    if (toneArranged)
                        continue;
                    toneArranged = true;
                    double groupWidth = segments.Sum(segment => segment.DesiredSize.Width) + 4;
                    _segments = new(finalSize.Width - 10 - groupWidth, y + 6, groupWidth, 26);
                    var label = Visible.SingleOrDefault(element => Role(element) == MoreRole.ToneLabel);
                    label?.Arrange(new(10, y + 6, label.DesiredSize.Width, 26));
                    double x = _segments.Left + 2;
                    foreach (var segment in segments)
                    {
                        segment.Arrange(new(x, y + 8, segment.DesiredSize.Width, 22));
                        x += segment.DesiredSize.Width;
                    }
                    y += 40;
                }
                else
                {
                    child.Arrange(new(0, y, finalSize.Width, child.DesiredSize.Height));
                    y += child.DesiredSize.Height;
                }
            }
            InvalidateVisual();
            return finalSize;
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            if (!_segments.IsEmpty)
                drawing.DrawRoundedRectangle((Brush)FindResource("Panel2"), null, _segments, 8, 8);
        }
    }

    private static FrameworkElement MoreToneLabel(bool locked)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var canvas = new Canvas { Width = 24, Height = 24 };
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(locked ? MoreLock : MoreStyle),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        path.SetResourceReference(Shape.StrokeProperty, "Muted");
        canvas.Children.Add(path);
        row.Children.Add(new Viewbox { Width = 16, Height = 16, Child = canvas, Margin = new(0, 0, 10, 0) });
        row.Children.Add(UiTheme.Text(UiTheme.L("风格", "Tone"), 13));
        row.ToolTip = locked ? UiTheme.L("已人工修改，风格已锁定", "Edited by you - tone locked")
            : UiTheme.L("当前应用风格", "Tone for This App");
        return row;
    }

    private static void ConfigureMoreKeyboard(ContextMenu menu, IReadOnlyList<MenuItem> segments,
        MenuItem alternative, MenuItem regenerate, MenuItem copy, Func<bool> current, Action copyTranslation)
    {
        menu.PreviewKeyDown += (_, e) =>
        {
            if (!current())
                return;
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && copy.IsEnabled)
            {
                e.Handled = true;
                copyTranslation();
                menu.IsOpen = false;
                return;
            }
            if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is not MenuItem focused)
                return;
            if (!segments.Contains(focused))
            {
                var enabled = segments.Where(item => item.IsEnabled).ToArray();
                MenuItem? entry = enabled.Length == 0 ? null
                    : e.Key == Key.Down && (ReferenceEquals(focused, alternative) || ReferenceEquals(focused, regenerate) && !alternative.IsEnabled) ? enabled[0]
                    : e.Key == Key.Up && ReferenceEquals(focused, copy) ? enabled[^1] : null;
                if (entry is not null)
                {
                    e.Handled = true;
                    entry.Focus();
                }
                return;
            }
            MenuItem? next = null;
            if (e.Key is Key.Left or Key.Right)
            {
                var enabled = segments.Where(item => item.IsEnabled).ToArray();
                int index = Array.IndexOf(enabled, focused);
                if (index >= 0 && enabled.Length > 0)
                    next = enabled[(index + (e.Key == Key.Left ? enabled.Length - 1 : 1)) % enabled.Length];
            }
            else if (e.Key == Key.Up)
                next = alternative.IsEnabled ? alternative : regenerate.IsEnabled ? regenerate : null;
            else if (e.Key == Key.Down)
                next = menu.Items.OfType<MenuItem>().SkipWhile(item => !ReferenceEquals(item, copy)).FirstOrDefault(item => item.IsEnabled);
            else
                return; // Enter/Escape and native menu dismissal remain WPF's.
            e.Handled = true;
            next?.Focus(); // Only an existing MenuItem in this same owned menu.
        };
    }

    private static void StyleMenu(ContextMenu menu, Button anchor)
    {
        menu.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="ContextMenu">
                <Setter Property="MinWidth" Value="236"/><Setter Property="FontSize" Value="13"/>
                <Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="Background" Value="{DynamicResource Glass}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource Line}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu">
                  <Border CornerRadius="12" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="0.5" Padding="5">
                    <ScrollViewer CanContentScroll="False" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" Focusable="False">
                      <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/>
                    </ScrollViewer>
                  </Border>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style x:Key="MoreAction" TargetType="MenuItem">
                <Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="FontSize" Value="13"/>
                <Setter Property="Height" Value="30"/><Setter Property="Padding" Value="10,0"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
                  <Border x:Name="row" CornerRadius="7" Background="Transparent" Padding="{TemplateBinding Padding}">
                    <Grid>
                      <Grid.ColumnDefinitions><ColumnDefinition Width="16"/><ColumnDefinition Width="10"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                      <Viewbox Width="16" Height="16"><Canvas Width="24" Height="24">
                        <Path x:Name="icon" Data="{TemplateBinding Tag}" Stroke="{DynamicResource Muted}" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
                        <Path x:Name="check" Data="M5,12.5 L9.5,17 L19,7.5" Stroke="{DynamicResource AccentInk}" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" Visibility="Collapsed"/>
                      </Canvas></Viewbox>
                      <ContentPresenter Grid.Column="2" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                      <TextBlock Grid.Column="3" Margin="14,0,0,0" Text="{TemplateBinding InputGestureText}" FontSize="12" Opacity=".6" VerticalAlignment="Center"/>
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True"><Setter TargetName="check" Property="Visibility" Value="Visible"/><Setter TargetName="icon" Property="Visibility" Value="Collapsed"/></Trigger>
                    <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource AccentStrong}"/><Setter Property="Foreground" Value="White"/><Setter TargetName="icon" Property="Stroke" Value="White"/><Setter TargetName="check" Property="Stroke" Value="White"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource Tertiary}"/><Setter TargetName="icon" Property="Stroke" Value="{DynamicResource Tertiary}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style x:Key="MoreToneSegment" TargetType="MenuItem">
                <Setter Property="Foreground" Value="{DynamicResource Muted}"/><Setter Property="FontSize" Value="11.5"/><Setter Property="FontWeight" Value="Medium"/>
                <Setter Property="Height" Value="22"/><Setter Property="Padding" Value="8,0"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
                  <Grid>
                    <Border x:Name="surface" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}">
                      <ContentPresenter ContentSource="Header" VerticalAlignment="Center" HorizontalAlignment="Center"/>
                    </Border>
                    <Border x:Name="focus" CornerRadius="6" BorderBrush="Transparent" BorderThickness="1" IsHitTestVisible="False"/>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True"><Setter TargetName="surface" Property="Background" Value="{DynamicResource Panel}"/><Setter Property="Foreground" Value="{DynamicResource Text}"/></Trigger>
                    <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="focus" Property="BorderBrush" Value="{DynamicResource Text}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".35"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style x:Key="MoreToneLabel" TargetType="Separator"><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><ContentPresenter Content="{TemplateBinding Tag}" VerticalAlignment="Center"/></ControlTemplate></Setter.Value></Setter></Style>
              <Style TargetType="Separator"><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="0.5" Margin="10,5" Background="{DynamicResource Line}"/></ControlTemplate></Setter.Value></Setter></Style>
            </ResourceDictionary>
            """));
        // MoreContextMenu is a lifecycle-preserving subclass. Implicit styles
        // are looked up by its exact type, so apply the existing base-type key.
        menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
        menu.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(MoreMenuPanel)));
        foreach (var child in menu.Items.OfType<FrameworkElement>())
        {
            var role = (MoreRole)child.GetValue(MoreRoleProperty);
            if (child is MenuItem)
                child.SetResourceReference(StyleProperty, role == MoreRole.ToneSegment ? "MoreToneSegment" : "MoreAction");
            else if (child is Separator)
                child.SetResourceReference(StyleProperty, role == MoreRole.ToneLabel ? "MoreToneLabel" : typeof(Separator));
        }
        menu.FontFamily = UiTheme.UiFont;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (size, target, _) =>
        [
            new(new Point(target.Width - size.Width, target.Height + 6), PopupPrimaryAxis.Vertical),
            new(new Point(target.Width - size.Width, -size.Height - 6), PopupPrimaryAxis.Vertical)
        ];
    }

    // Original prototype SVG paths, in the original 24 × 24 viewBox.
    private const string MoreRefresh = "M20,5 V10 H15 M4,19 V14 H9 M19.2,10 A7.5,7.5 0 0 0 6,7.2 M4.8,14 A7.5,7.5 0 0 0 18,16.8";
    private const string MoreSparkle = "M12,3.5 C12.6,7.9 14.1,9.5 18.5,10 C14.1,10.6 12.6,12.1 12,16.5 C11.4,12.1 9.9,10.6 5.5,10 C9.9,9.5 11.4,7.9 12,3.5 Z M18.5,15.5 C18.7,17.1 19.3,17.7 20.9,17.9 C19.3,18.1 18.7,18.7 18.5,20.3 C18.3,18.7 17.7,18.1 16.1,17.9 C17.7,17.7 18.3,17.1 18.5,15.5 Z";
    private const string MoreCopy = "M10.5,8 H17.5 A2.5,2.5 0 0 1 20,10.5 V17.5 A2.5,2.5 0 0 1 17.5,20 H10.5 A2.5,2.5 0 0 1 8,17.5 V10.5 A2.5,2.5 0 0 1 10.5,8 Z M16,8 V6 A2,2 0 0 0 14,4 H6 A2,2 0 0 0 4,6 V14 A2,2 0 0 0 6,16 H8";
    private const string MoreBook = "M5,4.5 H15.5 A2.5,2.5 0 0 1 18,7 V19.5 H7.5 A2.5,2.5 0 0 1 5,17 Z M5,17 A2.5,2.5 0 0 1 7.5,14.5 H18 M9,8.5 H14";
    private const string MoreHide = "M3,3 L21,21 M10.6,5.2 A9.6,9.6 0 0 1 12,5 C17,5 20.5,9.5 21.5,12 A13,13 0 0 1 18.6,16 M6.4,6.5 C4.4,7.8 3.1,9.8 2.5,12 C3.5,14.5 7,19 12,19 A9.4,9.4 0 0 0 16.6,17.8 M9.9,9.9 A3,3 0 0 0 14.1,14.1";
    private const string MoreExclude = "M20.5,12 A8.5,8.5 0 1 1 3.5,12 A8.5,8.5 0 1 1 20.5,12 Z M6,6 L18,18";
    private const string MoreStyle = "M4,19 H8 L19,8 L15,4 L4,15 Z M13,6 L17,10";
    private const string MoreLock = "M7.5,10.5 H16.5 A2.5,2.5 0 0 1 19,13 V17.5 A2.5,2.5 0 0 1 16.5,20 H7.5 A2.5,2.5 0 0 1 5,17.5 V13 A2.5,2.5 0 0 1 7.5,10.5 Z M8.5,10.5 V8 A3.5,3.5 0 0 1 15.5,8 V10.5";
    private const string MoreExpand = "M14,4 H20 V10 M20,4 L13.5,10.5 M10,20 H4 V14 M4,20 L10.5,13.5";
    private const string MoreSettings = "M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12 Z M12,2.8 V5.2 M12,18.8 V21.2 M21.2,12 H18.8 M5.2,12 H2.8 M18.5,5.5 L16.8,7.2 M7.2,16.8 L5.5,18.5 M18.5,18.5 L16.8,16.8 M7.2,7.2 L5.5,5.5";
    private const string MoreInfo = "M20.5,12 A8.5,8.5 0 1 1 3.5,12 A8.5,8.5 0 1 1 20.5,12 Z M12,11 V16.5 M12,7.8 V7.81";
}
