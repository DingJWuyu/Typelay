using System.IO;
using System.Runtime.InteropServices;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class GlobalPauseShortcutTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task FailedReleaseCannotExecuteTheOldShortcutAfterClearingThePreference() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-pause-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                PauseShortcut = "Ctrl+Shift+F24"
            });
            bool allowRelease = false;
            int actions = 0, releases = 0;
            using var shortcut = new GlobalPauseShortcut(settings, enabled =>
            {
                actions++;
                settings.Update(settings.Value with
                {
                    Enabled = enabled
                });
                return Task.FromResult(true);
            }, register: (_, _, _, _) => true, unregister: (_, _) => { releases++; return allowRelease; });
            SendMessageW(shortcut.WindowHandle, 0x0312, 0x5453, (0x87 << 16) | 6);
            Assert.Equal(1, actions);
            Assert.False(settings.Value.Enabled);
            settings.Update(settings.Value with
            {
                PauseShortcut = ""
            });
            Assert.Contains("未确认", shortcut.Status);
            SendMessageW(shortcut.WindowHandle, 0x0312, 0x5453, (0x87 << 16) | 6);
            Assert.Equal(1, actions);
            Assert.False(settings.Value.Enabled);
            // Physical ownership remains tracked so the next configuration and
            // Dispose can release it; logical activation remains disabled.
            allowRelease = true;
            settings.Update(settings.Value with
            {
                PauseShortcut = "Ctrl+Shift+F23"
            });
            Assert.Equal(2, releases);
            SendMessageW(shortcut.WindowHandle, 0x0312, 0x5453, (0x87 << 16) | 6);
            Assert.Equal(1, actions); // A queued notification for the old chord is stale.
            SendMessageW(shortcut.WindowHandle, 0x0312, 0x5453, (0x86 << 16) | 6);
            Assert.Equal(2, actions);
            Assert.True(settings.Value.Enabled);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task RegistrationFailureNeverInvokesThePauseAction() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-pause-failed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                PauseShortcut = "Ctrl+Shift+F24"
            });
            using var shortcut = new GlobalPauseShortcut(settings, _ => throw new InvalidOperationException("Unregistered shortcut cannot run."),
                register: (_, _, _, _) => false);
            SendMessageW(shortcut.WindowHandle, 0x0312, 0x5453, (0x87 << 16) | 6);
            Assert.True(settings.Value.Enabled);
            Assert.Contains("占用", shortcut.Status);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
