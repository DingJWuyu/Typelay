using TypeLay.Runtime;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace TypeLay.App;

internal sealed partial class LanguagePicker
{
    private readonly bool _searchPopover;
    private object? _popupIdentity;
    private TextBox? _search;
    private TextBlock? _empty;
    private TextBlock? _placeholder;
    private int _highlight = -1;

    private void InitializePopover()
    {
        DropDownOpened += (_, _) =>
        {
            if (!IsVisible || !IsEnabled)
            {
                IsDropDownOpen = false;
                return;
            }
            object identity = _popupIdentity = new();
            RenewChoiceIdentities();
            _highlight = -1;
            _search?.Clear();
            RefreshPopoverOptions();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(identity, _popupIdentity) && IsVisible && IsDropDownOpen)
                    _search?.Focus();
            }), DispatcherPriority.Input);
        };
        DropDownClosed += (_, _) => { if (!IsDropDownOpen) { _popupIdentity = null; _highlight = -1; } };
        Unloaded += (_, _) => { _popupIdentity = null; IsDropDownOpen = false; };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { _popupIdentity = null; IsDropDownOpen = false; } };
        ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                RefreshPopoverOptions();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (!IsDropDownOpen && e.Key is Key.Enter or Key.Space or Key.Down or Key.Up)
            {
                e.Handled = true;
                IsDropDownOpen = true;
            }
        };
    }

    private void RenewChoiceIdentities()
    {
        // A new opening or query retires existing item peers, including query
        // A -> B -> A. The separate search editor and its focus stay mounted.
        var choices = Items.Cast<LanguageChoice>().Select(choice => new LanguageChoice(choice.Code, choice.Label, choice.Available)).ToArray();
        _synchronizing = true;
        try
        {
            ItemsSource = choices;
            SelectedItem = choices.Single(choice => choice.Code == _selectionCode);
        }
        finally { _synchronizing = false; }
    }

    public override void OnApplyTemplate()
    {
        if (_search is not null)
        {
            _search.PreviewKeyDown -= SearchKey;
            _search.TextChanged -= SearchChanged;
        }
        base.OnApplyTemplate();
        if (!_searchPopover)
            return;
        _search = GetTemplateChild("PART_LanguageSearch") as TextBox;
        _empty = GetTemplateChild("PART_LanguageEmpty") as TextBlock;
        _placeholder = GetTemplateChild("PART_LanguagePlaceholder") as TextBlock;
        if (_placeholder is not null)
            _placeholder.Text = UiTheme.L("搜索语言", "Search languages");
        if (GetTemplateChild("PART_Popup") is Popup popup)
        {
            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback = (size, target, _) =>
            [new(new((target.Width - size.Width) / 2, target.Height + 6), PopupPrimaryAxis.Vertical),
                new(new((target.Width - size.Width) / 2, -size.Height - 6), PopupPrimaryAxis.Vertical)];
        }
        if (_search is not null)
        {
            AutomationProperties.SetName(_search, UiTheme.L("搜索语言", "Search languages"));
            AutomationProperties.SetAutomationId(_search, "LanguageSearch");
            _search.ToolTip = UiTheme.L("搜索语言", "Search languages");
            _search.TextChanged += SearchChanged;
            _search.PreviewKeyDown += SearchKey;
        }
        if (_empty is not null)
            _empty.Text = UiTheme.L("没有匹配的语言", "No matching languages");
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _search) || _search is null)
            return;
        if (IsDropDownOpen && !_synchronizing)
            RenewChoiceIdentities();
        _highlight = _search.Text.Length == 0 ? -1 : 0;
        RefreshPopoverOptions();
    }

    private void SearchKey(object sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(sender, _search) || !IsVisible || !IsDropDownOpen || _popupIdentity is null || _search?.IsKeyboardFocused != true)
            return;
        var visible = MatchingChoices().Where(choice => choice.Available).ToArray();
        if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            if (visible.Length == 0)
                return;
            _highlight = _highlight < 0 ? (e.Key == Key.Down ? 1 % visible.Length : visible.Length - 1)
                : (_highlight + (e.Key == Key.Down ? 1 : -1) + visible.Length) % visible.Length;
            RefreshPopoverOptions();
            (ItemContainerGenerator.ContainerFromItem(visible[_highlight]) as ComboBoxItem)?.BringIntoView();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (visible.Length == 0)
                return;
            ChooseCurrent(visible[Math.Clamp(_highlight, 0, visible.Length - 1)]);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            IsDropDownOpen = false;
            if (IsVisible && !IsDropDownOpen)
                Focus();
        }
        // Tab follows native focus navigation; it never commits a highlighted row.
    }

    private void ChooseCurrent(LanguageChoice choice)
    {
        if (!IsVisible || !IsEnabled || !IsDropDownOpen || _popupIdentity is null || !choice.Available || !MatchingChoices().Contains(choice))
            return;
        object identity = _popupIdentity;
        SelectedItem = choice;
        // A changed selection closes before saving. Only the unchanged
        // selection needs closing here; never close a re-entrant opening.
        if (ReferenceEquals(identity, _popupIdentity))
            IsDropDownOpen = false;
        if (IsVisible && !IsDropDownOpen)
            Focus();
    }

    protected override DependencyObject GetContainerForItemOverride() => _searchPopover
        ? new PopoverOption(this) : base.GetContainerForItemOverride();

    private sealed class PopoverOption(LanguagePicker owner) : ComboBoxItem
    {
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            // Native ComboBoxItem closes again after SelectionChanged, which
            // could close a new popup opened synchronously by the save callback.
            e.Handled = true;
            if (IsMouseOver && owner.ItemContainerGenerator.ItemFromContainer(this) is LanguageChoice choice)
                owner.ChooseCurrent(choice);
        }
    }

    private IEnumerable<LanguageChoice> MatchingChoices()
    {
        string query = _search?.Text.Trim() ?? "";
        return Items.Cast<LanguageChoice>().Where(choice => query.Length == 0
            || choice.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || choice.NativeName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || choice.Code.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshPopoverOptions()
    {
        if (!_searchPopover)
            return;
        if (_placeholder is not null)
            _placeholder.Visibility = string.IsNullOrEmpty(_search?.Text) ? Visibility.Visible : Visibility.Collapsed;
        var visible = MatchingChoices().ToArray();
        var enabled = visible.Where(choice => choice.Available).ToArray();
        if (_highlight >= enabled.Length)
            _highlight = enabled.Length - 1;
        foreach (var choice in Items.Cast<LanguageChoice>())
            if (ItemContainerGenerator.ContainerFromItem(choice) is ComboBoxItem container)
            {
                container.Visibility = visible.Contains(choice) ? Visibility.Visible : Visibility.Collapsed;
                container.Tag = _highlight >= 0 && _highlight < enabled.Length && ReferenceEquals(enabled[_highlight], choice) ? "selected" : "";
            }
        if (_empty is not null)
            _empty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PreparePopoverOption(ComboBoxItem container, LanguageChoice choice)
    {
        container.SetResourceReference(StyleProperty, "SettingsLanguageOption");
        container.ToolTip = choice.Available ? null : UiTheme.L("当前模型未提供此语言。", "This language is not available in the current model.");
        ToolTipService.SetShowOnDisabled(container, true);
    }

    private void RestoreConfirmedSelection()
    {
        _synchronizing = true;
        try
        {
            SelectedItem = Items.Cast<LanguageChoice>().Single(choice => choice.Code == _selectionCode);
        }
        finally { _synchronizing = false; }
    }

    private static string PopoverLabel(string code, bool available = true)
    {
        var presentation = LanguageChoicePresentation.Create(code, available);
        return UiTheme.English ? presentation.LabelEn : presentation.LabelZh;
    }
}
