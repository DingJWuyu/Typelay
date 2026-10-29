using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class OwnedPracticeSettingsTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task ConfirmedShortcutSaveDisablesEditingUntilRetirementSettles() => desktop.RunAsync(async () =>
    {
        using var fixture = new SettingsFixture();
        var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        fixture.Window.PrepareSettingsChange = _ => { requests++; return retirement.Task; };
        fixture.Window.ShowPage("shortcuts");
        fixture.Window.UpdateLayout();
        Wake().Text = "Ctrl+C";
        string persisted = File.ReadAllText(fixture.Path);
        Assert.Equal(0, requests); // The warning itself cannot retire the input session.
        Assert.Equal(persisted, File.ReadAllText(fixture.Path));
        Button("强制保存").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(1, requests);
        Assert.Equal(persisted, File.ReadAllText(fixture.Path));
        fixture.Window.ShowPage("general");
        fixture.Window.ShowPage("shortcuts");
        fixture.Window.UpdateLayout();
        string[] labels = ["唤醒翻译 UI 快捷键", "直接翻译并替换快捷键", "接受译文", "退出当前翻译"];
        var controls = Descendants(fixture.Window).OfType<Control>()
            .Where(control => labels.Contains(AutomationProperties.GetName(control))).ToArray();
        Assert.Equal(4, controls.Length);
        Assert.All(controls, control => Assert.False(control.IsEnabled));
        var peer = UIElementAutomationPeer.CreatePeerForElement(Wake());
        var value = Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value));
        Assert.Throws<ElementNotEnabledException>(() => value.SetValue("Ctrl+V"));
        Assert.Equal("Ctrl+C", Wake().Text);
        Assert.Equal(persisted, File.ReadAllText(fixture.Path));
        retirement.SetResult(true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Ctrl+C", new SettingsStore(fixture.Path).Value.WakeShortcut);
        Assert.True(Wake().IsEnabled);
        Assert.Equal("Ctrl+C", Wake().Text);
        Assert.False(Button("强制保存").IsVisible);
        TextBox Wake() => Descendants(fixture.Window).OfType<TextBox>()
            .Single(control => AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键");
        Button Button(string label) => Descendants(fixture.Window).OfType<Button>()
            .Single(button => Equals(button.Content, label));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ModeSaveWaitsForRetirementAndRefusalKeepsPersistedSettings(bool acknowledged) => desktop.RunAsync(async () =>
    {
        using var fixture = new SettingsFixture();
        var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        fixture.Window.PrepareSettingsChange = _ => { requests++; return retirement.Task; };
        fixture.Window.ShowPage();
        fixture.Window.UpdateLayout();
        var modes = Descendants(fixture.Window).OfType<RadioButton>()
            .Single(control => AutomationProperties.GetName(control) == "工作模式 按需");
        Assert.True(modes.Focus());
        modes.IsChecked = true;
        Assert.Equal(1, requests);
        Assert.Equal(WorkMode.Automatic, fixture.Settings.Value.Mode);
        Assert.Equal(WorkMode.Automatic, new SettingsStore(fixture.Path).Value.Mode);
        Assert.False(modes.IsEnabled);
        retirement.SetResult(acknowledged);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(acknowledged ? WorkMode.OnDemand : WorkMode.Automatic, fixture.Settings.Value.Mode);
        Assert.Equal(fixture.Settings.Value.Mode, new SettingsStore(fixture.Path).Value.Mode);
        Assert.True(Descendants(fixture.Window).OfType<RadioButton>()
            .Single(control => AutomationProperties.GetName(control) == "工作模式 按需").IsKeyboardFocused);
        if (!acknowledged)
            Assert.Contains(Descendants(fixture.Window).OfType<TextBlock>(), control => control.Text.Contains("设置未保存"));
    });

    [Theory]
    [InlineData("control")]
    [InlineData("page")]
    [InlineData("window")]
    public Task RetirementCompletionDoesNotReclaimFocusAfterTheUserMovesOn(string destination) => desktop.RunAsync(async () =>
    {
        using var fixture = new SettingsFixture();
        var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Window.PrepareSettingsChange = _ => retirement.Task;
        fixture.Window.ShowPage();
        fixture.Window.UpdateLayout();
        var modes = Descendants(fixture.Window).OfType<RadioButton>()
            .Single(control => AutomationProperties.GetName(control) == "工作模式 按需");
        Assert.True(modes.Focus());
        modes.IsChecked = true;
        Assert.False(modes.IsEnabled);
        Window? other = null;
        try
        {
            Control focused;
            if (destination == "window")
            {
                focused = new TextBox();
                other = new Window { Content = focused, Width = 240, Height = 100 };
                other.Show();
                Assert.True(other.Activate());
            }
            else
            {
                if (destination == "page")
                    Descendants(fixture.Window).OfType<Button>()
                        .Single(button => AutomationProperties.GetAutomationId(button) == "SettingsNavigation_language")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                focused = Descendants(fixture.Window).OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "关闭设置");
            }
            Assert.True(focused.Focus());
            retirement.SetResult(true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(WorkMode.OnDemand, new SettingsStore(fixture.Path).Value.Mode);
            Assert.True(focused.IsKeyboardFocused);
            if (destination == "page")
                Assert.DoesNotContain(Descendants(fixture.Window).OfType<RadioButton>(),
                    control => AutomationProperties.GetName(control) == "工作模式 按需");
        }
        finally
        {
            other?.Close();
        }
    });

    [Fact]
    public Task LateRetirementCannotOverwriteNewerSettings() => desktop.RunAsync(async () =>
    {
        using var fixture = new SettingsFixture();
        var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Window.PrepareSettingsChange = _ => retirement.Task;
        fixture.Window.ShowPage();
        fixture.Window.UpdateLayout();
        Descendants(fixture.Window).OfType<RadioButton>()
            .Single(control => AutomationProperties.GetName(control) == "工作模式 按需").IsChecked = true;
        fixture.Settings.Update(fixture.Settings.Value with
        {
            Target = "zh"
        });
        retirement.SetResult(true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(WorkMode.Automatic, fixture.Settings.Value.Mode);
        Assert.Equal("zh", new SettingsStore(fixture.Path).Value.Target);
        Assert.Contains(Descendants(fixture.Window).OfType<TextBlock>(), control => control.Text.Contains("设置已更新"));
    });

    [Theory]
    [InlineData(SettingsDraftCloseChoice.KeepEditing)]
    [InlineData(SettingsDraftCloseChoice.Discard)]
    public Task PendingDraftSaveRespectsTheCloseChoiceBeforeRetirementCompletes(SettingsDraftCloseChoice choice) => desktop.RunAsync(async () =>
    {
        using var fixture = new SettingsFixture();
        fixture.Settings.Update(fixture.Settings.Value with
        {
            ExcludedApplications = ["old"]
        });
        var retirement = new TaskCompletionSource<bool>();
        fixture.Window.PrepareSettingsChange = _ => retirement.Task;
        fixture.Window.ShowPage("shortcuts");
        fixture.Window.UpdateLayout();
        Descendants(fixture.Window).OfType<TextBox>()
            .Single(control => AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键").Text = "Ctrl+Shift+U";
        int changes = 0;
        fixture.Settings.Changed += () => changes++;
        string persisted = File.ReadAllText(fixture.Path);
        var save = Descendants(fixture.Window).OfType<Button>()
            .Single(button => Equals(button.Content, "重试保存"));
        Assert.False(save.IsEnabled);
        Assert.Equal(persisted, File.ReadAllText(fixture.Path));
        SettingsRenderingTests.InteractWithCloseDialog(fixture.Window, fixture.Window.Close, choice, duringClose: () =>
        {
            // The modal close can dispatch the ACK before RequestClose returns.
            retirement.SetResult(true);
            fixture.Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        });
        Assert.Equal(choice == SettingsDraftCloseChoice.KeepEditing, fixture.Window.IsVisible);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(choice == SettingsDraftCloseChoice.KeepEditing ? 1 : 0, changes);
        if (choice == SettingsDraftCloseChoice.Discard)
        {
            Assert.Equal(persisted, File.ReadAllText(fixture.Path));
            fixture.Window.ShowPage("apps");
            fixture.Window.UpdateLayout();
            Assert.Contains(Descendants(fixture.Window).OfType<TextBlock>(), control => control.Text == "old.exe");
            Assert.DoesNotContain(Descendants(fixture.Window).OfType<TextBlock>(), control =>
                control.Text == SettingsDraftPresentation.Create().SavedMessage
                || control.Text.Contains(SettingsDraftPresentation.Create().InvalidMessage, StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(new[] { "old" }, new SettingsStore(fixture.Path).Value.Excluded);
            Assert.Equal("Ctrl+Shift+U", new SettingsStore(fixture.Path).Value.WakeShortcut);
            SettingsRenderingTests.InteractWithCloseDialog(fixture.Window, fixture.Window.Close, choice: null);
            Assert.False(fixture.Window.IsVisible);
            Assert.Equal(new[] { "old" }, new SettingsStore(fixture.Path).Value.Excluded);
        }
    });

    [Fact]
    public Task CleanDraftCannotRequestRetirementOrReportASave() => desktop.RunAsync(() =>
    {
        using var fixture = new SettingsFixture();
        int requests = 0, changes = 0;
        fixture.Window.PrepareSettingsChange = _ => { requests++; return Task.FromResult(false); };
        fixture.Settings.Changed += () => changes++;
        fixture.Window.ShowPage("shortcuts");
        fixture.Window.UpdateLayout();
        var save = Descendants(fixture.Window).OfType<Button>()
            .Single(button => Equals(button.Content, "重试保存"));
        Assert.False(save.IsEnabled);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, requests);
        Assert.Equal(0, changes);
        Assert.Equal(fixture.Settings.Value.AcceptShortcut, new SettingsStore(fixture.Path).Value.AcceptShortcut);
        Assert.DoesNotContain(Descendants(fixture.Window).OfType<TextBlock>(),
            control => control.Text == SettingsDraftPresentation.Create().SavedMessage);
    });

    private sealed class SettingsFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typelay-owned-save-" + Guid.NewGuid().ToString("N"));
        private readonly ModelClient _model;
        public string Path
        {
            get;
        }
        public SettingsStore Settings
        {
            get;
        }
        public SettingsWindow Window
        {
            get;
        }
        public SettingsFixture()
        {
            Path = System.IO.Path.Combine(_directory, "settings.json");
            Settings = new(Path);
            Settings.Update(Settings.Value with
            {
                Target = "en",
                Mode = WorkMode.Automatic
            });
            _model = WindowsRuntime.CreateModel(); // No model process or download.
            Window = new(Settings, _model, () => { });
        }
        public void Dispose()
        {
            Window.PrepareForEmergencyShutdown();
            Window.Close();
            _model.Dispose();
            Directory.Delete(_directory, true);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}
