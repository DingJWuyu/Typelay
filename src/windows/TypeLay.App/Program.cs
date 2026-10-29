using TypeLay.Core;
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
        GlobalPauseShortcut? pauseShortcut = null;
        Drawing.Icon? icon = null;
        bool disposed = false;
        string? trayTracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
        using var startup = new CancellationTokenSource();
        void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            startup.Cancel();
            settingsWindow?.CancelPreparation();
            pauseShortcut?.Dispose();
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
            UiTheme.Initialize(settings.Value);
            model = WindowsRuntime.CreateModel();
            var overlay = new OverlayWindow(automationTest);
            var support = new WindowsApplicationSupport();
            practiceHost = new PracticeHost();
            ShortcutRegistrationRecovery? shortcutRecovery = null;
            session = WindowsRuntime.CreateSession(settings, model, overlay, automationTest, support: support, practice: practiceHost,
                bindShortcutRecovery: recovery => shortcutRecovery = recovery);
            practiceHost.Bind(session);
            var window = new SettingsWindow(settings, model,
                () => practiceHost.Open(Path.Combine(AppPaths.Data, "settings.json")), support, controller: session)
            {
                SessionMustEnd = session.StopSession,
                PrepareSettingsChange = session.PrepareSettingsChangeAsync,
                PrepareModelChange = session.PrepareModelChangeAsync,
                ShortcutStatus = () => session.ShortcutStatus,
                ShortcutRecovery = shortcutRecovery,
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
                    {
                        await support.CompletePendingAsync();
                        app.Shutdown();
                    }
                    else
                        tray?.ShowBalloonTip(5000, "TypeLay", "练习尚未确认停止，请关闭练习窗口后再退出。", Forms.ToolTipIcon.Warning);
                }
                finally { quitting = false; }
            }
            var quick = new QuickPanel(settings, model, session, () => window.ShowPage(), Quit);
            overlay.SettingsRequested = () => window.ShowPage();
            overlay.NativeLanguageRequested = anchor =>
            {
                session.StopSession();
                overlay.ShowLanguageConfiguration(anchor, settings, model,
                    (source, target, current) => quick.SetLanguagesAsync(source, target, current));
            };
            LanguageSelectionWindow? languageWindow = null;
            overlay.LanguageRequested = () =>
            {
                session.StopSession();
                if (languageWindow is { IsVisible: true })
                {
                    languageWindow.Activate();
                    return;
                }
                languageWindow = new LanguageSelectionWindow(settings, model, quick.SetLanguagesAsync);
                languageWindow.Closed += (_, _) => languageWindow = null;
                languageWindow.Show();
            };
            pauseShortcut = new GlobalPauseShortcut(settings, quick.SetEnabledAsync);
            window.PauseShortcutStatus = () => pauseShortcut.Status;
            window.PauseShortcutConflicts = pauseShortcut.InspectConflicts;
            window.CanRetryPauseShortcut = () => pauseShortcut.CanRetry;
            window.RetryPauseShortcut = pauseShortcut.RetryRegistration;
            pauseShortcut.Changed += window.RefreshShortcutStatus;
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
            var settingsMenu = menu.Items.Add(UiTheme.L("设置…", "Settings…"), null, (_, _) => app.Dispatcher.Invoke(() => window.ShowPage()));
            var practiceMenu = menu.Items.Add(UiTheme.L("翻译练习", "Translation Practice"), null, (_, _) => app.Dispatcher.Invoke(window.StartPractice));
            menu.Items.Add(new Forms.ToolStripSeparator());
            var quitMenu = menu.Items.Add(UiTheme.L("退出 TypeLay", "Quit TypeLay"), null, (_, _) => app.Dispatcher.Invoke(Quit));
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) app.Dispatcher.Invoke(quick.OpenAtTray); };
            void TrayStatus()
            {
                if (disposed)
                    return;
                if (!app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(new Action(TrayStatus));
                    return;
                }
                string status = !settings.Value.Enabled ? UiTheme.L("已暂停", "Paused") : !model.Ready ? model.Status
                    : !session.InputAvailable ? session.ConnectionStatus
                    : settings.Value.Mode == WorkMode.OnDemand ? session.ShortcutStatus : model.Status;
                status = UiStrings.Translate(status);
                settingsMenu.Text = UiTheme.L("设置…", "Settings…");
                practiceMenu.Text = UiTheme.L("翻译练习", "Translation Practice");
                quitMenu.Text = UiTheme.L("退出 TypeLay", "Quit TypeLay");
                string text = "TypeLay · " + status + (automationTest ? " · 自动化测试" : "");
                string trayText = text.Length > 63 ? text[..63] : text;
                tray.Text = trayText;
                BrowserInput.TraceTrayStatus(trayTracePath, trayText);
                toggle.Text = settings.Value.Enabled ? UiTheme.L("暂停 TypeLay", "Pause TypeLay") : UiTheme.L("启用 TypeLay", "Enable TypeLay");
            }
            settings.Changed += TrayStatus;
            settings.Changed += () => { if (!settings.Value.Enabled) support.CancelCheck(); };
            model.Changed += TrayStatus;
            session.Changed += () =>
            {
                if (disposed)
                    return;
                TrayStatus();
                window.RefreshShortcutStatus();
            };
            UserPreferenceChangedEventHandler themeChanged = (_, _) => app.Dispatcher.BeginInvoke(new Action(() => UiTheme.Apply(settings.Value)));
            SystemEvents.UserPreferenceChanged += themeChanged;
            app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= themeChanged;
            OnboardingWindow? onboarding = null;
            void ShowOnboarding(bool restart = false)
            {
                session.StopSession();
                if (onboarding is null)
                {
                    onboarding = new OnboardingWindow(settings, model, window);
                    onboarding.Closed += (_, _) => onboarding = null;
                }
                onboarding.ShowOrActivate(restart);
            }
            window.ShowOnboarding = () => ShowOnboarding(restart: true);
            bool needsOnboarding = !settings.Value.OnboardingComplete;
            if (needsOnboarding)
                ShowOnboarding();
            else if (!model.Installed)
                window.ShowStartupPage();
            await session.StartAsync();
            if (disposed)
                return;
            try
            {
                await WindowsRuntime.StartModelAsync(model, startup.Token);
            }
            catch (OperationCanceledException) when (startup.IsCancellationRequested) { return; }
            catch (ModelSetupException failure) { window.ReportSetupFailure(failure); }
            if (disposed)
                return;
            TrayStatus();
            // Startup completion must not reopen a wizard the user dismissed.
            if (!model.Ready && !needsOnboarding)
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
        var toggle = new Forms.ToolStripMenuItem(settings.Value.Enabled ? UiTheme.L("暂停 TypeLay", "Pause TypeLay") : UiTheme.L("启用 TypeLay", "Enable TypeLay"));
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
            using var clay = new Drawing.SolidBrush(Drawing.Color.FromArgb(217, 119, 87));
            using var background = RoundedRect(1, 1, 30, 30, 7);
            g.FillPath(clay, background);
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            using var line = new Drawing.Pen(Drawing.Color.White, 1.8f);
            using var rear = RoundedRect(5, 7, 17, 12, 3);
            g.DrawPath(line, rear);
            using var front = RoundedRect(10, 14, 17, 12, 3);
            g.FillPath(white, front);
        }
        nint handle = bitmap.GetHicon();
        try
        {
            using var native = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)native.Clone();
        }
        finally { DestroyIcon(handle); }
    }
    private static Drawing.Drawing2D.GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
    {
        float diameter = radius * 2;
        var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
