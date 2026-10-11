using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class HotkeyRegistrationTests
{
    [Fact]
    public void ConflictsAreVisibleAndAutomaticModeReleasesRegisteredChords()
    {
        // Thread-owned registrations do not require or change foreground focus.
        const uint modifiers = 2 | 4 | 0x4000;
        const uint key = 0x87; // F24, kept away from the product defaults.
        const int blocker = 0x5400;
        Assert.True(Native.RegisterHotKey(0, blocker, modifiers, key));
        try
        {
            using var hotkeys = new OnDemandHotkeys(0);
            hotkeys.Configure(true, "Ctrl+Shift+F24", "Ctrl+Shift+F23");
            Assert.DoesNotContain(hotkeys.Bindings, binding => binding.Action == "wake");
            Assert.Contains("唤醒翻译 UI", hotkeys.Status);
            hotkeys.Configure(false, "Ctrl+Shift+F24", "Ctrl+Shift+F23");
            Assert.Empty(hotkeys.Bindings);
            Assert.True(Native.RegisterHotKey(0, blocker + 1, modifiers, 0x86));
            Native.UnregisterHotKey(0, blocker + 1);
        }
        finally { Native.UnregisterHotKey(0, blocker); }
    }
}
