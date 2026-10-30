using System.Windows.Controls;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class OverlayTranslationMenuTests
{
    internal static Task OpenSourceMoreByMouse(Button more, Action verifySource, CancellationToken cancellation) =>
        ClickMouse(more, Button.ClickEvent, "source-more", cancellation, verifySource: verifySource);

    internal static Task ChooseSourceRegenerateByMouse(OverlayWindow overlay, Button more, MenuItem item,
        nint sourceForeground, Action verifySource, CancellationToken cancellation) =>
        ClickMouse(item, MenuItem.ClickEvent, "source-regenerate", cancellation,
            new(Assert.IsAssignableFrom<ContextMenu>(more.ContextMenu), more, overlay,
                ExpectedForeground: sourceForeground, VerifySource: verifySource));
}
