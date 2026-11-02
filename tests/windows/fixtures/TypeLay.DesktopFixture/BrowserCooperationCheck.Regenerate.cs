using System.Text.Json;
using TypeLay.Windows.Testing;

internal static partial class BrowserCooperationCheck
{
    private sealed partial class Cdp
    {
        public JsonElement? LastEditorRegenerateObservation
        {
            get; private set;
        }
        public async Task<JsonElement> VerifyEditorRegenerateAsync(string trace, List<CleanupError> cleanupErrors, CancellationToken ct)
        {
            LastEditorRegenerateObservation = null;
            int kept = 0, replaced = 0, observations = 0;
            string stage = "preview", input = "none";
            string? expected = null;
            object? firstObservation = null, lastObservation = null;
            bool diagnosticFailed = false;
            void Stage(string value)
            {
                stage = value;
                observations = 0;
                firstObservation = lastObservation = null;
            }
            string? editor = await PreviewObjectAsync(ct);
            Require(editor is not null, "regenerate-preview-missing");
            try
            {
                async Task<JsonElement> Observe()
                {
                    var value = (await PageCallAsync("Runtime.callFunctionOn", new
                    {
                        objectId = editor,
                        returnByValue = true,
                        functionDeclaration = """
                            function(){const root=this.getRootNode(),box=root.querySelector('[role=group][aria-label="替换你修改过的译文？"]'),buttons=box?[...box.querySelectorAll('button')]:[];
                            return {connected:this.isConnected,readOnly:this.readOnly,text:this.value,selectionStart:this.selectionStart,selectionEnd:this.selectionEnd,
                            editorFocused:root.activeElement===this,confirmation:!!box,
                            keepFocused:buttons.length===2&&root.activeElement===buttons[0],replaceFocused:buttons.length===2&&root.activeElement===buttons[1]};}
                            """
                    }, ct)).GetProperty("result").GetProperty("value").Clone();
                    try
                    {
                        // Reuse this predicate's response; retain no source or draft text.
                        var observation = new
                        {
                            connected = value.GetProperty("connected").GetBoolean(),
                            readOnly = value.GetProperty("readOnly").GetBoolean(),
                            editorFocused = value.GetProperty("editorFocused").GetBoolean(),
                            confirmation = value.GetProperty("confirmation").GetBoolean(),
                            keepFocused = value.GetProperty("keepFocused").GetBoolean(),
                            replaceFocused = value.GetProperty("replaceFocused").GetBoolean(),
                            textLength = value.GetProperty("text").GetString()?.Length,
                            selectionStart = value.GetProperty("selectionStart").GetInt32(),
                            selectionEnd = value.GetProperty("selectionEnd").GetInt32(),
                            expectedEqual = expected is null ? (bool?)null : value.GetProperty("text").GetString() == expected
                        };
                        observations++;
                        firstObservation ??= observation;
                        lastObservation = observation;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException) { diagnosticFailed = true; }
                    return value;
                }
                async Task Until(string nextStage, Func<JsonElement, bool> ready)
                {
                    Stage(nextStage);
                    while (!ready(await Observe()))
                        await Task.Delay(20, ct);
                }
                async Task ClickEditor()
                {
                    Stage("click-editor");
                    var point = await PageCallAsync("Runtime.callFunctionOn", new
                    {
                        objectId = editor,
                        returnByValue = true,
                        functionDeclaration = "function(){const r=this.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};}"
                    }, ct);
                    await ClickPointAsync(point.GetProperty("result").GetProperty("value"), ct);
                    await Until("wait-editor", value => !value.GetProperty("readOnly").GetBoolean() && value.GetProperty("editorFocused").GetBoolean());
                }
                async Task ClickChoice(bool replace)
                {
                    Stage(replace ? "click-replace" : "click-keep");
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
                    Stage("open-confirmation-menu");
                    await ClickOverlayMenuActionAsync("重新翻译", ct);
                    await Until("wait-confirmation", value => value.GetProperty("confirmation").GetBoolean() && value.GetProperty("keepFocused").GetBoolean());
                }
                async Task Unchanged()
                {
                    Stage("verify-source-unchanged");
                    var source = await StateAsync("", ct);
                    Require(source.A == Source && source.Replacements == 0 && source.Submits == 0,
                        "regenerate-confirmation-mutated-or-submitted-source");
                }
                await ClickEditor();
                const string draft = "Synthetic edited draft retained by Keep.";
                expected = draft;
                Stage("type-keep-draft");
                SendChord([0x11, 0x41], cleanupErrors);
                BrowserWindows.TypeText(draft);
                await Until("wait-keep-draft", value => value.GetProperty("text").GetString() == draft);
                foreach (string keep in new[] { "click", "escape", "enter", "space" })
                {
                    input = keep;
                    await Open();
                    // Both directions cycle only this actual two-button family.
                    SendChord([0x09], cleanupErrors);
                    await Until("wait-replace-focus", value => value.GetProperty("replaceFocused").GetBoolean());
                    SendChord([0x10, 0x09], cleanupErrors);
                    await Until("wait-keep-focus", value => value.GetProperty("keepFocused").GetBoolean());
                    if (keep == "click")
                        await ClickChoice(false);
                    else
                    {
                        Stage("activate-keep");
                        SendChord([keep == "escape" ? (ushort)0x1b : keep == "enter" ? (ushort)0x0d : (ushort)0x20], cleanupErrors);
                    }
                    await Until("wait-keep-dismissal", value => !value.GetProperty("confirmation").GetBoolean()
                        && value.GetProperty("editorFocused").GetBoolean() && value.GetProperty("text").GetString() == draft);
                    await Unchanged();
                    kept++;
                }
                foreach (string activation in new[] { "click", "enter", "space" })
                {
                    input = activation;
                    expected = null;
                    Stage("check-replace-editor");
                    string manual = "Synthetic manual draft before " + activation + " Replace.";
                    if ((await Observe()).GetProperty("readOnly").GetBoolean())
                        await ClickEditor();
                    expected = manual;
                    Stage("type-replace-draft");
                    SendChord([0x11, 0x41], cleanupErrors);
                    BrowserWindows.TypeText(manual);
                    await Until("wait-replace-draft", value => value.GetProperty("text").GetString() == manual);
                    await Open();
                    long before = TraceLength(trace);
                    if (activation == "click")
                        await ClickChoice(true);
                    else
                    {
                        SendChord([0x09], cleanupErrors);
                        await Until("wait-replace-focus", value => value.GetProperty("replaceFocused").GetBoolean());
                        Stage("activate-replace");
                        SendChord([activation == "enter" ? (ushort)0x0d : (ushort)0x20], cleanupErrors);
                    }
                    Stage("wait-replace-translation");
                    await WaitTraceAsync(trace, "translate-done", before, ct);
                    await Until("wait-replace-result", value => !value.GetProperty("confirmation").GetBoolean()
                        && value.GetProperty("readOnly").GetBoolean() && value.GetProperty("text").GetString() is { Length: > 0 } text
                        && text != manual);
                    await Unchanged();
                    Stage("verify-source-focus");
                    Require((await StateAsync("", ct)).Focus == "a", "regenerate-did-not-return-to-original-source");
                    replaced++;
                }
                Stage("complete");
                return JsonSerializer.SerializeToElement(new
                {
                    keepDismissals = kept,
                    replaceActivations = replaced,
                    sourceUnchanged = true,
                    submits = 0,
                    physicalIme = "not-tested"
                });
            }
            finally
            {
                try
                {
                    LastEditorRegenerateObservation = JsonSerializer.SerializeToElement(new
                    {
                        stage,
                        input,
                        keepDismissals = kept,
                        replaceActivations = replaced,
                        observations,
                        firstObservation,
                        lastObservation,
                        diagnosticFailed
                    });
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                await PageCallAsync("Runtime.releaseObject", new
                {
                    objectId = editor
                }, ct);
            }
        }
    }
}
