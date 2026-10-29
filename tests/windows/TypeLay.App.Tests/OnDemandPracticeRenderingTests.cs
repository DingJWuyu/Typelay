using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using N = TypeLay.Platform.Windows.NativeMethods;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class OnDemandPracticeRenderingTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task PreviewCueWaitsForARealReadAndWithdrawsWhenPublicationExpires() => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        string directory = Path.Combine(Path.GetTempPath(), "typelay-practice-preview-cue-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        ChildProcess? child = null;
        var overlay = new OverlayWindow(automationTest: true);
        using var broker = new PausedCaptureBroker(deadline.Token);
        try
        {
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Mode = WorkMode.OnDemand
            });
            using var model = WindowsRuntime.CreateModel(); // Real observations, no inference required.
            using var host = new PracticeHost(info => child = new ChildProcess(info));
            using var controller = WindowsRuntime.CreateSession(settings, model, overlay,
                automationTest: true, broker: broker, practice: host);
            host.Bind(controller);
            await controller.StartAsync().WaitAsync(deadline.Token);
            host.Open(path);
            nint root = 0;
            await Until(() =>
            {
                Assert.False(child!.Process.HasExited);
                child.Process.Refresh();
                root = child.Process.MainWindowHandle;
                return root != 0 && GetDlgItem(root, 103) != 0 && controller.PracticeProgress is not null;
            });
            FocusPracticeField(root, child!.Process, "preview-initial", deadline.Token);
            await Until(() => N.Focused() == (root, GetDlgItem(root, 101)));
            await Until(() => controller.PracticeProgress is { PreviewReady: false, DirectReady: false } && broker.Waiting);
            var initial = controller.PracticeProgress!;
            string ReadyNotice() => OnDemandPracticePresentation.Create(settings.Value,
                controller.PracticeProgress! with
                {
                    PreviewReady = true
                }).ProgressNotice;
            Assert.DoesNotContain("已就绪", N.ReadStaticNotice(GetDlgItem(root, 103)));
            Assert.Null(initial.Target); // A held real capture cannot bind or announce readiness.
            broker.Resume();
            await Until(() => controller.PracticeProgress?.Target is not null
                && N.ReadStaticNotice(GetDlgItem(root, 103)) == ReadyNotice());
            var bound = controller.PracticeProgress!;
            Assert.Equal(initial.RunId, bound.RunId);
            Assert.True(bound.PreviewReady);
            await Until(() => controller.PracticeProgress?.DirectReady == true);
            broker.Pause(); // No replacement read can keep an old publication alive.
            await Until(() => broker.Waiting && controller.PracticeProgress is { PreviewReady: false, DirectReady: false }
                && N.ReadStaticNotice(GetDlgItem(root, 103)) == OnDemandPracticePresentation.Create(settings.Value,
                    controller.PracticeProgress).ProgressNotice);
            Assert.Equal(bound.Target, controller.PracticeProgress!.Target);
            Assert.False(controller.PracticeProgress.PreviewCompleted);
            Assert.False(controller.PracticeProgress.Completed);
            Assert.Equal(0, broker.Writes);
            Assert.Equal(OnDemandPracticePresentation.Create(settings.Value).SampleText,
                N.ReadText(GetDlgItem(root, 101), Limits.MaxTextUnits));
            broker.Resume();
            await Until(() => controller.PracticeProgress?.DirectReady == true
                && N.ReadStaticNotice(GetDlgItem(root, 103)) == ReadyNotice());
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                WakeShortcut = "Alt+F8"
            });
            await Until(() => controller.PracticeProgress is null
                && !N.ReadStaticNotice(GetDlgItem(root, 103)).Contains("已就绪", StringComparison.Ordinal));
            Assert.Equal(0, broker.Writes);

            async Task Until(Func<bool> condition)
            {
                while (!condition())
                    await Task.Delay(15, deadline.Token);
            }
        }
        finally
        {
            broker.Resume();
            overlay.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public Task PreviewCompletedCueRendersDirectReadinessAndWithdrawalFromTheProgressPipe(string language) => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        string directory = Path.Combine(Path.GetTempPath(), "typelay-practice-direct-cue-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        ChildProcess? child = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                UiLanguage = language,
                Mode = WorkMode.OnDemand,
                DirectShortcut = "Alt+F9"
            });
            Guid connection = Guid.NewGuid(), run = Guid.NewGuid();
            var start = AppPaths.Child("TypeLay.Practice");
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add("--progress");
            start.ArgumentList.Add(connection.ToString("N"));
            child = new ChildProcess(start);
            await PracticeProgressPipe.WriteAsync(child.Process.StandardInput.BaseStream,
                new("begin", connection, run, 1), deadline.Token);
            var started = await PracticeProgressPipe.ReadAsync(child.Process.StandardOutput.BaseStream, deadline.Token);
            Assert.Equal("started", started.Kind);
            Assert.Equal(connection, started.Connection);
            Assert.Equal(run, started.Run);
            Assert.Equal(1, started.Sequence);
            nint root = 0;
            await Until(() =>
            {
                child.Process.Refresh();
                root = child.Process.MainWindowHandle;
                return root != 0 && GetDlgItem(root, 103) != 0;
            });

            // Controlled body-free progress tests the production pipe and native renderer,
            // not inference completion, capture authority, or permission to send input.
            var progress = new OnDemandPracticeProgress(run, new("render-only", "field", Guid.NewGuid()),
                Guid.NewGuid(), 1, 1, true, false);
            (bool? Ready, string Notice)[] states =
            [
                (null, language == "en" ? "Preview generated. Return to the practice field for direct replacement." : "已生成预览。请回到练习输入框，完成第 2 步直接替换。"),
                (false, language == "en" ? "Preview generated. Return to the practice field and wait for direct replacement to be ready." : "已生成预览。请回到练习输入框，等待直接替换快捷键就绪。"),
                (true, language == "en" ? "Preview generated. Direct replacement is ready: press Alt+F9 for step 2." : "已生成预览。直接替换快捷键已就绪，可按 Alt+F9 完成第 2 步。"),
                (false, language == "en" ? "Preview generated. Return to the practice field and wait for direct replacement to be ready." : "已生成预览。请回到练习输入框，等待直接替换快捷键就绪。")
            ];
            foreach (var state in states)
            {
                await PracticeProgressPipe.WriteAsync(child.Process.StandardInput.BaseStream,
                    new("progress", connection, run, 1, progress with
                    {
                        DirectReady = state.Ready
                    }), deadline.Token);
                await Until(() => N.ReadStaticNotice(GetDlgItem(root, 103)) == state.Notice);
            }
            Assert.Equal(OnDemandPracticePresentation.Create(settings.Value).SampleText,
                N.ReadText(GetDlgItem(root, 101), Limits.MaxTextUnits));

            async Task Until(Func<bool> condition)
            {
                while (!condition())
                {
                    Assert.False(child.Process.HasExited);
                    await Task.Delay(15, deadline.Token);
                }
            }
        }
        finally
        {
            try
            {
                if (child is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await child.TerminateAndWaitAsync(cleanup.Token);
                }
            }
            finally
            {
                child?.Dispose();
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }
    });

    // Hold the real Broker request, not a manufactured capture or shortcut ACK.
    private sealed class PausedCaptureBroker(CancellationToken cancellation) : IInputBroker
    {
        private readonly BrokerClient _inner = new();
        private TaskCompletionSource? _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Waiting
        {
            get; private set;
        }
        public int Writes
        {
            get; private set;
        }
        public bool Available => _inner.Available;
        public int ProcessId => _inner.ProcessId;
        public Task StartAsync() => _inner.StartAsync();
        public void Pause() => _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Resume()
        {
            _resume?.TrySetResult();
            _resume = null;
        }
        public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
        {
            if (request.Op is "write" or "direct-write")
                Writes++;
            if (request.Op == "capture" && _resume is { } held)
            {
                Waiting = true;
                try
                {
                    await held.Task.WaitAsync(cancellation);
                }
                finally { Waiting = false; }
            }
            return await _inner.CallAsync(request, timeout);
        }
        public void Dispose()
        {
            Resume();
            _inner.Dispose();
        }
    }

    [Theory]
    [InlineData("current")]
    [InlineData("restart")]
    [InlineData("dispose")]
    public Task PracticeExitFeedbackBelongsToTheCurrentWindowOnTheUiThread(string transition) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-practice-exit-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        PracticeHost? host = null;
        Process? active = null;
        int starts = 0;
        try
        {
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Mode = WorkMode.OnDemand
            });
            var dispatcher = Dispatcher.CurrentDispatcher;
            var notifications = new List<bool>();
            host = new PracticeHost(info =>
            {
                active = null; // The previous child has already been disposed before a restart.
                var child = new ChildProcess(info); // Launch the production practice from the build output, not a fake process.
                active = child.Process;
                starts++;
                return child;
            });
            host.Failed += () => notifications.Add(dispatcher.CheckAccess());
            host.Open(path);
            Assert.NotNull(active);
            Assert.False(active.HasExited);
            host.Open(path);
            Assert.Equal(1, starts); // Repeated starts keep the one owned live host.

            // Hold this UI turn until the real exit handlers have run. Their UI
            // callbacks must wait; a newer request can arrive before delivery.
            using (var exited = new ManualResetEventSlim())
            {
                EventHandler observed = (_, _) => exited.Set();
                active.Exited += observed; // Registered after the production exit handler.
                try
                {
                    active.Kill();
                    Assert.True(exited.Wait(TimeSpan.FromSeconds(5)), "The owned practice did not report exit.");
                    Assert.True(active.WaitForExit(5000));
                    Assert.NotEqual(0, active.ExitCode);
                }
                finally { active.Exited -= observed; }
            }
            Assert.Empty(notifications);
            if (transition == "restart")
            {
                host.Open(path);
                Assert.Equal(2, starts);
                Assert.False(active.HasExited);
                host.Open(path);
                Assert.Equal(2, starts);
            }
            else if (transition == "dispose")
            {
                host.Dispose();
                active = null;
            }
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (transition == "current")
                Assert.True(Assert.Single(notifications));
            else
                Assert.Empty(notifications);
        }
        finally
        {
            try
            {
                if (active is not null)
                {
                    if (!active.HasExited)
                        active.Kill();
                    Assert.True(active.WaitForExit(5000), "The owned practice did not stop during cleanup.");
                }
            }
            finally
            {
                host?.Dispose();
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }
    });

    [Fact]
    public async Task PracticeWithoutAReadyModelReturnsToPreparationWithoutChangingMode()
    {
        await desktop.RunAsync(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-practice-gate-" + Guid.NewGuid().ToString("N"));
            SettingsWindow? window = null;
            try
            {
                var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
                settings.Update(settings.Value with
                {
                    Mode = WorkMode.Automatic,
                    OnboardingComplete = false
                });
                using var model = new ModelClient(new(Path.Combine(directory, "missing-python"),
                    Path.Combine(directory, "missing-worker"), Path.Combine(directory, "missing-models")),
                    info => new ChildProcess(info)); // No worker started or installation requested.
                int launches = 0;
                window = new SettingsWindow(settings, model, () => launches++,
                    installations: new ModelInstallationStore(directory, Path.Combine(directory, "worker.py")));
                window.ShowPage();
                window.UpdateLayout();
                var start = Descendants(window).OfType<Button>().Single(button =>
                    Equals(button.Content, AutomaticPracticePresentation.Create(settings.Value).StartLabel));
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.Equal(0, launches);
                Assert.False(model.Ready);
                Assert.Equal(WorkMode.Automatic, WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json")).Value.Mode);
                Assert.False(settings.Value.OnboardingComplete);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                    text.Text == ApplicationSupportPresentation.Create([]).Title);
                var prepare = Descendants(window).OfType<Button>().Single(button => button.Name == "PreparationPrimaryAction");
                Assert.Equal("准备 Index 本地翻译…", prepare.Content);
                Assert.False(prepare.IsEnabled); // No Index native payload; never fall back implicitly.
                var details = Descendants(window).OfType<Expander>().Single(expander => Equals(expander.Header, "组件与安装说明"));
                details.IsExpanded = true;
                window.UpdateLayout();
                var explicitArgos = Descendants(window).OfType<Button>().Single(button =>
                    AutomationProperties.GetAutomationId(button) == "ModelPackage_argos-zh-en_Install");
                Assert.True(explicitArgos.IsEnabled);
            }
            finally
            {
                window?.PrepareForEmergencyShutdown();
                window?.Close();
                Directory.Delete(directory, true);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RetiredAutomaticPracticeDoesNotReserveUnrelatedInputWindows(bool disconnect) => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        string directory = Path.Combine(Path.GetTempPath(), "typelay-practice-route-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        ChildProcess? owned = null, ordinary = null;
        var overlay = new OverlayWindow(automationTest: true);
        try
        {
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Mode = WorkMode.Automatic
            });
            using var model = WindowsRuntime.CreateModel(); // Capture and retirement need no inference.
            using var host = new PracticeHost(info => owned = new ChildProcess(info));
            using var other = new PracticeHost(info => ordinary = new ChildProcess(info));
            using var broker = new BrokerClient();
            using var controller = WindowsRuntime.CreateSession(settings, model, overlay,
                automationTest: true, broker: broker, practice: host);
            host.Bind(controller);
            await controller.StartAsync().WaitAsync(deadline.Token);
            var input = new WindowsInputService(broker, 0, practice: host);
            var context = new InputContext(0, 0, 0, null, [], false);
            other.Open(path); // No progress connection or owned-native registration.
            nint otherRoot = await Window(ordinary!);
            host.Open(path);
            nint ownedRoot = await Window(owned!);
            await Focus(ownedRoot, owned!.Process, "owned-initial");
            await Until(() => controller.AutomaticPracticeProgress?.Target is not null);

            var next = settings.Value with
            {
                Mode = WorkMode.OnDemand
            };
            if (disconnect)
                owned!.Process.StandardInput.Close(); // A real lost control pipe is not a retirement ACK.
            Task<bool> retirement = controller.PrepareSettingsChangeAsync(next);
            Assert.Null((await input.CaptureAsync(context)).Snapshot); // Request alone grants no fallback.
            Assert.Equal(!disconnect, await retirement.WaitAsync(deadline.Token));
            if (disconnect)
            {
                Assert.False(owned!.Process.HasExited);
                Assert.False(await controller.PrepareSettingsChangeAsync(next));
                Assert.Equal(WorkMode.Automatic, settings.Value.Mode);
                Assert.Null((await input.CaptureAsync(context)).Snapshot);
                Assert.True(N.PostMessage(ownedRoot, 0x0010, 0, 0));
                await owned.Process.WaitForExitAsync(deadline.Token);
                Assert.True(await controller.PrepareSettingsChangeAsync(next));
                Assert.Null(controller.AutomaticPracticeProgress);
            }
            settings.Update(next);
            Assert.Equal(!disconnect, host.IsAutomatic); // A normal ACK retains identity for restart.

            await Focus(otherRoot, ordinary!.Process, "unrelated-after-retirement");
            InputSnapshot? captured = null;
            while (captured is null)
            {
                captured = (await input.CaptureAsync(context).WaitAsync(deadline.Token)).Snapshot;
                if (captured is null)
                    await Task.Delay(15, deadline.Token);
            }
            Assert.True(WindowsTarget.TryDecode(captured.Target, out var target));
            Assert.Equal(ordinary!.Process.Id, target.ProcessId);
            Assert.Equal((long)otherRoot, target.RootHwnd);
            Assert.Equal((long)GetDlgItem(otherRoot, 101), target.ControlHwnd);
            string expectedText = OnDemandPracticePresentation.Create(settings.Value).SampleText;
            if (captured.Ime == ImeState.Clear)
                Assert.Equal(expectedText, captured.Text);
            else
            {
                Assert.Equal(ImeState.Unknown, captured.Ime);
                Assert.Empty(captured.Text);
                Assert.Equal(expectedText, captured.PreviewText);
                Assert.False(captured.WriteSupported);
            }

            if (!disconnect)
            {
                await Focus(ownedRoot, owned!.Process, "owned-after-retirement");
                Assert.Null((await input.CaptureAsync(context)).Snapshot); // Old owned root stays reserved until exit.
            }
            Assert.Null(controller.AutomaticPracticeProgress);

            async Task<nint> Window(ChildProcess child)
            {
                nint root = 0;
                await Until(() =>
                {
                    Assert.False(child.Process.HasExited);
                    child.Process.Refresh();
                    root = child.Process.MainWindowHandle;
                    return root != 0 && GetDlgItem(root, 101) != 0;
                });
                return root;
            }
            async Task Focus(nint root, Process process, string phase)
            {
                FocusPracticeField(root, process, phase, deadline.Token);
                N.FocusObservation last = default;
                nint field = 0;
                try
                {
                    await Until(() =>
                    {
                        // Focused() uses this same single native observation.
                        last = N.ReadFocus();
                        field = GetDlgItem(root, 101);
                        return (last.Root, last.Control) == (root, field);
                    });
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested
                    && !TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                    string foreground = last.Root == 0 ? "none" : last.Root == ownedRoot ? "owned-root"
                        : last.Root == otherRoot ? "unrelated-root" : last.ProcessId == Environment.ProcessId ? "test-process"
                        : last.ProcessId == owned!.Process.Id ? "owned-process-other-window"
                        : last.ProcessId == ordinary!.Process.Id ? "unrelated-process-other-window"
                        : last.ProcessId == 0 ? "unknown" : "other-process";
                    Assert.Fail($"Practice focus deadline: phase={phase}; rootMatched={last.Root == root}; "
                        + $"controlMatched={last.Control == field}; fieldPresent={field != 0}; foreground={foreground}; "
                        + $"guiSucceeded={last.GuiThreadInfoSucceeded}; guiError={last.GuiThreadInfoError}; threadError={last.ThreadLookupError}; "
                        + $"rootAlive={N.IsWindow(root)}; rootVisible={N.IsWindowVisible(root)}; rootEnabled={N.IsWindowEnabled(root)}; "
                        + $"fieldAlive={N.IsWindow(field)}; fieldVisible={N.IsWindowVisible(field)}; fieldEnabled={N.IsWindowEnabled(field)}.");
                }
            }
            async Task Until(Func<bool> condition)
            {
                while (!condition())
                    await Task.Delay(15, deadline.Token);
            }
        }
        finally
        {
            overlay.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task CurrentPracticeBindsOnlyItsOwnedNativeFieldAndRequiresExplicitRestart() => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        string directory = Path.Combine(Path.GetTempPath(), "typelay-practice-progress-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        ChildProcess? child = null, other = null;
        var overlay = new OverlayWindow(automationTest: true);
        try
        {
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Mode = WorkMode.OnDemand,
                OnboardingComplete = true
            });
            using var model = WindowsRuntime.CreateModel(); // Binding/clearing does not need inference or a downloaded model.
            using var host = new PracticeHost(info => child = new ChildProcess(info));
            using var unrelated = new PracticeHost(info => other = new ChildProcess(info));
            var support = new WindowsApplicationSupport();
            using var controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true,
                support: support, practice: host);
            host.Bind(controller);
            await controller.StartAsync().WaitAsync(deadline.Token);
            unrelated.Open(path);
            nint otherRoot = await Window(other!);
            host.Open(path);
            await Until(() => controller.PracticeProgress is not null);
            Guid firstRun = controller.PracticeProgress!.RunId;
            nint root = await Window(child!);
            nint field = GetDlgItem(root, 101);
            await Focus(root, child!.Process);
            await Until(() => controller.PracticeProgress?.Target is not null);
            await Until(() => controller.PracticeProgress?.PreviewReady == true
                && N.ReadStaticNotice(GetDlgItem(root, 103)) == OnDemandPracticePresentation.Create(settings.Value,
                    controller.PracticeProgress).ProgressNotice);
            var first = controller.PracticeProgress!;
            Assert.False(first.Completed); // Historical onboarding preference is never evidence.
            Assert.False(first.PreviewCompleted);
            Assert.False(first.DirectReplacementCompleted);
            Assert.True(WindowsTarget.TryDecode(first.Target!.Value, out var target));
            Assert.Equal(child!.Process.Id, target.ProcessId);
            Assert.Equal(child.Process.StartTime.ToUniversalTime().Ticks, target.ProcessStartUtcTicks);
            Assert.Equal((long)root, target.RootHwnd);
            Assert.Equal((long)field, target.ControlHwnd);
            Assert.Equal("win32-edit-transaction-v2", target.Provider);
            Assert.NotEqual(Guid.Empty, target.Lifetime);
            nint firstCookie = N.GetProp(field, "TypeLay.NativeInput.Lifetime.v2");
            Assert.NotEqual(nint.Zero, firstCookie);

            await Focus(otherRoot, other!.Process);
            await Until(() => controller.PracticeProgress?.PreviewReady == false
                && N.ReadStaticNotice(GetDlgItem(root, 103)) == OnDemandPracticePresentation.Create(settings.Value,
                    controller.PracticeProgress).ProgressNotice);
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Theme = "dark"
            });
            Assert.Null(controller.PracticeProgress);
            // A click may cross the already-sent retirement. It must wait for
            // that ACK rather than disconnect or begin on the old lifetime.
            N.Send(GetDlgItem(root, 102), 0x00f5); // Click the real restart button without changing source focus.
            await Until(() => controller.PracticeProgress is { Target: null } progress && progress.RunId != firstRun);
            Guid secondRun = controller.PracticeProgress!.RunId;
            bool checkedOther = false;
            support.CheckCompleted += _ => checkedOther = true;
            support.BeginCheck("desktop-text");
            await Until(() => checkedOther); // A real production capture of the same-titled foreign process completed.
            Assert.Null(controller.PracticeProgress!.Target);
            Assert.False(controller.PracticeProgress.Completed);
            await Focus(root, child!.Process);
            await Until(() => controller.PracticeProgress?.Target is not null);
            Assert.Equal(secondRun, controller.PracticeProgress!.RunId);
            Assert.NotEqual(firstCookie, N.GetProp(field, "TypeLay.NativeInput.Lifetime.v2"));
            Assert.NotEqual(nint.Zero, N.GetProp(field, "TypeLay.NativeInput.Lifetime.v2"));
            Assert.False(controller.PracticeProgress.PreviewCompleted);
            Assert.False(controller.PracticeProgress.DirectReplacementCompleted);

            Assert.True(N.PostMessage(root, 0x0010, 0, 0));
            await child.Process.WaitForExitAsync(deadline.Token);
            await Until(() => controller.PracticeProgress is null);
            host.Open(path);
            await Until(() => controller.PracticeProgress is not null);
            Assert.NotEqual(secondRun, controller.PracticeProgress!.RunId);
            Assert.False(controller.PracticeProgress.Completed);
            Assert.Equal(WorkMode.OnDemand, settings.Value.Mode);
            Assert.False(settings.Value.ExperimentalWriteback);
            Assert.True(settings.Value.OnboardingComplete);

            async Task<nint> Window(ChildProcess process)
            {
                nint window = 0;
                await Until(() =>
                {
                    Assert.False(process.Process.HasExited);
                    process.Process.Refresh();
                    window = process.Process.MainWindowHandle;
                    return window != 0 && GetDlgItem(window, 101) != 0;
                });
                return window;
            }
            async Task Focus(nint window, Process process)
            {
                FocusPracticeField(window, process, "current-practice", deadline.Token);
                await Until(() => N.Focused() == (window, GetDlgItem(window, 101)));
            }
            async Task Until(Func<bool> condition)
            {
                while (!condition())
                    await Task.Delay(15, deadline.Token);
            }
        }
        finally
        {
            overlay.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    private static void FocusPracticeField(nint root, Process process, string phase, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        nint field = GetDlgItem(root, 101);
        Assert.True(!process.HasExited
            && N.GetWindowThreadProcessId(root, out uint rootPid) != 0 && rootPid == process.Id
            && N.GetWindowThreadProcessId(field, out uint fieldPid) != 0 && fieldPid == process.Id
            && N.GetAncestor(field, 2) == root,
            $"Practice source focus: phase={phase}; owned root/field identity changed.");
        // These tests exercise binding, retirement and progress, not pointer
        // targeting. Use the same public focus setup as OwnedPracticeQueueTests;
        // the caller still observes the exact native root and field afterward.
        ct.ThrowIfCancellationRequested();
        Assert.True(N.SetForegroundWindow(root), $"Practice source focus: phase={phase}; foreground request refused.");
        ct.ThrowIfCancellationRequested();
        AutomationElement.FromHandle(field).SetFocus();
    }

    [DllImport("user32.dll")]
    private static extern nint GetDlgItem(nint window, int id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
