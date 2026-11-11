using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private AiEditorSession OpenAgentImageEditor(EditorDocument owner, GeneratedImageEdit edit)
    {
        var session = new AiEditorSession(owner, null)
        {
            External = true, Edit = edit, RequestId = Guid.NewGuid().ToString("N"), Status = "preparing", Stage = "preparing",
        };
        session.InputDirectory = Path.Combine(Path.GetTempPath(), "Compositor", "AgentImages", session.RequestId);
        aiEditor = session;
        bool Current() => ReferenceEquals(aiEditor, session) && ReferenceEquals(document, owner) && !windowClosed;
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = Loc.T("AI_AgentNote"), TextWrapping = TextWrapping.Wrap });
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var versions = new ComboBox { Header = Loc.T("AI_Version"), HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        var compare = new ToggleSwitch { Header = Loc.T("AI_Compare"), OnContent = Loc.T("AI_Original"), OffContent = Loc.T("AI_Result"), Visibility = Visibility.Collapsed };
        body.Children.Add(message); body.Children.Add(versions); body.Children.Add(compare);
        void ShowCandidate()
        {
            if (!Current() || versions.SelectedIndex < 0 || versions.SelectedIndex >= session.Candidates.Count) return;
            AiPreviewImage.Source = compare.IsOn ? session.Before : session.Candidates[versions.SelectedIndex].Preview;
            AiPreviewHost.Visibility = Visibility.Visible;
        }
        session.RefreshExternal = () =>
        {
            if (!Current()) return;
            message.Text = Loc.T(session.Status switch
            {
                "ready" => "AI_Ready", "cancelled" => "AI_Cancelled", "preparing" => "AI_AgentPreparing",
                "running" => "AI_AgentImporting", _ => "AI_AgentWaiting",
            });
            versions.IsEnabled = compare.IsEnabled = session.Work is null;
            SetFloatingPanelConfirmEnabled(AiPanelKey, session.Status == "ready" && session.Work is null && session.Candidates.Count > 0);
        };
        session.PresentExternal = (layer, preview) =>
        {
            if (!Current()) return;
            if (session.Candidates.Count == 3) session.Candidates.RemoveAt(0);
            session.Candidates.Add((layer, MakeAiBitmap(preview)));
            versions.Items.Clear();
            for (int i = 0; i < session.Candidates.Count; i++) versions.Items.Add($"{Loc.T("AI_Version")} {i + 1}");
            versions.SelectedIndex = session.Candidates.Count - 1;
            versions.Visibility = compare.Visibility = Visibility.Visible;
            compare.IsOn = false; ShowCandidate();
            session.Status = session.Stage = "ready";
        };
        session.SelectCandidate = index => versions.SelectedIndex = index;
        versions.SelectionChanged += (_, _) => ShowCandidate(); compare.Toggled += (_, _) => ShowCandidate();
        session.OnChanged = () => { if (Current()) RetireAiEdit(); };
        owner.Changed += session.OnChanged; owner.PixelsChanged += session.OnChanged; owner.SelectionChanged += session.OnChanged;
        ShowFloatingPanel(AiPanelKey, Loc.T("AI_AgentTitle"), body, commit =>
        {
            var result = versions.SelectedIndex >= 0 && versions.SelectedIndex < session.Candidates.Count ? session.Candidates[versions.SelectedIndex].Layer : null;
            bool current = Current() && session.Status == "ready" && session.Work is null;
            RetireAiEdit();
            if (commit && current && result is not null && owner.AcceptGeneratedImage(edit, result, Loc.T("AI_AgentLayer"))) SetStatus(Loc.T("AI_Accepted"));
            return Task.CompletedTask;
        }, onAbandon: RetireAiEdit);
        floatingPanels[AiPanelKey].Confirm.Content = Loc.T("AI_Accept");
        session.RefreshExternal(); RefreshAiActions(); StartAiWorkingEffect();
        return session;
    }

    private async Task PrepareAgentImageAsync(JsonObject args, EditorDocument owner)
    {
        RequireEditorIdle();
        string operation = args["operation"]?.GetValue<string>() ?? "replace";
        if (operation is not ("remove" or "replace" or "expand")) throw new ArgumentException("operation must be remove, replace or expand.");
        int left = EditorEdge(args, "left"), right = EditorEdge(args, "right"), top = EditorEdge(args, "top"), bottom = EditorEdge(args, "bottom");
        if (operation == "expand" ? left + right + top + bottom == 0 : left + right + top + bottom != 0)
            throw new ArgumentException("Only expand accepts expansion edges; set at least one nonzero edge for expand.");
        var edit = GeneratedImageEdit.Capture(owner, left, right, top, bottom);
        var session = OpenAgentImageEditor(owner, edit);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellation = deadline.Token;
        session.Work = deadline;
        try
        {
            var before = await Task.Run(async () =>
            {
                try
                {
                    var input = edit.RenderInput(cancellation);
                    byte[] image = await WicImaging.EncodePngAsync(input.Image, cancellation);
                    byte[] mask = await WicImaging.EncodePngAsync(input.Mask, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(session.InputDirectory!);
                    await WriteAgentInputAsync(Path.Combine(session.InputDirectory!, "input.png"), image, cancellation);
                    await WriteAgentInputAsync(Path.Combine(session.InputDirectory!, "mask.png"), mask, cancellation);
                    return PrepareAiBitmap(input.Image);
                }
                finally { if (cancellation.IsCancellationRequested) DeleteAgentInput(session); }
            }, cancellation).WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            RequireEditorTarget(args);
            if (!ReferenceEquals(aiEditor, session) || !edit.IsCurrent(owner)) throw new InvalidOperationException("The agent input was retired. Prepare a new edit.");
            session.Before = MakeAiBitmap(before);
            session.Status = session.Stage = "waiting";
        }
        catch
        {
            if (ReferenceEquals(aiEditor, session)) RetireAiEdit();
            throw;
        }
        finally
        {
            session.Work = null;
            if (ReferenceEquals(aiEditor, session)) session.RefreshExternal!();
            else DeleteAgentInput(session);
        }
    }

    private async Task ImportAgentImageAsync(JsonObject args, EditorDocument owner)
    {
        RequireEditorIdle(allowAi: true);
        var session = RequireEditorAi(args);
        if (!session.External || session.Work is not null || session.Status is not ("waiting" or "ready") || session.Edit?.IsCurrent(owner) != true)
            throw new InvalidOperationException("Prepare an external-agent edit first; only its waiting or ready request accepts results.");
        string path = EditorPath(args["path"]?.GetValue<string>(), ".png");
        var edit = session.Edit;
        string layerName = Loc.T("AI_AgentLayer");
        string previous = session.Status;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellation = deadline.Token;
        session.Work = deadline;
        session.Status = "running"; session.Stage = "importing";
        session.RefreshExternal!(); StartAiWorkingEffect();
        try
        {
            var prepared = await Task.Run(async () =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                if (file.Length > Limits.AssetMaxBytes) throw new ArgumentException("The PNG exceeds the encoded asset limit.");
                var bytes = new byte[checked((int)file.Length)];
                await file.ReadExactlyAsync(bytes, cancellation);
                var decoded = await WicImaging.DecodeSurfaceAsync(bytes, Limits.MaxSide, GeneratedImageEdit.MaxPixels, cancellation);
                var layer = edit.PrepareResult(decoded, cancellation);
                var preview = new CanvasCompositor(edit.Preview(layer, layerName)).Render();
                cancellation.ThrowIfCancellationRequested();
                return (Layer: layer, Preview: PrepareAiBitmap(preview));
            }, cancellation).WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            RequireEditorTarget(args); RequireEditorIdle(allowAi: true);
            if (!ReferenceEquals(aiEditor, session) || session.Status == "cancelled" || !edit.IsCurrent(owner))
                throw new InvalidOperationException("The agent result was retired. Prepare a new edit.");
            session.PresentExternal!(prepared.Layer, prepared.Preview);
        }
        finally
        {
            session.Work = null;
            if (ReferenceEquals(aiEditor, session))
            {
                if (session.Status == "running") session.Status = session.Stage = previous;
                if (session.Status != "waiting") StopAiWorkingEffect();
                session.RefreshExternal!();
            }
        }
    }

    private static async Task WriteAgentInputAsync(string path, byte[] png, CancellationToken cancellation)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await file.WriteAsync(png, cancellation);
    }

    private static void DeleteAgentInput(AiEditorSession session)
    {
        if (session.InputDirectory is not { } directory) return;
        try
        {
            File.Delete(Path.Combine(directory, "input.png")); File.Delete(Path.Combine(directory, "mask.png"));
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
