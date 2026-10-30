using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    // This entire case uses the pre-existing public composition/UI seams. It
    // can be applied to the original implementation without the new API or
    // read-only observation hook: missing new methods are not a behavioral RED.
    [Fact]
    public Task ASourcePendingDraftOpensTheRealRegenerateConfirmationOnKeep() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        await native.Prepare(native.A, "你好", new(0, 2));
        using var broker = new FixtureBroker(native);
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.SourceConfirmationEntry." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool initialTabHeld = false;
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Source = "auto",
                Target = "en"
            });
            var translation = new LanguagePreviewTranslation();
            using var controller = WindowsRuntime.CreateSession(settings, translation, overlay, automationTest: true, broker: broker);
            await controller.StartAsync().WaitAsync(native.Token);
            await Until(() => overlay.IsVisible && RuntimeTranslation(overlay) == "Hello", native.Token);
            var editor = RuntimeDescendants(overlay).OfType<TextBox>().Single();
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = editor
            });
            Assert.True(editor.IsKeyboardFocused);
            Assert.False(editor.IsReadOnly);
            editor.AppendText(" - manual draft");
            Assert.True(editor.CanUndo);
            Tab(false);
            initialTabHeld = true;
            Tab(true);
            initialTabHeld = false;
            await Until(() => N.Focused() == (native.Root, native.A) && editor.IsReadOnly
                && AutomationProperties.GetItemStatus(editor) == "当前译文", native.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(native.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var more = RuntimeDescendants(overlay).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "更多翻译操作");
            Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(more).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => more.ContextMenu?.IsOpen == true, deadline.Token);
            var menu = Assert.IsAssignableFrom<ContextMenu>(more.ContextMenu);
            var regenerate = menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "MoreRegenerate");
            Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(regenerate).GetPattern(PatternInterface.Invoke)).Invoke();
            Button? Keep() => RuntimeDescendants(overlay).OfType<Button>().SingleOrDefault(button =>
                AutomationProperties.GetAutomationId(button) == "RegenerateKeep");
            await Until(() => Keep()?.IsKeyboardFocused == true, deadline.Token);
            Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            Assert.Equal("Hello - manual draft", editor.Text);
            Assert.True(editor.IsReadOnly);
            Assert.Equal(1, translation.Calls);
            Assert.Equal(0, broker.Writes);
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(0, 2), N.ReadSelection(native.A));
            native.AssertZeroSubmits();
        }
        finally
        {
            if (initialTabHeld)
                Tab(true);
            overlay.Close();
            Directory.Delete(directory, true);
        }
    });
}
