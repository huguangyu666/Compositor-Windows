namespace Compositor.Core;

/// <summary>
/// The non-printing layout grid: a major line every <paramref name="spacing"/> document pixels, each square
/// split into <paramref name="subdivisions"/> parts (the Mac's LayoutGrid, Guides.swift:36-61 — 64 and eight,
/// every 8 pixels, until View ▸ Grid Settings… says otherwise). Lines are counted from the origin rather than
/// accumulated, so a step that doesn't divide the spacing evenly cannot drift off the majors, and each line
/// lands on a whole document pixel because that is where the Mac rounds it.
/// </summary>
public sealed class LayoutGrid : IEquatable<LayoutGrid>
{
    public const int SpacingMin = 2, SpacingMax = 4096, SubdivisionMin = 1, SubdivisionMax = 64;

    /// What a document starts with, before anything has been chosen in Grid Settings….
    public static LayoutGrid Default { get; } = new();

    public int Spacing { get; }
    public int Subdivisions { get; }

    /// Distance between two neighbouring lines, in document pixels.
    public double Step => (double)Spacing / Subdivisions;

    public LayoutGrid(int spacing = 64, int subdivisions = 8)
    {
        Spacing = Math.Min(Math.Max(spacing, SpacingMin), SpacingMax);
        // A subdivision finer than a pixel is not a line anyone can see, so the parts never outnumber the
        // pixels between majors.
        Subdivisions = Math.Min(Math.Min(Math.Max(subdivisions, SubdivisionMin), SubdivisionMax), Spacing);
    }

    /// Every line along an edge of this length, majors and subdivisions, whole document pixels.
    public IReadOnlyList<double> Lines(double length)
    {
        if (length < 0) return [0];
        int count = (int)Math.Floor(length / Step + .001);
        double[] lines = new double[count + 1];
        for (int index = 0; index <= count; index++)
            lines[index] = Math.Round(index * Step, MidpointRounding.AwayFromZero);
        return lines;
    }

    /// Majors are drawn in the chosen style; everything between them is a subdivision, dotted and fainter.
    public bool IsMajor(double value)
        => Math.Abs(Math.Round(value, MidpointRounding.AwayFromZero) % Spacing) < .001;

    public bool Equals(LayoutGrid? other) => other is not null && Spacing == other.Spacing && Subdivisions == other.Subdivisions;
    public override bool Equals(object? obj) => Equals(obj as LayoutGrid);
    public override int GetHashCode() => HashCode.Combine(Spacing, Subdivisions);
}
