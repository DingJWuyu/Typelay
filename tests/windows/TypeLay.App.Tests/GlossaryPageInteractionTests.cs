using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Runtime;
using TypeLay.Windows.Testing;
using TypeLay.Core;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class TranslationSettingsTests
{
    [Fact]
    public Task GlossaryRowsSelectWithoutEditingAndEnterEditsOnlyTheSelectedRow() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(new(Glossary: new(1, [new("PR", "review"), new("项目方", "client team", "zh", "en")])));
        scene.Window.ShowPage("glossary");
        string before = File.ReadAllText(scene.PathToSettings);
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("源词 "));
        var row = GlossaryRow(scene.Window, 2);
        SelectGlossaryRow(row);
        Assert.True(row.IsSelected);
        Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
        GlossaryKey(row, Key.Enter);
        Assert.Equal("项目方", Term(scene.Window, "源词 2").Text);
        Assert.Single(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("源词 "));
        Term(scene.Window, "译词 2").Text = "new draft";
        GlossaryKey(Term(scene.Window, "译词 2"), Key.Escape);
        Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("源词 "));
        Assert.True(GlossaryRow(scene.Window, 2).IsSelected);
        GlossaryKey(GlossaryRow(scene.Window, 2), Key.Enter);
        Term(scene.Window, "译词 2").Text = "customer team";
        CommitGlossary(scene.Window);
        Assert.Equal("customer team", scene.Reload().Glossary!.Rules[1].TargetTerm);
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("源词 "));
        Assert.False(scene.Controller.GetGlossarySettingsState().CanApply);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PublicGlossaryNavigationCommitsBeforeLeavingAndPreservesFailedDrafts(bool failSave) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Glossary: new(1, [new("PR", "review")])));
        scene.Window.ShowPage("glossary");
        SelectGlossaryRow(GlossaryRow(scene.Window, 1));
        GlossaryKey(GlossaryRow(scene.Window, 1), Key.Enter);
        Term(scene.Window, "源词 1").Text = "  MR  ";
        Term(scene.Window, "译词 1").Text = "  merge request  ";
        if (failSave)
            Directory.CreateDirectory(scene.PathToSettings + ".tmp");
        var root = AutomationElement.FromHandle(new WindowInteropHelper(scene.Window).Handle);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await SettingsPageActions.OpenAsync(root, "general", "通用", deadline.Token);
        Assert.Equal(failSave ? new GlossaryRule("PR", "review") : new GlossaryRule("MR", "merge request"), Assert.Single(scene.Reload().Glossary!.Rules));
        await SettingsPageActions.OpenAsync(root, "glossary", "术语表", deadline.Token);
        if (failSave)
        {
            Assert.Equal("  MR  ", Term(scene.Window, "源词 1").Text);
            Assert.Equal("  merge request  ", Term(scene.Window, "译词 1").Text);
            Directory.Delete(scene.PathToSettings + ".tmp");
            Click(scene.Window, "重试保存");
            Assert.Equal(new GlossaryRule("MR", "merge request"), Assert.Single(scene.Reload().Glossary!.Rules));
        }
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("源词 "));
        Assert.False(scene.Controller.GetGlossarySettingsState().CanApply);
    });

    [Fact]
    public Task ANewerNavigationWinsWhileTheSameGlossarySaveWaitsForPracticeRetirement() => desktop.RunAsync(async () =>
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var scene = new SettingsScene(retire: _ => acknowledgement.Task);
        Guid run = Guid.NewGuid();
        Assert.True(scene.Controller.BeginAutomaticPractice(run));
        Assert.True(scene.Controller.BindAutomaticPractice(new(run, scene.Input.OwnedTarget, scene.Input.ConnectionId, scene.Input.CommandGeneration)));
        var retirement = scene.Controller.EndAutomaticPracticeAsync(run);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        scene.Window.ShowPage("general");
        Assert.Null(scene.Reload().Glossary);
        Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => AutomationProperties.GetAutomationId(text) == "SettingsPageHeading" && text.Text == "术语表");
        scene.Window.ShowPage("appearance");
        acknowledgement.SetResult(true);
        await retirement;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
        Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => AutomationProperties.GetAutomationId(text) == "SettingsPageHeading" && text.Text == "外观");
    });

    [Fact]
    public Task ClosingDiscardsThePendingNavigationInsteadOfReopeningItsDestination() => desktop.RunAsync(async () =>
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var scene = new SettingsScene(retire: _ => acknowledgement.Task);
        Guid run = Guid.NewGuid();
        Assert.True(scene.Controller.BeginAutomaticPractice(run));
        Assert.True(scene.Controller.BindAutomaticPractice(new(run, scene.Input.OwnedTarget, scene.Input.ConnectionId, scene.Input.CommandGeneration)));
        var retirement = scene.Controller.EndAutomaticPracticeAsync(run);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        scene.Window.ShowPage("general");
        Assert.Null(scene.Reload().Glossary);
        SettingsRenderingTests.InteractWithCloseDialog(scene.Window, scene.Window.Close, SettingsDraftCloseChoice.Discard);
        Assert.False(scene.Window.IsVisible);
        acknowledgement.SetResult(true);
        await retirement;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(scene.Window.IsVisible);
        Assert.Null(scene.Reload().Glossary);
        scene.Window.ShowPage("glossary");
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("源词 "));
    });

    [Fact]
    public Task EnglishGlossaryConflictLocalizesScopeWithoutChangingUserTerms() => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(UiLanguage: "en"));
        scene.Window.ShowPage("glossary");
        GlossaryButton(scene.Window, "GlossaryAdd").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Term(scene.Window, "Source term 1").Text = "mine";
        Term(scene.Window, "Translation 1").Text = "draft";
        Assert.True((await scene.Controller.ChangeGlossaryAsync([new("无术语", "术语表已更新")], scene.Settings.Revision, cancellationToken: TestContext.Current.CancellationToken)).Saved);
        Exception? failure = null;
        var pending = scene.Window.Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().Single(item => item.Owner == scene.Window);
            try
            {
                dialog.UpdateLayout();
                Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == "无术语 → 术语表已更新 · All directions");
                Assert.DoesNotContain(Descendants(dialog).OfType<TextBlock>(), text => text.Text.Contains("全局"));
                Click(dialog, "Keep Editing");
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        }), DispatcherPriority.ContextIdle);
        GlossaryKey(Term(scene.Window, "Translation 1"), Key.Enter);
        pending.Abort();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal("术语表已更新", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
    });

    [Fact]
    public Task GlossaryRealDoubleClickOpensTheInlineEditorWithoutSaving() => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Glossary: new(1, [new("PR", "review")])));
        scene.Window.ShowPage("glossary");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var row = GlossaryRow(scene.Window, 1);
        string before = File.ReadAllText(scene.PathToSettings);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        row.Unloaded += (_, _) => retired.TrySetResult();
        var host = Assert.IsType<HwndSource>(PresentationSource.FromVisual(row));
        var point = row.PointToScreen(new(row.ActualWidth / 2, row.ActualHeight / 2));
        var pixel = new Native.Point { X = (int)point.X, Y = (int)point.Y };
        Assert.Equal(host.Handle, GlossaryWindowFromPoint(pixel));
        Assert.True(Native.GetCursorPos(out var original));
        Assert.False(Native.GetAsyncKeyState(1) < 0);
        try
        {
            Assert.True(GlossarySetCursorPos(pixel.X, pixel.Y));
            Assert.Equal(host.Handle, GlossaryWindowFromPoint(pixel));
            GlossaryMouseInput[] input = [new() { Flags = 2 }, new() { Flags = 4 }, new() { Flags = 2 }, new() { Flags = 4 }];
            uint inserted = GlossarySendInput(4, input, Marshal.SizeOf<GlossaryMouseInput>());
            if (inserted is 1 or 3)
                Assert.Equal(1u, GlossarySendInput(1, [input[1]], Marshal.SizeOf<GlossaryMouseInput>()));
            Assert.Equal(4u, inserted);
            await retired.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("PR", Term(scene.Window, "源词 1").Text);
            Assert.Equal("review", Term(scene.Window, "译词 1").Text);
            Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
            GlossaryKey(Term(scene.Window, "译词 1"), Key.Escape);
        }
        finally { Assert.True(GlossarySetCursorPos(original.X, original.Y)); }
    });

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct GlossaryMouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(20)] public uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint GlossaryWindowFromPoint(Native.Point point);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlossarySetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint GlossarySendInput(uint count, GlossaryMouseInput[] input, int size);

    [Fact]
    public Task GlossaryEmptySearchAndCancelNeverPersistOrReviveRetiredRows() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(new(Glossary: new(1, [new("A", "alpha"), new("B", "beta")])));
        scene.Window.ShowPage("glossary");
        string before = File.ReadAllText(scene.PathToSettings);
        var oldRow = GlossaryRow(scene.Window, 1);
        var oldSelection = GlossarySelection(oldRow);
        oldSelection.Select();
        var oldRowEnter = GlossaryKeyEvent(oldRow, Key.Enter);
        var oldRemove = GlossaryButton(scene.Window, "GlossaryRemove");
        var search = Named<TextBox>(scene.Window, "搜索术语");
        search.Text = "no matching term";
        Assert.Empty(Descendants(scene.Window).OfType<ListBoxItem>());
        Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.IsVisible && text.Text == "没有匹配的术语");
        Assert.False(GlossaryButton(scene.Window, "GlossaryRemove").IsEnabled);
        search.Text = "A";
        try
        {
            oldSelection.Select();
        }
        catch (ElementNotAvailableException) { }
        catch (ElementNotEnabledException) { }
        oldRow.RaiseEvent(oldRowEnter);
        oldRemove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("源词 "));
        SelectGlossaryRow(GlossaryRow(scene.Window, 1));
        GlossaryKey(GlossaryRow(scene.Window, 1), Key.Enter);
        var oldSource = Term(scene.Window, "源词 1");
        var oldSourceEnter = GlossaryKeyEvent(oldSource, Key.Enter);
        Term(scene.Window, "译词 1").Text = "canceled";
        GlossaryKey(oldSource, Key.Escape);
        oldSource.Text = "late";
        oldSource.RaiseEvent(oldSourceEnter);
        Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
        Click(scene.Window, "添加术语");
        Assert.Equal("", search.Text);
        Term(scene.Window, "源词 3").Text = "unsaved";
        GlossaryKey(Term(scene.Window, "源词 3"), Key.Escape);
        Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
        // Escape creates new native item containers. Observe the laid-out
        // current rows just as the earlier selection/editor helpers do.
        var restored = new[] { GlossaryRow(scene.Window, 1), GlossaryRow(scene.Window, 2) };
        Assert.All(restored, row => Assert.True(row.IsVisible));
        Assert.Equal(new[] { "A → alpha", "B → beta" }, restored.Select(AutomationProperties.GetName));
        Assert.Equal(2, Descendants(scene.Window).OfType<ListBoxItem>().Count());
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("源词 "));
    });

    [Fact]
    public Task GlossaryLeavingTheRowCommitsButMovingWithinItPreservesTheDraft() => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Glossary: new(1, [new("PR", "review")])));
        scene.Window.ShowPage("glossary");
        SelectGlossaryRow(GlossaryRow(scene.Window, 1));
        GlossaryKey(GlossaryRow(scene.Window, 1), Key.Enter);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var source = Term(scene.Window, "源词 1");
        var target = Term(scene.Window, "译词 1");
        Assert.True(source.Focus());
        source.Text = "MR";
        Assert.True(target.Focus());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("PR", Assert.Single(scene.Reload().Glossary!.Rules).SourceTerm);
        Assert.Same(target, Keyboard.FocusedElement);
        target.Text = "merge request";
        Assert.True(Named<TextBox>(scene.Window, "搜索术语").Focus());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(new GlossaryRule("MR", "merge request"), Assert.Single(scene.Reload().Glossary!.Rules));
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("源词 "));
    });

    [Fact]
    public Task GlossaryDeleteAndUndoPeersBelongToTheExactSelectionAndSavedRevision() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(new(Glossary: new(1, [new("A", "alpha"), new("B", "beta")])));
        scene.Window.ShowPage("glossary");
        SelectGlossaryRow(GlossaryRow(scene.Window, 1));
        var oldRemove = GlossaryButton(scene.Window, "GlossaryRemove");
        SelectGlossaryRow(GlossaryRow(scene.Window, 2));
        string before = File.ReadAllText(scene.PathToSettings);
        oldRemove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
        GlossaryButton(scene.Window, "GlossaryRemove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("A", Assert.Single(scene.Reload().Glossary!.Rules).SourceTerm);
        Click(scene.Window, "撤销");
        Assert.Equal(2, scene.Reload().Glossary!.Rules.Count);
        SelectGlossaryRow(GlossaryRow(scene.Window, 2));
        GlossaryButton(scene.Window, "GlossaryRemove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var oldUndo = Descendants(scene.Window).OfType<Button>().Single(button => Equals(button.Content, "撤销") && button.IsVisible);
        scene.Settings.Update(scene.Settings.Value with
        {
            Accent = "sage"
        });
        before = File.ReadAllText(scene.PathToSettings);
        oldUndo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
        Assert.Equal("sage", scene.Reload().Accent);
        Assert.Equal("A", Assert.Single(scene.Reload().Glossary!.Rules).SourceTerm);
        Assert.False(scene.Controller.GetGlossarySettingsState().CanApply);
    });

    [Fact]
    public Task GlossaryEmptyAddCancelAndClosedPeersCannotCreateALaterDraft() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(new(UiLanguage: "en"));
        scene.Window.ShowPage("glossary");
        var emptyAdd = GlossaryButton(scene.Window, "GlossaryEmptyAdd");
        Assert.True(emptyAdd.IsVisible);
        Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.IsVisible && text.Text == "No terms yet");
        emptyAdd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var source = Term(scene.Window, "Source term 1");
        source.Text = "draft";
        GlossaryKey(source, Key.Escape);
        emptyAdd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("Source term "));
        var currentAdd = GlossaryButton(scene.Window, "GlossaryEmptyAdd");
        currentAdd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        source = Term(scene.Window, "Source term 1");
        var sourceEnter = GlossaryKeyEvent(source, Key.Enter);
        GlossaryKey(source, Key.Escape);
        var oldToolbarAdd = GlossaryButton(scene.Window, "GlossaryAdd");
        Assert.True(scene.Window.RequestClose());
        scene.Window.ShowPage("glossary");
        oldToolbarAdd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        currentAdd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        source.Text = "late draft";
        source.RaiseEvent(sourceEnter);
        Assert.Null(scene.Reload().Glossary);
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("Source term "));
    });

    [Fact]
    public Task GlossaryEnglishChromeDoesNotTranslateUserTermText() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(new(UiLanguage: "en", Glossary: new(1, [new("无术语", "术语表已更新")])));
        scene.Window.ShowPage("glossary");
        var row = GlossaryRow(scene.Window, 1);
        Assert.Contains(Descendants(row).OfType<TextBlock>(), text => text.Text == "无术语");
        Assert.Contains(Descendants(row).OfType<TextBlock>(), text => text.Text == "术语表已更新");
        Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.Text == "All directions");
        Assert.Equal("Remove term", AutomationProperties.GetName(GlossaryButton(scene.Window, "GlossaryRemove")));
    });

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public Task GlossaryInlineLengthBoundaryUsesTheExistingUtf8Rule(int length, bool valid) => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene();
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = new string('a', length);
        Term(scene.Window, "译词 1").Text = "translation";
        CommitGlossary(scene.Window);
        Assert.Equal(valid, scene.Reload().Glossary is not null);
        if (!valid)
        {
            Assert.Equal(length, Term(scene.Window, "源词 1").Text.Length);
            Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.Text.Contains("长度"));
            GlossaryKey(Term(scene.Window, "源词 1"), Key.Escape);
            Assert.Null(scene.Reload().Glossary);
        }
    });

    [Theory]
    [InlineData("zh-CN", "所有方向", "中文 → 英语")]
    [InlineData("en", "All directions", "Chinese → English")]
    public Task InlineScopeSelectionDisplaysTheLocalizedLabelInsteadOfTheRuleObject(string language, string global, string directed) => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(new(UiLanguage: language, Glossary: new(1, [new("PR", "review"), new("项目方", "client team", "zh", "en")])));
        scene.Window.ShowPage("glossary");
        string before = File.ReadAllText(scene.PathToSettings);
        for (int number = 1; number <= 2; number++)
        {
            var row = GlossaryRow(scene.Window, number);
            SelectGlossaryRow(row);
            GlossaryKey(row, Key.Enter);
            var scope = Named<ComboBox>(scene.Window, (language == "en" ? "Term scope " : "术语范围 ") + number);
            AssertGlossaryScopeLabel(scope, number == 1 ? global : directed);
            var source = Term(scene.Window, (language == "en" ? "Source term " : "源词 ") + number);
            GlossaryKey(source, Key.Escape);
            Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
        }
    });
    private static void AssertGlossaryScopeLabel(ComboBox scope, string expected)
    {
        scope.UpdateLayout();
        var selection = Assert.IsType<ContentPresenter>(scope.Template.FindName("selection", scope));
        var text = Assert.Single(Descendants(selection).OfType<TextBlock>());
        Assert.True(text.IsVisible);
        Assert.Equal(expected, text.Text);
        Assert.DoesNotContain("GlossaryScope", text.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zh-CN", "light", 920)]
    [InlineData("zh-CN", "dark", 920)]
    [InlineData("en", "light", 920)]
    [InlineData("en", "dark", 920)]
    [InlineData("zh-CN", "light", 800)]
    [InlineData("zh-CN", "dark", 800)]
    [InlineData("en", "light", 800)]
    [InlineData("en", "dark", 800)]
    public Task GlossaryActualConnectedPageRendersReadOnlySelectedInlineAndEmptyStates(string language, string theme, int width) => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(new(UiLanguage: language, Theme: theme, Font: "claude", TextSize: 15, Accent: "clay",
            Glossary: new(1, [new("TypeLay", "TypeLay"), new("PR", "PR"), new("项目方", "client team", "zh", "en"), new("排期", "timeline", "zh", "en")])));
        scene.Window.Width = width;
        scene.Window.Height = width == 920 ? 700 : 560;
        scene.Window.ShowPage("glossary");
        scene.Window.UpdateLayout();
        var row = GlossaryRow(scene.Window, 3);
        Assert.Equal(38, row.ActualHeight);
        var columns = Assert.IsType<Grid>(row.Content);
        Assert.Equal(3, columns.ColumnDefinitions.Count);
        Assert.Equal(150, columns.ColumnDefinitions[2].ActualWidth);
        SelectGlossaryRow(row);
        Assert.Equal(Assert.IsType<SolidColorBrush>(scene.Window.FindResource("AccentSoft")).Color, Assert.IsType<SolidColorBrush>(row.Background).Color);
        Assert.Equal(28, GlossaryButton(scene.Window, "GlossaryRemove").ActualWidth);
        Assert.Equal(24, GlossaryButton(scene.Window, "GlossaryRemove").ActualHeight);
        RenderGlossary(scene.Window, $"glossary-selected-{language}-{theme}-{width}.png");
        GlossaryKey(row, Key.Enter);
        scene.Window.UpdateLayout();
        var source = Term(scene.Window, language == "en" ? "Source term 3" : "源词 3");
        Assert.Equal(26, source.ActualHeight);
        var host = Assert.IsType<ScrollViewer>(source.Template.FindName("PART_ContentHost", source));
        Assert.Equal(new Thickness(0), host.Margin);
        Assert.Equal(source.Padding, host.Padding);
        Assert.True(host.ViewportHeight >= source.FontSize);
        Assert.Equal("项目方", source.Text);
        var scope = Named<ComboBox>(scene.Window, language == "en" ? "Term scope 3" : "术语范围 3");
        Assert.Equal(24, scope.ActualHeight);
        AssertGlossaryScopeLabel(scope, language == "en" ? "Chinese → English" : "中文 → 英语");
        RenderGlossary(scene.Window, $"glossary-edit-{language}-{theme}-{width}.png");
        GlossaryKey(source, Key.Escape);
        Named<TextBox>(scene.Window, language == "en" ? "Search terms" : "搜索术语").Text = "no matches";
        Assert.Empty(Descendants(scene.Window).OfType<ListBoxItem>());
        Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.IsVisible && text.Text == (language == "en" ? "No matching terms" : "没有匹配的术语"));
        RenderGlossary(scene.Window, $"glossary-no-match-{language}-{theme}-{width}.png");
    });

    private static void RenderGlossary(Window window, string name)
    {
        window.UpdateLayout();
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string folder = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(folder);
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }

    private static Button GlossaryButton(Window window, string id)
    {
        window.UpdateLayout();
        return Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == id);
    }
    private static ListBoxItem GlossaryRow(Window window, int number)
    {
        window.UpdateLayout();
        return Descendants(window).OfType<ListBoxItem>().Single(row => AutomationProperties.GetAutomationId(row) == "GlossaryRow_" + number);
    }
    private static ISelectionItemProvider GlossarySelection(ListBoxItem row)
    {
        // WPF exposes SelectionItem on the parent list's data-item peer, not
        // the ListBoxItem container's wrapper peer.
        var list = Assert.IsType<ListBox>(ItemsControl.ItemsControlFromItemContainer(row));
        var peer = Assert.IsType<ListBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(list));
        var item = Assert.Single(peer.GetChildren(), child => child.GetAutomationId() == AutomationProperties.GetAutomationId(row));
        return Assert.IsAssignableFrom<ISelectionItemProvider>(item.GetPattern(PatternInterface.SelectionItem));
    }
    private static void SelectGlossaryRow(ListBoxItem row) => GlossarySelection(row).Select();
    private static KeyEventArgs GlossaryKeyEvent(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element);
        Assert.NotNull(source);
        return new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
    }
    private static void GlossaryKey(UIElement element, Key key) => element.RaiseEvent(GlossaryKeyEvent(element, key));
}
