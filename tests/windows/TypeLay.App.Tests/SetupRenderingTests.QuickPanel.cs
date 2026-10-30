using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SetupRenderingTests
{
    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task TrayWithoutNativePayloadShowsUnavailableDownloadWithoutInventingProgress(string language, string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene(language, theme);
        scene.Panel.OpenAtTray();
        var download = TrayElement<Button>(scene.Panel, "QuickPanelModelAction");
        Assert.Equal(language == "en" ? "Download" : "下载", download.Content);
        Assert.Equal(22, download.ActualHeight);
        Assert.Equal(11.5, download.FontSize);
        Assert.False(download.IsEnabled);
        Assert.Throws<ElementNotEnabledException>(() => TrayInvoke(download));
        Assert.False(TrayElement<ProgressBar>(scene.Panel, "QuickPanelModelProgress").IsVisible);
        Assert.False(scene.Model.Ready);
        Assert.Equal(0, scene.Attempts);
        Assert.False(scene.Settings.Value.OnboardingComplete);
        RenderTray(scene.Panel, $"tray-model-missing-{language}-{theme}.png");
    });

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task TrayReadyComesFromTheRealModelProtocolAndDoesNotOfferAnUpdateOrDownload(string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", theme, protocolModel: true);
        await scene.Model.StartAsync();
        Assert.True(scene.Model.Ready);
        scene.Panel.OpenAtTray();
        Assert.Equal("Local model ready", TrayElement<TextBlock>(scene.Panel, "QuickPanelModelStatus").Text);
        Assert.DoesNotContain(Descendants(scene.Panel).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "QuickPanelModelAction");
        Assert.False(TrayElement<ProgressBar>(scene.Panel, "QuickPanelModelProgress").IsVisible);
        Assert.Equal(0, scene.Attempts);
        Assert.Equal(new[] { "argos-zh-en", "argos-en-zh" }.Order(),
            Installations(scene.Directory).ReadInstallationState().CurrentPackages.Select(package => package.Package.Id).Order());
        RenderTray(scene.Panel, $"tray-model-ready-en-{theme}.png");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TrayDownloadConsentPauseResumeAndProgressUseTheOriginalInstaller(bool knownTotal) => desktop.RunAsync(async () =>
    {
        var transports = new List<TrayProgressHttp>();
        await using var scene = new TrayPreparationScene("zh-CN", "light", nativePresence: true,
            handler: () => { var transport = new TrayProgressHttp(knownTotal); transports.Add(transport); return transport; });
        string settingsBefore = File.ReadAllText(scene.SettingsPath);
        scene.Panel.OpenAtTray();
        var oldDownload = TrayProvider(TrayElement<Button>(scene.Panel, "QuickPanelModelAction"));
        await TrayConsent(scene.Host, () => oldDownload.Invoke(), accept: false);
        Assert.Equal(0, scene.Attempts);
        Assert.Equal(settingsBefore, File.ReadAllText(scene.SettingsPath));
        scene.Panel.OpenAtTray();
        AssertRetiredTrayAction(scene, oldDownload);
        await TrayConsent(scene.Host, () => TrayInvoke(TrayElement<Button>(scene.Panel, "QuickPanelModelAction")), accept: true);
        await Until(() => transports.Count == 1 && transports[0].Body.Started.Task.IsCompleted);
        scene.Panel.OpenAtTray();
        await Until(() => TrayElement<TextBlock>(scene.Panel, "QuickPanelModelProgressText").Text.Contains("MB", StringComparison.Ordinal));
        var progress = TrayElement<ProgressBar>(scene.Panel, "QuickPanelModelProgress");
        Assert.True(progress.IsVisible);
        Assert.Equal(!knownTotal, progress.IsIndeterminate);
        Assert.Equal(knownTotal ? 50 : 0, progress.Value);
        Assert.Equal("暂停", TrayElement<Button>(scene.Panel, "QuickPanelModelAction").Content);
        if (!knownTotal)
        {
            var original = scene.Settings.Value;
            scene.Settings.Update(original with
            {
                Motion = "full"
            });
            var retiredProgress = TrayElement<ProgressBar>(scene.Panel, "QuickPanelModelProgress");
            var retiredPending = Assert.IsType<Border>(retiredProgress.Template.FindName("pending", retiredProgress));
            Assert.True(retiredPending.HasAnimatedProperties);
            await AssertAfterTrayAnimationTick(() => scene.Settings.Update(original with
            {
                Motion = "reduce"
            }), () => Assert.False(retiredPending.HasAnimatedProperties));
            progress = TrayElement<ProgressBar>(scene.Panel, "QuickPanelModelProgress");
            Assert.NotSame(retiredProgress, progress);
            Assert.Equal("static", progress.Tag);
            var pending = Assert.IsType<Border>(progress.Template.FindName("pending", progress));
            Assert.False(pending.HasAnimatedProperties);
            scene.Settings.Update(original with
            {
                Motion = "full"
            });
            progress = TrayElement<ProgressBar>(scene.Panel, "QuickPanelModelProgress");
            Assert.Equal("animate", progress.Tag);
            pending = Assert.IsType<Border>(progress.Template.FindName("pending", progress));
            Assert.True(pending.HasAnimatedProperties);
            await AssertAfterTrayAnimationTick(scene.Panel.Hide, () => Assert.False(pending.HasAnimatedProperties));
            Assert.False(retiredPending.HasAnimatedProperties);
            scene.Panel.OpenAtTray();
            progress = TrayElement<ProgressBar>(scene.Panel, "QuickPanelModelProgress");
            var reopenedPending = Assert.IsType<Border>(progress.Template.FindName("pending", progress));
            Assert.NotSame(pending, reopenedPending);
            Assert.True(reopenedPending.HasAnimatedProperties);
            Assert.False(retiredPending.HasAnimatedProperties);
            Assert.False(pending.HasAnimatedProperties);
            await AssertAfterTrayAnimationTick(() => scene.Settings.Update(original),
                () => Assert.False(reopenedPending.HasAnimatedProperties));
        }
        AssertTrayWithinWorkArea(scene.Panel);
        RenderTray(scene.Panel, $"tray-model-downloading-{(knownTotal ? "known" : "unknown")}.png");

        // Closing the tray is not cancellation of an explicitly started install.
        scene.Panel.Hide();
        Assert.False(transports[0].Body.Cancelled);
        Assert.Equal(TranslationPreparationPhase.Preparing, scene.Host.ReadQuickPanelPreparation().Phase);
        scene.Panel.OpenAtTray();
        var retiredPause = TrayProvider(TrayElement<Button>(scene.Panel, "QuickPanelModelAction"));
        retiredPause.Invoke();
        await Until(() => scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Paused);
        Assert.True(transports[0].Body.Cancelled);
        Assert.Equal("继续", TrayElement<Button>(scene.Panel, "QuickPanelModelAction").Content);
        Assert.False(TrayElement<ProgressBar>(scene.Panel, "QuickPanelModelProgress").IsIndeterminate);
        AssertRetiredTrayAction(scene, retiredPause);
        RenderTray(scene.Panel, "tray-model-paused.png");
        TrayInvoke(TrayElement<Button>(scene.Panel, "QuickPanelModelAction"));
        await Until(() => transports.Count == 2 && transports[1].Body.Started.Task.IsCompleted);
        Assert.Equal(transports[0].BodyUri, transports[1].BodyUri);
        Assert.Equal(2, scene.Attempts);
        Assert.False(scene.Model.Ready);
        Assert.Equal(settingsBefore, File.ReadAllText(scene.SettingsPath));
        Assert.False(File.Exists(Path.Combine(scene.Directory, "components.json")));
    });

    [Fact]
    public Task TrayRetryKeepsFailureRealAndCannotReuseAnOldActionAfterSettingsChange() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "dark", nativePresence: true, handler: () => new TrayFailureHttp());
        scene.Panel.OpenAtTray();
        await TrayConsent(scene.Host, () => TrayInvoke(TrayElement<Button>(scene.Panel, "QuickPanelModelAction")), accept: true);
        await Until(() => scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Failed);
        scene.Panel.OpenAtTray();
        Assert.Equal("Retry", TrayElement<Button>(scene.Panel, "QuickPanelModelAction").Content);
        Assert.Contains("download", TrayElement<TextBlock>(scene.Panel, "QuickPanelModelStatus").Text, StringComparison.OrdinalIgnoreCase);
        var stale = TrayProvider(TrayElement<Button>(scene.Panel, "QuickPanelModelAction"));
        scene.Settings.Update(scene.Settings.Value with
        {
            Accent = "sage"
        });
        int attempts = scene.Attempts;
        AssertRetiredTrayAction(scene, stale);
        Assert.Equal(attempts, scene.Attempts);
        TrayInvoke(TrayElement<Button>(scene.Panel, "QuickPanelModelAction"));
        await Until(() => scene.Attempts == attempts + 1 && scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Failed);
        Assert.False(scene.Model.Ready);
        Assert.Equal("sage", scene.Settings.Value.Accent);
        RenderTray(scene.Panel, "tray-model-failed-en-dark.png");
    });

    [Fact]
    public Task NewInstallationIdentityCannotInheritAnOldProgressOrResumeIntent() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("zh-CN", "light", nativePresence: true, handler: () => new TrayProgressHttp(true));
        scene.Panel.OpenAtTray();
        await TrayConsent(scene.Host, () => TrayInvoke(TrayElement<Button>(scene.Panel, "QuickPanelModelAction")), accept: true);
        scene.Panel.OpenAtTray();
        await Until(() => TrayElement<ProgressBar>(scene.Panel, "QuickPanelModelProgress").Value == 50);
        TrayInvoke(TrayElement<Button>(scene.Panel, "QuickPanelModelAction"));
        await Until(() => scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Paused);
        var oldResume = TrayProvider(TrayElement<Button>(scene.Panel, "QuickPanelModelAction"));
        int attempts = scene.Attempts;
        // A separately saved, inventoried selection is a different installation
        // generation. Protocol fixture bytes do not grant native/model readiness.
        using var replacement = ProtocolModel(scene.Directory, ["en_zh"]);
        scene.Host.ShowPage("model");
        scene.Panel.OpenAtTray();
        Assert.Contains("Argos", scene.Host.ReadQuickPanelPreparation().View.CurrentModelText, StringComparison.Ordinal);
        Assert.False(TrayElement<ProgressBar>(scene.Panel, "QuickPanelModelProgress").IsVisible);
        Assert.False(TrayElement<Button>(scene.Panel, "QuickPanelModelAction").IsEnabled);
        AssertRetiredTrayAction(scene, oldResume);
        Assert.Equal(attempts, scene.Attempts);
        Assert.False(scene.Model.Ready);
        Assert.Equal("argos-en-zh", Assert.Single(Installations(scene.Directory).ReadInstallationState().CurrentPackages).Package.Id);
    });

    private static T TrayElement<T>(QuickPanel panel, string id) where T : FrameworkElement
    {
        panel.UpdateLayout();
        return Assert.IsType<T>(Assert.Single(Descendants(panel).OfType<FrameworkElement>(), element => AutomationProperties.GetAutomationId(element) == id));
    }
    private static IInvokeProvider TrayProvider(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(
        UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke));
    private static void TrayInvoke(Button button) => TrayProvider(button).Invoke();

    private static void AssertRetiredTrayAction(TrayPreparationScene scene, IInvokeProvider old) => AssertRetiredTrayAction(scene, old.Invoke);

    private static void AssertRetiredTrayAction(TrayPreparationScene scene, Action old, bool allowNativeRejection = true)
    {
        bool unexpectedDialog = false;
        nint owner = new WindowInteropHelper(scene.Host).Handle;
        var guard = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(3) };
        guard.Tick += (_, _) =>
        {
            nint popup = GetWindow(owner, 6);
            if (popup != 0 && popup != owner && GetWindow(popup, 4) == owner)
            {
                unexpectedDialog = true;
                PostMessage(popup, 0x0111, 2, 0);
            }
        };
        guard.Start();
        try
        {
            try
            {
                old();
            }
            catch (ElementNotAvailableException) when (allowNativeRejection) { }
            catch (ElementNotEnabledException) when (allowNativeRejection) { }
            scene.Panel.Dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        }
        finally { guard.Stop(); }
        Assert.False(unexpectedDialog);
    }

    private static async Task TrayConsent(SettingsWindow ownerWindow, Action open, bool accept, Action<string>? inspect = null, Func<nint, Task>? beforeResponse = null)
    {
        nint owner = new WindowInteropHelper(ownerWindow).Handle;
        string expectedTitle = UiTheme.L("本地模型", "Local model");
        var responder = Task.Run(async () =>
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                nint dialog = GetWindow(owner, 6);
                if (dialog != 0 && dialog != owner && GetWindow(dialog, 4) == owner)
                {
                    var type = new StringBuilder(64);
                    GetClassName(dialog, type, type.Capacity);
                    if (type.ToString() == "#32770")
                    {
                        var title = new StringBuilder(128);
                        GetWindowText(dialog, title, title.Capacity);
                        bool expected = title.ToString() == expectedTitle;
                        var lines = new List<string>();
                        for (nint child = GetWindow(dialog, 5); child != 0; child = GetWindow(child, 2))
                        {
                            var kind = new StringBuilder(64);
                            GetClassName(child, kind, kind.Capacity);
                            if (kind.ToString() != "Static")
                                continue;
                            var message = new StringBuilder(8192);
                            GetWindowText(child, message, message.Capacity);
                            lines.Add(message.ToString());
                        }
                        try
                        {
                            if (expected && beforeResponse is not null)
                                await beforeResponse(dialog);
                        }
                        finally
                        {
                            Assert.True(PostMessage(dialog, 0x0111, expected && accept ? 1 : 2, 0));
                        }
                        Assert.True(expected);
                        inspect?.Invoke(string.Join("\n", lines));
                        return;
                    }
                }
                await Task.Delay(20);
            }
            nint remaining = GetWindow(owner, 6);
            if (remaining != 0 && remaining != owner && GetWindow(remaining, 4) == owner)
                PostMessage(remaining, 0x0010, 0, 0);
            Assert.Fail("The tray action did not present the existing native installation consent.");
        });
        open();
        await responder;
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }

    // RemoveStoryboard retires its clocks at the next timing tick, before
    // Rendering. Check that exact first frame, not "eventually stopped".
    private static async Task AssertAfterTrayAnimationTick(Action retire, Action assertStopped)
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? frame = null;
        frame = (_, _) =>
        {
            CompositionTarget.Rendering -= frame;
            try
            {
                assertStopped();
                rendered.TrySetResult();
            }
            catch (Exception error)
            {
                rendered.TrySetException(error);
            }
        };
        CompositionTarget.Rendering += frame;
        try
        {
            retire();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally
        {
            CompositionTarget.Rendering -= frame;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTrayWindowRect(nint window, out Native.Rect rect);

    private static void AssertTrayWithinWorkArea(QuickPanel panel)
    {
        panel.UpdateLayout();
        nint handle = new WindowInteropHelper(panel).Handle;
        Assert.True(GetTrayWindowRect(handle, out var rect));
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        Assert.True(Native.GetMonitorInfoW(Native.MonitorFromRect(ref rect, 2), ref monitor));
        Assert.InRange(rect.Left, monitor.Work.Left, monitor.Work.Right);
        Assert.InRange(rect.Right, rect.Left, monitor.Work.Right);
        Assert.InRange(rect.Top, monitor.Work.Top, monitor.Work.Bottom);
        Assert.InRange(rect.Bottom, rect.Top, monitor.Work.Bottom);
    }

    private static void RenderTray(Window window, string name)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render((FrameworkElement)window.Content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string directory = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private sealed class TrayPreparationScene : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "typelay-tray-model-" + Guid.NewGuid().ToString("N"));
        public string SettingsPath => Path.Combine(Directory, "settings.json");
        public SettingsStore Settings
        {
            get;
        }
        public ModelClient Model
        {
            get;
        }
        public SettingsWindow Host
        {
            get;
        }
        public QuickPanel Panel
        {
            get;
        }
        private readonly OverlayWindow _overlay;
        private readonly SessionController _session;
        public int Attempts
        {
            get; private set;
        }
        public TrayPreparationScene(string language, string theme, bool protocolModel = false, bool nativePresence = false, Func<HttpMessageHandler>? handler = null, string[]? directions = null, bool exactPackages = false)
        {
            Settings = WindowsRuntime.CreateSettings(SettingsPath);
            Settings.Update(Settings.Value with
            {
                UiLanguage = language,
                Theme = theme
            });
            Model = protocolModel ? ProtocolModel(Directory, directions, exactPackages) : SetupRenderingTests.Model(Directory);
            if (nativePresence)
                WriteTrayNativePresence(Directory);
            Host = new(Settings, Model, () => Assert.Fail("Tray model actions must not start practice."),
                installations: Installations(Directory), createInstaller: () =>
                {
                    Attempts++;
                    Assert.NotNull(handler);
                    return new ModelInstaller(Directory, Path.Combine(Directory, "worker.py"), handler());
                });
            Host.ShowPage("model");
            _overlay = new OverlayWindow(automationTest: true);
            _session = WindowsRuntime.CreateSession(Settings, Model, _overlay, automationTest: true);
            Panel = new(Settings, Model, _session, () => Host.ShowPage(), () => { }, Host);
        }
        public async ValueTask DisposeAsync()
        {
            Panel.Hide();
            Host.CancelPreparation();
            await Until(() => Host.ReadQuickPanelPreparation().Phase is not (TranslationPreparationPhase.Preparing or TranslationPreparationPhase.Pausing or TranslationPreparationPhase.StartingModel));
            Host.PrepareForEmergencyShutdown();
            Host.Close();
            _session.Dispose();
            Model.Dispose();
            _overlay.Close();
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, true);
        }
    }

    // Preparation presence only, identical to the pure contract fixture shape.
    // These bytes are never loaded/executed and cannot establish Ready or quality.
    private static void WriteTrayNativePresence(string directory)
    {
        const string template = "273d8e0e683b885071fb17e08d71e5f2a5ddfb5309756181681de4f5a1822d80";
        const string revision = "7aca0d2ea8d92b56911233b822db01f9fbb1ae50";
        string root = Path.Combine(directory, "native", "index"), cpu = Path.Combine(root, "cpu-runtime");
        System.IO.Directory.CreateDirectory(cpu);
        File.WriteAllText(Path.Combine(directory, "worker.py"), "controlled-presence-only");
        string[] names = ["llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu-x64.dll"];
        foreach (var name in names)
            File.WriteAllText(Path.Combine(cpu, name), "controlled-presence-only");
        foreach (var name in new[] { "typelay_index.dll", "LICENSE.llama.cpp", "LICENSE.Index-Translate" })
            File.WriteAllText(Path.Combine(root, name), "controlled-presence-only");
        object Record(string folder, string name) => new
        {
            file = name,
            bytes = new FileInfo(Path.Combine(folder, name)).Length,
            sha256 = Hash(Path.Combine(folder, name))
        };
        File.WriteAllText(Path.Combine(root, "cpu-runtime.win-x64.lock.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            platform = "win-x64",
            upstream_revision = revision,
            files = names.Select(name => Record(cpu, name))
        }));
        File.WriteAllText(Path.Combine(root, "model.lock.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            abi = 1,
            template_sha256 = template,
            package_id = "index-translate-2b-q4-k-m",
            version = "3a0eca6ce931d88d00614d427ab3ed8ff0743902"
        }));
        File.WriteAllText(Path.Combine(root, "production-adapter.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            abi = 1,
            platform = "win-x64",
            template_sha256 = template,
            upstream_revision = revision,
            exports = new[] { "index_close", "index_open_v1", "index_translate_alternative_v1", "index_translate_v1" },
            capabilities = new
            {
                styles = new[] { "natural", "formal" },
                glossary = true,
                styled_glossary = true,
                alternative = true
            },
            adapter = Record(root, "typelay_index.dll"),
            license = Record(root, "LICENSE.llama.cpp"),
            template_license = Record(root, "LICENSE.Index-Translate")
        }));
        Assert.True(WindowsIndexPayload.CanPrepare(Path.Combine(directory, "worker.py")));
    }

    private sealed class TrayFailureHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("controlled-network-failure");
    }

    private sealed class TrayProgressHttp(bool knownTotal, bool allowRemainder = false, bool allowFinalSegment = false) : HttpMessageHandler
    {
        public TrayProgressStream Body { get; } = new(allowRemainder, allowFinalSegment);
        public Uri? BodyUri
        {
            get; private set;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object? value = request.RequestUri!.AbsolutePath switch
            {
                "/v3/index.json" => new { resources = new[] { new Dictionary<string, string> { ["@type"] = "RegistrationsBaseUrl/3.6.0", ["@id"] = "https://api.nuget.org/registration/" } } },
                "/registration/python/3.13.16.json" => new { packageContent = "https://api.nuget.org/python.zip", catalogEntry = "https://api.nuget.org/catalog/python.json" },
                "/catalog/python.json" => new { id = "python", version = "3.13.16", packageHashAlgorithm = "SHA512", packageHash = Convert.ToBase64String(new byte[64]) },
                _ => null
            };
            if (value is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) });
            Assert.Equal("/python.zip", request.RequestUri.AbsolutePath);
            BodyUri = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Body) };
            if (knownTotal)
                response.Content.Headers.ContentLength = 131072;
            return Task.FromResult(response);
        }
    }

    private sealed class TrayProgressStream(bool allowRemainder = false, bool allowFinalSegment = false) : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled
        {
            get; private set;
        }
        private bool _sent, _remainderSent, _finalSent;
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinalSegment { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                Assert.True(buffer.Length >= 65536);
                buffer.Span[..65536].Clear();
                Started.TrySetResult();
                return 65536;
            }
            try
            {
                if (allowRemainder && !_remainderSent)
                {
                    await Continue.Task.WaitAsync(cancellationToken);
                    _remainderSent = true;
                    Assert.True(buffer.Length >= 32768);
                    buffer.Span[..32768].Clear();
                    return 32768;
                }
                if (allowFinalSegment && !_finalSent)
                {
                    await FinalSegment.Task.WaitAsync(cancellationToken);
                    _finalSent = true;
                    Assert.True(buffer.Length >= 16384);
                    buffer.Span[..16384].Clear();
                    return 16384;
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Cancelled = true; throw; }
            throw new InvalidOperationException("The controlled download must be paused before completion.");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
