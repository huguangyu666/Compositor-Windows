using Compositor.Core;
using Compositor.Segmentation;
using Xunit;

public sealed class MobileSamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusesAmbiguousOrOutsidePromptsBeforeLoadingTheModel(bool sam2)
    {
        using var model = new SamSegmenter(new(Path.GetTempPath(), sam2 ? ModelStore.Sam2Files : ModelStore.MobileSamFiles), false);
        var image = new Surface(20, 10);
        await Assert.ThrowsAsync<ArgumentException>(() => model.SelectAsync(image, [], null, true, default));
        await Assert.ThrowsAsync<ArgumentException>(() => model.SelectAsync(image, [new(new(4, 4), false)], null, true, default));
        await Assert.ThrowsAsync<ArgumentException>(() => model.SelectAsync(image, [new(new(double.NaN, 4))], null, true, default));
        await Assert.ThrowsAsync<ArgumentException>(() => model.SelectAsync(image, [], new(4, 4, 0, 3), true, default));
    }
}
