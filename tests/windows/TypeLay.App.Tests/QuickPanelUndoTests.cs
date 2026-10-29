using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class QuickPanelUndoTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task RetiredUndoCannotReenableANewerExclusionOrRunAfterPanelClosure() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-panel-undo-" + Guid.NewGuid().ToString("N"));
        var view = new MetadataView();
        QuickPanel? panel = null;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            using var model = WindowsRuntime.CreateModel();
            var loop = new MetadataLoop();
            using var input = new MetadataInput(loop);
            using var controller = new SessionController(settings, model, view, input, input, loop);
            await controller.StartAsync();
            loop.Advance();
            Assert.Equal("controlled", controller.GetStyleSettingsState().Application);
            Assert.Equal("controlled", input.ObservedShortcut?.Application);
            Assert.False(input.ShortcutsReady);
            Assert.False(input.CanInterceptAcceptance);
            panel = new(settings, model, controller, () => { }, () => { });
            panel.OpenAtTray();
            Toggle().IsChecked = false;
            var currentUndo = Undo();
            Click(currentUndo);
            Assert.Empty(WindowsRuntime.CreateSettings(path).Value.Excluded);

            Toggle().IsChecked = false;
            var oldUndo = Undo();
            settings.Update(settings.Value with
            {
                ExcludedApplications = []
            });
            settings.Update(settings.Value with
            {
                ExcludedApplications = ["controlled"]
            });
            long latestRevision = settings.Revision;
            Click(oldUndo);
            Assert.Equal(latestRevision, settings.Revision);
            Assert.Equal(new[] { "controlled" }, WindowsRuntime.CreateSettings(path).Value.Excluded);

            Toggle().IsChecked = true;
            Toggle().IsChecked = false;
            var reopenedUndo = Undo();
            Assert.NotSame(oldUndo, reopenedUndo);
            Click(oldUndo);
            Assert.Same(reopenedUndo, Undo());
            Assert.True(reopenedUndo.IsVisible);
            panel.Hide();
            Click(reopenedUndo);
            Assert.Equal(new[] { "controlled" }, WindowsRuntime.CreateSettings(path).Value.Excluded);
            Assert.Equal(0, input.Writes);
            CheckBox Toggle()
            {
                panel.UpdateLayout();
                return Descendants(panel).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "在应用中启用 TypeLay");
            }
            Button Undo()
            {
                panel.UpdateLayout();
                return Descendants(panel).OfType<Button>().Single(button => Equals(button.Content, "撤销"));
            }
        }
        finally
        {
            panel?.Hide();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    // The actual UI under test is QuickPanel. A controlled metadata source has
    // no Windows HWND, so it must not be rendered through a native source overlay.
    private sealed class MetadataView : ISessionView
    {
        public Func<SessionViewToken, bool>? BeginEditing
        {
            get; set;
        }
        public Action<string>? DraftChanged
        {
            get; set;
        }
        public Action<SessionViewToken>? ReturnRequested
        {
            get; set;
        }
        public Action<SessionViewToken>? EscapeRequested
        {
            get; set;
        }
        public Action<SessionViewToken>? AlternativeRequested
        {
            get; set;
        }
        public Func<SessionStyleRequest, Task<StyleChangeResult>>? StyleRequested
        {
            get; set;
        }
        public Action? RegenerateRequested
        {
            get; set;
        }
        public Func<SessionViewToken, SessionGlossaryEntry?>? BeginGlossaryRequested
        {
            get; set;
        }
        public Func<SessionGlossaryRequest, Task<GlossaryChangeResult>>? AddGlossaryRequested
        {
            get; set;
        }
        public Action<Guid>? CloseGlossaryRequested
        {
            get; set;
        }
        public SessionGlossaryFocus? GlossaryEntryFocus => null;
        public Action? ExcludeRequested
        {
            get; set;
        }
        public Action<long>? ResumeRequested
        {
            get; set;
        }
        public bool EditingComposition => false;
        public void Configure(UserSettings settings)
        {
        }
        public void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null, SessionViewContext? context = null) => Assert.False(canAccept);
        public void RenderPaused(PauseAnchor anchor, long revision)
        {
        }
        public void HideQuietly()
        {
        }
        public void ResumeEditor()
        {
        }
    }

    // Controlled public observation ports expose only the current-app metadata
    // needed by this UI test. No model is ready; no write capability is granted.
    private sealed class MetadataLoop : ISessionLoop
    {
        public long NowMs { get; private set; } = 1000;
        public event Action? Tick;
        public void Advance()
        {
            NowMs += 40;
            Tick?.Invoke();
        }
        public void Start()
        {
        }
        public void Stop()
        {
        }
    }
    private sealed class MetadataInput(MetadataLoop loop) : IInputMonitor, IInputService
    {
        private readonly TargetId _target = new("controlled", "metadata-only", Guid.NewGuid());
        public int Writes
        {
            get; private set;
        }
        public bool Ready => true;
        public bool Available => true;
        public bool ShortcutsReady => false;
        public bool CanInterceptAcceptance => false;
        public string ShortcutStatus => "unavailable";
        public long UserEpoch => 1;
        public long SourceUserEpoch => 1;
        public long ForegroundEpoch => 1;
        public long WindowEpoch => 1;
        public long ContentEpoch => 1;
        public Guid CommandGeneration { get; } = Guid.NewGuid();
        public FocusMember GetFocus(TargetId target) => target == _target ? FocusMember.Source : FocusMember.Other;
        public void ConfigureShortcuts(bool enabled, string wake, string direct, SessionShortcutBindings sessionBindings, bool sessionEnabled)
        {
        }
        public bool TryRead(out InputNotice? notice)
        {
            notice = null;
            return false;
        }
        public void Publish(InputLease lease, AcceptancePermit? permit) => Assert.Null(permit);
        public ShortcutLease? ObservedShortcut
        {
            get; private set;
        }
        // The controller publishes a read-only binding in both modes. Recording
        // it does not register hotkeys, emit commands or grant an acceptance permit.
        public void PublishShortcut(ShortcutLease lease) => ObservedShortcut = lease;
        public void Revoke()
        {
        }
        public Task StartAsync() => Task.CompletedTask;
        public Task<InputCapture> CaptureAsync(InputContext context) => Task.FromResult(new InputCapture(
            new(_target, "term", new(4, 4), new(10, 20, 300, 40), false, FieldAccess.Supported, ImeState.Clear, loop.NowMs, 1, 1),
            FocusMember.Source, "controlled", 1, 1));
        public Task<PauseObservation> ObservePauseAsync(InputContext context) => Task.FromResult(new PauseObservation(null));
        public Task<InputReturn> ReturnToSourceAsync(InputContext context) => Task.FromResult(new InputReturn(false));
        public Task<WriteReceipt> WriteAsync(WritePlan plan, InputContext context, long expiresAtMs)
        {
            Writes++;
            throw new InvalidOperationException("No write authority.");
        }
        public Task<WriteReceipt> WriteDirectAsync(WritePlan plan, string[] excluded)
        {
            Writes++;
            throw new InvalidOperationException("No write authority.");
        }
        public Task ClearAsync(InputContext context) => Task.CompletedTask;
        public void Dispose()
        {
        }
    }
}
