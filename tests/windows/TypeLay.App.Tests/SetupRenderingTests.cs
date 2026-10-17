using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using TypeLay.Core;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class SetupRenderingTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task DownloadTimeoutOffersRecoveryAndRetryWithoutClaimingUserPause() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-setup-ui-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = Model(directory);
            int attempts = 0;
            window = new SettingsWindow(settings, model, () => { }, createInstaller: () =>
            {
                attempts++;
                return new ModelInstaller(directory, Path.Combine(directory, "worker.py"),
                    new WaitingHttp(), downloadTimeout: TimeSpan.FromMilliseconds(50));
            }, installations: Installations(directory));
            window.Width = 790;
            window.Height = 560;
            window.ShowPage("apps");
            window.UpdateLayout();
            var primary = Named<Button>(window, "PreparationPrimaryAction");
            AssertPrimary(window, primary);
            AssertWithinViewport(window, primary);
            AssertWithinViewport(window, Named<TextBlock>(window, "PreparationGuidance"));
            DirectionChoices(window)[0].IsChecked = false;
            Click(window, ApplicationSupportPresentation.Preparation(new(false, false)).PrimaryAction.Label);
            var failure = SetupFailurePresentation.Create(SetupFailureCategory.Network);
            await Until(() => Buttons(window).Any(button => Equals(button.Content, failure.RetryLabel)));
            window.UpdateLayout();
            Assert.Equal(failure.Message, Named<TextBlock>(window, "PreparationStatus").Text);
            Assert.Equal(failure.NextStep, Named<TextBlock>(window, "PreparationGuidance").Text);
            Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), block => block.Text.Contains("已暂停"));
            var diagnostic = Descendants(window).OfType<Expander>().Single(expander => Equals(expander.Header, "安装诊断"));
            Assert.False(diagnostic.IsExpanded);
            diagnostic.IsExpanded = true;
            window.UpdateLayout();
            Assert.Contains(Descendants(diagnostic).OfType<TextBlock>(), block => block.Text.Contains("network")
                && !block.Text.Contains(directory));
            Assert.False(settings.Value.OnboardingComplete);
            Assert.False(model.Ready);
            AssertPrimary(window, Named<Button>(window, "PreparationPrimaryAction"));
            AssertWithinViewport(window, Named<TextBlock>(window, "PreparationGuidance"));
            AssertWithinViewport(window, Named<Button>(window, "PreparationPrimaryAction"));
            window.RequestClose();
            window.ShowPage("apps");
            var directions = DirectionChoices(window);
            Assert.False(directions[0].IsChecked);
            Assert.True(directions[1].IsChecked);
            Assert.All(directions, choice => Assert.False(choice.IsEnabled));
            Click(window, failure.RetryLabel);
            await Until(() => attempts == 2 && Buttons(window).Any(button => Equals(button.Content, failure.RetryLabel)));
            Assert.False(model.Ready);
        }
        finally { await ClosePreparation(window, directory); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExplicitPauseOffersResumeInsteadOfFailure(bool selectSingleDirection) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-setup-ui-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = Model(directory);
            var attempts = new List<WaitingHttp>();
            window = new SettingsWindow(settings, model, () => { }, createInstaller: () =>
            {
                var http = new WaitingHttp();
                attempts.Add(http);
                return new ModelInstaller(directory, Path.Combine(directory, "worker.py"), http);
            }, installations: Installations(directory));
            window.ShowPage("apps");
            Assert.False(Descendants(window).OfType<Expander>()
                .Single(expander => Equals(expander.Header, "组件与安装说明")).IsExpanded);
            if (selectSingleDirection)
            {
                var choices = DirectionChoices(window);
                Assert.Equal(new[] { "zh_en", "en_zh" }, choices.Select(choice => (string)choice.Tag));
                Assert.All(choices, choice => { Assert.True(choice.IsChecked); Assert.True(choice.IsEnabled); });
                choices[1].IsChecked = false;
                choices[0].IsChecked = false;
                Assert.True(choices[0].IsChecked);
                Assert.False(choices[1].IsChecked);
            }
            // The default path starts directly while the direction options are still collapsed.
            Click(window, ApplicationSupportPresentation.Preparation(new(false, false)).PrimaryAction.Label);
            await attempts[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var directions = DirectionChoices(window);
            Assert.True(directions[0].IsChecked);
            Assert.Equal(!selectSingleDirection, directions[1].IsChecked == true);
            Assert.All(directions, choice => Assert.False(choice.IsEnabled));
            var primary = Named<Button>(window, "PreparationPrimaryAction");
            Assert.True(primary.Focus());
            Click(window, "暂停下载");
            Assert.Same(primary, Named<Button>(window, "PreparationPrimaryAction"));
            Assert.False(primary.IsEnabled);
            Assert.Equal("正在暂停…", primary.Content);
            primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(attempts);
            await Until(() => Buttons(window).Any(button => Equals(button.Content, SetupFailurePresentation.ResumeLabel)));
            window.UpdateLayout();
            Assert.Contains(Descendants(window).OfType<TextBlock>(), block => block.Text == SetupFailurePresentation.PausedMessage);
            Assert.DoesNotContain(Descendants(window).OfType<Expander>(), expander => Equals(expander.Header, "安装诊断"));
            Assert.False(settings.Value.OnboardingComplete);
            Assert.False(model.Ready);
            Assert.Same(primary, Named<Button>(window, "PreparationPrimaryAction"));
            Assert.True(primary.IsEnabled);
            window.ShowPage("translation");
            directions = DirectionChoices(window);
            Assert.True(directions[0].IsChecked);
            Assert.Equal(!selectSingleDirection, directions[1].IsChecked == true);
            Assert.All(directions, choice => Assert.False(choice.IsEnabled));
            primary = Named<Button>(window, "PreparationPrimaryAction");
            Click(window, SetupFailurePresentation.ResumeLabel);
            Assert.Equal(2, attempts.Count);
            await attempts[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(primary, Named<Button>(window, "PreparationPrimaryAction"));
            Assert.Equal("暂停下载", primary.Content);
        }
        finally { await ClosePreparation(window, directory); }
    });

    [Theory]
    [InlineData("zh_en", false)]
    [InlineData("en_zh", true)]
    public Task InstalledDirectionStaysLockedWhileRepairAndCompletionKeepTheirDistinctScopes(string installed, bool complete) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-direction-ui-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = ProtocolModel(directory, [installed]);
            await model.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var attempts = new List<WaitingHttp>();
            window = new SettingsWindow(settings, model, () => { }, createInstaller: () =>
            {
                var http = new WaitingHttp();
                attempts.Add(http);
                return new ModelInstaller(directory, Path.Combine(directory, "worker.py"), http);
            }, installations: Installations(directory));
            window.ShowPage("translation");
            var directions = DirectionChoices(window);
            Assert.All(directions, choice =>
            {
                Assert.Equal(Equals(choice.Tag, installed), choice.IsChecked == true);
                Assert.False(choice.IsEnabled);
                Assert.Equal(Equals(choice.Tag, installed), ((string)choice.Content).Contains("已安装"));
            });
            Assert.True(Named<Button>(window, "PreparationCompleteDirectionsAction").IsEnabled);
            Assert.True(Named<Button>(window, "PreparationRepairAction").IsEnabled);
            Click(window, complete ? "补齐另一方向" : "修复本地翻译组件…");
            await attempts[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(directions, choice => Assert.Equal(complete || Equals(choice.Tag, installed), choice.IsChecked == true));
            Click(window, "暂停下载");
            await Until(() => Buttons(window).Any(button => Equals(button.Content, SetupFailurePresentation.ResumeLabel)));
            window.ShowPage("apps");
            directions = DirectionChoices(window);
            Assert.All(directions, choice =>
            {
                Assert.Equal(complete || Equals(choice.Tag, installed), choice.IsChecked == true);
                Assert.False(choice.IsEnabled);
            });
            Click(window, SetupFailurePresentation.ResumeLabel);
            await attempts[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(model.Ready);
            Assert.Equal(new[] { installed }, model.AvailableDirections);
            Assert.False(settings.Value.OnboardingComplete);
        }
        finally { await ClosePreparation(window, directory); }
    });

    [Fact]
    public Task UnreadableInstallationOffersExplicitFullRepairAndRetainsItAfterPause() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-repair-ui-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            Directory.CreateDirectory(Path.Combine(directory, "models"));
            File.WriteAllText(Path.Combine(directory, "models", "installed.json"), "{");
            using var model = Model(directory);
            var attempts = new List<WaitingHttp>();
            window = new SettingsWindow(settings, model, () => { }, createInstaller: () =>
            {
                var http = new WaitingHttp();
                attempts.Add(http);
                return new ModelInstaller(directory, Path.Combine(directory, "worker.py"), http);
            }, installations: Installations(directory));
            window.ReportSetupFailure(new(SetupFailureCategory.Integrity, ModelSetupStage.Activate));
            window.ShowPage("translation");
            var directions = DirectionChoices(window);
            Assert.All(directions, choice => { Assert.False(choice.IsChecked); Assert.False(choice.IsEnabled); });
            Assert.Equal("请完整修复中英双向组件。", Named<TextBlock>(window, "PreparationGuidance").Text);
            Click(window, "完整修复本地翻译…");
            await attempts[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(directions, choice => Assert.True(choice.IsChecked));
            Click(window, "暂停下载");
            await Until(() => Buttons(window).Any(button => Equals(button.Content, SetupFailurePresentation.ResumeLabel)));
            window.RequestClose();
            window.ShowPage("translation");
            directions = DirectionChoices(window);
            Assert.All(directions, choice => { Assert.True(choice.IsChecked); Assert.False(choice.IsEnabled); });
            Click(window, SetupFailurePresentation.ResumeLabel);
            await attempts[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(model.Ready);
            Assert.False(settings.Value.OnboardingComplete);
            Assert.Equal("{", File.ReadAllText(Path.Combine(directory, "models", "installed.json")));
        }
        finally { await ClosePreparation(window, directory); }
    });

    [Theory]
    [InlineData(WorkMode.OnDemand, "Tab")]
    [InlineData(WorkMode.Automatic, "F8")]
    public Task ReadyPreparationPreservesTheSelectedModeAndShortcuts(
        WorkMode mode, string acceptKey) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-ready-ui-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Mode = mode,
                AcceptShortcut = acceptKey
            });
            using var model = ProtocolModel(directory);
            int launches = 0;
            window = new SettingsWindow(settings, model, () => launches++, new WindowsApplicationSupport(), installations: Installations(directory));
            window.Width = 790;
            window.Height = 560;
            window.ShowPage("apps");
            window.UpdateLayout();
            Assert.True(model.Installed);
            Assert.False(model.Ready);
            var primary = Named<Button>(window, "PreparationPrimaryAction");
            Assert.DoesNotContain("已就绪", Named<TextBlock>(window, "PreparationStatus").Text);
            Assert.False(Named<Button>(window, "PreparationNextStepAction").IsEnabled);
            Assert.True(primary.Focus());
            await model.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(model.Ready);
            Assert.Same(primary, Keyboard.FocusedElement);
            foreach (string theme in new[] { "light", "dark" })
            {
                UiTheme.Apply(theme);
                window.UpdateLayout();
                Assert.Same(primary, Named<Button>(window, "PreparationPrimaryAction"));
                Assert.Equal("试一试", primary.Content);
                AssertPrimary(window, primary);
                var repair = Named<Button>(window, "PreparationRepairAction");
                Assert.Equal("修复本地翻译组件…", repair.Content);
                Assert.NotEqual(Assert.IsType<SolidColorBrush>(primary.Background).Color,
                    Assert.IsType<SolidColorBrush>(repair.Background).Color);
                AssertWithinViewport(window, repair);
                AssertWithinViewport(window, primary);
                AssertWithinViewport(window, Named<TextBlock>(window, "PreparationGuidance"));
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == ApplicationSupportPresentation.ReasonText(ApplicationSupportReason.NoFocusedField));
                Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.Text == "已连接");
            }
            Assert.Equal(acceptKey, settings.Value.AcceptShortcut);
            bool sawConfirmation = false;
            Exception? confirmationFailure = null;
            // Close an unexpected mode-switch modal so this regression fails
            // at the entry behavior rather than hanging in its nested dispatcher.
            var confirmation = window.Dispatcher.BeginInvoke(new Action(() =>
            {
                Window[] dialogs = [];
                try
                {
                    dialogs = window.OwnedWindows.Cast<Window>().Where(item => item.IsVisible).ToArray();
                    sawConfirmation = dialogs.Length != 0;
                }
                catch (Exception error) { confirmationFailure = error; }
                finally
                {
                    foreach (var dialog in dialogs.Where(item => item.IsVisible))
                        dialog.Close();
                }
            }), DispatcherPriority.ContextIdle);
            try
            {
                Click(window, "试一试");
            }
            finally { confirmation.Abort(); }
            if (confirmationFailure is not null)
                ExceptionDispatchInfo.Capture(confirmationFailure).Throw();
            Assert.False(sawConfirmation);
            Assert.Equal(1, launches);
            Assert.Equal(mode, settings.Value.Mode);
            var persisted = new SettingsStore(Path.Combine(directory, "settings.json")).Value;
            Assert.Equal(mode, persisted.Mode);
            Assert.Equal(acceptKey, persisted.AcceptShortcut);
            Assert.False(settings.Value.OnboardingComplete);
            window.ShowPage("translation");
            window.UpdateLayout();
            Assert.True(Named<Button>(window, "PreparationRepairAction").IsEnabled);
        }
        finally { await ClosePreparation(window, directory); }
    });

    [Fact]
    public Task FailedPreparationKeepsRetryWhenModelReadinessReturns() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-retry-ui-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = ProtocolModel(directory);
            window = new SettingsWindow(settings, model, () => Assert.Fail("A failed preparation must keep recovery first."),
                createInstaller: () => new ModelInstaller(directory, Path.Combine(directory, "worker.py"),
                    new WaitingHttp(), downloadTimeout: TimeSpan.FromMilliseconds(50)), installations: Installations(directory));
            window.ShowPage("apps");
            Click(window, "重新准备");
            await Until(() => Descendants(window).OfType<Expander>().Any(expander => Equals(expander.Header, "安装诊断")));
            await model.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(model.Ready);
            window.UpdateLayout();
            Assert.Equal("重新准备", Named<Button>(window, "PreparationPrimaryAction").Content);
            Assert.Contains("下载连接失败", Named<TextBlock>(window, "PreparationStatus").Text);
            Assert.DoesNotContain(Buttons(window), button => Equals(button.Content, "试一试"));
            window.ShowPage("general");
            window.UpdateLayout();
            Assert.Equal("重新准备", Named<Button>(window, "PreparationPrimaryAction").Content);
        }
        finally { await ClosePreparation(window, directory); }
    });

    [Fact]
    public Task PreparationRefreshPreservesShortcutDraftFocusAndSelection() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-preparation-draft-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = ProtocolModel(directory);
            window = new SettingsWindow(settings, model, () => { }, installations: Installations(directory));
            window.ShowPage("shortcuts");
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var input = Descendants(window).OfType<TextBox>().Single(control =>
                AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键");
            input.Text = "Ctrl+Shift+U";
            Assert.True(input.Focus());
            input.Select(5, 5);
            await model.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(model.Ready);
            Assert.Same(input, Keyboard.FocusedElement);
            Assert.Equal("Ctrl+Shift+U", input.Text);
            Assert.Equal(5, input.SelectionStart);
            Assert.Equal(5, input.SelectionLength);
            Assert.NotEqual(input.Text, settings.Value.WakeShortcut);
        }
        finally { await ClosePreparation(window, directory); }
    });

    // A controlled worker protocol reply drives the real ModelClient and real WPF
    // controls. It verifies presentation only, not model inference or installation.
    private static ModelClient ProtocolModel(string directory, string[]? directions = null)
    {
        Directory.CreateDirectory(Path.Combine(directory, "models"));
        string python = Path.Combine(directory, "python.exe");
        string worker = Path.Combine(directory, "worker.py");
        File.WriteAllText(python, "controlled executable marker");
        File.WriteAllText(Path.Combine(directory, "models", "installed.json"),
            JsonSerializer.Serialize(new
            {
                schema = 2,
                directions = directions ?? ["zh_en", "en_zh"]
            }));
        File.WriteAllText(worker, """
            import json, struct, sys
            size = struct.unpack("<i", sys.stdin.buffer.read(4))[0]
            request = json.loads(sys.stdin.buffer.read(size))
            reply = json.dumps({"id": request["id"], "status": "ready", "directions": json.loads(sys.argv[1])}).encode()
            sys.stdout.buffer.write(struct.pack("<i", len(reply)) + reply)
            sys.stdout.buffer.flush()
            sys.stdin.buffer.read()
            """);
        return new(new(python, worker, Path.Combine(directory, "models")), _ =>
        {
            var start = new ProcessStartInfo("python")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-I");
            start.ArgumentList.Add("-B");
            start.ArgumentList.Add(worker);
            start.ArgumentList.Add(JsonSerializer.Serialize(directions ?? ["zh_en", "en_zh"]));
            return new ChildProcess(start);
        });
    }

    private static ModelInstallationStore Installations(string directory) => new(directory, Path.Combine(directory, "worker.py"));

    private static CheckBox[] DirectionChoices(Window window)
    {
        window.UpdateLayout();
        var details = Descendants(window).OfType<Expander>().Single(expander => Equals(expander.Header, "组件与安装说明"));
        details.IsExpanded = true;
        window.UpdateLayout();
        return Descendants(details).OfType<CheckBox>().ToArray();
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(control => control.Name == name);

    private static void AssertPrimary(Window window, Button button)
    {
        var accent = Assert.IsType<SolidColorBrush>(window.FindResource("Accent")).Color;
        Assert.Equal(accent, Assert.IsType<SolidColorBrush>(button.Background).Color);
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(button.Foreground).Color);
        Assert.Same(button, Assert.Single(Buttons(Named<Border>(window, "TranslationPreparationCard")),
            control => Assert.IsType<SolidColorBrush>(control.Background).Color == accent));
    }

    private static void AssertWithinViewport(Window window, FrameworkElement control)
    {
        var viewport = Descendants(window).OfType<ScrollContentPresenter>().First();
        var bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
        Assert.True(control.IsVisible);
        Assert.InRange(bounds.Top, 0, viewport.ActualHeight);
        Assert.InRange(bounds.Bottom, 0, viewport.ActualHeight);
        Assert.InRange(bounds.Left, 0, viewport.ActualWidth);
        Assert.InRange(bounds.Right, 0, viewport.ActualWidth);
    }

    private static ModelClient Model(string directory) => new(new(Path.Combine(directory, "python.exe"),
        Path.Combine(directory, "worker.py"), Path.Combine(directory, "models")),
        _ => throw new InvalidOperationException("An incomplete installation must not start a model."));

    private sealed class WaitingHttp : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The transport must be cancelled.");
        }
    }

    private static IEnumerable<Button> Buttons(DependencyObject root) => Descendants(root).OfType<Button>();
    private static async Task ClosePreparation(SettingsWindow? window, string directory)
    {
        try
        {
            if (window is not null)
            {
                if (Buttons(window).Any(button => Equals(button.Content, "暂停下载")))
                    Click(window, "暂停下载");
                if (Buttons(window).Any(button => Equals(button.Content, "正在暂停…")))
                    await Until(() => Buttons(window).Any(button =>
                        Equals(button.Content, SetupFailurePresentation.ResumeLabel) && button.IsEnabled));
                window.PrepareForEmergencyShutdown();
                window.Close();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
    private static void Click(DependencyObject root, string label)
    {
        var button = Buttons(root).Single(button => Equals(button.Content, label));
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task Until(Func<bool> complete)
    {
        var elapsed = Stopwatch.StartNew();
        while (!complete() && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        Assert.True(complete(), "The setup operation did not reach its visible terminal state.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
