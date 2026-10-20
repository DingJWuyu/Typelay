using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

// Controlled render states exercise actual WPF menu controls. They do not
// qualify a model, bypass the controller, or claim physical input coverage.
[Collection("Desktop")]
public sealed class OverlayTranslationMenuTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task UnreleasedMissingOrMalformedActionsPreserveTheExistingMenu() => desktop.RunAsync(() =>
    {
        var (overlay, session, context) = OpenPreview();
        foreach (var actions in new TranslationMenuState?[] { null, Actions() with
        {
            StyleReleased = false,
            AlternativeReleased = false
        }, Actions() with { SettingsRevision = -1 }, Actions() with { ApplicationStyle = (TranslationStyle)99 } })
        {
            overlay.Render(session, new(), false, context: context with
            {
                TranslationActions = actions
            });
            var menu = OpenMenu(overlay);
            try
            {
                string[] labels = menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
                Assert.Contains("复制译文", labels);
                Assert.Contains("重新翻译", labels);
                Assert.Contains("不要在这个应用中显示", labels);
                Assert.Contains("隐藏当前翻译", labels);
                Assert.DoesNotContain("当前应用风格", labels);
                Assert.DoesNotContain("换种说法", labels);
                Assert.DoesNotContain("加入术语表", labels);
            }
            finally { menu.IsOpen = false; }
        }
    });

    [Theory]
    [InlineData(null, "跟随全局设置")]
    [InlineData(TranslationStyle.Natural, "自然")]
    [InlineData(TranslationStyle.Formal, "正式")]
    public Task MenuChecksTheApplicationOverrideAndFreezesTheOriginalSaveContext(TranslationStyle? applicationStyle, string selected)
        => desktop.RunAsync(async () =>
    {
        var (overlay, session, context) = OpenPreview();
        var settings = new UserSettings(Styles: new(TranslationStyle.Formal));
        context = context with
        {
            TranslationActions = Actions() with
            {
                ApplicationStyle = applicationStyle
            }
        };
        overlay.Render(session, settings, false, context: context);
        var received = new TaskCompletionSource<SessionStyleRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        int saves = 0;
        var completion = new TaskCompletionSource<StyleChangeResult>();
        overlay.StyleRequested = request =>
        {
            saves++;
            received.TrySetResult(request);
            return completion.Task;
        };
        var menu = OpenMenu(overlay);
        try
        {
            var submenu = Item(menu, "当前应用风格");
            submenu.IsSubmenuOpen = true;
            menu.UpdateLayout();
            var choices = submenu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(new[] { "跟随全局设置", "自然", "正式" }, choices.Select(item => (string)item.Header));
            Assert.Equal(selected, Assert.Single(choices, item => item.IsChecked).Header);
            var next = context with
            {
                Token = context.Token with
                {
                    Revision = context.Token.Revision + 1
                },
                TranslationActions = Actions() with
                {
                    SettingsRevision = 9,
                    ApplicationStyle = TranslationStyle.Natural
                }
            };
            overlay.Render(session, settings, false, context: next);
            var choice = choices.Single(item => Equals(item.Header, "正式"));
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(choice).GetPattern(PatternInterface.Invoke));
            invoke.Invoke();
            Assert.Equal(new SessionStyleRequest(context.Token, 4, TranslationStyle.Formal),
                await received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Equal(selected, Assert.Single(choices, item => item.IsChecked).Header);
            choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(choice.IsEnabled);
            Assert.Equal(1, saves);
            menu.IsOpen = false;
            completion.SetResult(new(false, "style-stale"));
            await completion.Task;
            menu = OpenMenu(overlay);
            Assert.Equal("自然", Assert.Single(Item(menu, "当前应用风格").Items.OfType<MenuItem>(), item => item.IsChecked).Header);
            Assert.DoesNotContain(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "style-stale");
        }
        finally { menu.IsOpen = false; }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task InheritanceAndAlternativeFollowTheirIndependentSharedEligibility(bool canInherit) => desktop.RunAsync(() =>
    {
        var (overlay, session, context) = OpenPreview();
        context = context with
        {
            TranslationActions = Actions() with
            {
                CanChangeStyle = false,
                CanInheritStyle = canInherit,
                CanRequestAlternative = false,
                ApplicationStyle = TranslationStyle.Formal,
                StyleUnavailableReason = "当前模型暂不支持风格",
                AlternativeUnavailableReason = "请先重新翻译"
            }
        };
        overlay.Render(session, new(), false, context: context);
        var requests = new List<SessionStyleRequest>();
        int alternatives = 0;
        overlay.StyleRequested = request =>
        {
            requests.Add(request);
            return Task.FromResult(new StyleChangeResult(false, "style-stale"));
        };
        overlay.AlternativeRequested = _ => alternatives++;
        var menu = OpenMenu(overlay);
        try
        {
            var choices = Item(menu, "当前应用风格").Items.OfType<MenuItem>().ToArray();
            foreach (var choice in choices.Where(item => !Equals(item.Header, "跟随全局设置")))
            {
                Assert.False(choice.IsEnabled);
                Assert.Equal("当前模型暂不支持风格", choice.ToolTip);
                choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            var alternative = Item(menu, "换种说法");
            Assert.False(alternative.IsEnabled);
            Assert.Equal("请先重新翻译", alternative.ToolTip);
            alternative.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Empty(requests);
            Assert.Equal(0, alternatives);
            var inherit = choices.Single(item => Equals(item.Header, "跟随全局设置"));
            Assert.Equal(canInherit, inherit.IsEnabled);
            inherit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (canInherit)
                Assert.Equal(new SessionStyleRequest(context.Token, 4, null), Assert.Single(requests));
            else
                Assert.Empty(requests);
        }
        finally { menu.IsOpen = false; }
    });

    [Fact]
    public Task AlternativeKeepsItsMenuTokenAndFeedbackNeverOverridesExistingStatus() => desktop.RunAsync(() =>
    {
        var (overlay, session, context) = OpenPreview();
        context = context with
        {
            TranslationActions = Actions() with
            {
                Feedback = "暂时没有不同的译法"
            }
        };
        overlay.Render(session, new(), false, statusOverride: "输入法候选中", context: context);
        Assert.Contains(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "输入法候选中");
        Assert.DoesNotContain(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "暂时没有不同的译法");
        var tokens = new List<SessionViewToken>();
        overlay.AlternativeRequested = tokens.Add;
        var menu = OpenMenu(overlay);
        try
        {
            Assert.False(Item(menu, "暂时没有不同的译法").IsEnabled);
            overlay.Render(session, new(), false, context: context with
            {
                Token = context.Token with
                {
                    Revision = context.Token.Revision + 1
                },
                TranslationActions = Actions()
            });
            Item(menu, "换种说法").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(context.Token, Assert.Single(tokens));
            Assert.Contains(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "译文就绪");
            Assert.DoesNotContain(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "暂时没有不同的译法");
        }
        finally { menu.IsOpen = false; }
        overlay.Render(session, new(), false, statusOverride: "", context: context);
        Assert.Contains(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "暂时没有不同的译法");
    });

    private static MenuItem Item(ContextMenu menu, string label) => menu.Items.OfType<MenuItem>()
        .Single(item => Equals(item.Header, label));

    private static TranslationMenuState Actions() => new(4, true, true, true, null, null, null, true, true, null, null);

    private static (OverlayWindow Overlay, Session Session, SessionViewContext Context) OpenPreview()
    {
        var input = new TextBox { Text = "Synthetic source", Margin = new(20) };
        var work = SystemParameters.WorkArea;
        var window = new Window { Content = input, Width = 600, Height = 150, Left = work.Left + 40, Top = work.Top + 40 };
        window.Show();
        window.UpdateLayout();
        Assert.True(window.Activate());
        Assert.True(input.Focus());
        var point = input.PointToScreen(new(0, 0));
        var target = new WindowsTarget(Environment.ProcessId, 1, (long)new WindowInteropHelper(window).Handle,
            1, "translation-menu-fixture", "source", Guid.NewGuid());
        var source = new InputSnapshot(target, input.Text, new(0, 0), new(point.X, point.Y, input.ActualWidth, input.ActualHeight),
            true, FieldAccess.Supported, ImeState.Clear, 1000, 1, 1, WriteSupported: true);
        var session = new Session(new("auto", "en"));
        session.Observe(source, FocusMember.Source, 1000);
        Assert.True(session.Complete(Assert.IsType<TranslationJob>(session.TakeTranslationJob(1300)), "Synthetic preview"));
        var context = new SessionViewContext(new(source.Target, Guid.NewGuid(), 7), SessionShortcutBindings.Default, true, Actions());
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        overlay.Render(session, new(), false, context: context);
        overlay.UpdateLayout();
        return (overlay, session, context);
    }

    private static ContextMenu OpenMenu(OverlayWindow overlay)
    {
        Descendants(overlay).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "更多翻译操作")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return PresentationSource.CurrentSources.OfType<HwndSource>().Select(source => source.RootVisual)
            .OfType<DependencyObject>().SelectMany(root => Descendants(root).Prepend(root))
            .OfType<ContextMenu>().Single(menu => menu.IsOpen);
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
