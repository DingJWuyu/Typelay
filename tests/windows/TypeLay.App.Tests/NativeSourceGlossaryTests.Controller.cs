using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceGlossaryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RealControllerPersistsAnExplicitRuleAfterNativeReturnWithoutAWritePermit(bool applicationReleased) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.GlossaryController." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(returnTrace: Path.Combine(directory, "return"));
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        var monitor = new KeyboardHook(allowInjectedInput: true, editorHwnd: overlay.Handle);
        SessionController? controller = null;
        Action? reportFailure = null;
        try
        {
            while (!monitor.ShortcutsReady)
                await Task.Delay(10, native.Token);
            overlay.ConfigureOrdinaryInput(monitor);
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Mode = WorkMode.OnDemand,
                Source = "zh",
                Target = "ja"
            });
            Assert.False(settings.Value.ExperimentalWriteback);
            using var model = await GlossaryProtocolPeer.Start(directory, native.Token);
            var broker = new FixtureBroker(native);
            var input = new WindowsInputService(broker, overlay.Handle, monitor: monitor);
            overlay.ConfigureSourceGlossary(input, settings);
            var view = new WindowsSessionView(overlay, new OverlayWindow(clientAreaAnimation: () => false));
            // This is a deterministic model-protocol fixture, not a claim of
            // translation quality or permission to open the production gate.
            var release = new TranslationReleasePolicy(applicationReleased
                ? [new(TranslationOperation.Glossary, GlossaryProtocolPeer.Package, GlossaryProtocolPeer.Version, "zh_ja")]
                : []);
            controller = new(settings, model.Client, view, monitor, input, new DesktopLoop(), translationRelease: release,
                sourceInteraction: view);
            var expected = new GlossaryRule("你好", "こんにちは");
            ContextMenu? menu = null;
            bool commandClicked = false, sourceFocused = false, beginRequested = false, saveClicked = false;
            Guid? expectedForm = null, closedForm = null;
            long? brokerCaptureAgeAtBegin = null;
            string? settingsReasonAfterRejection = null;
            (nint Root, nint Control)? clickFocus = null;
            bool? sourceConfirmed = null;
            var confirm = view.ConfirmGlossarySource!;
            view.ConfirmGlossarySource = async token =>
            {
                bool confirmed = await confirm(token);
                sourceConfirmed = confirmed;
                return confirmed;
            };
            var begin = view.BeginGlossaryRequested!;
            view.BeginGlossaryRequested = token =>
            {
                beginRequested = true;
                brokerCaptureAgeAtBegin = WindowsInterruptClock.NowMs - broker.LastCapture?.Snapshot?.ObservedAtMs;
                var entry = begin(token);
                expectedForm = entry?.FormId;
                if (entry is null)
                    settingsReasonAfterRejection = controller.GetGlossarySettingsState().UnavailableReason;
                return entry;
            };
            var close = view.CloseGlossaryRequested!;
            view.CloseGlossaryRequested = form =>
            {
                closedForm = form;
                close(form);
            };
            overlay.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
            {
                if (e.NewFocus is TextBox field && AutomationProperties.GetName(field) == "术语源词")
                    sourceFocused = true;
            }), true);
            string stage = "prepare-source", condition = "controller started and source field prepared";
            reportFailure = () =>
            {
                var actual = N.ReadFocus();
                var focus = view.GlossaryEntryFocus;
                var field = Children(overlay).OfType<TextBox>().FirstOrDefault(item => AutomationProperties.GetName(item) == "术语源词");
                var focused = Keyboard.FocusedElement as DependencyObject;
                var capture = broker.LastCapture;
                WindowsTarget? captured = capture?.Snapshot is { } snapshot && WindowsTarget.TryDecode(snapshot.Target, out var target)
                    ? target : null;
                TestContext.Current.TestOutputHelper?.WriteLine("Glossary controller before cleanup: " + JsonSerializer.Serialize(new
                {
                    Stage = stage == "open-form" && (field is not null || sourceFocused) ? "confirm-focus" : stage,
                    Expected = condition, CommandClicked = commandClicked, MenuOpen = menu?.IsOpen,
                    SourceConfirmed = sourceConfirmed, BeginRequested = beginRequested, ExpectedFormId = expectedForm, ClosedFormId = closedForm,
                    BrokerCaptureAgeAtBeginMs = brokerCaptureAgeAtBegin, FreshnessLimitMs = Limits.ObservationMaxAgeMs,
                    SettingsReasonAfterRejection = settingsReasonAfterRejection,
                    RootAfterClick = clickFocus?.Root.ToInt64(), ControlAfterClick = clickFocus?.Control.ToInt64(),
                    SourceFieldPresent = field is not null, SourceFieldEverFocused = sourceFocused,
                    GlossaryEntryFocus = focus is not null, ActualFormId = focus?.FormId, ActualIme = focus?.Ime.ToString(),
                    SourceRoot = native.Root.ToInt64(), SourceField = native.A.ToInt64(), OverlayRoot = overlay.Handle.ToInt64(),
                    ActualRoot = actual.Root.ToInt64(), ActualControl = actual.Control.ToInt64(), actual.GuiThreadInfoSucceeded,
                    ActualWpfControl = focused?.GetType().Name,
                    SourceFieldHasFocus = field is not null && ReferenceEquals(field, focused),
                    LastCaptureStatus = capture?.Status, LastCaptureFocus = capture?.Focus.ToString(),
                    LastCaptureRoot = captured?.RootHwnd, LastCaptureField = captured?.ControlHwnd,
                    SaveClicked = saveClicked, RuleInSettings = settings.Value.Glossary?.Rules.Contains(expected) == true,
                    BrokerReturn = broker.ReturnStatus, broker.SourceWrites
                }));
            };
            await controller.StartAsync();
            await native.Prepare(native.A, "你好", new(0, 2));
            condition = "controller has observed the fixture application";
            while (controller.LastApplication is null)
                await Task.Delay(10, native.Token);
            condition = "visible overlay contains the translated preview after the wake shortcut";
            SendKeys(0x11, 0x10, 0x78); // Actual configured Ctrl+Shift+F9 through the real hook.
            while (!overlay.IsVisible || !Children(overlay).OfType<TextBox>().Any(item => item.Text == "こんにちは"))
                await Task.Delay(10, native.Token);
            stage = "open-form";
            condition = "original native field focused before opening MoreGlossary";
            Assert.Equal((native.Root, native.A), N.Focused());
            condition = "MoreGlossary present and enabled in the open menu";
            Button(overlay, "更多翻译操作").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            menu = PresentationSource.CurrentSources.OfType<HwndSource>().Select(item => item.RootVisual)
                .OfType<DependencyObject>().SelectMany(root => Children(root).Prepend(root))
                .OfType<ContextMenu>().Single(item => item.IsOpen);
            var command = menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "MoreGlossary");
            Assert.True(command.IsEnabled);
            condition = "MoreGlossary Click observed and menu closed";
            command.Click += (_, _) =>
            {
                commandClicked = true;
                clickFocus = N.Focused();
            };
            await InvokeGlossaryCommandAsync(command, native.Token);
            Assert.False(menu.IsOpen);
            condition = "GlossaryEntryFocus present with overlay foreground and glossary source field focused";
            while (view.GlossaryEntryFocus is null)
                await Task.Delay(10, native.Token);
            stage = "save-rule";
            condition = "explicit save button focused and invoked";
            Children(overlay).OfType<TextBox>().Single(item => AutomationProperties.GetName(item) == "术语源词").Text = "你好";
            Children(overlay).OfType<TextBox>().Single(item => AutomationProperties.GetName(item) == "术语译词").Text = "こんにちは";
            var save = Button(overlay, "保存术语");
            Assert.True(save.Focus());
            save.Click += (_, _) => saveClicked = true;
            save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            condition = "expected rule present in settings after explicit save";
            while (settings.Value.Glossary?.Rules.Contains(expected) != true)
                await Task.Delay(10, native.Token);
            condition = "saved rule reread from disk";
            Assert.Contains(expected, new SettingsStore(Path.Combine(directory, "settings.json")).Value.Glossary!.Rules);
            stage = "verify-source-after-save";
            condition = "release policy preserved, original source focused and unchanged, zero writes and submits";
            Assert.Equal(applicationReleased, controller.GetGlossarySettingsState().IsReleased);
            if (!applicationReleased)
                Assert.False(controller.GetGlossarySettingsState().CanApply);
            Assert.Equal((native.Root, native.A), N.Focused());
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(0, 2), N.ReadSelection(native.A));
            Assert.False(settings.Value.ExperimentalWriteback);
            native.AssertZeroSubmits();
            Assert.Equal(0, broker.SourceWrites);
        }
        catch
        {
            try { reportFailure?.Invoke(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                TestContext.Current.TestOutputHelper?.WriteLine("Glossary failure observation unavailable: " + error.GetType().Name);
            }
            throw;
        }
        finally
        {
            if (controller is not null)
                controller.Dispose();
            else
                monitor.Dispose();
            overlay.Close();
            WriteReturnTrace(directory);
            Directory.Delete(directory, true);
        }
    });

    private sealed class DesktopLoop : ISessionLoop
    {
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        public long NowMs => WindowsInterruptClock.NowMs;
        public event Action? Tick;
        public DesktopLoop() => _timer.Tick += (_, _) => Tick?.Invoke();
        public void Start() => _timer.Start();
        public void Stop()
        {
            _timer.Stop();
            Tick = null;
        }
    }
    private sealed class GlossaryProtocolPeer(ModelClient client) : IDisposable
    {
        public const string Package = "hy-mt2-1-8b-q4-k-m";
        public const string Version = "a0c709d9fac510f2c807aa3af52872340dc37a4a";
        public ModelClient Client => client;
        public static async Task<GlossaryProtocolPeer> Start(string directory, CancellationToken cancellation)
        {
            string script = Path.Combine(directory, "model-peer.py"), python = Path.Combine(directory, "python-marker");
            File.WriteAllText(python, "controlled model protocol peer");
            File.WriteAllText(script, """
                import json, struct, sys
                while header := sys.stdin.buffer.read(4):
                    request = json.loads(sys.stdin.buffer.read(struct.unpack('<I', header)[0]))
                    response = {'id': request['id'], 'status': 'translated', 'text': 'こんにちは'}
                    if request['op'] == 'hello':
                        response = {'id': request['id'], 'status': 'ready', 'directions': ['zh_ja'],
                            'packages': [{'id': 'hy-mt2-1-8b-q4-k-m',
                                'version': 'a0c709d9fac510f2c807aa3af52872340dc37a4a', 'directions': ['zh_ja']}],
                            'glossaryTranslationVersion': 1}
                    payload = json.dumps(response).encode('utf-8')
                    sys.stdout.buffer.write(struct.pack('<I', len(payload)) + payload)
                    sys.stdout.buffer.flush()
                """);
            using var inventory = JsonDocument.Parse("""
                {"schema":3,"activePackages":["hy-mt2-1-8b-q4-k-m"],"packages":[
                  {"id":"hy-mt2-1-8b-q4-k-m","version":"a0c709d9fac510f2c807aa3af52872340dc37a4a",
                   "directions":["zh_ja"],"files":[{"path":"model.gguf","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}]}
                """);
            File.WriteAllText(Path.Combine(directory, "installed.json"), inventory.RootElement.GetRawText());
            var paths = new ModelRuntimePaths(python, script, directory);
            var model = new ModelClient(paths, _ =>
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
                start.ArgumentList.Add(script);
                return new ModelPeerProcess(Process.Start(start)!);
            });
            try
            {
                Assert.Equal(ModelPublicationOutcome.Confirmed,
                    await model.ReloadAsync(ModelLoadPlan.Create(paths, inventory.RootElement),
                        () => ModelPublicationOutcome.Confirmed, _ => false, cancellation));
                Assert.NotNull(model.VerifiedIdentity);
                Assert.NotNull(model.GlossaryTranslationCapability);
                return new(model);
            }
            catch { model.Dispose(); throw; }
        }
        public void Dispose() => client.Dispose();
    }
    private sealed class ModelPeerProcess(Process process) : IChildProcess
    {
        public Process Process => process;
        public async Task TerminateAndWaitAsync(CancellationToken cancellationToken)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }
        public void Dispose() => process.Dispose();
    }
    private static void SendKeys(params ushort[] keys)
    {
        var input = keys.Select(key => new KeyInput { Type = 1, Key = key })
            .Concat(keys.Reverse().Select(key => new KeyInput { Type = 1, Key = key, Flags = 2 })).ToArray();
        Assert.Equal((uint)input.Length, SendInput((uint)input.Length, input, Marshal.SizeOf<KeyInput>()));
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyInput[] input, int size);
}
