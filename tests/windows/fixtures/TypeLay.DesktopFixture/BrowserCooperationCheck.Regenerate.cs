using System.Text.Json;
using TypeLay.Windows.Testing;

internal static partial class BrowserCooperationCheck
{
    private sealed partial class Cdp
    {
        public async Task<JsonElement> VerifyEditorRegenerateAsync(string trace, List<CleanupError> cleanupErrors, CancellationToken ct)
        {
            int kept = 0, replaced = 0;
            string? editor = await PreviewObjectAsync(ct);
            Require(editor is not null, "regenerate-preview-missing");
            try
            {
                async Task<JsonElement> Observe() => (await PageCallAsync("Runtime.callFunctionOn", new
                {
                    objectId = editor,
                    returnByValue = true,
                    functionDeclaration = """
                        function(){const root=this.getRootNode(),box=root.querySelector('[role=group][aria-label="替换你修改过的译文？"]'),buttons=box?[...box.querySelectorAll('button')]:[];
                        return {connected:this.isConnected,readOnly:this.readOnly,text:this.value,editorFocused:root.activeElement===this,confirmation:!!box,
                        keepFocused:buttons.length===2&&root.activeElement===buttons[0],replaceFocused:buttons.length===2&&root.activeElement===buttons[1]};}
                        """
                }, ct)).GetProperty("result").GetProperty("value").Clone();
                async Task Until(Func<JsonElement, bool> ready)
                {
                    while (!ready(await Observe()))
                        await Task.Delay(20, ct);
                }
                async Task ClickEditor()
                {
                    var point = await PageCallAsync("Runtime.callFunctionOn", new
                    {
                        objectId = editor,
                        returnByValue = true,
                        functionDeclaration = "function(){const r=this.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};}"
                    }, ct);
                    await ClickPointAsync(point.GetProperty("result").GetProperty("value"), ct);
                    await Until(value => !value.GetProperty("readOnly").GetBoolean() && value.GetProperty("editorFocused").GetBoolean());
                }
                async Task ClickChoice(bool replace)
                {
                    var point = await PageCallAsync("Runtime.callFunctionOn", new
                    {
                        objectId = editor,
                        returnByValue = true,
                        arguments = new[] { new { value = replace } },
                        functionDeclaration = """
                            function(replace){const box=this.getRootNode().querySelector('[role=group][aria-label="替换你修改过的译文？"]'),b=box?.querySelectorAll('button')[replace?1:0];
                            if(!b||b.disabled||!b.isConnected)throw Error('confirmation-action-unavailable');const r=b.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};}
                            """
                    }, ct);
                    await ClickPointAsync(point.GetProperty("result").GetProperty("value"), ct);
                }
                async Task Open()
                {
                    await ClickOverlayMenuActionAsync("重新翻译", ct);
                    await Until(value => value.GetProperty("confirmation").GetBoolean() && value.GetProperty("keepFocused").GetBoolean());
                }
                async Task Unchanged()
                {
                    var source = await StateAsync("", ct);
                    Require(source.A == Source && source.Replacements == 0 && source.Submits == 0,
                        "regenerate-confirmation-mutated-or-submitted-source");
                }
                await ClickEditor();
                const string draft = "Synthetic edited draft retained by Keep.";
                SendChord([0x11, 0x41], cleanupErrors);
                BrowserWindows.TypeText(draft);
                await Until(value => value.GetProperty("text").GetString() == draft);
                foreach (string keep in new[] { "click", "escape", "enter", "space" })
                {
                    await Open();
                    // Both directions cycle only this actual two-button family.
                    SendChord([0x09], cleanupErrors);
                    await Until(value => value.GetProperty("replaceFocused").GetBoolean());
                    SendChord([0x10, 0x09], cleanupErrors);
                    await Until(value => value.GetProperty("keepFocused").GetBoolean());
                    if (keep == "click")
                        await ClickChoice(false);
                    else
                        SendChord([keep == "escape" ? (ushort)0x1b : keep == "enter" ? (ushort)0x0d : (ushort)0x20], cleanupErrors);
                    await Until(value => !value.GetProperty("confirmation").GetBoolean()
                        && value.GetProperty("editorFocused").GetBoolean() && value.GetProperty("text").GetString() == draft);
                    await Unchanged();
                    kept++;
                }
                foreach (string activation in new[] { "click", "enter", "space" })
                {
                    string manual = "Synthetic manual draft before " + activation + " Replace.";
                    if ((await Observe()).GetProperty("readOnly").GetBoolean())
                        await ClickEditor();
                    SendChord([0x11, 0x41], cleanupErrors);
                    BrowserWindows.TypeText(manual);
                    await Until(value => value.GetProperty("text").GetString() == manual);
                    await Open();
                    long before = TraceLength(trace);
                    if (activation == "click")
                        await ClickChoice(true);
                    else
                    {
                        SendChord([0x09], cleanupErrors);
                        await Until(value => value.GetProperty("replaceFocused").GetBoolean());
                        SendChord([activation == "enter" ? (ushort)0x0d : (ushort)0x20], cleanupErrors);
                    }
                    await WaitTraceAsync(trace, "translate-done", before, ct);
                    await Until(value => !value.GetProperty("confirmation").GetBoolean()
                        && value.GetProperty("readOnly").GetBoolean() && value.GetProperty("text").GetString() is { Length: > 0 } text
                        && text != manual);
                    await Unchanged();
                    Require((await StateAsync("", ct)).Focus == "a", "regenerate-did-not-return-to-original-source");
                    replaced++;
                }
                return JsonSerializer.SerializeToElement(new
                {
                    keepDismissals = kept,
                    replaceActivations = replaced,
                    sourceUnchanged = true,
                    submits = 0,
                    physicalIme = "not-tested"
                });
            }
            finally { await PageCallAsync("Runtime.releaseObject", new { objectId = editor }, ct); }
        }
    }
}
