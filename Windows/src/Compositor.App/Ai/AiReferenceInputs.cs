using System.Text;

namespace Compositor.App.Ai;

internal static class AiReferenceInputs
{
    public const int MaxReferences = 4;
    public static readonly string[] Roles = ["general", "identity", "clothing", "style", "composition"];

    public static void Validate(ImageGenerationRequest input, int maxImages = 16, int imageByteLimit = 14 * 1024 * 1024)
    {
        if (input.References.Count > MaxReferences || input.References.Count > 0 && input.Image is null)
            throw new ArgumentException("Use an editable source canvas and at most four reference images.");
        if (input.Preserve.Length > 1000) throw new ArgumentException("Preservation instructions support at most 1000 characters.");
        if ((input.Image is null ? 0 : 1) + input.References.Count > maxImages)
            throw new ArgumentException("The selected model cannot accept this many source and reference images.");
        long total = (input.Image?.Bytes.LongLength ?? 0) + (input.Mask?.Bytes.LongLength ?? 0);
        foreach (var image in new[] { input.Image, input.Mask })
            if (image is not null && (image.Bytes.Length is < 1 || image.Bytes.Length > imageByteLimit)) throw new ArgumentException("A source image or mask exceeds this model's encoded input limit.");
        foreach (var reference in input.References)
        {
            if (reference is null || !Roles.Contains(reference.Role) || reference.Image is null
                || reference.Image.Bytes.Length is < 1 || reference.Image.Bytes.Length > imageByteLimit
                || reference.Image.MediaType is not ("image/png" or "image/jpeg" or "image/webp"))
                throw new ArgumentException("Use valid PNG/JPEG/WebP references with an advertised reference role.");
            total += reference.Image.Bytes.LongLength;
        }
        if (total > 48L * 1024 * 1024) throw new ArgumentException("Combined encoded source, mask and reference images must fit within 48 MiB.");
    }

    public static string Prompt(ImageGenerationRequest input)
    {
        if (input.References.Count == 0 && string.IsNullOrWhiteSpace(input.Preserve)) return input.Prompt;
        var prompt = new StringBuilder("Image 1 is the editable source canvas. Additional reference images are visual guidance, not replacement canvases. Keep the source framing.\n");
        int index = 2;
        foreach (var reference in input.References)
            prompt.Append("Image ").Append(index++).Append(": ").Append(reference.Role).Append(" reference. Use it only for that role.\n");
        if (!string.IsNullOrWhiteSpace(input.Preserve)) prompt.Append("Preserve these features of the source: ").Append(input.Preserve.Trim()).Append('\n');
        return prompt.Append("Requested edit: ").Append(input.Prompt).ToString();
    }
}
