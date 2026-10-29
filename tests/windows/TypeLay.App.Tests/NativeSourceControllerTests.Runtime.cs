using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RuntimeRejectsMissingSourceEligibilityRegardlessOfAutomationOrBrokerInjection(bool automationTest) => desktop.RunAsync(async () =>
    {
        // A real source-capable Broker cannot grant authority without the exact
        // capture proof. Withhold only that proof at the existing transport seam;
        // neither automation nor the injected Broker can replace it.
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        await native.Prepare(native.A, "你好", new(0, 2));
        using var broker = new FixtureBroker(native)
        {
            TransformSourceEligibility = eligibility =>
            {
                Assert.NotNull(eligibility);
                return null;
            }
        };
        InputSnapshot? captured = null;
        broker.ObserveResponse = (request, response) =>
        {
            if (request.Op == "capture" && response.Focus == FocusMember.Source)
                captured = response.Snapshot;
        };
        var overlay = new OverlayWindow(automationTest: true);
        var support = new WindowsApplicationSupport();
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.DefaultRuntime." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            using var controller = WindowsRuntime.CreateSession(settings, new Translation(), overlay,
                automationTest: automationTest, broker: broker, support: support);
            await support.CompletePendingAsync().WaitAsync(native.Token);
            Assert.False(support.Browser!.MutationUnavailable);
            await controller.StartAsync().WaitAsync(native.Token);
            await Until(() => overlay.IsVisible && RuntimeTranslation(overlay) == "Hello", native.Token);
            int firstCaptures = broker.Captures;
            await Until(() => broker.Captures > firstCaptures, native.Token);
            Assert.NotNull(captured);
            Assert.Equal((long)native.A, captured.Target.AsWindows().ControlHwnd);
            Assert.True(captured.WriteSupported); // Raw Broker availability is not the missing capture proof.
            Assert.False(settings.Value.ExperimentalWriteback);
            Assert.False(RuntimeShowsAcceptance(overlay));
            Assert.Contains(RuntimeLabels(overlay), label => label.IsVisible
                && AutomationProperties.GetAutomationId(label) == "TranslationStage" && label.Text == "仅预览"
                && AutomationProperties.GetHelpText(label).Contains("交还原应用", StringComparison.Ordinal));
            var row = support.Read(settings.Value).Rows.Single(row => row.Id == "desktop-text");
            Assert.True(row.Capabilities.Preview);
            Assert.False(row.Capabilities.TabAccept);
            Assert.Equal(0, broker.Installs);
            Assert.Null(broker.Installation);
            Assert.Equal(0, broker.CompletionTransfers);
            Assert.Equal(0, broker.Writes);
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal((native.Root, native.A), N.Focused());
            native.AssertZeroSubmits();
        }
        finally
        {
            overlay.Close();
            Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task RealNativeMenuCanClearADormantOverrideWithoutReleasingStyle() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        await native.Prepare(native.A, "你好", new(0, 2));
        using var sourceProcess = Process.GetProcessById(native.FixtureProcessId);
        string application = sourceProcess.ProcessName.ToLowerInvariant();
        using var broker = new FixtureBroker(native);
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.DormantStyleMenu." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            // An existing installation may retain a dormant override. Author that
            // fixture input before opening the real store; ordinary Update must
            // continue rejecting feature changes outside the session controller.
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new UserSettings
            {
                Styles = new(TranslationStyle.Formal, new Dictionary<string, TranslationStyle>
                {
                    [application] = TranslationStyle.Natural
                })
            }), native.Token);
            var settings = new SettingsStore(path);
            using var controller = WindowsRuntime.CreateSession(settings, new Translation(), overlay, broker: broker);
            await controller.StartAsync().WaitAsync(native.Token);
            await Until(() => overlay.IsVisible && RuntimeTranslation(overlay) == "Hello", native.Token);
            var eligibility = controller.GetStyleSettingsState(StyleScope.CurrentApplication, null);
            Assert.False(eligibility.IsReleased);
            Assert.True(eligibility.CanChange);
            Assert.Equal(application, eligibility.Application);
            long revision = settings.Revision;
            var more = RuntimeDescendants(overlay).OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == "更多翻译操作");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = Assert.IsType<ContextMenu>(more.ContextMenu);
            try
            {
                var tone = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "当前应用风格"));
                var inherit = tone.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "跟随全局设置"));
                Assert.True(inherit.IsEnabled);
                Assert.All(tone.Items.OfType<MenuItem>().Where(item => item != inherit), item => Assert.False(item.IsEnabled));
                tone.IsSubmenuOpen = true;
                menu.UpdateLayout();
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler onClosed = (_, _) => closed.TrySetResult();
                menu.Closed += onClosed;
                try
                {
                    var invoke = Assert.IsAssignableFrom<IInvokeProvider>(
                        new MenuItemAutomationPeer(inherit).GetPattern(PatternInterface.Invoke));
                    invoke.Invoke();
                    await closed.Task.WaitAsync(native.Token);
                    await Until(() => settings.Revision > revision, native.Token);
                }
                finally { menu.Closed -= onClosed; }
                Assert.Empty(new SettingsStore(path).Value.Styles!.Applications!);
                Assert.Equal(TranslationStyle.Formal, settings.Value.Styles!.Default);
                Assert.False(controller.GetStyleSettingsState().IsReleased);
                Assert.Equal(0, broker.Writes);
                Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(0, 2), N.ReadSelection(native.A));
                Assert.Equal((native.Root, native.A), N.Focused());
                native.AssertZeroSubmits();
            }
            finally { menu.IsOpen = false; }
        }
        finally
        {
            overlay.Close();
            Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task OrderedAdapterPreservesDirectWriteWithoutGrantingAutomaticEligibility() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        await native.Prepare(native.A, "你好", new(0, 2));
        using var hook = new KeyboardHook();
        await Until(() => hook.Ready, native.Token);
        using var broker = new FixtureBroker(native);
        using var input = new WindowsInputService(broker, 0, monitor: hook,
            sourceMessageAcceptance: true, orderedSourceMessageWitness: true);
        var capture = await input.CaptureAsync(new(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch,
            null, [], true, new(hook.CommandGeneration, SessionShortcutBindings.Default)));
        var source = Assert.IsType<InputSnapshot>(capture.Snapshot);
        Assert.True(source.WriteSupported);
        Assert.True(source.DirectWriteSupported);
        Assert.Equal(0, capture.SessionShortcuts!.SourceLocalAcceptanceVersion);
        var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, source, source.TranslationScope,
            "Hello", "Hello", 5);
        var receipt = await input.WriteDirectAsync(plan, []);
        Assert.Equal(WriteStatus.Verified, receipt.Status);
        Assert.Equal("Hello", receipt.VerifiedSnapshot!.Text);
        Assert.Equal("Hello", N.ReadText(native.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(5, 5), N.ReadSelection(native.A));
        Assert.Equal(0, broker.Installs);
        Assert.Equal(1, broker.Writes);
        native.AssertZeroSubmits();
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public Task OrderedCaptureRejectsMissingOrMismatchedEligibilityEvenWithTheLegacySetting(int mismatch) => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        await native.Prepare(native.A, "你好", new(0, 2));
        using var hook = new KeyboardHook();
        await Until(() => hook.Ready, native.Token);
        using var broker = new FixtureBroker(native)
        {
            TransformSourceEligibility = eligibility =>
            {
                Assert.NotNull(eligibility); // The real source was eligible before the transport mismatch.
                return mismatch switch
                {
                    0 => null,
                    1 => eligibility with { Version = 0 },
                    2 => eligibility with { Version = 2 },
                    3 => eligibility with { Target = eligibility.Target with { Lifetime = Guid.NewGuid() } },
                    4 => eligibility with { ObservedAtMs = eligibility.ObservedAtMs - 1 },
                    5 => eligibility with { InputEpoch = eligibility.InputEpoch + 1 },
                    _ => eligibility with { FocusEpoch = eligibility.FocusEpoch + 1 }
                };
            }
        };
        var support = new WindowsApplicationSupport();
        support.Bind(broker, hook);
        using var input = new WindowsInputService(broker, 0, support, hook,
            sourceMessageAcceptance: true, orderedSourceMessageWitness: true);
        var capture = await input.CaptureAsync(new(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch,
            null, [], false, new(hook.CommandGeneration, SessionShortcutBindings.Default)));
        Assert.NotNull(capture.Snapshot);
        Assert.False(capture.Snapshot.WriteSupported);
        Assert.Equal(0, capture.SessionShortcuts!.SourceLocalAcceptanceVersion);
        Assert.False(support.Read(new UserSettings { ExperimentalWriteback = true }).Rows
            .Single(row => row.Id == "desktop-text").Capabilities.TabAccept);
        Assert.Equal(0, broker.Installs);
        Assert.Equal(0, broker.Writes);
        Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
        native.AssertZeroSubmits();
    });

    [Fact]
    public Task SourceCapabilityPresentationRequiresTheCurrentTargetsActualCaptureAcknowledgement() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        await native.Prepare(native.A, "你好", new(0, 2));
        using var hook = new KeyboardHook();
        await Until(() => hook.Ready, native.Token);
        using var broker = new FixtureBroker(native);
        var support = new WindowsApplicationSupport();
        support.Bind(broker, hook);
        using var input = new WindowsInputService(broker, 0, support, hook,
            sourceMessageAcceptance: true, orderedSourceMessageWitness: true);
        var settings = new UserSettings();
        var first = await Capture();
        var firstAck = Assert.IsType<SessionShortcutAcknowledgement>(first.SessionShortcuts);
        Assert.Equal(1, firstAck.SourceLocalAcceptanceVersion);
        Assert.True(Read().TabAccept);
        await native.Prepare(native.B, "另一个输入框", new(0, 6));
        // Only the presentation seam receives these mismatches. No controller,
        // permit or native source-install is created by this capability check.
        foreach (int mismatch in Enumerable.Range(0, 5))
        {
            var capture = await Capture();
            var acknowledgement = Assert.IsType<SessionShortcutAcknowledgement>(capture.SessionShortcuts);
            Assert.NotEqual(firstAck.Target, acknowledgement.Target);
            Assert.Equal((long)native.B, acknowledgement.Target.AsWindows().ControlHwnd);
            Assert.True(Read().TabAccept); // Establish fresh evidence before each refusal.
            var rejected = mismatch switch
            {
                0 => acknowledgement with { Target = firstAck.Target },
                1 => acknowledgement with { SourceLocalAcceptanceVersion = 0 },
                2 => acknowledgement with { SourceLocalAcceptanceVersion = 2 },
                3 => acknowledgement with { SourceReady = false },
                _ => null
            };
            support.Observe(capture with
            {
                SessionShortcuts = rejected
            }, broker.Available,
                hook.CanInterceptAcceptance, capture.Snapshot!.ObservedAtMs, support.CheckGeneration);
            Assert.True(Read().Preview);
            Assert.False(Read().TabAccept);
        }
        await Capture();
        Assert.True(Read().TabAccept);
        hook.ConfigureShortcuts(false, settings.WakeShortcut, settings.DirectShortcut,
            SessionShortcutBindings.Restore("F7", "Esc"), true);
        Assert.False(Read().TabAccept);
        hook.ConfigureShortcuts(false, settings.WakeShortcut, settings.DirectShortcut, SessionShortcutBindings.Default, true);
        Assert.False(Read().TabAccept); // Returning to equal keys cannot revive the old generation.
        await Capture();
        Assert.True(Read().TabAccept);
        support.Disconnected();
        Assert.Equal(new ApplicationSupportCapabilities(), Read());
        Assert.False(settings.ExperimentalWriteback);
        Assert.Equal(0, broker.Installs);
        Assert.Equal(0, broker.CompletionTransfers);
        Assert.Equal(0, broker.Writes);
        Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
        Assert.Equal("另一个输入框", N.ReadText(native.B, Limits.MaxTextUnits));
        native.AssertZeroSubmits();

        Task<InputCapture> Capture() => input.CaptureAsync(new(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch,
            null, [], false, new(hook.CommandGeneration, SessionShortcutBindings.Default)));
        ApplicationSupportCapabilities Read() => support.Read(settings).Rows.Single(row => row.Id == "desktop-text").Capabilities;
    });

    private static bool RuntimeShowsAcceptance(OverlayWindow overlay) => overlay.IsVisible
        && RuntimeLabels(overlay).Any(label => label.IsVisible
            && AutomationProperties.GetAutomationId(label) == "TranslationAcceptHint"
            && AutomationProperties.GetName(label) == "接受译文" && label.Text == "接受");
    private static string? RuntimeTranslation(OverlayWindow overlay) =>
        RuntimeDescendants(overlay).OfType<TextBox>().SingleOrDefault()?.Text;
    private static IEnumerable<TextBlock> RuntimeLabels(OverlayWindow overlay) => RuntimeDescendants(overlay).OfType<TextBlock>();
    private static IEnumerable<DependencyObject> RuntimeDescendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in RuntimeDescendants(child))
                yield return descendant;
        }
    }
}
