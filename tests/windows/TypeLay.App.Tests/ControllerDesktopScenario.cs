using TypeLay.Windows;
using TypeLay.Runtime;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Keyboard input uses the explicit automation-test option. Capture, activation,
// settings and broker IPC are production implementations, including the UIA
// fallback for a control that cannot qualify for native direct replacement.
[Collection("Desktop")]
public sealed class ControllerDesktopScenario(WpfDesktopFixture desktop, ITestOutputHelper testOutput)
{
    private readonly ConcurrentQueue<string> _stages = new();
    private readonly Stopwatch _elapsed = new();

    private void Stage(bool stockControls, string name) => _stages.Enqueue(
        $"{_elapsed.ElapsedMilliseconds} ms tick={WindowsInterruptClock.NowMs} stock-controls={stockControls}: {name}");

    [Fact]
    public async Task OnDemandWakeStaysBoundToTheFocusedFieldAndLosesDisconnectedSupport()
    {
        _elapsed.Start();
        try
        {
            await desktop.RunAsync(async () =>
            {
                await RunAsync(stockControls: true);
                await RunAsync(stockControls: false);
            });
        }
        finally { testOutput.WriteLine(string.Join(Environment.NewLine, _stages)); }
    }

    private async Task RunAsync(bool stockControls)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeLay.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        string directory = Path.Combine(Path.GetTempPath(), "typelay-controller-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root.FullName,
            "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"));
        if (stockControls)
            start.ArgumentList.Add("--stock-controls");
        using var fixture = Process.Start(start)!;
        Assert.NotNull(fixture);
        var overlay = new OverlayWindow(automationTest: true);
        try
        {
            string? line = await fixture.StandardOutput.ReadLineAsync(deadline.Token);
            var host = JsonSerializer.Deserialize<FixtureDescriptor>(line ?? "null");
            Assert.NotNull(host);
            nint a = (nint)host.Fields.Single(field => field.Name == "a").Hwnd;
            nint b = (nint)host.Fields.Single(field => field.Name == "b").Hwnd;
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Mode = WorkMode.OnDemand
            });
            using var model = WindowsRuntime.CreateModel(); // Empty-field wake needs no model download or fabricated translation.
            var support = new WindowsApplicationSupport();
            var broker = new BrokerClient();
            using var controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true,
                broker: broker, support: support);
            await controller.StartAsync().WaitAsync(deadline.Token);
            await Until(() => controller.InputAvailable, "The input service must be ready.", deadline.Token);
            async Task Focus(nint field)
            {
                Assert.True(N.PostMessage((nint)host.RootHwnd, N.FixtureFocus, (nuint)field, 0));
                await Until(() => N.Focused() == ((nint)host.RootHwnd, field), "The fixture must own focus.", deadline.Token);
            }
            N.SendString(a, N.WM_SETTEXT, "");
            N.SendString(b, N.WM_SETTEXT, "B stays unchanged");
            await Focus(a);
            await Until(() => controller.LastApplication is not null, "The source must be observed.", deadline.Token);
            Stage(stockControls, "support-read-before");
            var supported = support.Read(settings.Value);
            Stage(stockControls, "support-read-after");
            var supportedField = supported.Rows.Single(row => row.Id == "desktop-text");
            Assert.Equal(ApplicationSupportState.Connected, supportedField.State);
            Assert.True(supportedField.Capabilities.Preview);
            Assert.Equal(stockControls, supportedField.Capabilities.DirectReplace);
            Assert.False(supportedField.Capabilities.TabAccept); // The experimental setting remains off.
            Assert.NotNull(supportedField.CheckedAt);
            Stage(stockControls, "wake-delay-before");
            await Task.Delay(200, deadline.Token);
            Stage(stockControls, "wake-delay-after");
            Assert.False(overlay.IsVisible);
            Stage(stockControls, "wake-keys");
            Keys(0x11, 0x10, 0x78); // Ctrl+Shift+F9, the configured wake shortcut.
            await Until(() => overlay.IsVisible,
                stockControls ? "The shortcut must wake the qualified native field." : "The shortcut must also wake a preview-only UIA field.", deadline.Token);
            Assert.InRange(overlay.Height, 34, 70);
            Assert.Equal(((nint)host.RootHwnd, a), N.Focused());
            Assert.Equal(WorkMode.OnDemand, settings.Value.Mode);
            if (stockControls)
            {
                N.SendString(a, N.WM_SETTEXT, "hello");
                await Until(() => overlay.Height > 100, "Typing after wake must expand the persistent experience.", deadline.Token);
            }
            Assert.False(model.Busy);
            await Focus(b);
            await Until(() => !overlay.IsVisible, "Leaving A must end its awakened experience.", deadline.Token);
            await Task.Delay(200, deadline.Token);
            Assert.False(overlay.IsVisible);
            Assert.Equal("B stays unchanged", N.ReadText(b, Limits.MaxTextUnits));
            Assert.Equal(stockControls ? "hello" : "", N.ReadText(a, Limits.MaxTextUnits));
            Assert.Equal(0, (long)N.Send((nint)host.RootHwnd, N.FixtureSubmitCount));
            if (!stockControls)
                return; // An unobservable IME must not grant physical Esc/Tab or direct-write permission.
            await Focus(a);
            await Task.Delay(200, deadline.Token);
            Assert.False(overlay.IsVisible);
            Keys(0x11, 0x10, 0x78);
            await Until(() => overlay.IsVisible, "Returning to A requires a new explicit wake.", deadline.Token);
            Keys(27);
            await Until(() => !overlay.IsVisible, "Escape must hide the awakened experience.", deadline.Token);
            await Task.Delay(200, deadline.Token);
            Assert.False(overlay.IsVisible);
            settings.Update(settings.Value with
            {
                Mode = WorkMode.Automatic
            });
            await Task.Delay(200, deadline.Token);
            Assert.Null(controller.OperationStatus);
            Keys(0x11, 0x10, 0x79); // The direct shortcut must be inactive in automatic mode.
            await Task.Delay(200, deadline.Token);
            Assert.Null(controller.OperationStatus);
            Assert.Equal(WorkMode.Automatic, settings.Value.Mode);
            Assert.Equal(0, (long)N.Send((nint)host.RootHwnd, N.FixtureSubmitCount));
            Assert.Equal("hello", N.ReadText(a, Limits.MaxTextUnits));
            Assert.Equal("B stays unchanged", N.ReadText(b, Limits.MaxTextUnits));
            // No capture/tick between broker disposal and this read: loss of the
            // live service alone must revoke cached connected capabilities.
            int completedChecks = 0;
            support.CheckCompleted += _ => completedChecks++;
            support.BeginCheck("desktop-text");
            broker.Dispose();
            support.Poll();
            Assert.Equal(1, completedChecks);
            support.BeginCheck("desktop-text");
            support.CancelCheck();
            support.Poll();
            Assert.Equal(1, completedChecks);
            var disconnected = support.Read(settings.Value).Rows.Single(row => row.Id == "desktop-text");
            Assert.Equal(ApplicationSupportState.Error, disconnected.State);
            Assert.Equal(new ApplicationSupportCapabilities(), disconnected.Capabilities);
        }
        finally
        {
            overlay.Close();
            if (!fixture.HasExited)
            {
                fixture.Kill(true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await fixture.WaitForExitAsync(cleanup.Token);
            }
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static async Task Until(Func<bool> ready, string failure, CancellationToken cancellation)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(10, cancellation);
        Assert.True(ready(), failure);
    }

    private static void Keys(params ushort[] keys)
    {
        // The product and desktop suite target Windows x64; INPUT is 40 bytes.
        var input = keys.Select(key => new KeyboardInput { Type = 1, Key = key })
            .Concat(keys.Reverse().Select(key => new KeyboardInput { Type = 1, Key = key, Flags = 2 })).ToArray();
        Assert.Equal((uint)input.Length, SendInput((uint)input.Length, input, Marshal.SizeOf<KeyboardInput>()));
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] input, int size);
}
