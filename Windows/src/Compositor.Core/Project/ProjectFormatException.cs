namespace Compositor.Core;

public enum ProjectErrorKind
{
    /// <summary>Not a valid Compositor project, or its metadata is damaged.</summary>
    Invalid,
    /// <summary>The project uses a format version this app does not support.</summary>
    Version,
    /// <summary>An image inside the project is missing or damaged.</summary>
    MissingImage,
    /// <summary>The project exceeds a canvas, layer, file-size or pixel-budget limit.</summary>
    TooLarge,
    /// <summary>An image could not be encoded on save.</summary>
    Encode,
}

/// <summary>The same refusals the macOS ProjectStore raises, one exception type with a kind.</summary>
public sealed class ProjectFormatException : Exception
{
    public ProjectErrorKind Kind { get; }

    public int? Version { get; }

    public ProjectFormatException(ProjectErrorKind kind, int? version = null, Exception? innerException = null)
        : base(Describe(kind, version), innerException)
    {
        Kind = kind;
        Version = version;
    }

    private static string Describe(ProjectErrorKind kind, int? version) => kind switch
    {
        ProjectErrorKind.Version =>
            $"This project uses format version {version}. This app supports versions 1–{ProjectManifest.CurrentVersion}.",
        ProjectErrorKind.MissingImage =>
            "An image inside the project is missing or damaged. The current document has not been replaced.",
        ProjectErrorKind.TooLarge =>
            "This project exceeds the supported canvas, layer, file-size, or pixel budget limit.",
        ProjectErrorKind.Encode =>
            "An image could not be saved. The previous project has not been replaced.",
        _ => "This is not a valid Compositor project, or its metadata is damaged.",
    };
}

/// <summary>
/// The package on disk changed since the caller last acknowledged it, so saving would replace work
/// somebody else wrote. The macOS controller asks what to do instead of writing
/// (IO/ProjectController+ExternalChanges.swift:30-58, "Unsaved work is never replaced without asking");
/// this is the save-side half of that promise, raised before a byte is touched.
/// </summary>
public sealed class ProjectConflictException : Exception
{
    public string ProjectPath { get; }

    /// <summary>The digest the package holds right now.</summary>
    public byte[] DiskDigest { get; }

    /// <summary>The digest the caller believed it was saving over.</summary>
    public byte[] ExpectedDigest { get; }

    public ProjectConflictException(string projectPath, byte[] diskDigest, byte[] expectedDigest)
        : base($"The project at {projectPath} changed on disk since it was last saved or reloaded; nothing was written.")
    {
        ProjectPath = projectPath;
        DiskDigest = diskDigest;
        ExpectedDigest = expectedDigest;
    }
}
