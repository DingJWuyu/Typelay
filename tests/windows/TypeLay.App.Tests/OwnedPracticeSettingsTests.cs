using System.IO;
using System.Windows;
using System.Windows.Automation;
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
        var modes = Descendants(fixture.Window).OfType<ComboBox>()
            .Single(control => AutomationProperties.GetName(control) == "工作模式");
        Assert.True(modes.Focus());
        modes.SelectedIndex = 1;
        Assert.Equal(1, requests);
        Assert.Equal(WorkMode.Automatic, fixture.Settings.Value.Mode);
        Assert.Equal(WorkMode.Automatic, new SettingsStore(fixture.Path).Value.Mode);
        Assert.False(modes.IsEnabled);
        retirement.SetResult(acknowledged);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(acknowledged ? WorkMode.OnDemand : WorkMode.Automatic, fixture.Settings.Value.Mode);
        Assert.Equal(fixture.Settings.Value.Mode, new SettingsStore(fixture.Path).Value.Mode);
        Assert.True(Descendants(fixture.Window).OfType<ComboBox>()
            .Single(control => AutomationProperties.GetName(control) == "工作模式").IsKeyboardFocused);
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
        var modes = Descendants(fixture.Window).OfType<ComboBox>()
            .Single(control => AutomationProperties.GetName(control) == "工作模式");
        Assert.True(modes.Focus());
        modes.SelectedIndex = 1;
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
                        .Single(button => Equals(button.Content, "文  语言"))
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
                Assert.DoesNotContain(Descendants(fixture.Window).OfType<ComboBox>(),
                    control => AutomationProperties.GetName(control) == "工作模式");
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
        Descendants(fixture.Window).OfType<ComboBox>()
            .Single(control => AutomationProperties.GetName(control) == "工作模式").SelectedIndex = 1;
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

    [Fact]
    public Task UnchangedShortcutSaveDoesNotRequestRetirement() => desktop.RunAsync(() =>
    {
        using var fixture = new SettingsFixture();
        int requests = 0, changes = 0;
        fixture.Window.PrepareSettingsChange = _ => { requests++; return Task.FromResult(false); };
        fixture.Settings.Changed += () => changes++;
        fixture.Window.ShowPage("shortcuts");
        fixture.Window.UpdateLayout();
        Descendants(fixture.Window).OfType<Button>()
            .Single(button => Equals(button.Content, SettingsDraftPresentation.Create().SaveLabel))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, requests);
        Assert.Equal(0, changes);
        Assert.Equal(fixture.Settings.Value.AcceptShortcut, new SettingsStore(fixture.Path).Value.AcceptShortcut);
        Assert.Contains(Descendants(fixture.Window).OfType<TextBlock>(),
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
