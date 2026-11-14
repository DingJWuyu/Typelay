using System.IO;
using System.Text.Json;
using TypeLay.App;
using TypeLay.Windows.Testing;

internal static partial class BrowserCooperationCheck
{
    private sealed class DraftObservation
    {
        public string Draft { get; } = "Manual draft: Please confirm the time and place of tomorrow afternoon's meeting. Tell me what materials to prepare, whether I can arrive ten minutes early, and whether we can discuss the next stage afterward. Thank you. Reference: https://example.com 🙂";
        public string? InitialTranslation
        {
            get; set;
        }
        public PageState? Before
        {
            get; set;
        }
        public PageState? Returned
        {
            get; set;
        }
        public PageState? Accepted
        {
            get; set;
        }
        public bool ReturnKeysReleased
        {
            get; set;
        }
        public bool IndependentAcceptHint
        {
            get; set;
        }
    }
    private sealed partial class Cdp
    {
        public async Task CaptureAsync(string path, CancellationToken ct)
        {
            var screenshot = await PageCallAsync("Page.captureScreenshot", new
            {
                format = "png"
            }, ct);
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(screenshot.GetProperty("data").GetString()!), ct);
        }
        public async Task VerifyDraftAsync(Scene scene, DraftObservation observation, string directory,
            string trace, List<CleanupError> cleanupErrors, CancellationToken ct)
        {
            observation.Before = await StateAsync("", ct);
            observation.InitialTranslation = (await WaitPreviewAsync(this, ct)).Text;
            scene.ActualTranslation = observation.InitialTranslation;
            await CaptureAsync(Path.Combine(directory, "draft-preview.png"), ct);
            string? editor = await PreviewObjectAsync(ct);
            Require(editor is not null, "draft-preview-missing");
            try
            {
                async Task<JsonElement> Editor()
                {
                    var response = await PageCallAsync("Runtime.callFunctionOn", new
                    {
                        objectId = editor,
                        returnByValue = true,
                        functionDeclaration = "function(){return {text:this.value,editable:!this.readOnly,focused:this.getRootNode().activeElement===this,connected:this.isConnected};}"
                    }, ct);
                    return response.GetProperty("result").GetProperty("value");
                }
                scene.Stage = "click-visible-translation-editor";
                var point = await PageCallAsync("Runtime.callFunctionOn", new
                {
                    objectId = editor,
                    returnByValue = true,
                    functionDeclaration = "function(){const r=this.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};}"
                }, ct);
                await ClickPointAsync(point.GetProperty("result").GetProperty("value"), ct);
                scene.Stage = "wait-editor-focus";
                while (true)
                {
                    var observed = await Editor();
                    if (observed.GetProperty("editable").GetBoolean() && observed.GetProperty("focused").GetBoolean())
                        break;
                    await Task.Delay(20, ct);
                }
                scene.Stage = "type-exact-manual-draft";
                SendChord([0x11, 0x41], cleanupErrors);
                BrowserWindows.TypeText(observation.Draft);
                while ((await Editor()).GetProperty("text").GetString() != observation.Draft)
                    await Task.Delay(20, ct);
                var edited = await StateAsync("", ct);
                Require(edited.A == Source && edited.B == Other && edited.Replacements == 0 && edited.Submits == 0,
                    "editing-mutated-source");
                await CaptureAsync(Path.Combine(directory, "draft-edited.png"), ct);
                scene.Stage = "first-default-tab-return-only";
                SendChord([0x09], cleanupErrors);
                scene.ShortcutPresses = 1;
                observation.Returned = await WaitStateAsync(this, state =>
                {
                    scene.ActualFieldState = state;
                    Require(state.A == Source && state.B == Other && state.Replacements == 0 && state.Submits == 0,
                        "first-tab-wrote-or-submitted");
                    return state.Focus == "a" && state.PageFocused;
                }, ct);
                Require(observation.Returned.AStart == observation.Before.AStart && observation.Returned.AEnd == observation.Before.AEnd
                    && observation.Returned.BStart == observation.Before.BStart && observation.Returned.BEnd == observation.Before.BEnd,
                    "return-changed-source-selection");
                scene.Stage = "release-return-chord-and-requalify";
                while (!new ushort[] { 0x09, 0x10, 0x11, 0x12, 0x5b, 0x5c }.All(key => (Native.GetAsyncKeyState(key) & 0x8000) == 0))
                    await Task.Delay(20, ct);
                observation.ReturnKeysReleased = true;
                await CaptureAsync(Path.Combine(directory, "draft-returned.png"), ct);
                // Only the newly displayed public hint can select the second press.
                // No permit, ACK or internal lease is read by the fixture.
                var returnedPreview = await WaitPreviewAsync(this, ct);
                Require(returnedPreview.Text == observation.Draft, "returned-draft-not-preserved");
                observation.IndependentAcceptHint = true;
                var beforeAccept = await StateAsync("", ct);
                Require(beforeAccept.A == Source && beforeAccept.B == Other && beforeAccept.Replacements == 0
                    && beforeAccept.Submits == 0 && beforeAccept.Focus == "a" && beforeAccept.PageFocused,
                    "return-wrote-before-independent-accept");
                scene.Stage = "second-independent-default-tab-accept";
                long offset = TraceLength(trace);
                SendChord([0x09], cleanupErrors);
                scene.ShortcutPresses = 2;
                observation.Accepted = await WaitStateAsync(this, state => ObserveDom(state, scene, observation.Draft), ct);
                CheckReplacement(observation.Accepted, scene, observation.Draft, sourceFocus: true);
                await WaitTraceAsync(trace, "browser-accept-Verified", offset, ct);
                observation.Accepted = await StateAsync("", ct);
                CheckReplacement(observation.Accepted, scene, observation.Draft, sourceFocus: true);
                await CaptureAsync(Path.Combine(directory, "draft-accepted.png"), ct);
            }
            finally { await PageCallAsync("Runtime.releaseObject", new { objectId = editor }, ct); }
        }
    }
}
