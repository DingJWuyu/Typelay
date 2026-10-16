using TypeLay.Runtime;
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
        if (args.Length != 0 && !automationTest)
            return 2;
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
        PracticeHost? practiceHost = null;
        SettingsWindow? settingsWindow = null;
        Forms.NotifyIcon? tray = null;
        Drawing.Icon? icon = null;
        bool disposed = false;
        void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            session?.Dispose();
            model?.Dispose();
            practiceHost?.Dispose();
            if (tray is not null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            icon?.Dispose();
        }
        app.Exit += (_, _) => Dispose();
        app.DispatcherUnhandledException += (_, e) =>
        {
            // Never serialize exception messages: provider exceptions may contain user input.
            e.Handled = true;
            Dispose();
            settingsWindow?.PrepareForEmergencyShutdown();
            MessageBox.Show("TypeLay 已停止，以避免在异常状态下继续读取或写回。请重新启动。错误代码：UI-STOP。", "TypeLay", MessageBoxButton.OK, MessageBoxImage.Error);
            app.Shutdown(2);
        };
        app.Startup += async (_, _) =>
        {
            var settings = WindowsRuntime.CreateSettings();
            UiTheme.Initialize(settings.Value.Theme);
            model = WindowsRuntime.CreateModel();
            var overlay = new OverlayWindow(automationTest);
            var support = new WindowsApplicationSupport();
            practiceHost = new PracticeHost();
            session = WindowsRuntime.CreateSession(settings, model, overlay, automationTest, support: support, practice: practiceHost);
            practiceHost.Bind(session);
            var window = new SettingsWindow(settings, model,
                () => practiceHost.Open(Path.Combine(AppPaths.Data, "settings.json")), support)
            {
                SessionMustEnd = session.StopSession,
                PrepareSettingsChange = session.PrepareSettingsChangeAsync,
                ShortcutStatus = () => session.ShortcutStatus,
                PracticeProgress = () => session.PracticeProgress,
                AutomaticPracticeProgress = () => session.AutomaticPracticeProgress
            };
            session.PracticeProgressChanged += _ => window.RefreshPracticeProgress();
            session.AutomaticPracticeProgressChanged += _ => window.RefreshPracticeProgress();
            window.RefreshPracticeProgress();
            settingsWindow = window;
            bool quitting = false;
            async void Quit()
            {
                if (quitting || !window.RequestClose())
                    return;
                quitting = true;
                try
                {
                    if (await session.RetireAutomaticPracticeAsync())
                        app.Shutdown();
                    else
                        tray?.ShowBalloonTip(5000, "TypeLay", "练习尚未确认停止，请关闭练习窗口后再退出。", Forms.ToolTipIcon.Warning);
                }
                finally { quitting = false; }
            }
            overlay.LanguageRequested = () => window.ShowPage("language");
            var quick = new QuickPanel(settings, model, session, () => window.ShowPage(), Quit);
            icon = MakeTrayIcon();
            tray = new Forms.NotifyIcon { Icon = icon, Visible = true, Text = "TypeLay · 正在启动" };
            practiceHost.Failed += () =>
            {
                if (!disposed)
                    tray.ShowBalloonTip(5000, OnDemandPracticePresentation.Create(settings.Value).Title,
                        OnDemandPracticePresentation.Create(settings.Value).LaunchFailedMessage, Forms.ToolTipIcon.Info);
            };
            session.OperationFeedback += feedback =>
            {
                if (!disposed)
                    tray.ShowBalloonTip(5000, "TypeLay", feedback.Message, Forms.ToolTipIcon.Info);
            };
            support.CheckStarted += id =>
            {
                var target = ApplicationSupportPresentation.Create([]).Rows.Single(row => row.Id == id);
                tray.ShowBalloonTip(5000, target.Title,
                    ApplicationSupportPresentation.ReasonText(ApplicationSupportReason.NoFocusedField), Forms.ToolTipIcon.Info);
            };
            support.CheckCompleted += id =>
            {
                if (disposed)
                    return;
                var result = support.Read(settings.Value).Rows.Single(row => row.Id == id);
                tray.ShowBalloonTip(5000, result.Title, result.ReasonText, Forms.ToolTipIcon.Info);
            };
            var menu = new Forms.ContextMenuStrip();
            var toggle = CreateToggleMenuItem(settings, quick, () => tray.ShowBalloonTip(5000, "TypeLay",
                SettingsDraftPresentation.SaveUnconfirmedMessage, Forms.ToolTipIcon.Info));
            menu.Items.Add(toggle);
            menu.Items.Add("设置…", null, (_, _) => app.Dispatcher.Invoke(() => window.ShowPage()));
            menu.Items.Add("翻译练习", null, (_, _) => app.Dispatcher.Invoke(window.StartPractice));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出 TypeLay", null, (_, _) => app.Dispatcher.Invoke(Quit));
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) app.Dispatcher.Invoke(quick.OpenAtTray); };
            void TrayStatus()
            {
                if (disposed)
                    return;
                tray.Text = (settings.Value.Enabled ? "TypeLay · " + model.Status : "TypeLay · 已暂停")
                    + (automationTest ? " · 自动化测试" : "");
                toggle.Text = settings.Value.Enabled ? "暂停 TypeLay" : "启用 TypeLay";
            }
            settings.Changed += TrayStatus;
            settings.Changed += () => { if (!settings.Value.Enabled) support.CancelCheck(); };
            model.Changed += TrayStatus;
            UserPreferenceChangedEventHandler themeChanged = (_, _) => app.Dispatcher.BeginInvoke(new Action(() => UiTheme.Apply(settings.Value.Theme)));
            SystemEvents.UserPreferenceChanged += themeChanged;
            app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= themeChanged;
            if (!model.Installed)
                window.ShowStartupPage();
            await session.StartAsync();
            await model.StartAsync();
            TrayStatus();
            if (!model.Ready || !settings.Value.OnboardingComplete)
                window.ShowStartupPage();
        };
        try
        {
            return app.Run();
        }
        finally { Dispose(); mutex.ReleaseMutex(); }
    }
    internal static Forms.ToolStripMenuItem CreateToggleMenuItem(SettingsStore settings, QuickPanel quick, Action showSaveError)
    {
        var toggle = new Forms.ToolStripMenuItem(settings.Value.Enabled ? "暂停 TypeLay" : "启用 TypeLay");
        toggle.Click += (_, _) => quick.Dispatcher.Invoke(async () =>
        {
            if (!await quick.SetEnabledAsync(!settings.Value.Enabled))
                showSaveError();
        });
        return toggle;
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
            g.FillRectangle(white, 9, 8, 14, 4);
            g.FillRectangle(white, 14, 11, 4, 14);
        }
        nint handle = bitmap.GetHicon();
        try
        {
            using var native = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)native.Clone();
        }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
