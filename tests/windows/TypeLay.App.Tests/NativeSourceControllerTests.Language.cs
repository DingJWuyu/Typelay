using System.IO;
using System.Collections.Concurrent;
using TypeLay.Windows.Contracts;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Protocol;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    [Fact]
    public Task LanguageConfigurationIsRejectedAsASourceByTheRealBrokerAndRepeatedCaptureEmptyKeepsOnlyConfiguration()
        => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        await native.Prepare(native.A, "你好", new(0, 2));
        using var broker = new FixtureBroker(native);
        InputSnapshot? captured = null;
        broker.ObserveResponse = (request, response) =>
        {
            if (request.Op == "capture" && response.Focus == FocusMember.Source && response.Snapshot is { } source)
                captured = source;
        };
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.LanguageConfigurationBroker." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Source = "auto",
                Target = "en",
                UiLanguage = "zh-CN"
            });
            var translation = new LanguagePreviewTranslation();
            using var model = new ModelClient(new(Path.Combine(directory, "unused-python"), Path.Combine(directory, "unused-worker"), directory),
                _ => throw new InvalidOperationException("The language selector must not start another model."));
            using var controller = WindowsRuntime.CreateSession(settings, translation, overlay, automationTest: true, broker: broker);
            var quick = new QuickPanel(settings, model, controller, () => Assert.Fail("No settings window."), () => Assert.Fail("No quit."));
            overlay.NativeLanguageRequested = anchor =>
            {
                controller.StopSession();
                overlay.ShowLanguageConfiguration(anchor, settings, model,
                    (source, target, current) => quick.SetLanguagesAsync(source, target, current));
            };
            await controller.StartAsync().WaitAsync(native.Token);
            await Until(() => overlay.IsVisible && RuntimeTranslation(overlay) == "Hello", native.Token);
            int jobsBefore = translation.Calls;
            var previousTarget = Assert.IsType<InputSnapshot>(captured).Target;
            // This public capability query checks source metadata, not authority
            // to execute a command; it must stop advertising the retired target.
            var metadataToken = new SessionViewToken(previousTarget, Guid.NewGuid(), 1);
            Assert.True(overlay.SupportsSourceGlossaryEntry(metadataToken));
            var editor = RuntimeDescendants(overlay).OfType<TextBox>().Single();
            var language = RuntimeDescendants(overlay).OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == "翻译语言");
            Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(language).GetPattern(PatternInterface.Invoke)).Invoke();
            TextBox? Search() => PresentationSource.CurrentSources.OfType<HwndSource>()
                .SelectMany(host => RuntimeDescendants(host.RootVisual).Prepend(host.RootVisual)).OfType<TextBox>()
                .SingleOrDefault(control => control.IsVisible && AutomationProperties.GetAutomationId(control) == "ConfigurationLanguageSearch");
            await Until(() => Search()?.IsKeyboardFocused == true, native.Token);
            var search = Assert.IsType<TextBox>(Search());
            Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            var rejected = new ConcurrentQueue<(BrokerRequest Request, BrokerResponse Response)>();
            broker.ObserveResponse = (request, response) =>
            {
                if (request.Op != "capture" || request.OverlayHwnd != (long)overlay.Handle
                    || request.BoundTarget is not null || Native.GetForegroundWindow() != overlay.Handle)
                    return;
                rejected.Enqueue((request, response));
            };
            await Until(() => rejected.Count >= 3, native.Token);
            broker.ObserveResponse = null;
            Assert.All(rejected, observation =>
            {
                Assert.Null(observation.Request.BoundTarget);
                Assert.Null(observation.Response.Snapshot);
                Assert.Equal(FocusMember.Other, observation.Response.Focus);
                Assert.Null(observation.Response.SourceEligibility);
            });
            Assert.Same(search, Keyboard.FocusedElement);
            Assert.True(overlay.IsVisible);
            Assert.False(overlay.SupportsSourceGlossaryEntry(metadataToken));
            Assert.Empty(editor.Text);
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.Focusable);
            Assert.False(editor.IsVisible);
            Assert.False(editor.CanUndo);
            Assert.Equal(jobsBefore, translation.Calls);
            Assert.Equal(0, broker.Writes);
            Assert.Equal(0, broker.CompletionTransfers);
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(0, 2), N.ReadSelection(native.A));
            native.AssertZeroSubmits();
            // An explicitly activated other application retires configuration;
            // it is never refocused by the next capture-empty repaint.
            await native.Focus(native.A);
            await Until(() => !search.IsVisible, native.Token);
            Assert.Equal((native.Root, native.A), N.Focused());
        }
        finally
        {
            broker.ObserveResponse = null;
            overlay.Close();
            Directory.Delete(directory, true);
        }
    });

    private sealed class LanguagePreviewTranslation : ITranslationService
    {
        public int Calls
        {
            get; private set;
        }
        public bool Ready => true;
        public bool Busy => false;
        public Task<ModelResponse> TranslateAsync(TranslationJob job)
        {
            Calls++;
            return Task.FromResult(new ModelResponse(1, "translated", "Hello"));
        }
    }
}
