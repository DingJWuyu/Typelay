using System.Diagnostics;
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
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class PausedInputTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task PausedBrokerObservesOnlySafeFieldMetadataWithoutReadingTheValue() => desktop.RunAsync(async () =>
    {
        var source = Field("ordinary", "Synthetic source stays local");
        var readOnly = Field("readonly", "Synthetic readonly source");
        readOnly.IsReadOnly = true;
        var password = Field("protected", "Synthetic protected source");
        password.Protected = true;
        var sensitive = Field("sensitive", "Synthetic sensitive source");
        AutomationProperties.SetName(sensitive, "Verification code");
        var unsupported = Field("unobserved", "Synthetic unavailable source");
        unsupported.PatternsUnavailable = true;
        var textOnlyCombo = Field("text-only-combo", "Synthetic text-only selector");
        textOnlyCombo.ComboRole = true;
        textOnlyCombo.ValueUnavailable = true;
        var fields = new[] { source, readOnly, password, sensitive, unsupported, textOnlyCombo };
        var window = Open(fields);
        using var broker = new BrokerClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await broker.StartAsync().WaitAsync(deadline.Token);
            async Task<BrokerResponse> Observe(ProbeField field, string[]? excluded = null)
            {
                await Focus(window, field);
                return await broker.CallAsync(new(BrokerProtocol.Version, 0, "observe-pause", "",
                    ForegroundEpoch: 37, Excluded: excluded)).WaitAsync(deadline.Token);
            }
            var response = await Observe(source);
            Assert.Equal("pause-anchor", response.Status);
            var anchor = Assert.IsType<PauseAnchor>(response.PauseAnchor);
            Assert.Equal(FieldAccess.Supported, anchor.Access);
            Assert.True(anchor.Bounds.IsValid);
            Assert.Equal(37, anchor.FocusEpoch);
            Assert.Equal(37, response.ForegroundEpoch);
            Assert.Equal(FocusMember.Source, response.Focus);
            Assert.Null(response.Snapshot);
            Assert.Null(response.Receipt);
            Assert.All(fields, field => Assert.Equal(0, field.ValueReads));
            foreach (var field in fields.Skip(1))
            {
                var rejected = await Observe(field);
                Assert.Equal("silent", rejected.Status);
                Assert.Null(rejected.PauseAnchor);
                Assert.Null(rejected.Snapshot);
            }
            using var process = Process.GetCurrentProcess();
            var excludedResponse = await Observe(source, [process.ProcessName.ToLowerInvariant()]);
            Assert.Equal("silent", excludedResponse.Status);
            Assert.Null(excludedResponse.PauseAnchor);
            Assert.All(fields, field => Assert.Equal(0, field.ValueReads));

            // Positive control: ordinary capture must exercise this actual UIA
            // value provider, so zero reads above is not an unobserved counter.
            var capture = await broker.CallAsync(new(BrokerProtocol.Version, 0, "capture", ""))
                .WaitAsync(deadline.Token);
            Assert.NotNull(capture.Snapshot);
            Assert.Equal(source.Text, capture.Snapshot.PreviewText ?? capture.Snapshot.Text);
            Assert.True(source.ValueReads > 0);
            Assert.Equal("Synthetic source stays local", source.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task PausedControllerFollowsSafeFieldsAndResumesWithoutChangingOnDemandMode() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-paused-" + Guid.NewGuid().ToString("N"));
        var source = Field("first", "First synthetic source");
        var other = Field("second", "Second synthetic source");
        var password = Field("protected", "Synthetic protected source");
        password.Protected = true;
        var window = Open(source, other, password);
        var overlay = new OverlayWindow();
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Enabled = false,
                Mode = WorkMode.OnDemand
            });
            using var model = new ModelClient(new(Path.Combine(directory, "missing-python"),
                Path.Combine(directory, "missing-worker"), Path.Combine(directory, "missing-models")),
                info => new ChildProcess(info)); // No model installation or startup.
            using var controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true);
            await controller.StartAsync();
            await Focus(window, source);
            await Until(() => overlay.IsVisible);
            Assert.False(model.Busy);
            Assert.False(settings.Value.Enabled);
            Assert.True(source.IsKeyboardFocused);
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            await Focus(window, password);
            await Until(() => !overlay.IsVisible);
            await Focus(window, other);
            await Until(() => overlay.IsVisible);
            using var process = Process.GetCurrentProcess();
            settings.Update(settings.Value with
            {
                ExcludedApplications = [process.ProcessName.ToLowerInvariant()]
            });
            await Until(() => !overlay.IsVisible);
            settings.Update(settings.Value with
            {
                ExcludedApplications = []
            });
            await Until(() => overlay.IsVisible);
            Assert.Equal(0, source.ValueReads);
            Assert.Equal(0, other.ValueReads);
            Assert.Equal(0, password.ValueReads);
            overlay.UpdateLayout();
            var presentation = Presentation.From(SessionState.Paused, false, hasPauseAnchor: true);
            var resume = Descendants(overlay).OfType<Button>().Single(button => button.IsVisible
                && Descendants(button).OfType<TextBlock>().Any(text => text.Text.Contains(presentation.TabHint)));
            ClickOwned(overlay, resume);
            await Until(() => settings.Value.Enabled && !overlay.IsVisible);
            Assert.Equal(WorkMode.OnDemand, settings.Value.Mode);
            var persisted = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json")).Value;
            Assert.True(persisted.Enabled);
            Assert.Equal(WorkMode.OnDemand, persisted.Mode);
            Assert.False(model.Busy);
            Assert.True(other.IsKeyboardFocused);
            Assert.Equal("First synthetic source", source.Text);
            Assert.Equal("Second synthetic source", other.Text);
        }
        finally
        {
            overlay.Close();
            window.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    private static ProbeField Field(string id, string text)
    {
        var field = new ProbeField { Text = text, Height = 42, Margin = new(12) };
        AutomationProperties.SetName(field, "Synthetic ordinary field");
        AutomationProperties.SetAutomationId(field, id);
        return field;
    }
    private static Window Open(params ProbeField[] fields)
    {
        var body = new StackPanel();
        foreach (var field in fields)
            body.Children.Add(field);
        var window = new Window { Title = "TypeLay paused field fixture", Content = body, Width = 600, Height = 490 };
        window.Show();
        return window;
    }
    private static async Task Focus(Window window, ProbeField field)
    {
        Assert.True(window.Activate());
        Assert.True(field.Focus());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(field, Keyboard.FocusedElement);
        Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
    }
    private static async Task Until(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(ready(), "The real paused field flow did not reach its visible state.");
    }
    private static void ClickOwned(OverlayWindow overlay, Button button)
    {
        var point = button.PointToScreen(new(button.ActualWidth / 2, button.ActualHeight / 2));
        var pixel = new NativePoint { X = (int)point.X, Y = (int)point.Y };
        Assert.Equal(overlay.Handle, WindowFromPoint(pixel));
        Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
        Assert.True(SetCursorPos(pixel.X, pixel.Y));
        // One injected click exercises WM_MOUSEACTIVATE and WPF's actual button
        // route. It is desktop automation, not physical-pointer validation.
        MouseInput[] input = [new() { Flags = 2 }, new() { Flags = 4 }];
        uint sent = SendInput(2, input, Marshal.SizeOf<MouseInput>());
        if (sent == 1)
            SendInput(1, [new() { Flags = 4 }], Marshal.SizeOf<MouseInput>());
        Assert.Equal(2u, sent);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var item in Descendants(child))
                yield return item;
        }
    }
    private sealed class ProbeField : TextBox
    {
        internal bool Protected
        {
            get; set;
        }
        internal bool PatternsUnavailable
        {
            get; set;
        }
        internal bool ValueUnavailable
        {
            get; set;
        }
        internal bool ComboRole
        {
            get; set;
        }
        internal int ValueReads;
        protected override AutomationPeer OnCreateAutomationPeer() => new ProbePeer(this);
    }
    private sealed class ProbePeer(ProbeField owner) : TextBoxAutomationPeer(owner), IValueProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) => owner.PatternsUnavailable
            && patternInterface is PatternInterface.Value or PatternInterface.Text ? null
            : patternInterface == PatternInterface.Value ? owner.ValueUnavailable ? null : this : base.GetPattern(patternInterface);
        protected override AutomationControlType GetAutomationControlTypeCore() => owner.ComboRole
            ? AutomationControlType.ComboBox : base.GetAutomationControlTypeCore();
        protected override bool IsPasswordCore() => owner.Protected;
        bool IValueProvider.IsReadOnly => owner.IsReadOnly;
        string IValueProvider.Value
        {
            get
            {
                Interlocked.Increment(ref owner.ValueReads);
                return owner.Text;
            }
        }
        void IValueProvider.SetValue(string value) => throw new InvalidOperationException("This fixture never accepts UIA writes.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(20)] public uint Flags;
    }
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, MouseInput[] input, int size);
}
