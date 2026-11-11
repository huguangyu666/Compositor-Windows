using Compositor.App.Ai;
using Compositor.Core;
using Compositor.Renderer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private const string AiPanelKey = "aiImage";
    private AiEditorSession? aiEditor;
#if DEBUG
    private Func<HttpClient>? aiImageHttpProbe;
#endif
    private enum AiOperation { Remove, Replace, Expand }

    private sealed class AiEditorSession(EditorDocument owner, HttpClient? http)
    {
        public readonly EditorDocument Owner = owner;
        public readonly HttpClient? Http = http;
        public readonly AiCredentialStore Store = new();
        public ChatGptAccount? Account;
        public CancellationTokenSource? Work;
        public Action? OnChanged;
        public GeneratedImageEdit? Edit;
        public WriteableBitmap? Before;
        public string RequestId = "";
        public string Status = "idle";
        public string Stage = "idle";
        public Func<Task>? Generate;
        public Action<string?, string, string, int, int, int, int>? Configure;
        public Action<IReadOnlyList<AiReferenceFile>, string>? ConfigureReferences;
        public Action<int>? SelectCandidate;
        public bool External;
        public string? InputDirectory;
        public Action? RefreshExternal;
        public Action<Surface, AiBitmapData>? PresentExternal;
        public readonly List<(Surface Layer, WriteableBitmap Preview)> Candidates = [];
    }

    private void OnAiRemove(object sender, RoutedEventArgs e) => OpenAiEditor(AiOperation.Remove);
    private void OnAiReplace(object sender, RoutedEventArgs e) => OpenAiEditor(AiOperation.Replace);
    private void OnAiExpand(object sender, RoutedEventArgs e) => OpenAiEditor(AiOperation.Expand);

    private void RefreshAiActions()
    {
        if (AiActions is null) return;
        RefreshAiWorkingRegion();
        var selection = document?.ActiveSelection;
        bool selected = selection is { IsEmpty: false };
        AiActions.Visibility = selected && aiEditor is null && !ObjectSelectionActive && tool is CanvasTool.RectSelect or CanvasTool.EllipseSelect or CanvasTool.Lasso or CanvasTool.Wand or CanvasTool.ObjectSelect
            ? Visibility.Visible : Visibility.Collapsed;
        AiRemoveButton.IsEnabled = AiReplaceButton.IsEnabled = selected;
        double x = 16, y = Math.Max(8, CanvasStage.ActualHeight - 62);
        if (selected)
        {
            var bounds = selection!.Bounds;
            x = viewport.Origin.X + bounds.X * viewport.PointsPerPixel;
            y = viewport.Origin.Y + (bounds.Y + bounds.Height) * viewport.PointsPerPixel + 10;
        }
        AiActions.Margin = new Thickness(Math.Clamp(x, 8, Math.Max(8, CanvasStage.ActualWidth - 310)),
            Math.Clamp(y, 8, Math.Max(8, CanvasStage.ActualHeight - 62)), 0, 0);
    }

    private void RetireAiEdit()
    {
        StopAiWorkingEffect(immediate: true);
        var session = aiEditor;
        aiEditor = null;
        if (session is not null)
        {
            session.Work?.Cancel();
            session.Owner.Changed -= session.OnChanged;
            session.Owner.PixelsChanged -= session.OnChanged;
            session.Owner.SelectionChanged -= session.OnChanged;
            session.Http?.Dispose();
            DeleteAgentInput(session);
        }
        AbandonFloatingPanel(AiPanelKey);
        AiPreviewHost.Visibility = Visibility.Collapsed;
        AiPreviewImage.Source = null;
        RefreshAiActions();
    }

    private void OpenAiEditor(AiOperation operation)
    {
        if (document is not { } owner || !CanEditSelection) return;
        RetireAiEdit();
        var http = AiHttp.CreateClient();
#if DEBUG
        if (aiImageHttpProbe is { } createHttp) { http.Dispose(); http = createHttp(); }
#endif
        var session = new AiEditorSession(owner, http);
        aiEditor = session;
        bool Current() => ReferenceEquals(aiEditor, session) && ReferenceEquals(document, owner) && !windowClosed;
        var body = new StackPanel { Spacing = 10 };
        var operationBox = new ComboBox { Name = "AiOperation", Header = Loc.T("AI_Operation"), HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { Loc.T("AI_Remove"), Loc.T("AI_Replace"), Loc.T("AI_Expand") }, SelectedIndex = (int)operation };
        var prompt = new TextBox { Name = "AiPrompt", Header = Loc.T("AI_Prompt"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 78, MaxLength = 4000, PlaceholderText = Loc.T("AI_PromptHint") };
        prompt.Text = operation == AiOperation.Remove ? Loc.T("AI_RemovePrompt") : operation == AiOperation.Expand ? Loc.T("AI_ExpandPrompt") : "";
        var expansion = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
        expansion.ColumnDefinitions.Add(new()); expansion.ColumnDefinitions.Add(new());
        expansion.RowDefinitions.Add(new()); expansion.RowDefinitions.Add(new());
        NumberBox Edge(string label, int row, int column)
        {
            var box = new NumberBox { Header = label, Minimum = 0, Maximum = 8192, Value = 0,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            Grid.SetRow(box, row); Grid.SetColumn(box, column); expansion.Children.Add(box); return box;
        }
        var left = Edge(Loc.T("AI_Left"), 0, 0); var right = Edge(Loc.T("AI_Right"), 0, 1);
        var top = Edge(Loc.T("AI_Top"), 1, 0); var bottom = Edge(Loc.T("AI_Bottom"), 1, 1);
        right.Value = Math.Max(64, owner.Snapshot.Manifest.Width / 4);
        expansion.Visibility = operation == AiOperation.Expand ? Visibility.Visible : Visibility.Collapsed;
        var preferences = AiImageSettings.Load();
        var connection = new ComboBox { Name = "AiConnection", Header = Loc.T("AI_Connection"), HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "API Key", Loc.T("AI_Account") }, SelectedIndex = preferences.Connection == "account" ? 1 : 0 };
        var model = new ComboBox { Name = "AiModel", Header = Loc.T("AI_Model"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var key = new PasswordBox { Name = "AiApiKey", Header = "API Key", PlaceholderText = Loc.T("AI_KeyHint") };
        var endpoint = new TextBox { Name = "AiApiEndpoint", Header = Loc.T("AI_Endpoint"), Text = "https://api.openai.com/v1/" };
        var saveKey = new Button { Name = "AiSaveKey", Content = Loc.T("AI_SaveKey") };
        var forgetKey = new Button { Name = "AiForgetKey", Content = Loc.T("AI_ForgetKey") };
        var apiButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        apiButtons.Children.Add(saveKey); apiButtons.Children.Add(forgetKey);
        var api = new StackPanel { Spacing = 8 }; api.Children.Add(key); api.Children.Add(endpoint); api.Children.Add(apiButtons);
        var signIn = new Button { Content = Loc.T("AI_SignIn") };
        var signOut = new Button { Content = Loc.T("AI_SignOut") };
        var account = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        account.Children.Add(new TextBlock { Text = Loc.T("AI_AccountNote"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var accountButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        accountButtons.Children.Add(signIn); accountButtons.Children.Add(signOut); account.Children.Add(accountButtons);
        var settingsBody = new StackPanel { Spacing = 8 };
        settingsBody.Children.Add(api); settingsBody.Children.Add(account);
        var settings = new Expander { Name = "AiSettings", Header = Loc.T("AI_Settings"), Content = settingsBody, HorizontalAlignment = HorizontalAlignment.Stretch };
        var apiPrivacy = new InfoBar { Name = "AiApiPrivacyNotice", IsOpen = true, IsClosable = false,
            Severity = InfoBarSeverity.Warning, Title = Loc.T("AI_ApiPrivacyTitle"), Message = Loc.T("AI_ApiPrivacyNote") };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        try
        {
            if (AiApiConnection.Load(session.Store) is { } saved) endpoint.Text = saved.ApiBase.AbsoluteUri;
        }
        catch (Exception) { message.Text = Loc.T("AI_KeyRequired"); }
        var generate = new Button { Name = "AiGenerate", Content = Loc.T("AI_Generate"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var stop = new Button { Name = "AiStop", Content = Loc.T("AI_Stop"), Visibility = Visibility.Collapsed };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var versions = new ComboBox { Name = "AiVersions", Header = Loc.T("AI_Version"), HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        var compare = new ToggleSwitch { Name = "AiCompare", Header = Loc.T("AI_Compare"), OnContent = Loc.T("AI_Original"), OffContent = Loc.T("AI_Result"), Visibility = Visibility.Collapsed };
        var references = BuildAiReferences(Current, ClearCandidates, message);
        session.ConfigureReferences = references.Configure;
        body.Children.Add(operationBox); body.Children.Add(prompt); body.Children.Add(expansion);
        body.Children.Add(connection); body.Children.Add(model);
        body.Children.Add(settings);
        body.Children.Add(references.Panel);
        body.Children.Add(apiPrivacy);
        body.Children.Add(new TextBlock { Text = Loc.T("AI_UploadNote"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        body.Children.Add(generate); body.Children.Add(stop); body.Children.Add(progress); body.Children.Add(message);
        body.Children.Add(versions); body.Children.Add(compare);
        bool working = false;
        void Busy(bool value)
        {
            working = value;
            references.Panel.IsEnabled = !value;
            foreach (var control in new Control[] { operationBox, prompt, connection, model, settings, left, right, top, bottom, generate, versions }) control.IsEnabled = !value;
            stop.Visibility = progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            SetFloatingPanelConfirmEnabled(AiPanelKey, !value && session.Candidates.Count > 0);
        }
        void ShowCandidate()
        {
            if (!Current() || versions.SelectedIndex < 0 || versions.SelectedIndex >= session.Candidates.Count) return;
            AiPreviewImage.Source = compare.IsOn ? session.Before : session.Candidates[versions.SelectedIndex].Preview;
            AiPreviewHost.Visibility = Visibility.Visible;
        }
        void ClearCandidates()
        {
            session.Edit = null; session.Before = null; session.Candidates.Clear(); versions.Items.Clear();
            versions.Visibility = compare.Visibility = AiPreviewHost.Visibility = Visibility.Collapsed;
            AiPreviewImage.Source = null; SetFloatingPanelConfirmEnabled(AiPanelKey, false);
        }
        void Present((Surface Layer, AiBitmapData Before, AiBitmapData After) prepared)
        {
            session.Before ??= MakeAiBitmap(prepared.Before);
            if (session.Candidates.Count == 3) { session.Candidates.RemoveAt(0); versions.Items.RemoveAt(0); }
            session.Candidates.Add((prepared.Layer, MakeAiBitmap(prepared.After)));
            versions.Items.Clear();
            for (int i = 0; i < session.Candidates.Count; i++) versions.Items.Add($"{Loc.T("AI_Version")} {i + 1}");
            versions.SelectedIndex = session.Candidates.Count - 1;
            versions.Visibility = compare.Visibility = Visibility.Visible;
            compare.IsOn = false; ShowCandidate(); message.Text = Loc.T("AI_Ready");
            session.Status = session.Stage = "ready";
        }
        operationBox.SelectionChanged += (_, _) =>
        {
            if (!Current()) return;
            ClearCandidates();
            expansion.Visibility = operationBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            prompt.Text = operationBox.SelectedIndex == 0 ? Loc.T("AI_RemovePrompt") : operationBox.SelectedIndex == 2 ? Loc.T("AI_ExpandPrompt") : "";
        };
        foreach (var edge in new[] { left, right, top, bottom }) edge.ValueChanged += (_, _) => { if (Current() && !working) ClearCandidates(); };
        bool syncingSettings = false;
        string ModelId() => model.SelectedItem as string ?? ImageModels.Sunburst;
        void SavePreferences()
        {
            if (syncingSettings) return;
            try { new AiImageSettings(connection.SelectedIndex == 1 ? "account" : "api", ModelId()).Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        void UpdateConnection(string? preferredModel = null)
        {
            syncingSettings = true;
            api.Visibility = connection.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
            apiPrivacy.Visibility = api.Visibility;
            account.Visibility = connection.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            key.Password = "";
            model.ItemsSource = new[] { ImageModels.Sunburst, ImageModels.Flare };
            model.SelectedItem = preferredModel is ImageModels.Sunburst or ImageModels.Flare ? preferredModel : ImageModels.Sunburst;
            syncingSettings = false;
        }
        UpdateConnection(preferences.Model);
        connection.SelectionChanged += (_, _) => { ClearCandidates(); UpdateConnection(); SavePreferences(); };
        model.SelectionChanged += (_, _) => { if (!syncingSettings) { ClearCandidates(); SavePreferences(); } };
        saveKey.Click += (_, _) =>
        {
            try
            {
                if (!AiHttp.IsCredentialValue(key.Password)) throw new ArgumentException(Loc.T("AI_KeyRequired"));
                if (!Uri.TryCreate(endpoint.Text, UriKind.Absolute, out var apiBase)) throw new ArgumentException(Loc.T("AI_EndpointInvalid"));
                new AiApiConnection(key.Password, apiBase).Save(session.Store);
                key.Password = ""; message.Text = Loc.T("AI_KeySaved");
            }
            catch (Exception) { message.Text = Loc.T("AI_KeySaveFailed"); }
        };
        forgetKey.Click += (_, _) => { try { session.Store.Delete("api-key"); key.Password = ""; message.Text = Loc.T("AI_KeyForgotten"); } catch (Exception) { message.Text = Loc.T("AI_KeySaveFailed"); } };
        signOut.Click += (_, _) =>
        {
            try { if (session.Account is { } signedIn) signedIn.SignOut(); else session.Store.Delete("chatgpt-account"); session.Account = null; message.Text = Loc.T("AI_SignedOut"); }
            catch (Exception) { message.Text = Loc.T("AI_AccountSaveFailed"); }
        };
        signIn.Click += async (_, _) =>
        {
            var cancellation = new CancellationTokenSource(); session.Work = cancellation; Busy(true); message.Text = Loc.T("AI_SigningIn");
            try
            {
                var oauth = new ChatGptOAuth(http);
                var tokens = await oauth.SignInAsync(cancellation: cancellation.Token);
                if (!Current() || cancellation.IsCancellationRequested) return;
                session.Account ??= new ChatGptAccount(oauth, session.Store);
                session.Account.SetTokens(tokens); message.Text = Loc.T("AI_SignedIn");
            }
            catch (OperationCanceledException) { if (Current()) message.Text = Loc.T("AI_Cancelled"); }
            catch (Exception) { if (Current()) message.Text = Loc.T("AI_SignInFailed"); }
            finally { if (Current()) { session.Work = null; Busy(false); } cancellation.Dispose(); }
        };
        stop.Click += (_, _) => session.Work?.Cancel();
        versions.SelectionChanged += (_, _) => ShowCandidate(); compare.Toggled += (_, _) => ShowCandidate();
        async Task GenerateAsync()
        {
            if (working || !Current() || !CanEditSelection) return;
            var cancellation = new CancellationTokenSource(); session.Work = cancellation;
            session.RequestId = Guid.NewGuid().ToString("N"); session.Status = "failed";
            session.Stage = "preparing";
            int operationIndex = operationBox.SelectedIndex;
            try
            {
                if (string.IsNullOrWhiteSpace(prompt.Text)) { message.Text = Loc.T("AI_PromptRequired"); return; }
                int EdgeValue(NumberBox box)
                {
                    if (!double.IsFinite(box.Value) || box.Value < 0 || box.Value > 8192 || box.Value != Math.Floor(box.Value))
                        throw new ArgumentException(Loc.T("AI_EdgeInvalid"));
                    return (int)box.Value;
                }
                session.Edit ??= operationIndex == 2
                    ? GeneratedImageEdit.Capture(owner, EdgeValue(left), EdgeValue(right), EdgeValue(top), EdgeValue(bottom))
                    : GeneratedImageEdit.Capture(owner);
                if (operationIndex == 2 && !session.Edit.Expands) throw new ArgumentException(Loc.T("AI_EdgeInvalid"));
                Func<ImageGenerationRequest, CancellationToken, Task<ImageGenerationResult>> generateImage;
                if (connection.SelectedIndex == 0)
                {
                    if (!Uri.TryCreate(endpoint.Text, UriKind.Absolute, out var apiBase)) throw new ArgumentException(Loc.T("AI_EndpointInvalid"));
                    var apiKey = string.IsNullOrEmpty(key.Password) ? AiApiConnection.Load(session.Store)?.KeyFor(apiBase) : key.Password;
                    if (string.IsNullOrWhiteSpace(apiKey)) { settings.IsExpanded = true; message.Text = Loc.T("AI_KeyRequired"); return; }
                    generateImage = new ImageGenerationClient(http, apiKey, apiBase).GenerateAsync;
                }
                else
                {
                    session.Account ??= new ChatGptAccount(new ChatGptOAuth(http), session.Store);
                    generateImage = new ImageGenerationClient(http, session.Account.GetTokensAsync).GenerateAsync;
                }
                string instruction = prompt.Text;
                string modelName = ModelId();
                var referenceFiles = references.Files(); string preserveFeatures = references.Preserve.Text;
                var edit = session.Edit;
                Busy(true); message.Text = Loc.T("AI_Generating");
                session.Status = session.Stage = "running";
                StartAiWorkingEffect();
                var prepared = await Task.Run(async () =>
                {
                    var referenceImages = await LoadAiReferencesAsync(referenceFiles, cancellation.Token);
                    var input = edit.RenderInput(cancellation.Token);
                    var imagePng = await WicImaging.EncodePngAsync(input.Image, cancellation.Token);
                    var maskPng = await WicImaging.EncodePngAsync(input.Mask, cancellation.Token);
                    var response = await generateImage(new(instruction, modelName)
                    { Image = new(imagePng), Mask = new(maskPng),
                        References = referenceImages, Preserve = preserveFeatures,
                        Background = edit.Expands ? "opaque" : "auto" }, cancellation.Token);
                    var decoded = await WicImaging.DecodeSurfaceAsync(response.Png, Limits.MaxSide, GeneratedImageEdit.MaxPixels, cancellation.Token);
                    var layer = edit.PrepareResult(decoded, cancellation.Token);
                    var preview = new CanvasCompositor(edit.Preview(layer, "AI Preview")).Render();
                    cancellation.Token.ThrowIfCancellationRequested();
                    return (Layer: layer, Before: PrepareAiBitmap(input.Image), After: PrepareAiBitmap(preview));
                }, cancellation.Token);
                if (!Current() || cancellation.IsCancellationRequested || !edit.IsCurrent(owner)) return;
                Present(prepared);
            }
            catch (OperationCanceledException) { session.Status = "cancelled"; if (Current()) message.Text = Loc.T("AI_Cancelled"); }
            catch (AiServiceException ex) { session.Status = "failed"; if (Current()) message.Text = ex.Message; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { session.Status = "failed"; if (Current()) message.Text = ex.Message; }
            catch (Exception) { session.Status = "failed"; if (Current()) message.Text = Loc.T("AI_Failed"); }
            finally { if (Current()) { StopAiWorkingEffect(); session.Work = null; Busy(false); } cancellation.Dispose(); }
        }
        generate.Click += async (_, _) => await GenerateAsync();
        session.Generate = GenerateAsync;
        session.Configure = (instruction, modelName, connectionName, l, r, t, b) =>
        {
            if (instruction is not null) prompt.Text = instruction;
            connection.SelectedIndex = connectionName == "account" ? 1 : 0;
            UpdateConnection(modelName); SavePreferences();
            left.Value = l; right.Value = r; top.Value = t; bottom.Value = b;
        };
        session.SelectCandidate = index => versions.SelectedIndex = index;
        session.OnChanged = () =>
        {
            if (Current() && (session.Edit is not null || !ReferenceEquals(document, owner))) RetireAiEdit();
            else RefreshAiActions();
        };
        owner.Changed += session.OnChanged; owner.PixelsChanged += session.OnChanged; owner.SelectionChanged += session.OnChanged;
        ShowFloatingPanel(AiPanelKey, Loc.T("AI_Title"), body, commit =>
        {
            var edit = session.Edit;
            var result = versions.SelectedIndex >= 0 && versions.SelectedIndex < session.Candidates.Count ? session.Candidates[versions.SelectedIndex].Layer : null;
            bool current = Current();
            RetireAiEdit();
            if (commit && current && edit is not null && result is not null
                && owner.AcceptGeneratedImage(edit, result, $"AI · {operationBox.SelectedItem}")) SetStatus(Loc.T("AI_Accepted"));
            return Task.CompletedTask;
        }, onAbandon: RetireAiEdit);
        var panel = floatingPanels[AiPanelKey];
        panel.Confirm.Content = Loc.T("AI_Accept");
        // Keep the answer row visible on short windows; only the editor body scrolls.
        var stack = (StackPanel)panel.Root.Child;
        var sections = stack.Children.ToArray(); stack.Children.Clear();
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (int i = 0; i < sections.Length; i++) { Grid.SetRow((FrameworkElement)sections[i], i); layout.Children.Add(sections[i]); }
        panel.Editor.MaxHeight = double.PositiveInfinity;
        panel.Root.Child = layout;
        SetFloatingPanelConfirmEnabled(AiPanelKey, false);
        RefreshAiActions();
    }

    private sealed record AiBitmapData(int Width, int Height, byte[] Bgra);
    private static AiBitmapData PrepareAiBitmap(Surface image)
    {
        double scale = Math.Min(1, Math.Min(1600.0 / image.Width, 1200.0 / image.Height));
        if (scale < 1)
        {
            var smaller = new Surface(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
            Resampler.Draw(image, new LayerTransform { Size = new SizeD(smaller.Width, smaller.Height) }, smaller, 0, 0, smaller.Width, smaller.Height);
            image = smaller;
        }
        var bgra = new byte[image.Width * image.Height * 4];
        SurfacePresentation.FillBgra(image, bgra, image.Width, 0, 0, image.Width, image.Height);
        return new(image.Width, image.Height, bgra);
    }

    private static WriteableBitmap MakeAiBitmap(AiBitmapData data)
    {
        var bitmap = new WriteableBitmap(data.Width, data.Height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(data.Bgra);
        bitmap.Invalidate(); return bitmap;
    }
}
