using Compositor.App.Ai;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed record AiReferenceControls(Expander Panel, Func<AiReferenceFile[]> Files, TextBox Preserve,
        Action<IReadOnlyList<AiReferenceFile>, string> Configure);

    private AiReferenceControls BuildAiReferences(Func<bool> current, Action changed, TextBlock message)
    {
        var files = new List<AiReferenceFile>();
        var rows = new StackPanel { Spacing = 6 };
        var add = new Button { Name = "AiAddReferences", Content = Loc.T("AI_AddReferences") };
        var preserve = new TextBox { Name = "AiPreserve", Header = Loc.T("AI_Preserve"), PlaceholderText = Loc.T("AI_PreserveHint"),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 1000, MinHeight = 60 };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(rows); body.Children.Add(add); body.Children.Add(preserve);
        body.Children.Add(new TextBlock { Text = Loc.T("AI_ReferencesNote"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var panel = new Expander { Name = "AiReferences", Header = Loc.T("AI_References"), Content = body, HorizontalAlignment = HorizontalAlignment.Stretch };
        void Refresh()
        {
            rows.Children.Clear(); add.IsEnabled = files.Count < AiReferenceInputs.MaxReferences;
            foreach (var (file, fileIndex) in files.Select((file, index) => (file, index)).ToArray())
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = Path.GetFileName(file.Path), TextTrimming = TextTrimming.CharacterEllipsis });
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var role = new ComboBox { ItemsSource = AiReferenceInputs.Roles.Select(value => new ComboBoxItem { Content = Loc.T("AI_Reference_" + value), Tag = value }).ToArray() };
                role.SelectedIndex = Array.IndexOf(AiReferenceInputs.Roles, file.Role);
                var remove = new Button { Content = Loc.T("AI_RemoveReference") };
                role.SelectionChanged += (_, _) =>
                {
                    int index = fileIndex < files.Count ? fileIndex : -1;
                    if (index >= 0 && role.SelectedItem is ComboBoxItem { Tag: string value }) { files[index] = files[index] with { Role = value }; changed(); }
                };
                remove.Click += (_, _) => { if (fileIndex < files.Count) files.RemoveAt(fileIndex); Refresh(); changed(); };
                actions.Children.Add(role); actions.Children.Add(remove); row.Children.Add(actions); rows.Children.Add(row);
            }
        }
        add.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SettingsIdentifier = "aiReferences", SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(extension);
            InitializeWithWindow(picker);
            try
            {
                var selected = await picker.PickMultipleFilesAsync();
                if (!current() || !panel.IsEnabled) return;
                var additions = selected.Where(file => !files.Any(value => string.Equals(value.Path, file.Path, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (files.Count + additions.Length > AiReferenceInputs.MaxReferences) { message.Text = Loc.T("AI_ReferencesLimit"); return; }
                files.AddRange(additions.Select(file => new AiReferenceFile(file.Path))); Refresh(); changed();
            }
            catch (Exception) { if (current()) message.Text = Loc.T("AI_ReferencesFailed"); }
        };
        preserve.TextChanged += (_, _) => changed();
        return new(panel, () => files.ToArray(), preserve, (selected, text) =>
        {
            if (selected.Count > AiReferenceInputs.MaxReferences || selected.Any(file => !Path.IsPathFullyQualified(file.Path) || !AiReferenceInputs.Roles.Contains(file.Role)) || text.Length > 1000)
                throw new ArgumentException("Use at most four local references with valid roles, and at most 1000 preservation characters.");
            files.Clear(); files.AddRange(selected); preserve.Text = text; Refresh(); changed();
        });
    }

    private static async Task<IReadOnlyList<ImageReference>> LoadAiReferencesAsync(IReadOnlyList<AiReferenceFile> files, CancellationToken cancellation)
    {
        var result = new List<ImageReference>();
        foreach (var file in files)
        {
            if (!Path.IsPathFullyQualified(file.Path) || !AiReferenceInputs.Roles.Contains(file.Role)) throw new ArgumentException("Invalid reference path or role.");
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 14 * 1024 * 1024) throw new ArgumentException("Each reference image must fit within 14 MiB.");
            var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, cancellation);
            var image = await WicImaging.DecodeSurfaceAsync(bytes, Limits.MaxSide, GeneratedImageEdit.MaxPixels, cancellation);
            double scale = Math.Min(1, 1600d / Math.Max(image.Width, image.Height));
            var bounded = image;
            if (scale < 1)
            {
                bounded = new Surface(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
                Resampler.Draw(image, new LayerTransform { Size = new(bounded.Width, bounded.Height) }, bounded, 0, 0, bounded.Width, bounded.Height);
            }
            result.Add(new(new(await WicImaging.EncodePngAsync(bounded, cancellation)), file.Role));
        }
        return result;
    }
}
