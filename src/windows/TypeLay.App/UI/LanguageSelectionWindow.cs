using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TypeLay.Runtime;

namespace TypeLay.App;

// This is a separate settings surface, never a continuation of a source-field
// candidate. The caller retires the session before giving this window focus.
internal sealed class LanguageSelectionWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly ModelClient _model;
    private readonly Func<string, string, Task<bool>> _save;
    private readonly LanguagePicker _source, _target;
    private readonly Button _swap;
    private readonly TextBlock _feedback;
    private bool _saving;

    public LanguageSelectionWindow(SettingsStore settings, ModelClient model, Func<string, string, Task<bool>> save)
    {
        _settings = settings; _model = model; _save = save;
        Title = UiTheme.L("翻译语言", "Translation languages");
        Width = 390; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Window"); FontFamily = UiTheme.UiFont;
        var content = new StackPanel { Margin = new(24) };
        var heading = UiTheme.Text(UiTheme.L("翻译语言", "Translation languages"), 22);
        heading.FontFamily = UiTheme.ContentFont; heading.Margin = new(0, 0, 0, 18); content.Children.Add(heading);
        _source = new(true, settings.Value.Source, async value => await SaveAsync(value, _settings.Value.Target), Directions);
        _target = new(false, settings.Value.Target, async value => await SaveAsync(_settings.Value.Source, value), Directions);
        AddPicker(content, UiTheme.L("从", "From"), _source);
        _swap = UiTheme.Button("⇅", async () => await SaveAsync(_settings.Value.Target, _settings.Value.Source));
        _swap.HorizontalAlignment = HorizontalAlignment.Right; _swap.Width = 34; _swap.Margin = new(0, 4, 0, 4);
        AutomationProperties.SetName(_swap, UiTheme.L("交换语言", "Swap languages")); content.Children.Add(_swap);
        AddPicker(content, UiTheme.L("到", "To"), _target);
        _feedback = UiTheme.Text(UiTheme.L("选择即保存。原候选已结束；返回输入框后重新翻译。", "Choices save immediately. Return to your input field to start a new translation."), 12, true);
        _feedback.Margin = new(0, 16, 0, 0); content.Children.Add(_feedback); Content = content;
        settings.Changed += Refresh; model.Changed += Refresh;
        Closed += (_, _) => { settings.Changed -= Refresh; model.Changed -= Refresh; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_source.IsDropDownOpen && !_target.IsDropDownOpen) { e.Handled = true; Close(); } };
        Loaded += (_, _) => { _source.Focus(); _source.IsDropDownOpen = true; };
        Refresh();
    }
    private IReadOnlyList<string>? Directions => _model.Ready ? _model.AvailableDirections : null;
    private static void AddPicker(Panel parent, string label, LanguagePicker picker)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new(64) }); row.ColumnDefinitions.Add(new());
        row.Children.Add(UiTheme.Text(label, 13, true)); Grid.SetColumn(picker, 1); row.Children.Add(picker); parent.Children.Add(row);
    }
    private async Task SaveAsync(string source, string target)
    {
        if (_saving) { Refresh(); return; }
        _saving = true;
        try
        {
            bool saved = await _save(source, target);
            _feedback.Text = saved ? UiTheme.L("已保存。返回输入框后重新翻译。", "Saved. Return to your input field to start a new translation.")
                : UiTheme.L("尚未保存。请重新选择或在设置中检查。", "Not saved. Select again or review Settings.");
        }
        finally { _saving = false; Refresh(); }
    }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        FontFamily = UiTheme.UiFont;
        _source.Synchronize(_settings.Value.Source, Directions); _target.Synchronize(_settings.Value.Target, Directions);
        _swap.IsEnabled = !_saving && _settings.Value.Source != "auto";
    }
}
