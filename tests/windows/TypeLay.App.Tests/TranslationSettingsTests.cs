using System.IO;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class TranslationSettingsTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task UnreleasedTranslationSettingsExposeNoAdditionControls() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene();
        Assert.False(scene.Controller.GetStyleSettingsState().IsReleased);
        Assert.False(scene.Controller.GetGlossarySettingsState().IsReleased);
        scene.Window.ShowPage("translation");
        scene.Window.UpdateLayout();
        Assert.Empty(Descendants(scene.Window).OfType<ComboBox>());
        Assert.DoesNotContain(Descendants(scene.Window).OfType<Button>(), button => button.IsVisible && Equals(button.Content, "重置翻译风格"));
        scene.Window.ShowPage("glossary");
        scene.Window.UpdateLayout();
        Assert.Empty(Descendants(scene.Window).OfType<TextBox>());
        Assert.DoesNotContain(Descendants(scene.Window).OfType<Button>(), button => Equals(button.Content, "添加术语"));
        Assert.Null(scene.Reload().Styles);
        Assert.Null(scene.Reload().Glossary);
    });

    [Fact]
    public Task ReleasedGlossarySavesExplicitGlobalAndDirectedTermsWithoutAModel() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(released: true);
        Assert.True(scene.Controller.GetGlossarySettingsState().IsReleased);
        Assert.False(scene.Controller.GetGlossarySettingsState().TechnicalAvailable);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "pull request";
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 2").Text = "项目方";
        Term(scene.Window, "译词 2").Text = "client team";
        var scope = Named<ComboBox>(scene.Window, "术语范围 2");
        scope.SelectedIndex = 1; // The first directed catalog choice is Chinese -> English.
        Assert.Null(scene.Reload().Glossary);
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        Assert.Equal(new[] { new GlossaryRule("PR", "pull request"), new("项目方", "client team", "zh", "en") }, scene.Reload().Glossary!.Rules);
        Assert.Null(scene.Controller.GetGlossarySettingsState().Session);
        Assert.Null(scene.Reload().Styles);
    });

    [Fact]
    public Task ClosedReleaseAllowsOnlyExplicitDeletionOfSavedTerms() => desktop.RunAsync(() =>
    {
        var first = new GlossaryRule("PR", "pull request");
        var second = new GlossaryRule("项目方", "client team", "zh", "en");
        using var scene = new SettingsScene(new(Glossary: new(1, [first, second])));
        scene.Window.ShowPage("glossary");
        scene.Window.UpdateLayout();
        Assert.All(Descendants(scene.Window).OfType<TextBox>().Where(box => box.IsVisible), box => Assert.True(box.IsReadOnly));
        Assert.False(Named<ComboBox>(scene.Window, "术语范围 1").IsEnabled);
        Assert.DoesNotContain(Descendants(scene.Window).OfType<Button>(), button => button.IsVisible && Equals(button.Content, "添加术语"));
        Named<Button>(scene.Window, "删除术语 1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, scene.Reload().Glossary!.Rules.Count);
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        Assert.Equal(second, Assert.Single(scene.Reload().Glossary!.Rules));
        Assert.False(scene.Controller.GetGlossarySettingsState().IsReleased);
    });

    [Fact]
    public Task GlossaryDraftAndFocusSurviveRefreshNavigationAndCloseChoices() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(released: true);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        var source = Term(scene.Window, "源词 1");
        var target = Term(scene.Window, "译词 1");
        source.Text = "PR";
        target.Text = "review";
        Assert.True(target.Focus());
        target.Select(1, 3);
        scene.Settings.Update(scene.Settings.Value with
        {
            Theme = "dark"
        });
        Assert.Same(target, Term(scene.Window, "译词 1"));
        Assert.True(target.IsKeyboardFocused);
        Assert.Equal((1, 3), (target.SelectionStart, target.SelectionLength));
        Assert.Null(scene.Reload().Glossary);
        Assert.Equal("review", target.Text);
        scene.Window.ShowPage("language");
        scene.Window.ShowPage("glossary");
        Assert.Equal("review", Term(scene.Window, "译词 1").Text);
        SettingsRenderingTests.InteractWithCloseDialog(scene.Window, scene.Window.Close, SettingsDraftCloseChoice.KeepEditing);
        Assert.True(scene.Window.IsVisible);
        SettingsRenderingTests.InteractWithCloseDialog(scene.Window, scene.Window.Close, SettingsDraftCloseChoice.Discard);
        scene.Window.ShowPage("glossary");
        scene.Window.UpdateLayout();
        Assert.Empty(Descendants(scene.Window).OfType<TextBox>());
    });

    [Fact]
    public Task InvalidOrUnwritableGlossaryKeepsDraftAndRetrySavesIt() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(released: true);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 2").Text = "PR";
        Term(scene.Window, "译词 2").Text = "pull request";
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        Assert.Null(scene.Reload().Glossary);
        Assert.Equal("pull request", Term(scene.Window, "译词 2").Text);
        Named<Button>(scene.Window, "删除术语 2").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Directory.CreateDirectory(scene.PathToSettings + ".tmp");
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        Assert.Null(scene.Reload().Glossary);
        Assert.Equal("review", Term(scene.Window, "译词 1").Text);
        Directory.Delete(scene.PathToSettings + ".tmp");
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        Assert.Equal("review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
    });

    [Fact]
    public Task OrdinarySettingsSaveNeverPersistsAnUnsubmittedGlossary() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(released: true);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        scene.Window.ShowPage("general");
        Named<ComboBox>(scene.Window, "工作模式").SelectedIndex = 1;
        Assert.Equal(WorkMode.OnDemand, scene.Reload().Mode);
        Assert.Null(scene.Reload().Glossary);
        scene.Window.ShowPage("glossary");
        Assert.Equal("review", Term(scene.Window, "译词 1").Text);
    });

    [Fact]
    public Task OneExplicitSaveCommitsOrdinaryAndGlossaryDraftsThroughTheirSeparateControllers() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(released: true);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        scene.Window.ShowPage("shortcuts");
        Named<TextBox>(scene.Window, "唤醒翻译 UI 快捷键").Text = "Ctrl+Shift+U";
        int ordinary = 0, glossary = 0;
        scene.Settings.Changed += () => ordinary++;
        scene.Settings.GlossarySaved += () => glossary++;
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        Assert.Equal("Ctrl+Shift+U", scene.Reload().WakeShortcut);
        Assert.Equal("review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
        Assert.Equal(1, ordinary);
        Assert.Equal(1, glossary);
        Assert.False(SaveButton(scene.Window).IsEnabled);
    });

    [Fact]
    public Task AnOrdinarySaveNotificationCannotSilentlyOverwriteANewerGlossary() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(released: true);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        scene.Window.ShowPage("shortcuts");
        Named<TextBox>(scene.Window, "唤醒翻译 UI 快捷键").Text = "Ctrl+Shift+U";
        scene.Settings.Changed += () => Assert.True(scene.Controller.ChangeGlossaryAsync([new("term", "saved by another editor")],
            scene.Settings.Revision, cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult().Saved);
        bool reviewed = false;
        Exception? failure = null;
        var pending = scene.Window.Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().Single(item => item.Owner == scene.Window);
            try
            {
                dialog.UpdateLayout();
                Assert.Equal("术语表已更新", dialog.Title);
                Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == "term → saved by another editor · 全局");
                reviewed = true;
                Click(dialog, "继续编辑");
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        }), DispatcherPriority.ContextIdle);
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        pending.Abort();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(reviewed);
        Assert.Equal("Ctrl+Shift+U", scene.Reload().WakeShortcut);
        Assert.Equal("saved by another editor", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
        scene.Window.ShowPage("glossary");
        Assert.Equal("review", Term(scene.Window, "译词 1").Text);
        Assert.True(SaveButton(scene.Window).IsEnabled);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StyleResetRequiresAnExplicitChoiceAndClearsAllOverrides(bool confirm) => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(new(Styles: new(TranslationStyle.Formal,
            new Dictionary<string, TranslationStyle> { ["one"] = TranslationStyle.Natural, ["two"] = TranslationStyle.Formal }),
            Glossary: new(2, [new("PR", "review")])));
        scene.Window.ShowPage("translation");
        ConfirmReset(scene.Window, confirm);
        var saved = scene.Reload();
        Assert.Equal(confirm, saved.Styles is null);
        if (!confirm)
            Assert.Equal(2, saved.Styles!.Applications!.Count);
        Assert.Equal("review", Assert.Single(saved.Glossary!.Rules).TargetTerm);
        Assert.Null(scene.Controller.GetStyleSettingsState().Session);
    });

    [Fact]
    public Task RemovingASavedApplicationOverrideKeepsTheGlobalChoiceAndOtherApplications() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(new(Styles: new(TranslationStyle.Formal,
            new Dictionary<string, TranslationStyle> { ["one"] = TranslationStyle.Natural, ["two"] = TranslationStyle.Formal })));
        scene.Window.ShowPage("translation");
        Named<Button>(scene.Window, "移除风格覆盖 one").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(TranslationStyle.Formal, scene.Reload().Styles!.Default);
        Assert.Equal("two", Assert.Single(scene.Reload().Styles!.Applications!).Key);
        Assert.Null(scene.Controller.LastApplication);
        Assert.Null(scene.Controller.GetStyleSettingsState().Session);
    });

    [Theory]
    [InlineData(SettingsDraftCloseChoice.KeepEditing)]
    [InlineData(SettingsDraftCloseChoice.Discard)]
    public Task GlossaryCloseChoiceControlsTheExactPendingSaveBeforeRetirementAcknowledgement(SettingsDraftCloseChoice choice) => desktop.RunAsync(async () =>
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var scene = new SettingsScene(released: true, retire: _ => acknowledgement.Task);
        Guid run = Guid.NewGuid();
        Assert.True(scene.Controller.BeginAutomaticPractice(run));
        Assert.True(scene.Controller.BindAutomaticPractice(new(run, scene.Input.OwnedTarget, scene.Input.ConnectionId, scene.Input.CommandGeneration)));
        var retirement = scene.Controller.EndAutomaticPracticeAsync(run);
        Assert.False(retirement.IsCompleted);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        Assert.False(SaveButton(scene.Window).IsEnabled);
        Assert.Null(scene.Reload().Glossary);
        Term(scene.Window, "译词 1").Text = "newer draft";
        SettingsRenderingTests.InteractWithCloseDialog(scene.Window, scene.Window.Close, choice, duringClose: () =>
        {
            acknowledgement.SetResult(true);
            scene.Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        });
        await retirement;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(choice == SettingsDraftCloseChoice.KeepEditing, scene.Reload().Glossary is not null);
        Assert.Equal(choice == SettingsDraftCloseChoice.KeepEditing, scene.Window.IsVisible);
        if (choice == SettingsDraftCloseChoice.KeepEditing)
        {
            Assert.Equal("review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
            Assert.Equal("newer draft", Term(scene.Window, "译词 1").Text);
            Assert.True(SaveButton(scene.Window).IsEnabled);
            Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
            Assert.Equal("newer draft", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
            Assert.False(SaveButton(scene.Window).IsEnabled);
        }
        else
        {
            scene.Window.ShowPage("glossary");
            scene.Window.UpdateLayout();
            Assert.Empty(Descendants(scene.Window).OfType<TextBox>());
            Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBlock>(), text => text.Text == SettingsDraftPresentation.Create().SavedMessage);
        }
    });

    [Fact]
    public Task UnchangedGlossaryCanSaveAfterAnUnrelatedSettingsRevision() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(released: true);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        var target = Term(scene.Window, "译词 1");
        target.Text = "review";
        Assert.True(target.Focus());
        target.Select(1, 3);
        scene.Settings.Update(scene.Settings.Value with
        {
            Theme = "dark"
        });
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        Assert.Equal("review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
        Assert.Equal("dark", scene.Reload().Theme);
        Assert.Same(target, Term(scene.Window, "译词 1"));
        Assert.True(target.IsKeyboardFocused);
        Assert.Equal((1, 3), (target.SelectionStart, target.SelectionLength));
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("replace")]
    [InlineData("changed-again")]
    public Task ChangedGlossaryRequiresReviewAndConfirmationOfTheExactLatestRevision(string choice) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(released: true);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        Assert.True((await scene.Controller.ChangeGlossaryAsync([new("term", "new saved value")], scene.Settings.Revision,
            cancellationToken: TestContext.Current.CancellationToken)).Saved);
        Exception? failure = null;
        var pending = scene.Window.Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().Single(item => item.Owner == scene.Window);
            try
            {
                dialog.UpdateLayout();
                Assert.Equal("术语表已更新", dialog.Title);
                Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == "term → new saved value · 全局");
                Assert.Equal("继续编辑", Descendants(dialog).OfType<Button>().Single(button => button.IsDefault).Content);
                if (choice == "changed-again")
                    Assert.True(scene.Controller.ChangeGlossaryAsync([new("term", "newer saved value")], scene.Settings.Revision,
                        cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult().Saved);
                Click(dialog, choice == "cancel" ? "继续编辑" : "替换并保存");
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        }), DispatcherPriority.ContextIdle);
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        pending.Abort();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(choice == "replace" ? "review" : choice == "changed-again" ? "newer saved value" : "new saved value",
            Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
        Assert.Equal("review", Term(scene.Window, "译词 1").Text);
        Assert.Equal(choice != "replace", SaveButton(scene.Window).IsEnabled);
    });

    [Fact]
    public Task ClosingAfterPersistenceNeverPretendsTheCommittedGlossaryWasRolledBack() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(released: true);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        scene.Settings.GlossarySaved += () => SettingsRenderingTests.InteractWithCloseDialog(scene.Window,
            scene.Window.Close, SettingsDraftCloseChoice.Discard);
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        Assert.False(scene.Window.IsVisible);
        Assert.Equal("review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
        scene.Window.ShowPage("glossary");
        Assert.Equal("review", Term(scene.Window, "译词 1").Text);
        Assert.False(SaveButton(scene.Window).IsEnabled);
    });

    [Fact]
    public Task NotificationFailureReportsTheKnownCommit() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(released: true);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        scene.Settings.GlossarySaved += () => throw new InvalidOperationException("controlled observer failure");
        Click(scene.Window, SettingsDraftPresentation.Create().SaveLabel);
        Assert.Equal("review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
        Assert.False(SaveButton(scene.Window).IsEnabled);
        Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.Text == "已保存，请重新打开设置确认当前状态。");
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBlock>(), text => text.Text.Contains("设置未保存"));
    });

    [Fact]
    public Task GlobalStyleRequiresBothReleaseAndLiveNegotiatedCapability() => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Source: "zh", Target: "ja", Styles: new(TranslationStyle.Formal)), released: true);
        scene.Window.ShowPage("translation");
        var choice = Named<ComboBox>(scene.Window, "默认翻译风格");
        Assert.False(choice.IsEnabled);
        Assert.Equal(1, choice.SelectedIndex);
        await scene.StartStyleModelAsync();
        Assert.True(scene.Controller.GetStyleSettingsState().CanChange);
        Assert.True(choice.IsEnabled);
        int ordinary = 0, changed = 0;
        scene.Settings.Changed += () => ordinary++;
        scene.Settings.StyleChanged += () => changed++;
        choice.SelectedIndex = 0;
        Assert.Equal(TranslationStyle.Natural, scene.Reload().Styles!.Default);
        Assert.NotNull(scene.Reload().Styles); // Natural is a choice, never reset.
        Assert.Equal(0, ordinary);
        Assert.Equal(1, changed);
        Assert.Null(scene.Controller.GetStyleSettingsState().Session);
        Assert.Null(scene.Controller.LastApplication);
        scene.StopModel();
        Assert.False(choice.IsEnabled);
        Assert.Equal(TranslationStyle.Natural, scene.Reload().Styles!.Default);
    });

    [Fact]
    public Task LiveTechnicalCapabilityCannotRevealProductionStyleControls() => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Source: "zh", Target: "ja"));
        await scene.StartStyleModelAsync();
        Assert.True(scene.Controller.GetStyleSettingsState().TechnicalAvailable);
        Assert.False(scene.Controller.GetStyleSettingsState().IsReleased);
        scene.Window.ShowPage("translation");
        scene.Window.UpdateLayout();
        Assert.DoesNotContain(Descendants(scene.Window).OfType<ComboBox>(), box => AutomationProperties.GetName(box) == "默认翻译风格");
        Assert.Null(scene.Reload().Styles);
    });

    private static void ConfirmReset(SettingsWindow window, bool confirm)
    {
        Exception? failure = null;
        var pending = window.Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().Single(item => item.Owner == window);
            try
            {
                dialog.UpdateLayout();
                Assert.Equal("重置翻译风格", dialog.Title);
                Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == "清除全局风格和所有应用的风格覆盖？");
                Assert.Equal("取消", Descendants(dialog).OfType<Button>().Single(button => button.IsDefault).Content);
                Click(dialog, confirm ? "重置翻译风格" : "取消");
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        }), DispatcherPriority.ContextIdle);
        Click(window, "重置翻译风格");
        pending.Abort();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static T Named<T>(Window window, string name) where T : FrameworkElement
    {
        window.UpdateLayout();
        return Descendants(window).OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
    }
    private static TextBox Term(Window window, string name) => Named<TextBox>(window, name);
    private static Button SaveButton(Window window) => Descendants(window).OfType<Button>().Single(button => Equals(button.Content, SettingsDraftPresentation.Create().SaveLabel));
    private static void Click(Window window, string label)
    {
        window.UpdateLayout();
        Descendants(window).OfType<Button>().Single(button => Equals(button.Content, label))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    private sealed class SettingsScene : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "typelay-translation-settings-" + Guid.NewGuid().ToString("N"));
        private readonly OverlayWindow _overlay = new(automationTest: true);
        private readonly ModelClient _model;
        private readonly ModelRuntimePaths _paths;
        public string PathToSettings => Path.Combine(_directory, "settings.json");
        public SettingsStore Settings
        {
            get;
        }
        public SessionController Controller
        {
            get;
        }
        public SettingsWindow Window
        {
            get;
        }
        public SettingsInput Input
        {
            get;
        }
        public SettingsScene(UserSettings? initial = null, bool released = false, Func<OwnedPracticeBinding, Task<bool>>? retire = null)
        {
            Directory.CreateDirectory(_directory);
            if (initial is not null)
                File.WriteAllText(PathToSettings, JsonSerializer.Serialize(initial));
            Settings = WindowsRuntime.CreateSettings(PathToSettings);
            _paths = new(Path.Combine(_directory, "python-marker"), Path.Combine(_directory, "peer.py"), _directory);
            _model = new(_paths, _ =>
            {
                var start = new ProcessStartInfo("python")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("-I");
                start.ArgumentList.Add("-B");
                start.ArgumentList.Add(_paths.Worker);
                return new ChildProcess(start);
            });
            Input = new SettingsInput { Owned = retire is not null };
            var release = released ? new TranslationReleasePolicy([
                new(TranslationOperation.Style, "hy-mt2-1-8b-q4-k-m", "a0c709d9fac510f2c807aa3af52872340dc37a4a", "zh_ja"),
                new(TranslationOperation.Glossary, "hy-mt2-1-8b-q4-k-m", "a0c709d9fac510f2c807aa3af52872340dc37a4a", "zh_ja")]) : null;
            Controller = new(Settings, _model, _overlay, Input, Input, new SettingsLoop(), retireOwnedPractice: retire, translationRelease: release);
            Window = new(Settings, _model, () => { }, controller: Controller)
            {
                SessionMustEnd = Controller.StopSession,
                PrepareSettingsChange = Controller.PrepareSettingsChangeAsync
            };
        }
        // A real framed peer negotiates production ModelClient identity. It never
        // loads a model or produces a translation and is not language-quality evidence.
        public async Task StartStyleModelAsync()
        {
            File.WriteAllText(_paths.Python, "controlled process port");
            File.WriteAllText(_paths.Worker, """
                import json, struct, sys
                while header := sys.stdin.buffer.read(4):
                    request = json.loads(sys.stdin.buffer.read(struct.unpack('<I', header)[0]))
                    assert request['op'] == 'hello'
                    response = {'id': request['id'], 'status': 'ready', 'directions': ['zh_ja'],
                        'packages': [{'id': 'hy-mt2-1-8b-q4-k-m', 'version': 'a0c709d9fac510f2c807aa3af52872340dc37a4a', 'directions': ['zh_ja']}],
                        'styledTranslationVersion': 1, 'glossaryTranslationVersion': 1}
                    encoded = json.dumps(response).encode()
                    sys.stdout.buffer.write(struct.pack('<I', len(encoded)) + encoded)
                    sys.stdout.buffer.flush()
                """);
            using var inventory = JsonDocument.Parse("""
                {"schema":3,"activePackages":["hy-mt2-1-8b-q4-k-m"],"packages":[{"id":"hy-mt2-1-8b-q4-k-m","version":"a0c709d9fac510f2c807aa3af52872340dc37a4a","directions":["zh_ja"],"files":[{"path":"model.gguf","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}]}
                """);
            File.WriteAllText(Path.Combine(_directory, "installed.json"), inventory.RootElement.GetRawText());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            Assert.Equal(ModelPublicationOutcome.Confirmed, await _model.ReloadAsync(ModelLoadPlan.Create(_paths, inventory.RootElement),
                () => ModelPublicationOutcome.Confirmed, _ => false, timeout.Token));
            Assert.NotNull(_model.VerifiedIdentity);
        }
        public void StopModel()
        {
            _model.Dispose();
            Window.ShowPage("translation");
        }
        public UserSettings Reload() => WindowsRuntime.CreateSettings(PathToSettings).Value;
        public void Dispose()
        {
            Window.PrepareForEmergencyShutdown();
            Window.Close();
            Controller.Dispose();
            _overlay.Close();
            _model.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class SettingsLoop : ISessionLoop
    {
        public long NowMs => 1000;
        public event Action? Tick { add { } remove { } }
        public void Start()
        {
        }
        public void Stop()
        {
        }
    }

    // No desktop observations or input authority are invented for settings-only tests.
    private sealed class SettingsInput : IInputMonitor, IInputService
    {
        public bool Owned
        {
            get; init;
        }
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public TargetId OwnedTarget { get; } = new("controlled-settings", "owned-practice", Guid.NewGuid());
        public bool Ready => Owned;
        public bool Available => Owned;
        public bool ShortcutsReady => false;
        public bool CanInterceptAcceptance => false;
        public string ShortcutStatus => "unavailable";
        public long UserEpoch => 0;
        public long SourceUserEpoch => 0;
        public long ForegroundEpoch => 0;
        public long WindowEpoch => 0;
        public long ContentEpoch => 0;
        public Guid CommandGeneration { get; } = Guid.NewGuid();
        public FocusMember GetFocus(TargetId target) => Owned && target == OwnedTarget ? FocusMember.Source : FocusMember.Other;
        public void ConfigureShortcuts(bool enabled, string wake, string direct, SessionShortcutBindings sessionBindings, bool sessionEnabled)
        {
        }
        public bool TryRead(out InputNotice? notice)
        {
            notice = null;
            return false;
        }
        public void Publish(InputLease lease, AcceptancePermit? permit) => throw new InvalidOperationException("No settings input permit.");
        public void PublishShortcut(ShortcutLease lease) => throw new InvalidOperationException("No settings shortcut lease.");
        public void Revoke()
        {
        }
        public Task StartAsync() => Task.CompletedTask;
        public Task<InputCapture> CaptureAsync(InputContext context) => Task.FromResult(new InputCapture(null));
        public Task<PauseObservation> ObservePauseAsync(InputContext context) => Task.FromResult(new PauseObservation(null));
        public Task<InputReturn> ReturnToSourceAsync(InputContext context) => Task.FromResult(new InputReturn(false));
        public Task<WriteReceipt> WriteAsync(WritePlan plan, InputContext context, long expiresAtMs) => throw new InvalidOperationException("Settings cannot write input.");
        public Task<WriteReceipt> WriteDirectAsync(WritePlan plan, string[] excluded) => throw new InvalidOperationException("Settings cannot write input.");
        public Task ClearAsync(InputContext context) => Task.CompletedTask;
        public void Dispose()
        {
        }
    }
}
