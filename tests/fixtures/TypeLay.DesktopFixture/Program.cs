using TypeLay.Runtime;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
            return 2;
        string artifacts = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(artifacts);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var settings = WindowsRuntime.CreateSettings(Path.Combine(artifacts, "fixture-settings.json"));
        settings.Update(new(Theme: "light", OnboardingComplete: true, ExperimentalWriteback: true));
        UiTheme.Initialize("light");
        var source = new Window
        {
            Title = "TypeLay Desktop Fixture - real local model",
            Width = 850,
            Height = 620,
            Left = 120,
            Top = 100,
            FontFamily = new("Segoe UI"),
            FontSize = 14
        };
        source.SetResourceReference(Control.BackgroundProperty, "Panel2");
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(UiTheme.Text("TypeLay desktop regression", 22, bold: true));
        panel.Children.Add(UiTheme.Text("Real model, production overlay/controller/hook. Source is a WPF test fixture, not browser UIA.", 12, muted: true));
        var modelStatus = UiTheme.Text("Model starting", 13);
        modelStatus.Margin = new(0, 12, 0, 10);
        panel.Children.Add(modelStatus);
        panel.Children.Add(UiTheme.Text("A · Message · single line"));
        var a = new TextBox { Text = "你好，我想确认一下明天下午的会议时间。", Margin = new(0, 6, 0, 20), Padding = new(10), Height = 44 };
        AutomationProperties.SetName(a, "A Message");
        panel.Children.Add(a);
        panel.Children.Add(UiTheme.Text("B · Comment · multiline and emoji"));
        var b = new TextBox
        {
            Text = "Hello 🙂 明天下午见, thanks.\nSecond line.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            Padding = new(10),
            Margin = new(0, 6, 0, 20)
        };
        AutomationProperties.SetName(b, "B Comment");
        panel.Children.Add(b);
        var controls = new WrapPanel();
        panel.Children.Add(controls);
        var counts = UiTheme.Text("Writes: 0 · Submits: 0", 13, bold: true);
        counts.Margin = new(0, 15, 0, 0);
        panel.Children.Add(counts);
        source.Content = panel;
        using var model = WindowsRuntime.CreateModel();
        var overlay = new OverlayWindow(true);
        using var broker = new WpfFixtureBroker(source, a, b);
        using var controller = WindowsRuntime.CreateSession(settings, model, overlay, true, broker);
        int submits = 0;
        void RefreshCounts() => counts.Text = $"Writes: {broker.Writes} · Submits: {submits} · Paused: {!settings.Value.Enabled}";
        var reset = UiTheme.Button("Reset", () =>
        {
            controller.StopSession();
            a.Text = "你好，我想确认一下明天下午的会议时间。";
            b.Text = "Hello 🙂 明天下午见, thanks.\nSecond line.";
            broker.ResetCounts();
            submits = 0;
            RefreshCounts();
            a.Focus();
        });
        controls.Children.Add(reset);
        var select = UiTheme.Button("Select Chinese in B", () => { b.Focus(); b.Select(9, 5); });
        controls.Children.Add(select);
        // Deterministic replay of committed text changes, not physical keys or IME candidates.
        const string streamText = "你好我想确认明天下午的会议时间还有需要准备的材料请告诉我是否可以参加会议谢谢我还想讨论后续安排";
        var inputReplay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        int streamOffset = 0;
        var streamStatus = UiTheme.Text("Committed text replay: idle", 12, muted: true);
        panel.Children.Add(streamStatus);
        inputReplay.Tick += (_, _) =>
        {
            a.AppendText(streamText[streamOffset++].ToString());
            a.Select(a.Text.Length, 0);
            streamStatus.Text = $"Committed text replay: {streamOffset}/{streamText.Length}";
            if (streamOffset == streamText.Length)
                inputReplay.Stop();
        };
        controls.Children.Add(UiTheme.Button("Continuous input without punctuation", () =>
        {
            inputReplay.Stop();
            controller.StopSession();
            a.Clear();
            a.Focus();
            streamOffset = 0;
            inputReplay.Start();
        }));
        var pause = UiTheme.Button("Pause / Resume", () => { settings.Update(settings.Value with { Enabled = !settings.Value.Enabled }); RefreshCounts(); });
        controls.Children.Add(pause);
        var submit = UiTheme.Button("Submit positive control", () => { submits++; RefreshCounts(); });
        submit.IsDefault = true;
        controls.Children.Add(submit);
        broker.Written += RefreshCounts;
        model.Changed += () => modelStatus.Text = model.Status;
        var previewEditor = ((Grid)((Border)overlay.Content).Child).Children.OfType<TextBox>().Single();
        string last = "", lastPreview = "";
        int translatedPreviews = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            if (previewEditor.Text != lastPreview)
            {
                lastPreview = previewEditor.Text;
                if (lastPreview.Length > 0 && !lastPreview.Contains("翻译") && !lastPreview.Contains("准备"))
                    translatedPreviews++;
            }
            string report = JsonSerializer.Serialize(new
            {
                suite = "production-desktop-fixture",
                modelReady = model.Ready,
                inputAvailable = controller.InputAvailable,
                writes = broker.Writes,
                submits,
                paused = !settings.Value.Enabled,
                field = broker.LastField,
                ime = broker.LastIme.ToString(),
                lastWrite = broker.LastWrite.ToString(),
                scope = broker.LastScope,
                sourceBackend = "in-process-WPF-fixture",
                injectedKeys = true,
                replayRunning = inputReplay.IsEnabled,
                sourceLength = a.Text.Length,
                translatedPreviews,
                overlayVisible = overlay.IsVisible
            });
            if (report != last)
            {
                last = report;
                Console.WriteLine(report);
                Console.Out.Flush();
            }
        };
        source.Loaded += async (_, _) =>
        {
            await controller.StartAsync();
            await model.StartAsync();
            timer.Start();
            source.Activate();
            a.Focus();
        };
        source.Closed += (_, _) => { inputReplay.Stop(); timer.Stop(); overlay.Close(); };
        app.MainWindow = source;
        source.Show();
        return app.Run();
    }
}
