using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceGlossaryTests
{
    [Fact]
    public Task RealControllerPersistsAnExplicitRuleAfterNativeReturnWithoutAWritePermit() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.GlossaryController." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        var monitor = new KeyboardHook(allowInjectedInput: true, editorHwnd: overlay.Handle);
        SessionController? controller = null;
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
            var release = new TranslationReleasePolicy([
                new(TranslationOperation.Glossary, GlossaryProtocolPeer.Package, GlossaryProtocolPeer.Version, "zh_ja")]);
            controller = new(settings, model.Client, view, monitor, input, new DesktopLoop(), translationRelease: release);
            await controller.StartAsync();
            await native.Prepare(native.A, "你好", new(0, 2));
            while (controller.LastApplication is null)
                await Task.Delay(10, native.Token);
            SendKeys(0x11, 0x10, 0x78); // Actual configured Ctrl+Shift+F9 through the real hook.
            while (!overlay.IsVisible || !Children(overlay).OfType<TextBox>().Any(item => item.Text == "こんにちは"))
                await Task.Delay(10, native.Token);
            Assert.Equal((native.Root, native.A), N.Focused());
            Button(overlay, "更多翻译操作").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var menu = PresentationSource.CurrentSources.OfType<HwndSource>().Select(item => item.RootVisual)
                .OfType<DependencyObject>().SelectMany(root => Children(root).Prepend(root))
                .OfType<ContextMenu>().Single(item => item.IsOpen);
            var command = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "加入术语表"));
            Assert.True(command.IsEnabled);
            menu.IsOpen = false;
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            while (view.GlossaryEntryFocus is null)
                await Task.Delay(10, native.Token);
            Children(overlay).OfType<TextBox>().Single(item => AutomationProperties.GetName(item) == "术语源词").Text = "你好";
            Children(overlay).OfType<TextBox>().Single(item => AutomationProperties.GetName(item) == "术语译词").Text = "こんにちは";
            var save = Button(overlay, "保存术语");
            Assert.True(save.Focus());
            save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var expected = new GlossaryRule("你好", "こんにちは");
            while (settings.Value.Glossary?.Rules.Contains(expected) != true)
                await Task.Delay(10, native.Token);
            Assert.Contains(expected, new SettingsStore(Path.Combine(directory, "settings.json")).Value.Glossary!.Rules);
            Assert.Equal((native.Root, native.A), N.Focused());
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(0, 2), N.ReadSelection(native.A));
            Assert.False(settings.Value.ExperimentalWriteback);
            native.AssertZeroSubmits();
            Assert.Equal(0, broker.SourceWrites);
        }
        finally
        {
            if (controller is not null)
                controller.Dispose();
            else
                monitor.Dispose();
            overlay.Close();
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
