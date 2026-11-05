#if DEBUG
using Compositor.App.Ai;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    internal async Task RunAiImageProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            Require(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")), "Use an isolated preferences directory for this probe.");
            await WaitForFramesAsync();
            var encoded = new Surface(8, 6);
            for (int i = 0; i < encoded.Pixels.Length; i += 4) { encoded.Pixels[i] = 190; encoded.Pixels[i + 3] = 255; }
            encoded.Pixels[(1 * 8 + 3) * 4] = 0; encoded.Pixels[(1 * 8 + 3) * 4 + 2] = 190;
            var png = await WicImaging.EncodePngAsync(encoded);
            new AiImageSettings().Save();
            var store = new AiCredentialStore(); store.Write("api-key", "local-probe-key");
            var handler = new AiImageProbeHandler(png);
            aiImageHttpProbe = () => new HttpClient(handler, disposeHandler: false);
            EditorDocument Scene()
            {
                var scene = EditorDocument.Create(8, 6); OpenDocument(scene);
                var mask = new MaskSurface(8, 6); mask.Pixels[18] = 255;
                scene.SetSelection(Selection.FromCoverage(mask)); return scene;
            }
            T Find<T>(string name) where T : FrameworkElement
            {
                return floatingPanels[AiPanelKey].Body.Children.OfType<StackPanel>().Single().Children.OfType<T>()
                    .Single(element => element.Name == name);
            }
            static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            async Task Generate()
            {
                Invoke(Find<Button>("AiGenerate"));
                Require(await WaitUntilAsync(() => aiEditor?.Work is null, 15000), "AI request did not finish.");
            }
            var owner = Scene(); await WaitForFramesAsync(); OpenAiEditor(AiOperation.Remove); await WaitForFramesAsync();
            Require(handler.Calls == 0 && !FloatingPanelConfirmEnabled(AiPanelKey), "Opening AI uploaded data or enabled Accept.");
            await Generate();
            Require(handler.Background == "auto", "Selection edits unexpectedly forced an opaque background.");
            Require(handler.Calls == 1 && AiPreviewHost.Visibility == Visibility.Visible && owner.LayerCount == 1 && !owner.CanUndo,
                "Preview changed document history or was not displayed.");
            var after = AiPreviewImage.Source;
            Find<ToggleSwitch>("AiCompare").IsOn = true;
            Require(!ReferenceEquals(after, AiPreviewImage.Source), "Original comparison did not change the image.");
            Find<ToggleSwitch>("AiCompare").IsOn = false;
            Require(ReferenceEquals(after, AiPreviewImage.Source), "Result comparison did not restore the image.");
            await Generate(); await Generate(); await Generate();
            Require(aiEditor!.Candidates.Count == 3 && Find<ComboBox>("AiVersions").Items.Count == 3, "Candidate retention is not bounded.");
            Invoke(floatingPanels[AiPanelKey].Confirm); await WaitForFramesAsync();
            Require(owner.LayerCount == 2 && aiEditor is null && AiPreviewImage.Source is null, "Accept did not clean up and add a layer.");
            var composite = new CanvasCompositor(owner.Snapshot).Render();
            Require(composite.Pixels[0] == 255 && composite.Pixels[18 * 4] == 190, "Selection protection changed image pixels.");
            var path = Path.Combine(Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")!, "accepted.comp");
            await ProjectStore.SaveAsync(owner.Snapshot, path);
            var loaded = new CanvasCompositor(await ProjectStore.LoadAsync(path)).Render();
            Require(loaded.Pixels.SequenceEqual(composite.Pixels), "Saved AI layer did not round-trip.");
            Require(owner.Undo() && owner.LayerCount == 1 && !owner.CanUndo, "Accept is not one undo step.");
            report["previewCompareCandidatesAcceptSaveUndo"] = "passed";

            owner = Scene(); await WaitForFramesAsync(); OpenAiEditor(AiOperation.Expand); await WaitForFramesAsync(); await Generate();
            Invoke(floatingPanels[AiPanelKey].Confirm); await WaitForFramesAsync();
            Require(owner.Snapshot.Manifest.Width == 72 && owner.LayerCount == 2 && owner.ActiveSelection is null, "Expansion did not commit.");
            Require(DocSizeLabel.Text == "72 × 6", "Expansion left the document-size readout stale: " + DocSizeLabel.Text);
            Require(owner.Undo() && owner.Snapshot.Manifest.Width == 8 && owner.LayerCount == 1 && !owner.CanUndo, "Expansion is not one undo.");
            Require(DocSizeLabel.Text == "8 × 6", "Expansion undo left the document-size readout stale.");
            Require(owner.Redo() && DocSizeLabel.Text == "72 × 6", "Expansion redo left the document-size readout stale.");
            Require(owner.Undo() && DocSizeLabel.Text == "8 × 6", "Expansion second undo left the document-size readout stale.");
            Require(handler.Background == "opaque", "Expansion did not request an opaque generated background: " + handler.Background);
            report["expandAcceptUndo"] = "passed";

            owner = Scene(); await WaitForFramesAsync(); OpenAiEditor(AiOperation.Remove); await WaitForFramesAsync();
            bool effectPreview = Environment.GetEnvironmentVariable("COMPOSITOR_AI_EFFECT_PREVIEW") == "1";
            if (effectPreview)
            {
                owner = EditorDocument.Create(640, 400); OpenDocument(owner);
                var area = new MaskSurface(640, 400);
                for (int y = 80; y < 300; y++) area.Pixels.AsSpan(y * 640 + 80, 400).Fill(255);
                owner.SetSelection(Selection.FromCoverage(area)); await WaitForFramesAsync(); OpenAiEditor(AiOperation.Remove);
            }
            handler.Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Invoke(Find<Button>("AiGenerate"));
            Require(await WaitUntilAsync(() => handler.Pending, 5000), "Cancelled request never entered the handler.");
            Require(AiWorkingHost.Visibility == Visibility.Visible && AiWorkingRegion.Width > 0, "The waiting image request has no canvas effect.");
            if (effectPreview) await Task.Delay(30000);
            Invoke(Find<Button>("AiStop"));
            Require(await WaitUntilAsync(() => aiEditor?.Work is null, 5000), "Stop did not release the request.");
            Require(await WaitUntilAsync(() => AiWorkingHost.Visibility == Visibility.Collapsed, 2000), "The canvas effect survived cancellation.");
            Require(owner.LayerCount == 1 && !owner.CanUndo && !FloatingPanelConfirmEnabled(AiPanelKey), "Stop changed history.");
            report["cancelLeavesHistory"] = "passed";

            handler.Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously); handler.Pending = false;
            Invoke(Find<Button>("AiGenerate"));
            Require(await WaitUntilAsync(() => handler.Pending, 5000), "Late request never entered the handler.");
            var replacement = EditorDocument.Create(9, 7); OpenDocument(replacement); handler.Waiting.TrySetResult();
            await WaitForFramesAsync(); await Task.Delay(100);
            Require(ReferenceEquals(document, replacement) && replacement.LayerCount == 1 && !replacement.CanUndo
                && aiEditor is null && AiPreviewHost.Visibility == Visibility.Collapsed && AiWorkingHost.Visibility == Visibility.Collapsed,
                "Late result or running effect reached the replacement document.");
            report["documentReplacementRetiresResult"] = "passed";
            handler.Waiting = null; handler.Pending = false;
            owner = Scene(); await WaitForFramesAsync(); OpenAiEditor(AiOperation.Replace); await WaitForFramesAsync();
            var connection = Find<ComboBox>("AiConnection");
            var credentialBody = (StackPanel)Find<Expander>("AiSettings").Content;
            var password = credentialBody.Children.OfType<StackPanel>().First().Children.OfType<PasswordBox>().Single();
            Require(connection.Items.Count == 2, "The connection list exposes a retired provider.");
            password.Password = "unsaved-api-key"; connection.SelectedIndex = 1;
            Require(password.Password.Length == 0 && Find<InfoBar>("AiApiPrivacyNotice").Visibility == Visibility.Collapsed, "Account selection retained an unsaved key.");
            connection.SelectedIndex = 0;
            Require(Find<InfoBar>("AiApiPrivacyNotice").Visibility == Visibility.Visible, "The custom API connection lost its privacy notice.");
            var apiBody = credentialBody.Children.OfType<StackPanel>().First();
            var endpoint = apiBody.Children.OfType<TextBox>().Single();
            Require(endpoint.IsEnabled, "The API address is not editable.");
            endpoint.Text = "https://images.example.test/v1/";
            password.Password = "local-probe-key";
            Invoke(apiBody.Children.OfType<StackPanel>().Single().Children.OfType<Button>().First());
            Find<ComboBox>("AiModel").SelectedItem = ImageModels.Flare;
            OpenAiEditor(AiOperation.Replace); await WaitForFramesAsync();
            Require(Find<ComboBox>("AiConnection").SelectedIndex == 0 && Find<ComboBox>("AiModel").SelectedItem as string == ImageModels.Flare,
                "Custom API settings did not survive reopening.");
            Find<TextBox>("AiPrompt").Text = "Custom API fixture";
            await Generate();
            Require(aiEditor?.Status == "ready" && handler.LastHost == "images.example.test" && handler.LastCredential == "local-probe-key",
                "The custom API address or saved credential was not used.");
            Invoke(floatingPanels[AiPanelKey].Confirm); await WaitForFramesAsync();
            Require(owner.LayerCount == 2 && owner.Undo() && owner.LayerCount == 1 && !owner.CanUndo, "The custom API edit lost single-step Undo.");
            RetireAiEdit(); new AiImageSettings().Save();
            store.Write("api-key", "local-probe-key");
            report["customApiSettingsCredentialsPreviewUndo"] = "passed";
            await VerifyLiveEditorMcpAsync(handler);
            report["liveMcpEditing"] = "passed";
            report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            RetireAiEdit(); aiImageHttpProbe = null;
            WriteProbeReport("ai-image-probe.json", report);
            forceClose = true; Close();
        }
    }

    private async Task VerifyLiveEditorMcpAsync(AiImageProbeHandler handler)
    {
        handler.Waiting = null; handler.Pending = false;
        var scene = EditorDocument.Create(8, 6); OpenDocument(scene); await WaitForFramesAsync();
        string executable = Environment.GetEnvironmentVariable("COMPOSITOR_MCP_EXECUTABLE")
            ?? throw new InvalidOperationException("Build MCP and set COMPOSITOR_MCP_EXECUTABLE.");
        using var server = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8,
        }) ?? throw new InvalidOperationException("MCP did not start.");
        var errors = server.StandardError.ReadToEndAsync();
        int sequence = 0;
        JsonObject? state = null;
        async Task<JsonObject> Rpc(string method, JsonObject? parameters = null)
        {
            int id = ++sequence;
            await server.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString());
            await server.StandardInput.FlushAsync();
            var line = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var response = JsonNode.Parse(line ?? "null") as JsonObject ?? throw new InvalidOperationException("MCP closed unexpectedly.");
            Require(response["id"]?.GetValue<int>() == id && response["error"] is null, "MCP protocol failure: " + response.ToJsonString());
            return (JsonObject)response["result"]!;
        }
        async Task<JsonObject> Call(string name, JsonObject? args = null, bool mutation = false, bool error = false)
        {
            args ??= new(); args["appPid"] = Environment.ProcessId;
            if (mutation)
            { args["documentToken"] = state!["documentToken"]!.DeepClone(); args["revision"] = state["revision"]!.DeepClone(); }
            var result = await Rpc("tools/call", new() { ["name"] = name, ["arguments"] = args });
            Require((result["isError"]?.GetValue<bool>() == true) == error, name + " returned unexpected status: " + result.ToJsonString());
            if (!error && name != "editor_preview") state = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
            return result;
        }
        string Job() => state!["ai"]!["requestId"]!.GetValue<string>();
        async Task WaitForAi(string expected)
        {
            Require(await WaitUntilAsync(() => aiEditor?.Work is null, 10000), "MCP AI did not finish.");
            await Call("editor_state");
            Require(state!["ai"]!["status"]!.GetValue<string>() == expected, "MCP AI state was not " + expected);
        }
        try
        {
            await Rpc("initialize", new() { ["protocolVersion"] = "2025-06-18" });
            var catalog = await Rpc("tools/list");
            Require(catalog["tools"]!.AsArray().Any(tool => tool?["name"]?.GetValue<string>() == "editor_ai_start"), "Live MCP tools are not discoverable.");
            await Call("editor_state");
            var oldState = state!.DeepClone().AsObject();
            await Call("editor_select", new() { ["mode"] = "rectangle", ["x"] = 2, ["y"] = 1, ["width"] = 3, ["height"] = 2 }, true);
            var selectedState = state!.DeepClone().AsObject();
            state = oldState;
            await Call("editor_history", new() { ["action"] = "undo" }, true, true);
            state = selectedState;
            Require(scene.ActiveSelection?.Bounds == new RectI(2, 1, 3, 2), "A stale MCP request changed the selection.");
            int beforeInvalidConnection = handler.Calls;
            await Call("editor_ai_start", new() { ["operation"] = "replace", ["prompt"] = "invalid", ["connection"] = "retired-provider", ["model"] = ImageModels.Flare }, true, true);
            await Call("editor_ai_start", new() { ["operation"] = "replace", ["prompt"] = "invalid", ["model"] = ImageModels.Flare, ["resolution"] = "4K" }, true, true);
            Require(handler.Calls == beforeInvalidConnection && aiEditor is null, "Invalid MCP settings opened a draft or uploaded data.");
            await Call("editor_ai_start", new() { ["operation"] = "replace", ["prompt"] = "MCP 金色测试", ["model"] = Ai.ImageModels.Flare }, true);
            await WaitForAi("ready");
            string requestId = Job();
            Require(scene.LayerCount == 1, "MCP generation committed without acceptance.");
            var preview = await Call("editor_preview", new() { ["view"] = "result", ["maxSide"] = 256 });
            var image = preview["content"]!.AsArray().Single(item => item?["type"]?.GetValue<string>() == "image")!;
            var decoded = await WicImaging.DecodeSurfaceAsync(Convert.FromBase64String(image["data"]!.GetValue<string>()), 256, 65536);
            Require(decoded.Width == 8 && decoded.Pixels[0] == 255 && decoded.Pixels[(1 * 8 + 2) * 4] == 190, "MCP preview changed protected pixels.");
            await Call("editor_ai_accept", new() { ["requestId"] = "wrong-request" }, true, true);
            await Call("editor_ai_accept", new() { ["requestId"] = requestId }, true);
            Require(scene.LayerCount == 2 && scene.CanUndo, "MCP acceptance did not add an undoable layer.");
            string layerId = scene.Snapshot.Manifest.ActiveLayerId!.Value.ToString();
            var acceptedLayer = scene.Snapshot.Manifest.Layers[^1];
            Require(acceptedLayer.Transform.Origin == new PointD(2, 1) && acceptedLayer.Transform.Size == new SizeD(3, 2)
                && scene.Snapshot.Images[acceptedLayer.Id].Width == 3, "AI acceptance retained transparent canvas margins.");
            await Call("editor_mask_color", new() { ["layerId"] = layerId, ["colors"] = new JsonArray(190, 0, 0), ["fuzziness"] = 0 }, true);
            Require(scene.Snapshot.Masks[Guid.Parse(layerId)].Pixels[1] == 0
                && scene.Snapshot.Masks[Guid.Parse(layerId)].Pixels[0] == 255, "MCP color mask retained the background or removed the subject.");
            await Call("editor_mask_color", new() { ["layerId"] = layerId, ["colors"] = new JsonArray(190, 0, 0) }, true, true);
            var isolated = await Call("editor_preview", new() { ["view"] = "layer", ["layerId"] = layerId });
            var isolatedImage = isolated["content"]!.AsArray().Single(item => item?["type"]?.GetValue<string>() == "image")!;
            var isolatedPixels = await WicImaging.DecodeSurfaceAsync(Convert.FromBase64String(isolatedImage["data"]!.GetValue<string>()), 256, 65536);
            Require(isolatedPixels.Pixels[(1 * 8 + 3) * 4 + 3] == 0 && isolatedPixels.Pixels[(1 * 8 + 2) * 4 + 3] == 255, "The isolated layer preview is not transparent outside its subject.");
            await Call("editor_history", new() { ["action"] = "undo" }, true);
            Require(!scene.Snapshot.Masks.ContainsKey(Guid.Parse(layerId)), "Color mask undo did not restore the layer.");
            await Call("editor_history", new() { ["action"] = "undo" }, true);
            Require(scene.LayerCount == 1, "MCP undo failed.");
            await Call("editor_history", new() { ["action"] = "redo" }, true);
            Require(scene.LayerCount == 2, "MCP redo failed.");
            string root = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")!;
            string path = Path.Combine(root, "MCP 金色.comp"), export = Path.Combine(root, "MCP 金色.png");
            await Call("editor_export", new() { ["path"] = export }, true);
            await Call("editor_save", new() { ["path"] = path }, true);
            Require(!scene.IsDirty && File.Exists(export), "MCP save/export failed.");
            var savedPixels = new CanvasCompositor(await ProjectStore.LoadAsync(path)).Render();
            Require(savedPixels.Pixels.SequenceEqual(new CanvasCompositor(scene.Snapshot).Render().Pixels), "MCP saved pixels differ.");
            await Call("editor_ai_start", new() { ["operation"] = "expand", ["model"] = Ai.ImageModels.Flare, ["left"] = 2, ["right"] = 3, ["top"] = 1 }, true);
            await WaitForAi("ready");
            Require(handler.Background == "opaque", "MCP expansion lost the opaque output request.");
            await Call("editor_ai_accept", new() { ["requestId"] = Job() }, true);
            Require(scene.Snapshot.Manifest.Width == 13 && DocSizeLabel.Text == "13 × 7", "MCP expansion/readout failed.");
            await Call("editor_history", new() { ["action"] = "undo" }, true);
            Require(scene.Snapshot.Manifest.Width == 8 && DocSizeLabel.Text == "8 × 6", "MCP expansion undo failed.");
            await Call("editor_save", mutation: true);
            handler.Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously); handler.Pending = false;
            await Call("editor_ai_start", new() { ["operation"] = "replace", ["prompt"] = "cancelled MCP", ["model"] = Ai.ImageModels.Flare }, true);
            Require(await WaitUntilAsync(() => handler.Pending, 5000), "MCP cancellation request did not start.");
            await Call("editor_ai_cancel", new() { ["requestId"] = Job() }, true);
            await WaitForAi("cancelled");
            await Call("editor_ai_cancel", new() { ["requestId"] = Job(), ["discard"] = true }, true);
            string otherPath = Path.Combine(root, "other.comp");
            await ProjectStore.SaveAsync(EditorDocument.Create(9, 7).Snapshot, otherPath);
            await Call("editor_open", new() { ["path"] = otherPath }, true);
            Require(document!.Snapshot.Manifest.Width == 9, "MCP did not open the other project.");
            await Call("editor_open", new() { ["path"] = path }, true);
            Require(document!.LayerCount == 2 && !document.IsDirty
                && new CanvasCompositor(document.Snapshot).Render().Pixels.SequenceEqual(savedPixels.Pixels), "MCP reopening lost the saved AI layer.");
            state = selectedState;
            await Call("editor_select", new() { ["mode"] = "all" }, true, true);
            var legacy = EditorDocument.Create(8, 6);
            var legacyId = legacy.Snapshot.Manifest.ActiveLayerId!.Value;
            var legacyMask = new MaskSurface(8, 6); legacyMask.Pixels[2 * 8 + 3] = 255;
            legacy.AddRasterMask(legacyId, legacyMask); OpenDocument(legacy); await WaitForFramesAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Move;
            Require(await WaitUntilAsync(() => tool == CanvasTool.Move && toolChangeApplied == toolChangeRevision, 5000), "Move tool did not settle.");
            showTransformControls = true; RefreshTransformInspector(true); UpdateTransformHandles();
            Require(TransformBox.Width == 8, "The trim scene did not start with the full-size controls.");
            var legacyPixels = new CanvasCompositor(legacy.Snapshot).Render().Pixels;
            await Call("editor_state");
            await Call("editor_trim_layer", new() { ["layerId"] = legacyId.ToString() }, true);
            Require(legacy.Snapshot.Images[legacyId].Width == 1 && legacy.Snapshot.Masks[legacyId].Width == 1
                && legacyPixels.SequenceEqual(new CanvasCompositor(legacy.Snapshot).Render().Pixels), "MCP trimming moved the masked content.");
            Require(TransformBox.Visibility == Visibility.Visible && TransformBox.Width == 1 && TransformBox.Height == 1
                && Canvas.GetLeft(TransformBox) == 3 && Canvas.GetTop(TransformBox) == 2, "Trim left the transform controls at the old bounds.");
            await Call("editor_history", new() { ["action"] = "undo" }, true);
            Require(legacy.Snapshot.Images[legacyId].Width == 8 && TransformBox.Width == 8, "MCP trim undo did not restore the source grid and controls.");
            await Call("editor_history", new() { ["action"] = "redo" }, true);
            Require(legacy.Snapshot.Images[legacyId].Width == 1 && TransformBox.Width == 1, "MCP trim redo did not restore tight bounds and controls.");
            await VerifyLiveLayerMcpAsync(Call);
            await VerifyAutomationMcpAsync(Call);
            await VerifyMaskReferenceMcpAsync(Call, handler);
        }
        finally
        {
            server.StandardInput.Close();
            if (!await WaitUntilAsync(() => server.HasExited, 3000)) server.Kill(entireProcessTree: true);
            await errors;
        }
    }

    private sealed class AiImageProbeHandler(byte[] png) : HttpMessageHandler
    {
        public int Calls;
        public bool Pending;
        public string? Background;
        public TaskCompletionSource? Waiting;
        public string? LastHost, LastCredential;
        public int InputImageCount;
        public string? LastPrompt;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++;
            LastHost = request.RequestUri?.Host; LastCredential = request.Headers.Authorization?.Parameter;
            Require(request.RequestUri?.AbsolutePath == "/v1/images/edits", "The editor used the wrong image endpoint.");
            Require(request.Content is MultipartFormDataContent, "The API editor did not upload multipart image files.");
            var form = (MultipartFormDataContent)request.Content!;
            InputImageCount = form.Count(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "image[]");
            LastPrompt = await form.Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "prompt").ReadAsStringAsync(cancellation);
            Background = await form.Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "background").ReadAsStringAsync(cancellation);
            if (Waiting is { } waiting) { Pending = true; await waiting.Task.WaitAsync(cancellation); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new[] { new { b64_json = Convert.ToBase64String(png) } } }) };
        }
    }
}
#endif
