using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.Runtime;
using TypeLay.Core;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private int _glossarySelection = -1;
    private GlossaryEdit? _glossaryEdit;
    private string _glossaryQuery = "";
    private TaskCompletionSource? _glossarySaveCompletion;

    private async Task CommitGlossaryBeforeNavigationAsync()
    {
        if (_glossarySaveCompletion is { } pending)
            await pending.Task;
        else if (_glossaryDraft is { } rules && rules.All(rule => !string.IsNullOrWhiteSpace(rule.SourceTerm) && !string.IsNullOrWhiteSpace(rule.TargetTerm)))
            await SaveGlossaryDraftAsync();
    }

    // This is the user's single inline edit, not a new business identity. The
    // controller still validates and persists the complete revision-bound draft.
    private sealed class GlossaryEdit(int index, IReadOnlyList<GlossaryRule> before, bool hadDraft, long revision)
    {
        internal int Index { get; } = index;
        internal IReadOnlyList<GlossaryRule> Before { get; set; } = before;
        internal bool HadDraft { get; set; } = hadDraft;
        internal long Revision { get; } = revision;
    }

    private void Glossary()
    {
        Heading(UiTheme.L("术语表", "Glossary"));
        object page = _pageIdentity;
        if (_glossaryEdit is null)
            _glossarySelection = -1;
        var state = _controller?.GetGlossarySettingsState();
        var modelMessage = UiTheme.Text("", 13);
        var modelRow = new DockPanel { LastChildFill = true };
        var viewModel = UiTheme.Button(UiTheme.L("查看", "View"), () => { if (IsVisible && ReferenceEquals(page, _pageIdentity)) ShowPage("model"); });
        viewModel.Padding = new(10, 4, 10, 4);
        DockPanel.SetDock(viewModel, Dock.Right);
        modelRow.Children.Add(viewModel);
        var warning = PrototypeGlyph("M12,4 L2.8,19.5 H21.2 Z M12,10 V14.5 M12,17 V17.01", "Warn");
        warning.Width = warning.Height = 18;
        warning.Margin = new(0, 0, 12, 0);
        DockPanel.SetDock(warning, Dock.Left);
        modelRow.Children.Add(warning);
        modelRow.Children.Add(modelMessage);
        var modelCard = UiTheme.Border(modelRow, 12);
        modelCard.Padding = new(14, 12, 14, 12);
        modelCard.BorderThickness = new(.5);
        modelCard.Margin = new(0, 0, 0, 18);
        _content.Children.Add(modelCard);
        void RefreshModelBanner()
        {
            modelCard.Visibility = _model.Ready ? Visibility.Collapsed : Visibility.Visible;
            modelMessage.Text = _model.Installed ? UiTheme.L("模型尚未就绪", "Model not ready") : UiTheme.L("需要下载本地翻译模型", "Download the local translation model");
        }
        RefreshModelBanner();
        var search = new TextBox { Text = _glossaryQuery, Margin = new(0, 0, 0, 12) };
        AutomationProperties.SetName(search, UiTheme.L("搜索术语", "Search terms"));
        AutomationProperties.SetAutomationId(search, "GlossarySearch");
        search.ToolTip = UiTheme.L("搜索原文或译文", "Search source or translated terms");
        _content.Children.Add(SearchField(search, UiTheme.L("搜索术语", "Search terms")));
        if (state is null)
        {
            var unavailable = Note(UiTheme.L("术语表不可用", "Glossary unavailable"), UiTheme.L("翻译服务尚未连接。", "The translation service is not connected."));
            RegisterSearchTarget("glossary", (Panel)unavailable.Parent);
            return;
        }
        var scopes = new[] { new GlossaryScope(UiTheme.L("所有方向", "All directions"), null, null) }.Concat(ModelCatalog.Packages.Packages
            .SelectMany(package => package.Directions).DistinctBy(direction => direction.Id)
            .Select(direction => new GlossaryScope(ScopeLanguage(direction.Source) + " → " + ScopeLanguage(direction.Target), direction.Source, direction.Target))).ToArray();
        GlossarySnapshot? displayed = state.SavedSnapshot;
        long displayedRevision = state.SettingsRevision;
        IReadOnlyList<GlossaryRule> mountedRules = [];
        GlossaryEdit? mountedEdit = null;
        object rowsIdentity = new(), toolbarIdentity = new();
        (int Selected, GlossaryRule? Rule, object Generation, bool Writable, int Count)? toolbarState = null;
        bool rendering = false;
        var table = new StackPanel();
        RegisterSearchTarget("glossary", table);
        table.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(GlossaryStyles));
        var header = GlossaryColumns();
        header.Margin = new(0, 10, 0, 8);
        string[] titles = [UiTheme.L("原文", "Source"), UiTheme.L("译文", "Translation"), UiTheme.L("范围", "Applies to")];
        for (int column = 0; column < titles.Length; column++)
        {
            var label = UiTheme.Text(titles[column], 11.5, true, true);
            label.LineHeight = 11.5;
            label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            label.Margin = new(14, 0, 14, 0);
            Grid.SetColumn(label, column);
            header.Children.Add(label);
        }
        table.Children.Add(header);
        var rows = new ListBox { BorderThickness = new(0), Padding = new(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(rows, "GlossaryRows");
        AutomationProperties.SetName(rows, UiTheme.L("术语", "Terms"));
        ScrollViewer.SetVerticalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
        rows.Resources.Add(typeof(ListBoxItem), XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
              <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
              <Setter Property="Padding" Value="0"/><Setter Property="BorderThickness" Value="0"/>
              <Setter Property="Background" Value="Transparent"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
                <Border Background="{TemplateBinding Background}" BorderBrush="{DynamicResource Line}" BorderThickness="0,.5,0,0">
                  <ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Center"/>
                </Border>
              </ControlTemplate></Setter.Value></Setter>
              <Style.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource SettingsLanguageFill}"/></Trigger>
                <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{DynamicResource AccentSoft}"/></Trigger>
              </Style.Triggers>
            </Style>
            """));
        table.Children.Add(rows);
        var empty = new StackPanel { Margin = new(20, 36, 20, 36), HorizontalAlignment = HorizontalAlignment.Center };
        var book = PrototypeGlyph("M5,4.5 H15.5 A2.5,2.5 0 0 1 18,7 V19.5 H7.5 A2.5,2.5 0 0 1 5,17 Z M5,17 A2.5,2.5 0 0 1 7.5,14.5 H18 M9,8.5 H14", "Tertiary", stroke: 1.3);
        book.Width = book.Height = 28;
        empty.Children.Add(book);
        var emptyText = UiTheme.Text("", 13, true);
        emptyText.Margin = new(0, 10, 0, 10);
        emptyText.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Children.Add(emptyText);
        var emptyAdd = new Button();
        empty.Children.Add(emptyAdd);
        table.Children.Add(empty);
        var listbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(4) };
        var bar = new Border { BorderThickness = new(0, .5, 0, 0), Child = listbar };
        bar.SetResourceReference(Border.BorderBrushProperty, "Line");
        table.Children.Add(bar);
        var card = UiTheme.Border(table, 12);
        AutomationProperties.SetAutomationId(card, "GlossaryTable");
        card.BorderThickness = new(.5);
        card.Margin = new(0, 0, 0, 14);
        _content.Children.Add(card);
        var availability = UiTheme.Text("", 12, true);
        availability.Margin = new(4, 0, 4, 8);
        _content.Children.Add(availability);

        bool Mounted() => ReferenceEquals(page, _pageIdentity) && _content.Children.Contains(card);
        bool CurrentPage() => IsVisible && Mounted();
        bool Writable() => _controller!.GetGlossarySettingsState().CanSave && !_savingTranslation;
        IReadOnlyList<GlossaryRule> Rules() => _glossaryDraft ?? (IReadOnlyList<GlossaryRule>?)displayed?.Rules ?? [];
        void BeginDraft()
        {
            if (_glossaryDraft is not null)
                return;
            _glossaryDraft = Rules().ToList();
            _glossaryDraftRevision = _glossaryEdit?.Revision ?? displayedRevision;
            _glossaryDraftBaseline = _glossaryEdit?.Before ?? displayed?.Rules.ToArray() ?? [];
        }
        bool CurrentEdit(GlossaryEdit edit) => CurrentPage() && ReferenceEquals(edit, _glossaryEdit) && edit.Index >= 0 && edit.Index < Rules().Count;
        async Task<bool> CommitAsync()
        {
            if (!CurrentPage() || _savingTranslation)
                return false;
            var edit = _glossaryEdit;
            if (_glossaryDraft is not null)
                await SaveGlossaryDraftAsync();
            if (!CurrentPage() || _glossaryDraft is not null || _savingTranslation)
                return false;
            if (ReferenceEquals(edit, _glossaryEdit))
                _glossaryEdit = null;
            RenderRows();
            RefreshDraftBar();
            return true;
        }
        async Task AddAsync()
        {
            if (!CurrentPage() || !Writable() || !await CommitAsync() || !CurrentPage() || !Writable() || Rules().Count >= GlossarySnapshot.MaxRules)
                return;
            var before = Rules().ToArray();
            bool hadDraft = _glossaryDraft is not null;
            BeginDraft();
            _glossarySelection = _glossaryDraft!.Count;
            _glossaryEdit = new(_glossarySelection, before, hadDraft, _glossaryDraftRevision);
            _glossaryDraft.Add(new("", ""));
            search.Clear();
            RenderRows(focusEditor: true);
            RefreshDraftBar();
        }
        async Task EditAsync(int index, GlossaryRule rule, object identity)
        {
            if (!CurrentPage() || !ReferenceEquals(identity, rowsIdentity) || !Writable() || index >= Rules().Count || !Equals(Rules()[index], rule))
                return;
            if (_glossaryEdit?.Index == index)
                return;
            if (!await CommitAsync() || !CurrentPage() || !Writable() || index >= Rules().Count || !Equals(Rules()[index], rule))
                return;
            _glossarySelection = index;
            _glossaryEdit = new(index, Rules().ToArray(), _glossaryDraft is not null, displayedRevision);
            RenderRows(focusEditor: true);
        }
        void CancelEdit(GlossaryEdit edit)
        {
            if (!CurrentEdit(edit) || _savingTranslation)
                return;
            _glossaryDraft = edit.HadDraft ? edit.Before.ToList() : null;
            _glossaryEdit = null;
            if (_glossarySelection >= Rules().Count)
                _glossarySelection = -1;
            _settingsError = _draftFeedback = null;
            RenderRows();
            RefreshDraftBar();
            (rows.SelectedItem as ListBoxItem)?.Focus();
        }
        async Task RemoveAsync(int index, GlossaryRule rule, object identity)
        {
            if (!CurrentPage() || !ReferenceEquals(identity, rowsIdentity) || !Writable() || index < 0 || index >= Rules().Count || !Equals(Rules()[index], rule))
                return;
            if (_glossaryEdit is { } added && added.Index == index && index >= added.Before.Count)
            {
                CancelEdit(added);
                return;
            }
            // Finish the selected edit before deleting. A failed or canceled save
            // remains editable rather than being silently replaced by deletion.
            var committedRule = rule with
            {
                SourceTerm = rule.SourceTerm.Trim(),
                TargetTerm = rule.TargetTerm.Trim()
            };
            if (!await CommitAsync() || !CurrentPage() || !Writable() || index >= Rules().Count || !Equals(Rules()[index], committedRule))
                return;
            var before = Rules().ToArray();
            BeginDraft();
            _glossaryDraft!.RemoveAt(index);
            var after = _glossaryDraft.ToArray();
            _glossarySelection = -1;
            RenderRows();
            await SaveGlossaryDraftAsync();
            if (!CurrentPage() || _glossaryDraft is not null || !after.SequenceEqual(_settings.Value.Glossary?.Rules ?? []))
                return;
            long revision = _settings.Revision;
            Toast(UiTheme.L("已删除术语", "Term removed"), async () =>
            {
                if (!IsVisible || _glossaryDraft is not null || _glossaryEdit is not null || _settings.Revision != revision || !after.SequenceEqual(_settings.Value.Glossary?.Rules ?? []))
                    return;
                var result = await _controller!.ChangeGlossaryAsync(before, revision);
                Toast(result.Saved ? UiTheme.L("已恢复术语", "Term restored") : UiTheme.L("恢复未确认，请检查术语表。", "Restore was not confirmed. Review the glossary."));
            }, () => _glossaryDraft is null && _glossaryEdit is null && _settings.Revision == revision && after.SequenceEqual(_settings.Value.Glossary?.Rules ?? []));
        }
        void RenderToolbar()
        {
            object generation = rowsIdentity;
            int selected = _glossarySelection;
            GlossaryRule? rule = selected >= 0 && selected < Rules().Count && rows.SelectedItem is not null ? Rules()[selected] : null;
            var current = (selected, rule, generation, Writable(), Rules().Count);
            if (toolbarState == current)
                return;
            toolbarState = current;
            object toolbar = toolbarIdentity = new();
            listbar.Children.Clear();
            var add = ToolButton("GlossaryAdd", UiTheme.L("添加术语", "Add term"), "M12,5 V19 M5,12 H19", async () =>
            {
                if (ReferenceEquals(toolbar, toolbarIdentity) && CurrentPage())
                    await AddAsync();
            });
            add.IsEnabled = Writable() && Rules().Count < GlossarySnapshot.MaxRules;
            listbar.Children.Add(add);
            var remove = ToolButton("GlossaryRemove", UiTheme.L("删除术语", "Remove term"), "M5,12 H19", async () =>
            {
                if (rule is not null && ReferenceEquals(toolbar, toolbarIdentity))
                    await RemoveAsync(selected, rule, generation);
            });
            remove.IsEnabled = Writable() && rule is not null && rows.SelectedItem is not null;
            listbar.Children.Add(remove);
            emptyAdd.IsEnabled = add.IsEnabled;
        }
        void RenderRows(bool focusEditor = false)
        {
            if (!Mounted())
                return;
            if (_glossaryDraft is null && _glossaryEdit is null)
            {
                var saved = _controller!.GetGlossarySettingsState();
                displayed = saved.SavedSnapshot;
                displayedRevision = saved.SettingsRevision;
            }
            object identity = rowsIdentity = new();
            mountedEdit = _glossaryEdit;
            mountedRules = Rules().ToArray();
            rendering = true;
            rows.Items.Clear();
            TextBox? focus = null;
            string query = search.Text.Trim();
            for (int index = 0; index < mountedRules.Count; index++)
            {
                int rowIndex = index;
                var rule = mountedRules[index];
                var edit = _glossaryEdit?.Index == index ? _glossaryEdit : null;
                if (edit is null && query.Length > 0 && !rule.SourceTerm.Contains(query, StringComparison.OrdinalIgnoreCase) && !rule.TargetTerm.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                var content = GlossaryColumns();
                var row = new ListBoxItem { Content = content, Height = 38, IsSelected = index == _glossarySelection };
                AutomationProperties.SetAutomationId(row, "GlossaryRow_" + (index + 1));
                AutomationProperties.SetName(row, rule.SourceTerm + " → " + rule.TargetTerm);
                bool CurrentRow() => CurrentPage() && ReferenceEquals(identity, rowsIdentity) && rowIndex < Rules().Count && Equals(Rules()[rowIndex], rule);
                if (edit is null)
                {
                    string scopeLabel = scopes.SingleOrDefault(item => item.Source == rule.SourceLanguage && item.Target == rule.TargetLanguage)?.Label
                        ?? ScopeLanguage(rule.SourceLanguage!) + " → " + ScopeLanguage(rule.TargetLanguage!);
                    string[] values = [rule.SourceTerm, rule.TargetTerm, scopeLabel];
                    for (int column = 0; column < values.Length; column++)
                    {
                        var label = UiTheme.Text(values[column], column == 2 ? 13 : 14, muted: column == 2);
                        label.Text = values[column]; // User terms are never UI-localized.
                        label.Margin = new(14, 0, 14, 0);
                        label.TextWrapping = TextWrapping.NoWrap;
                        label.TextTrimming = TextTrimming.CharacterEllipsis;
                        label.ToolTip = values[column];
                        if (column != 2)
                            label.FontFamily = UiTheme.ContentFontForLanguage(column == 0 ? rule.SourceLanguage ?? _settings.Value.Source : rule.TargetLanguage ?? _settings.Value.Target);
                        Grid.SetColumn(label, column);
                        content.Children.Add(label);
                    }
                    row.MouseDoubleClick += async (_, e) => { if (CurrentRow()) { e.Handled = true; await EditAsync(rowIndex, rule, identity); } };
                    row.PreviewKeyDown += async (_, e) =>
                    {
                        if (!CurrentRow())
                            return;
                        if (e.Key == Key.Enter)
                        {
                            e.Handled = true;
                            await EditAsync(rowIndex, rule, identity);
                        }
                        else if (e.Key is Key.Delete or Key.Back)
                        {
                            e.Handled = true;
                            await RemoveAsync(rowIndex, rule, identity);
                        }
                    };
                }
                else
                {
                    var source = GlossaryEditor(rule.SourceTerm, rule.SourceLanguage ?? _settings.Value.Source, UiTheme.L("源词 ", "Source term ") + (index + 1));
                    var target = GlossaryEditor(rule.TargetTerm, rule.TargetLanguage ?? _settings.Value.Target, UiTheme.L("译词 ", "Translation ") + (index + 1));
                    Grid.SetColumn(target, 1);
                    content.Children.Add(source);
                    content.Children.Add(target);
                    var selected = scopes.SingleOrDefault(item => item.Source == rule.SourceLanguage && item.Target == rule.TargetLanguage)
                        ?? new GlossaryScope(ScopeLanguage(rule.SourceLanguage!) + " → " + ScopeLanguage(rule.TargetLanguage!), rule.SourceLanguage, rule.TargetLanguage);
                    var scope = new ComboBox { ItemsSource = scopes.Contains(selected) ? scopes : scopes.Append(selected).ToArray(), DisplayMemberPath = nameof(GlossaryScope.Label), SelectedItem = selected, Height = 24, MinHeight = 24, Margin = new(14, 0, 14, 0), FontSize = 12.5, FontWeight = FontWeights.Medium, ToolTip = selected.Label };
                    scope.SetResourceReference(StyleProperty, "GlossaryScope");
                    AutomationProperties.SetName(scope, UiTheme.L("术语范围 ", "Term scope ") + (index + 1));
                    Grid.SetColumn(scope, 2);
                    content.Children.Add(scope);
                    source.IsReadOnly = target.IsReadOnly = !Writable();
                    scope.IsEnabled = Writable();
                    void Change()
                    {
                        if (!CurrentRow() || !CurrentEdit(edit) || scope.SelectedItem is not GlossaryScope choice)
                            return;
                        BeginDraft();
                        rule = new(source.Text, target.Text, choice.Source, choice.Target);
                        _glossaryDraft![rowIndex] = rule;
                        mountedRules = _glossaryDraft.ToArray();
                        _draftFeedback = _settingsError = null;
                        RefreshDraftBar();
                        RenderToolbar();
                    }
                    source.TextChanged += (_, _) => Change();
                    target.TextChanged += (_, _) => Change();
                    scope.SelectionChanged += (_, _) => Change();
                    row.PreviewKeyDown += async (_, e) =>
                    {
                        if (!CurrentRow() || !CurrentEdit(edit))
                            return;
                        if (e.Key == Key.Escape)
                        {
                            e.Handled = true;
                            CancelEdit(edit);
                        }
                        else if (e.Key == Key.Enter && !scope.IsDropDownOpen)
                        {
                            e.Handled = true;
                            if (await CommitAsync() && CurrentPage())
                                (rows.SelectedItem as ListBoxItem)?.Focus();
                        }
                    };
                    row.LostKeyboardFocus += (_, _) =>
                    {
                        _ = Dispatcher.BeginInvoke(new Action(async () =>
                        {
                            if (CurrentRow() && CurrentEdit(edit) && !row.IsKeyboardFocusWithin && !scope.IsDropDownOpen && _closeDialog is null)
                                await CommitAsync();
                        }), DispatcherPriority.Background);
                    };
                    focus = rule.SourceTerm.Length == 0 ? source : target;
                }
                rows.Items.Add(row);
            }
            rendering = false;
            if (rows.SelectedItem is null)
                _glossarySelection = -1;
            var oldEmptyAdd = emptyAdd;
            emptyAdd = UiTheme.Button(UiTheme.L("添加术语", "Add term"), async () =>
            {
                if (CurrentPage() && ReferenceEquals(identity, rowsIdentity) && Rules().Count == 0)
                    await AddAsync();
            });
            AutomationProperties.SetAutomationId(emptyAdd, "GlossaryEmptyAdd");
            emptyAdd.HorizontalAlignment = HorizontalAlignment.Center;
            emptyAdd.Height = 26;
            emptyAdd.FontSize = 12.5;
            emptyAdd.FontWeight = FontWeights.Medium;
            emptyAdd.Padding = new(12, 0, 12, 0);
            empty.Children.Remove(oldEmptyAdd);
            empty.Children.Add(emptyAdd);
            bool none = rows.Items.Count == 0;
            header.Visibility = rows.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            emptyText.Text = UiTheme.L(Rules().Count == 0 ? "还没有术语" : "没有匹配的术语", Rules().Count == 0 ? "No terms yet" : "No matching terms");
            emptyAdd.Visibility = Rules().Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderToolbar();
            if (focusEditor && focus is not null)
            {
                rows.UpdateLayout();
                if (CurrentPage() && ReferenceEquals(identity, rowsIdentity))
                    focus.Focus();
            }
        }
        rows.SelectionChanged += (_, _) =>
        {
            if (rendering || !CurrentPage())
                return;
            if (rows.SelectedItem is ListBoxItem selected && rows.Items.Contains(selected))
            {
                string id = AutomationProperties.GetAutomationId(selected);
                if (int.TryParse(id.AsSpan("GlossaryRow_".Length), out int index))
                    _glossarySelection = index - 1;
            }
            else
                _glossarySelection = -1;
            RenderToolbar();
        };
        search.TextChanged += (_, _) =>
        {
            if (!CurrentPage())
                return;
            _glossaryQuery = search.Text;
            RenderRows();
        };
        _refreshGlossarySettings = () =>
        {
            if (!Mounted())
                return;
            var current = _controller!.GetGlossarySettingsState();
            RefreshModelBanner();
            if (_glossaryDraft is null && _glossaryEdit is null)
            {
                if (mountedEdit is null && !mountedRules.SequenceEqual(current.SavedSnapshot?.Rules ?? []))
                    _glossarySelection = -1;
                displayed = current.SavedSnapshot;
                displayedRevision = current.SettingsRevision;
            }
            if (!mountedRules.SequenceEqual(Rules()) || !ReferenceEquals(mountedEdit, _glossaryEdit))
                RenderRows();
            else
            {
                foreach (var input in VisualControls(rows).OfType<TextBox>())
                    input.IsReadOnly = !Writable();
                foreach (var choice in VisualControls(rows).OfType<ComboBox>())
                    choice.IsEnabled = Writable();
                RenderToolbar();
            }
            availability.Text = current.IsReleased
                ? UiTheme.L("术语保存在本机；翻译时仍会检查模型、语言方向与输入状态。", "Terms are stored on this device. Model, direction and input-state checks still apply.")
                : UiTheme.L("术语保存在本机。当前模型方向尚未开放术语应用。", "Terms are stored on this device. Glossary application is not available for the current model and direction.");
        };
        RenderRows();
        _refreshGlossarySettings();
    }

    private const string GlossaryStyles = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="GlossaryTool" TargetType="Button">
            <Setter Property="Foreground" Value="{DynamicResource Muted}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="surface" CornerRadius="6" Background="Transparent" BorderBrush="Transparent" BorderThickness="1"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="Background" Value="{DynamicResource SettingsControlFill}"/><Setter Property="Foreground" Value="{DynamicResource Text}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="surface" Property="BorderBrush" Value="{DynamicResource Text}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".35"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="GlossaryField" TargetType="TextBox">
            <Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="Background" Value="{DynamicResource Panel}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Line}"/><Setter Property="BorderThickness" Value=".5"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox"><Grid>
              <Border x:Name="halo" BorderBrush="{DynamicResource AccentSoft}" BorderThickness="3" CornerRadius="10" Margin="-3" Visibility="Collapsed" IsHitTestVisible="False"/>
              <Border x:Name="surface" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7">
                <ScrollViewer x:Name="PART_ContentHost" Focusable="False" VerticalAlignment="Center"/>
              </Border>
            </Grid><ControlTemplate.Triggers>
              <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="surface" Property="BorderBrush" Value="{DynamicResource Accent}"/><Setter TargetName="surface" Property="BorderThickness" Value="1"/><Setter TargetName="halo" Property="Visibility" Value="Visible"/></Trigger>
            </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="GlossaryScope" TargetType="ComboBox">
            <Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="Background" Value="{DynamicResource Panel}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Line}"/><Setter Property="BorderThickness" Value=".5"/>
            <Setter Property="Padding" Value="10,0,28,0"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid>
              <ToggleButton Focusable="False" ClickMode="Press" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                  <Border Background="{DynamicResource Panel}" BorderBrush="{DynamicResource Line}" BorderThickness=".5" CornerRadius="7">
                    <Canvas Width="10" Height="12" Margin="0,0,9,0" HorizontalAlignment="Right" VerticalAlignment="Center" IsHitTestVisible="False">
                      <Path Data="M2,4.5 L5,1.8 L8,4.5 M2,7.5 L5,10.2 L8,7.5" Stroke="{DynamicResource Muted}" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
                    </Canvas>
                  </Border>
                </ControlTemplate></ToggleButton.Template>
              </ToggleButton>
              <ContentPresenter Margin="{TemplateBinding Padding}" VerticalAlignment="Center" IsHitTestVisible="False" ClipToBounds="True" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
              <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False">
                <Border Background="{DynamicResource Panel}" BorderBrush="{DynamicResource Line}" BorderThickness=".5" CornerRadius="8" Padding="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                  <ScrollViewer MaxHeight="260" VerticalScrollBarVisibility="Auto"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                </Border>
              </Popup>
            </Grid><ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".4"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """;

    private static string ScopeLanguage(string code)
    {
        var label = LanguageChoicePresentation.Create(code);
        return UiTheme.English ? label.LabelEn : label.LabelZh;
    }
    private static Grid GlossaryColumns()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new()
        {
            Width = new(150)
        });
        return grid;
    }
    private static TextBox GlossaryEditor(string text, string language, string name)
    {
        var box = new TextBox { Text = text, Height = 26, Margin = new(14, 0, 14, 0), Padding = new(10, 0, 10, 0), FontSize = 13, FontFamily = UiTheme.UiFontForLanguage(language), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, name);
        box.SetResourceReference(StyleProperty, "GlossaryField");
        return box;
    }
    private static Button ToolButton(string id, string name, string geometry, Action click)
    {
        var button = UiTheme.Button("", click);
        var icon = (Viewbox)PrototypeGlyph(geometry, "Muted");
        var path = (System.Windows.Shapes.Path)((Canvas)icon.Child).Children[0];
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = button });
        button.Content = icon;
        button.SetResourceReference(StyleProperty, "GlossaryTool");
        button.Width = 28;
        button.Height = 24;
        button.Padding = new(0);
        button.BorderThickness = new(0);
        button.Background = Brushes.Transparent;
        button.ToolTip = name;
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }
    private bool ConfirmGlossaryReplacement(IReadOnlyList<GlossaryRule> savedRules)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = UiStrings.Translate("术语表已更新"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 500,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog.SetResourceReference(BackgroundProperty, "Panel");
        var body = new StackPanel { Margin = new(22) };
        body.Children.Add(UiTheme.Text("用当前修改替换最新的完整术语表？", 13));
        var latest = new StackPanel { Margin = new(0, 12, 0, 12) };
        latest.Children.Add(UiTheme.Text("最新已保存的术语", 12, bold: true));
        if (savedRules.Count == 0)
            latest.Children.Add(UiTheme.Text("无术语", 12, true));
        foreach (var rule in savedRules)
        {
            string scope = rule.SourceLanguage is null ? UiTheme.L("所有方向", "All directions")
                : ScopeLanguage(rule.SourceLanguage) + " → " + ScopeLanguage(rule.TargetLanguage!);
            var term = UiTheme.Text("", 12);
            term.Text = rule.SourceTerm + " → " + rule.TargetTerm + " · " + scope;
            latest.Children.Add(term);
        }
        body.Children.Add(new ScrollViewer { Content = latest, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var keep = UiTheme.Button("继续编辑", () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = false; }, primary: true);
        keep.IsCancel = keep.IsDefault = true;
        keep.Margin = new(0, 0, 10, 0);
        actions.Children.Add(keep);
        actions.Children.Add(UiTheme.Button("替换并保存", () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = true; }));
        body.Children.Add(actions);
        dialog.Content = body;
        dialog.Loaded += (_, _) => keep.Focus();
        _closeDialog = dialog;
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally { _closeDialog = null; }
    }
    private sealed record GlossaryScope(string Label, string? Source, string? Target);
    private async Task SaveGlossaryDraftAsync()
    {
        if (_controller is null || _glossaryDraft is null || _savingTranslation)
            return;
        var submitted = _glossaryDraft.ToArray();
        var rules = submitted.Select(rule => rule with { SourceTerm = rule.SourceTerm.Trim(), TargetTerm = rule.TargetTerm.Trim() }).ToArray();
        var completion = _glossarySaveCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var request = new CancellationTokenSource();
        _translationRequest = request;
        _savingTranslation = true;
        RefreshTranslationAvailability();
        try
        {
            var current = _controller.GetGlossarySettingsState();
            if (_glossaryDraftRevision != current.SettingsRevision)
            {
                // An unrelated ordinary setting can advance the revision. A
                // changed glossary requires reviewing exactly what will be replaced.
                var savedRules = current.SavedSnapshot?.Rules ?? [];
                if (!_glossaryDraftBaseline.SequenceEqual(savedRules) && !ConfirmGlossaryReplacement(savedRules))
                    return;
                _glossaryDraftRevision = current.SettingsRevision;
                _glossaryDraftBaseline = savedRules.ToArray();
            }
            var result = await _controller.ChangeGlossaryAsync(rules, _glossaryDraftRevision, cancellationToken: request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(_translationRequest, request))
                return;
            bool confirmed = result.Saved && result.Code is "glossary-saved" or "glossary-saved-inactive" or "glossary-saved-notification-failed" or "glossary-saved-inactive-notification-failed";
            if (confirmed)
            {
                if (_glossaryDraft?.SequenceEqual(submitted) == true)
                {
                    _glossaryDraft = null;
                    _glossaryEdit = null;
                }
                else
                {
                    _glossaryDraftBaseline = rules;
                    if (_glossaryEdit is { } edit)
                    {
                        edit.Before = rules;
                        edit.HadDraft = false;
                    }
                }
            }
            _draftFeedback = confirmed ? result.Code is "glossary-saved-notification-failed" or "glossary-saved-inactive-notification-failed"
                ? "已保存，请重新打开设置确认当前状态。" : result.Code == "glossary-saved-inactive" ? UiTheme.L("已保存到术语表，当前模型方向尚未开放术语应用。", "Saved to the glossary. Application is not available for this model and direction.") : _draftMessages.SavedMessage : _draftMessages.InvalidMessage;
            _settingsError = confirmed ? null : TranslationSettingsError(result.Code);
            if (confirmed && HasDraft)
                _draftFeedback = _draftMessages.UnsavedNotice;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!request.IsCancellationRequested)
                _settingsError = "保存结果未确认，术语修改已保留。请重新打开设置检查当前值。";
        }
        finally
        {
            if (ReferenceEquals(_translationRequest, request))
                _translationRequest = null;
            _savingTranslation = false;
            try
            {
                RefreshTranslationAvailability();
            }
            finally
            {
                if (ReferenceEquals(completion, _glossarySaveCompletion))
                    _glossarySaveCompletion = null;
                completion.TrySetResult();
            }
        }
    }
}
