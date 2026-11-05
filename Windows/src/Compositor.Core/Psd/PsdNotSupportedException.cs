namespace Compositor.Core.Psd;

/// <summary>The file parsed fine but uses features this importer does not support.</summary>
public sealed class PsdNotSupportedException : Exception
{
    public PsdNotSupportedException(string message) : base(message)
    {
    }
}
