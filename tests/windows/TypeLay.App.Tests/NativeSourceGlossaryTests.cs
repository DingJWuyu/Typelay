using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed partial class NativeSourceGlossaryTests(WpfDesktopFixture desktop)
{
    private static async Task InvokeGlossaryCommandAsync(MenuItem command, CancellationToken cancellation)
    {
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler observe = (_, _) => clicked.TrySetResult();
        command.Click += observe;
        try
        {
            Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(command).GetPattern(PatternInterface.Invoke)).Invoke();
            await clicked.Task.WaitAsync(cancellation);
        }
        finally { command.Click -= observe; }
    }

    // The WPF menu/fields and foreground hook are real; source reads and returns
    // cross production Broker IPC into a separate-process stock Edit. Runtime's
    // persistence/quality checks are covered separately by InlineGlossaryTests.
    public static IEnumerable<object[]> SourceFormCases()
    {
        foreach (string action in new[] { "save", "cancel", "foreign", "source-changed", "selection-changed", "readonly",
            "wrong-source-field", "return-root", "return-second", "foreign-pending", "foreign-back-pending", "close-pending", "config-pending",
            "barrier-save", "barrier-cancel", "barrier-close", "barrier-config", "barrier-cookie",
            "barrier-source", "barrier-timeout", "barrier-wrong-field", "barrier-foreign" })
            foreach (bool editedPending in new[] { false, true })
                yield return new object[] { action, editedPending };
    }

    [Theory]
    [MemberData(nameof(SourceFormCases))]
    public Task SourceFormUsesRealForegroundAndBrokerWithoutWritingSource(string action, bool editedPending) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.SourceGlossary." + Guid.NewGuid().ToString("N"));
        string? returnFocus = action == "barrier-wrong-field" ? "second"
            : action.StartsWith("return-", StringComparison.Ordinal) ? action[7..] : null;
        var native = await ProductionForegroundWriteTests.Desktop.Start(returnFocus: returnFocus, returnTrace: Path.Combine(directory, "return"));
        Exception? scenarioFailure = null;
        try
        {
            var overlay = new OverlayWindow(clientAreaAnimation: () => false);
            using var monitor = new KeyboardHook(allowInjectedInput: true, editorHwnd: overlay.Handle);
            while (!monitor.ShortcutsReady)
                await Task.Delay(10, native.Token);
            overlay.ConfigureOrdinaryInput(monitor);
            Directory.CreateDirectory(directory);
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            var broker = new FixtureBroker(native);
            using var input = new WindowsInputService(broker, overlay.Handle, monitor: monitor);
            overlay.ConfigureSourceGlossary(input, settings);
            var view = new WindowsSessionView(overlay, new OverlayWindow(clientAreaAnimation: () => false));
            await native.Prepare(native.A, "A🙂你好Z", new(3, 5));
            InputContext Context(TargetId? target = null) => new(monitor.UserEpoch, monitor.ForegroundEpoch,
                monitor.SourceUserEpoch, target, [], true, new(monitor.CommandGeneration, SessionShortcutBindings.Default));
            var captured = await input.CaptureAsync(Context());
            var source = Assert.IsType<InputSnapshot>(captured.Snapshot);
            Assert.Equal(ImeState.Clear, source.Ime); // Actual native IME, not a fixture-supplied value.
            Assert.Equal("win32-edit-transaction-v2", source.Target.AsWindows().Provider);
            nuint sourceUndo = N.Send(native.A, 0x00c6); // EM_CANUNDO, unchanged by the auxiliary form.
            var session = new Session(new("auto", "en"));
            session.Observe(source, FocusMember.Source, source.ObservedAtMs);
            Assert.True(session.Complete(Assert.IsType<TranslationJob>(session.TakeTranslationJob(source.ObservedAtMs + 300)), "Hello"));
            if (editedPending)
            {
                Assert.True(session.BeginEdit());
                session.ChangeDraft("Edited Hello");
                Assert.True(session.RequestSourceReturn());
                Assert.True(session.ConfirmSourceReturn(source, source.ObservedAtMs));
            }
            Assert.Equal(editedPending ? SessionState.EditedTranslationPending : SessionState.ValidTranslation, session.State);
            string candidate = session.Candidate;
            var token = new SessionViewToken(source.Target, monitor.CommandGeneration, 7);
            var context = new SessionViewContext(token, SessionShortcutBindings.Default, false,
                new(settings.Revision, false, false, false, null, null, null, false, false, null, null, true, true, null));
            view.Render(session, settings.Value, false, context: context); // B-01 write disabled.
            overlay.UpdateLayout();
            Assert.True(view.SupportsSourceGlossaryEntry(token));
            SessionGlossaryEntry? entry = null;
            long initialEpoch = monitor.WindowEpoch;
            view.BeginGlossaryRequested = requested =>
            {
                Assert.Equal(token, requested);
                entry = new(Guid.NewGuid(), token, settings.Revision, [], true);
                Assert.True(view.IsGlossarySourceWindowCurrent(entry, initialEpoch, monitor.WindowEpoch));
                return entry;
            };
            var saved = new TaskCompletionSource<GlossaryChangeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.AddGlossaryRequested = async _ =>
            {
                bool returned = await view.ReturnGlossaryToSourceAsync(entry!);
                if (returned)
                {
                    var fresh = await input.CaptureAsync(Context(source.Target));
                    Assert.NotNull(fresh.Snapshot);
                    Assert.Equal(source.Target, fresh.Snapshot.Target);
                    Assert.Equal(source.Text, fresh.Snapshot.Text);
                    Assert.Equal(source.Selection, fresh.Snapshot.Selection);
                    Assert.Equal(source.InputEpoch, fresh.Snapshot.InputEpoch);
                    Assert.Equal(ImeState.Clear, fresh.Snapshot.Ime);
                }
                var result = new GlossaryChangeResult(returned, returned ? "glossary-saved" : "glossary-entry-return-failed");
                saved.TrySetResult(result);
                return result;
            };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.CloseGlossaryRequested = _ => closed.TrySetResult();
            if (action == "wrong-source-field")
                await native.Focus(native.B);
            Button(overlay, "更多翻译操作").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var menu = PresentationSource.CurrentSources.OfType<HwndSource>().Select(item => item.RootVisual)
                .OfType<DependencyObject>().SelectMany(root => Children(root).Prepend(root))
                .OfType<ContextMenu>().Single(item => item.IsOpen);
            var command = menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "MoreGlossary");
            Assert.True(command.IsEnabled);
            await InvokeGlossaryCommandAsync(command, native.Token);
            Assert.False(menu.IsOpen);
            if (action == "wrong-source-field")
            {
                Assert.Null(entry);
                Assert.Null(view.GlossaryEntryFocus);
                Assert.Equal((native.Root, native.B), N.Focused());
                Assert.Equal(source.Text, N.ReadText(native.A, Limits.MaxTextUnits));
                Assert.Equal(source.Selection, N.ReadSelection(native.A));
                return;
            }
            while (view.GlossaryEntryFocus is null && !closed.Task.IsCompleted)
                await Task.Delay(10, native.Token);
            Assert.NotNull(view.GlossaryEntryFocus);
            Assert.Equal(initialEpoch + 1, monitor.WindowEpoch);
            Assert.True(view.IsGlossarySourceWindowCurrent(entry!, initialEpoch, monitor.WindowEpoch));
            Children(overlay).OfType<TextBox>().Single(item => AutomationProperties.GetName(item) == "术语源词").Text = "你好";
            Children(overlay).OfType<TextBox>().Single(item => AutomationProperties.GetName(item) == "术语译词").Text = "Hello";
            if (action.StartsWith("barrier-", StringComparison.Ordinal))
            {
                using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(native.FixtureProcessId, "entered"));
                using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(native.FixtureProcessId, "release"));
                using var drained = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(native.FixtureProcessId, "drained"));
                Assert.False(drained.WaitOne(0));
                Assert.Equal((nuint)1, N.Send(native.Root, N.FixtureArmNullBarrier, action == "barrier-source" ? 1u : 0u));
                nint expectedRoot = native.Root;
                try
                {
                    var save = Button(overlay, "保存术语");
                    Assert.True(save.Focus());
                    save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), native.Token),
                        $"The real original root did not enter its WM_NULL barrier; broker={broker.ReturnStatus}.");
                    Assert.False(saved.Task.IsCompleted);
                    if (action == "barrier-save")
                        Assert.True(view.IsGlossarySourceReturnPending(entry!));
                    if (action == "barrier-timeout")
                        Assert.False((await saved.Task.WaitAsync(native.Token)).Saved);
                    else
                    {
                        if (action == "barrier-cancel")
                            Button(overlay, "取消加入术语表").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        if (action == "barrier-close")
                            view.HideQuietly();
                        if (action == "barrier-config")
                            monitor.ConfigureSessionShortcuts(SessionShortcutBindings.Default);
                        if (action == "barrier-cookie")
                            Assert.NotEqual(nint.Zero, N.RemoveProp(native.A, "TypeLay.NativeInput.Lifetime.v2"));
                        if (action == "barrier-foreign")
                        {
                            var elsewhere = new Window { Content = new TextBox(), Width = 250, Height = 100 };
                            elsewhere.Show();
                            Assert.True(elsewhere.Activate());
                            Assert.True(((TextBox)elsewhere.Content).Focus());
                            expectedRoot = new WindowInteropHelper(elsewhere).Handle;
                        }
                        release.Set();
                        var result = await saved.Task.WaitAsync(native.Token);
                        Assert.True(result.Saved == (action == "barrier-save"),
                            $"{action}: {result.Code}; broker={broker.ReturnStatus}");
                    }
                }
                finally { release.Set(); }
                Assert.False(view.IsGlossarySourceReturnPending(entry!));
                // A rejected/canceled Broker return can finish before the source
                // GUI resumes its activation. Observe its existing posted marker
                // before asserting the final focus; never correct or retry it.
                var beforeDrain = N.ReadFocus();
                Assert.True(N.PostMessage(native.Root, N.FixtureGuiDrained, 0, 0));
                Assert.True(await Task.Run(() => drained.WaitOne(TimeSpan.FromSeconds(2)), native.Token),
                    $"The source GUI did not process its post-barrier marker; broker={broker.ReturnStatus}.");
                var afterDrain = N.ReadFocus();
                nint expectedControl = action == "barrier-wrong-field" ? native.B : native.A;
                Console.WriteLine($"glossary-return-drain: action={action}; editedPending={editedPending}; "
                    + $"rootBefore={beforeDrain.Root == expectedRoot}; fieldBefore={beforeDrain.Control == expectedControl}; "
                    + $"rootAfter={afterDrain.Root == expectedRoot}; fieldAfter={afterDrain.Control == expectedControl}; "
                    + $"guiBefore={beforeDrain.GuiThreadInfoSucceeded}; guiErrorBefore={beforeDrain.GuiThreadInfoError}; "
                    + $"guiAfter={afterDrain.GuiThreadInfoSucceeded}; guiErrorAfter={afterDrain.GuiThreadInfoError}; "
                    + $"broker={broker.ReturnStatus}");
                Assert.Equal(expectedRoot, Native.GetForegroundWindow());
                if (action != "barrier-foreign")
                    Assert.Equal((native.Root, expectedControl), (afterDrain.Root, afterDrain.Control));
            }
            else if (action.EndsWith("-pending", StringComparison.Ordinal))
            {
                // Hold the actual stock field GUI thread while a real Broker
                // return is in flight. No fake IME/capture/focus result is used.
                using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(native.FixtureProcessId, "entered"));
                using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(native.FixtureProcessId, "release"));
                Assert.True(N.PostMessage(native.Root, N.FixtureBlockGui, 0, 0));
                Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), native.Token));
                nint expectedForeground = overlay.Handle;
                try
                {
                    var save = Button(overlay, "保存术语");
                    Assert.True(save.Focus());
                    save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await broker.ReturnDispatched.Task.WaitAsync(native.Token);
                    Assert.False(saved.Task.IsCompleted);
                    if (action == "config-pending")
                        monitor.ConfigureSessionShortcuts(SessionShortcutBindings.Default);
                    else
                    {
                        var elsewhere = new Window { Content = new TextBox(), Width = 250, Height = 100 };
                        elsewhere.Show();
                        Assert.True(elsewhere.Activate());
                        Assert.True(((TextBox)elsewhere.Content).Focus());
                        expectedForeground = new WindowInteropHelper(elsewhere).Handle;
                        while (view.IsGlossarySourceWindowCurrent(entry!, initialEpoch, monitor.WindowEpoch))
                            await Task.Delay(10, native.Token);
                        if (action == "foreign-back-pending")
                        {
                            Assert.True(overlay.Activate());
                            expectedForeground = overlay.Handle;
                        }
                        if (action == "close-pending")
                            view.HideQuietly();
                    }
                }
                finally { release.Set(); }
                Assert.False((await saved.Task.WaitAsync(native.Token)).Saved);
                Assert.Equal(expectedForeground, Native.GetForegroundWindow());
            }
            else if (action == "foreign")
            {
                var elsewhere = new Window { Content = new TextBox(), Width = 250, Height = 100 };
                elsewhere.Show();
                Assert.True(elsewhere.Activate());
                Assert.True(((TextBox)elsewhere.Content).Focus());
                Assert.False(await view.ReturnGlossaryToSourceAsync(entry!));
                Assert.Equal(new WindowInteropHelper(elsewhere).Handle, Native.GetForegroundWindow());
            }
            else if (action == "cancel")
            {
                var cancel = Button(overlay, "取消加入术语表");
                Assert.True(cancel.Focus());
                cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await closed.Task.WaitAsync(native.Token);
                Assert.Equal((native.Root, native.A), N.Focused());
                Assert.False(saved.Task.IsCompleted);
            }
            else
            {
                if (action == "source-changed")
                    N.SendString(native.A, N.WM_SETTEXT, "Changed while form owns focus");
                if (action == "selection-changed")
                    N.Send(native.A, N.EM_SETSEL, 0, 0);
                if (action == "readonly")
                    N.Send(native.A, 0x00cf, 1);
                var save = Button(overlay, "保存术语");
                Assert.True(save.Focus());
                save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var outcome = await saved.Task.WaitAsync(native.Token);
                Assert.True((action == "save") == outcome.Saved,
                    $"{action}: {outcome.Code}; broker={broker.ReturnStatus}; focus={N.Focused()}");
                if (action == "save")
                {
                    Assert.Equal((native.Root, native.A), N.Focused());
                    Assert.Equal(initialEpoch + 2, monitor.WindowEpoch);
                }
                else if (returnFocus is not null)
                    Assert.Equal((native.Root, returnFocus == "root" ? native.Root : native.B), N.Focused());
                else
                    Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            }
            Assert.Equal(action is "source-changed" or "barrier-source" ? "Changed while form owns focus" : source.Text,
                N.ReadText(native.A, Limits.MaxTextUnits));
            if (action is not ("source-changed" or "barrier-source"))
            {
                Assert.Equal(action == "selection-changed" ? new TextRange(0, 0) : source.Selection, N.ReadSelection(native.A));
                Assert.Equal(sourceUndo, N.Send(native.A, 0x00c6));
            }
            native.AssertZeroSubmits();
            Assert.Equal(0, broker.SourceWrites);
            Assert.Equal(candidate, session.Candidate);
            Assert.Equal(editedPending ? SessionState.EditedTranslationPending : SessionState.ValidTranslation, session.State);
            view.HideQuietly();
            Assert.False(view.IsGlossarySourceWindowCurrent(entry!, initialEpoch, initialEpoch));
        }
        catch (Exception error)
        {
            scenarioFailure = error;
            throw;
        }
        finally
        {
            // The Broker may append its terminal trace after replying. Retire
            // its real process before reading or deleting the trace directory.
            Exception? cleanupFailure = null;
            try
            {
                await native.DisposeAsync();
            }
            catch (Exception error) { cleanupFailure = error; }
            WriteReturnTrace(directory);
            if (cleanupFailure is not null)
            {
                if (scenarioFailure is not null)
                    throw new AggregateException("Source glossary scenario and process cleanup failed.", scenarioFailure, cleanupFailure);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
            // Keep the trace when the scenario failed; cleanup must not mask
            // its original assertion or destroy the corresponding evidence.
            if (scenarioFailure is null)
                Directory.Delete(directory, true);
        }
    });

    [Theory]
    [InlineData("no-control")]
    [InlineData("unknown-ime")]
    [InlineData("uia-without-native-lifetime")]
    public Task SourceCapabilityCannotUpgradeUnprovenFieldMetadata(string missing) => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        await native.Prepare(native.A, "Source", new(0, 3));
        var captured = await native.Capture();
        var identity = captured.Target.AsWindows();
        var source = missing switch
        {
            "no-control" => captured with { Target = (identity with { ControlHwnd = 0 }).Id },
            "unknown-ime" => captured with { Ime = ImeState.Unknown },
            _ => captured with { Target = (identity with { Provider = "uia-value-text-v2" }).Id }
        };
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        using var monitor = new KeyboardHook(editorHwnd: overlay.Handle);
        overlay.ConfigureOrdinaryInput(monitor);
        using var input = new WindowsInputService(new FixtureBroker(native), overlay.Handle, monitor: monitor);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.GlossaryCapability." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            overlay.ConfigureSourceGlossary(input, settings);
            var view = new WindowsSessionView(overlay, new OverlayWindow(clientAreaAnimation: () => false));
            var session = new Session(new("auto", "en"));
            session.Observe(source, FocusMember.Source, source.ObservedAtMs);
            var token = new SessionViewToken(source.Target, monitor.CommandGeneration, 7);
            view.Render(session, settings.Value, false, context: new(token, SessionShortcutBindings.Default, false));
            Assert.False(view.SupportsSourceGlossaryEntry(token));
            Assert.Equal(captured.Text, N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(captured.Selection, N.ReadSelection(native.A));
        }
        finally { Directory.Delete(directory, true); }
    });

    private sealed class FixtureBroker(ProductionForegroundWriteTests.Desktop native) : IInputBroker
    {
        public TaskCompletionSource ReturnDispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SourceWrites
        {
            get; private set;
        }
        public string? ReturnStatus
        {
            get; private set;
        }
        public bool Available => true;
        public int ProcessId => native.BrokerProcessId;
        public Task StartAsync() => Task.CompletedTask;
        public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
        {
            if (request.Op is "write" or "direct-write")
                SourceWrites++;
            var response = await native.Call(request, request.Op == "return-glossary" ? () => ReturnDispatched.TrySetResult() : null);
            if (request.Op == "return-glossary")
                ReturnStatus = response.Status;
            return response;
        }
        public void Dispose()
        {
        } // The enclosing fixture owns its real child process.
    }
    private static void WriteReturnTrace(string directory)
    {
        try
        {
            string path = Path.Combine(directory, "return.broker.jsonl");
            if (File.Exists(path))
                TestContext.Current.TestOutputHelper?.WriteLine(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private static Button Button(OverlayWindow overlay, string name) => Children(overlay).OfType<Button>()
        .Single(item => AutomationProperties.GetName(item) == name);
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Children(child))
                yield return nested;
        }
    }
}
