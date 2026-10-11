using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace TypeLay.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool automationTest = args.Length == 1 && args[0] == "--automation-test";
        if (args.Length != 0 && !automationTest) return 2;
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var mutex = new Mutex(true, @"Local\TypeLay.Client." + sid, out bool first);
        if (!first)
        {
            MessageBox.Show("TypeLay 已在运行。请点击系统托盘中的 T 图标；它可能位于折叠图标区域。", "TypeLay", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SessionController? session = null;
        ModelClient? model = null;
        Forms.NotifyIcon? tray = null;
        Drawing.Icon? icon = null;
        bool disposed = false;
        void Dispose()
        {
            if (disposed) return; disposed = true;
            session?.Dispose(); model?.Dispose();
            if (tray is not null) { tray.Visible = false; tray.Dispose(); }
            icon?.Dispose();
        }
        app.Exit += (_, _) => Dispose();
        app.DispatcherUnhandledException += (_, e) =>
        {
            // Never serialize exception messages: provider exceptions may contain user input.
            e.Handled = true; Dispose();
            MessageBox.Show("TypeLay 已停止，以避免在异常状态下继续读取或写回。请重新启动。错误代码：UI-STOP。", "TypeLay", MessageBoxButton.OK, MessageBoxImage.Error);
            app.Shutdown(2);
        };
        app.Startup += async (_, _) =>
        {
            var settings = new SettingsStore(); UiTheme.Initialize(settings.Value.Theme);
            model = new ModelClient();
            var overlay = new OverlayWindow(automationTest);
            session = new SessionController(settings, model, overlay, automationTest);
            void Tutorial()
            {
                session.StopSession();
                string file = Path.Combine(AppPaths.Home, "fixtures", "browser", "index.html");
                // A normal browser form, served from a local file. There is no translation mock.
                try { Process.Start(new ProcessStartInfo("msedge.exe") { UseShellExecute = true, ArgumentList = { new Uri(file).AbsoluteUri } }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
            }
            var window = new SettingsWindow(settings, model, Tutorial) { SessionMustEnd = session.StopSession };
            overlay.LanguageRequested = () => window.ShowPage("language");
            var quick = new QuickPanel(settings, model, session, () => window.ShowPage(), () => app.Shutdown());
            icon = MakeTrayIcon();
            tray = new Forms.NotifyIcon { Icon = icon, Visible = true, Text = "TypeLay · 正在启动" };
            var menu = new Forms.ContextMenuStrip();
            var toggle = new Forms.ToolStripMenuItem("暂停 TypeLay");
            toggle.Click += (_, _) => app.Dispatcher.Invoke(() => settings.Update(settings.Value with { Enabled = !settings.Value.Enabled }));
            menu.Items.Add(toggle);
            menu.Items.Add("设置…", null, (_, _) => app.Dispatcher.Invoke(() => window.ShowPage()));
            menu.Items.Add("实操教程", null, (_, _) => app.Dispatcher.Invoke(Tutorial));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出 TypeLay", null, (_, _) => app.Dispatcher.Invoke(() => app.Shutdown()));
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) app.Dispatcher.Invoke(quick.OpenAtTray); };
            void TrayStatus()
            {
                if (disposed) return;
                tray.Text = (settings.Value.Enabled ? "TypeLay · " + model.Status : "TypeLay · 已暂停")
                    + (automationTest ? " · 自动化测试" : "");
                toggle.Text = settings.Value.Enabled ? "暂停 TypeLay" : "启用 TypeLay";
            }
            settings.Changed += TrayStatus; model.Changed += TrayStatus;
            UserPreferenceChangedEventHandler themeChanged = (_, _) => app.Dispatcher.BeginInvoke(new Action(() => UiTheme.Apply(settings.Value.Theme)));
            SystemEvents.UserPreferenceChanged += themeChanged;
            app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= themeChanged;
            if (!model.Installed) window.ShowPage();
            await session.StartAsync();
            await model.StartAsync();
            TrayStatus();
            if (!model.Ready || !settings.Value.OnboardingComplete) window.ShowPage();
        };
        try { return app.Run(); }
        finally { Dispose(); mutex.ReleaseMutex(); }
    }
    private static Drawing.Icon MakeTrayIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var blue = new Drawing.SolidBrush(Drawing.Color.FromArgb(50, 103, 227));
            g.FillEllipse(blue, 1, 1, 30, 30);
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            g.FillRectangle(white, 9, 8, 14, 4); g.FillRectangle(white, 14, 11, 4, 14);
        }
        nint handle = bitmap.GetHicon();
        try { using var native = Drawing.Icon.FromHandle(handle); return (Drawing.Icon)native.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
