using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.Core;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Runtime;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class SetupRenderingTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task PackageDetailsReopenAfterSettingsSaveRebuildsApplicationsPage() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-package-refresh-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = limit.Token;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = Model(directory);
            window = new SettingsWindow(settings, model, () => { }, installations: Installations(directory),
                createInstaller: () => throw new InvalidOperationException("Opening details must not start installation."));
            window.ShowPage("apps");
            window.UpdateLayout();
            nint handle = new WindowInteropHelper(window).Handle;
            var root = await Task.Run(() => AutomationElement.FromHandle(handle), ct).WaitAsync(ct);
            var previous = await SettingsPackageActions.OpenDetailsAsync(root, _ => { }, ct);
            int[] previousId = await Task.Run(previous.GetRuntimeId, ct).WaitAsync(ct);

            // Successful model adoption saves this same public setting and publishes
            // Changed. Exercise the real WPF rebuild without manufacturing model Ready.
            settings.Update(settings.Value with
            {
                OnboardingComplete = true
            });
            window.UpdateLayout();
            int[] currentId = await Task.Run(() =>
            {
                var current = root.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "组件与安装说明"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)));
                Assert.NotNull(current);
                Assert.False(current.GetRuntimeId().SequenceEqual(previousId));
                Assert.Equal(ExpandCollapseState.Collapsed,
                    ((ExpandCollapsePattern)current.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
                return current.GetRuntimeId();
            }, ct).WaitAsync(ct);
            var details = await SettingsPackageActions.OpenDetailsAsync(root, _ => { }, ct);
            window.UpdateLayout();
            await Task.Run(() =>
            {
                Assert.Equal(currentId, details.GetRuntimeId());
                Assert.Equal(ExpandCollapseState.Expanded,
                    ((ExpandCollapsePattern)details.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
                Assert.Equal(2, details.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.NameProperty, "尚未安装"))).Count);
                Assert.Empty(details.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.NameProperty, "当前 · 已就绪"))).Cast<AutomationElement>());
            }, ct).WaitAsync(ct);
            Assert.False(model.Ready);
            Assert.False(Directory.Exists(Path.Combine(directory, "components")));
        }
        finally { await ClosePreparation(window, directory); }
    });

    [Fact]
    public Task PackageActionsOpenTheCollapsedDetailsAndScrollIntoViewAfterNavigation() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-package-navigation-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = Model(directory);
            window = new SettingsWindow(settings, model, () => { }, installations: Installations(directory),
                createInstaller: () => throw new InvalidOperationException("Finding an action must not start installation."));
            foreach (string page in new[] { "apps", "translation" })
            {
                window.ShowPage(page);
                window.UpdateLayout();
                nint handle = new WindowInteropHelper(window).Handle;
                var root = await Task.Run(() =>
                {
                    var root = AutomationElement.FromHandle(handle);
                    var details = root.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "组件与安装说明"),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)));
                    Assert.NotNull(details);
                    Assert.Equal(ExpandCollapseState.Collapsed,
                        ((ExpandCollapsePattern)details.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
                    return root;
                });
                var button = await SettingsPackageActions.FindAsync(root, "argos-en-zh", ModelPackageOperation.Install,
                    _ => { }, TestContext.Current.CancellationToken);
                await Task.Run(() =>
                {
                    Assert.Equal("ModelPackage_argos-en-zh_Install", button.Current.AutomationId);
                    Assert.True(button.Current.IsEnabled);
                    Assert.False(button.Current.IsOffscreen);
                    Assert.True(button.TryGetClickablePoint(out Point point));
                    AutomationElement? viewport = null;
                    var ancestor = button;
                    for (int depth = 0; depth < 64 && ancestor is not null; depth++)
                    {
                        ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
                        if (ancestor is not null && ancestor.TryGetCurrentPattern(ScrollPattern.Pattern, out _))
                        {
                            viewport = ancestor;
                            break;
                        }
                    }
                    Assert.NotNull(viewport);
                    Assert.True(viewport.Current.BoundingRectangle.Contains(point));
                });
            }
            Assert.False(model.Ready);
            Assert.False(Directory.Exists(Path.Combine(directory, "components")));
        }
        finally { await ClosePreparation(window, directory); }
    });

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
    public Task InstalledDirectionStaysLockedWhilePackageRepairAndCompletionKeepTheirDistinctScopes(string installed, bool complete) => desktop.RunAsync(async () =>
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
            string other = installed == "zh_en" ? "en_zh" : "zh_en";
            string requested = complete ? other : installed;
            string packageId = "argos-" + requested.Replace('_', '-');
            string inventory = File.ReadAllText(Path.Combine(directory, "models", "installed.json"));
            Assert.True(PackageAction(window, "argos-" + other.Replace('_', '-'), ModelPackageOperation.Install).IsEnabled);
            Assert.True(PackageAction(window, "argos-" + installed.Replace('_', '-'), ModelPackageOperation.Repair).IsEnabled);
            await ConfirmPackageAction(window, PackageAction(window, packageId,
                complete ? ModelPackageOperation.Install : ModelPackageOperation.Repair));
            await attempts[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertPackageDownloadScope(directory, attempts[0], installed, requested, complete);
            // A pending download is not an installed direction, including after pause.
            Assert.All(directions, choice => Assert.Equal(Equals(choice.Tag, installed), choice.IsChecked == true));
            Click(window, "暂停下载");
            await Until(() => Buttons(window).Any(button => Equals(button.Content, SetupFailurePresentation.ResumeLabel)));
            window.ShowPage("apps");
            directions = DirectionChoices(window);
            Assert.All(directions, choice =>
            {
                Assert.Equal(Equals(choice.Tag, installed), choice.IsChecked == true);
                Assert.False(choice.IsEnabled);
            });
            Click(window, SetupFailurePresentation.ResumeLabel);
            await attempts[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertPackageDownloadScope(directory, attempts[1], installed, requested, complete);
            Assert.Equal(inventory, File.ReadAllText(Path.Combine(directory, "models", "installed.json")));
            Assert.False(File.Exists(Path.Combine(directory, "components.json")));
            Assert.True(model.Ready);
            Assert.Equal(new[] { installed }, model.AvailableDirections);
            Assert.False(settings.Value.OnboardingComplete);
        }
        finally { await ClosePreparation(window, directory); }
    });

    [Fact]
    public Task UnreadableInstallationDisablesMutationsAndPreservesTheInventory() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-repair-ui-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            Directory.CreateDirectory(Path.Combine(directory, "models"));
            string inventory = Path.Combine(directory, "models", "installed.json");
            File.WriteAllText(inventory, "{");
            using var model = Model(directory);
            int attempts = 0;
            window = new SettingsWindow(settings, model, () => Assert.Fail("Unknown installation must not start practice."),
                createInstaller: () =>
                {
                    attempts++;
                    throw new InvalidOperationException("Unknown installation must not be changed.");
                }, installations: Installations(directory));
            window.ReportSetupFailure(new(SetupFailureCategory.Integrity, ModelSetupStage.Activate));
            foreach (string page in new[] { "translation", "apps" })
            {
                window.ShowPage(page);
                var directions = DirectionChoices(window);
                Assert.All(directions, choice => { Assert.False(choice.IsChecked); Assert.False(choice.IsEnabled); });
                Assert.Equal("未更改模型。请重新打开 TypeLay 检查安装记录。", Named<TextBlock>(window, "PreparationGuidance").Text);
                var primary = Named<Button>(window, "PreparationPrimaryAction");
                Assert.False(primary.IsEnabled);
                Assert.DoesNotContain(Buttons(window), button => AutomationProperties.GetAutomationId(button).StartsWith("ModelPackage_", StringComparison.Ordinal));
                primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.RequestClose();
            }
            Assert.Equal(0, attempts);
            Assert.False(model.Ready);
            Assert.False(settings.Value.OnboardingComplete);
            Assert.Equal("{", File.ReadAllText(inventory));
            Assert.False(File.Exists(Path.Combine(directory, "components.json")));
            Assert.False(Directory.Exists(Path.Combine(directory, "components")));
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
            Assert.Equal("试一试", primary.Content);
            Assert.False(primary.IsEnabled);
            await model.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(model.Ready);
            Assert.True(primary.IsEnabled);
            Assert.True(primary.Focus());
            Assert.Same(primary, Keyboard.FocusedElement);
            foreach (string theme in new[] { "light", "dark" })
            {
                UiTheme.Apply(theme);
                window.UpdateLayout();
                Assert.Same(primary, Named<Button>(window, "PreparationPrimaryAction"));
                Assert.Same(primary, Keyboard.FocusedElement);
                Assert.Equal("试一试", primary.Content);
                AssertPrimary(window, primary);
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
            DirectionChoices(window);
            var repair = PackageAction(window, "argos-zh-en", ModelPackageOperation.Repair);
            Assert.True(repair.IsEnabled);
            Assert.Equal("修复…", repair.Content);
            Assert.NotEqual(Assert.IsType<SolidColorBrush>(Named<Button>(window, "PreparationPrimaryAction").Background).Color,
                Assert.IsType<SolidColorBrush>(repair.Background).Color);
            repair.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            AssertWithinViewport(window, repair);
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
            DirectionChoices(window);
            await ConfirmPackageAction(window, PackageAction(window, "argos-zh-en", ModelPackageOperation.Repair));
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
    // controls. Controlled inventoried bytes also exercise package copy/download
    // scope; they do not demonstrate real model loading or inference.
    private static ModelClient ProtocolModel(string directory, string[]? directions = null)
    {
        string[] selected = directions ?? ["zh_en", "en_zh"];
        string python = Path.Combine(directory, "runtime", "python", "tools", "python.exe");
        string worker = Path.Combine(directory, "worker.py");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "controlled executable marker");
        File.WriteAllText(Path.Combine(directory, "runtime-inventory.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            files = new[] { new { path = "python/tools/python.exe", sha256 = Hash(python) } }
        }));
        var files = new List<object>();
        foreach (string direction in selected)
        {
            string[] pair = direction.Split('_');
            var contents = new Dictionary<string, string>
            {
                ["metadata.json"] = JsonSerializer.Serialize(new { from_code = pair[0], to_code = pair[1], package_version = "1.9" }),
                ["sentencepiece.model"] = "controlled tokenizer",
                ["model/model.bin"] = "controlled model",
                ["model/config.json"] = "{}",
                ["model/shared_vocabulary.json"] = "[]"
            };
            foreach (var (relative, content) in contents)
            {
                string path = Path.Combine(directory, "models", direction, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                files.Add(new
                {
                    path = direction + "/" + relative,
                    sha256 = Hash(path)
                });
            }
        }
        File.WriteAllText(Path.Combine(directory, "models", "installed.json"),
            JsonSerializer.Serialize(new
            {
                schema = 2,
                directions = selected,
                files
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

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static Button PackageAction(Window window, string package, ModelPackageOperation operation) =>
        Buttons(window).Single(button => AutomationProperties.GetAutomationId(button) == $"ModelPackage_{package}_{operation}");

    private static void AssertPackageDownloadScope(string directory, WaitingHttp http, string installed, string requested, bool complete)
    {
        var package = ModelCatalog.Packages.Packages.Single(item => item.Id == "argos-" + requested.Replace('_', '-'));
        Assert.Equal(new Uri(package.Url), http.RequestedUri);
        string staged = Assert.Single(Directory.GetDirectories(Path.Combine(directory, "components")));
        string retained = Path.Combine(staged, "models", "packages", "argos-" + installed.Replace('_', '-'), "1.9");
        if (complete)
            Assert.Equal("controlled model", File.ReadAllText(Path.Combine(retained, "model", "model.bin")));
        else
            Assert.False(Directory.Exists(retained));
    }

    private static async Task ConfirmPackageAction(Window window, Button button)
    {
        Assert.True(button.IsEnabled);
        nint owner = new WindowInteropHelper(window).Handle;
        // Close only this test window's native confirmation. The real modal and
        // real package action run unchanged; no private WPF state is inspected.
        var confirm = Task.Run(async () =>
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                nint dialog = GetWindow(owner, 6); // GW_ENABLEDPOPUP
                if (dialog != owner && dialog != 0 && GetWindow(dialog, 4) == owner) // GW_OWNER
                {
                    var className = new StringBuilder(64);
                    GetClassName(dialog, className, className.Capacity);
                    if (className.ToString() == "#32770")
                    {
                        var title = new StringBuilder(128);
                        GetWindowText(dialog, title, title.Capacity);
                        bool expected = title.ToString() == "本地模型";
                        if (!PostMessage(dialog, 0x0111, expected ? 1 : 2, 0)) // WM_COMMAND: IDOK/IDCANCEL
                            throw new InvalidOperationException("Could not close the test-owned model confirmation.");
                        Assert.True(expected, "The package action opened an unexpected confirmation.");
                        return;
                    }
                }
                await Task.Delay(20);
            }
            nint remaining = GetWindow(owner, 6);
            if (remaining != owner && remaining != 0 && GetWindow(remaining, 4) == owner)
                PostMessage(remaining, 0x0010, 0, 0); // WM_CLOSE only our test-owned popup.
            Assert.Fail("The package action did not open its model confirmation.");
        });
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await confirm;
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder value, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder value, int capacity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

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
        public Uri? RequestedUri
        {
            get; private set;
        }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;
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
