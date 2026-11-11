using Compositor.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace Compositor.Segmentation.Tests;

public sealed class ObjectSelectionRequestTests
{
    [Fact]
    public void ForegroundBackgroundAndBoxUseDocumentCoordinates()
    {
        var request = ObjectSelectionRequest.Parse(JsonNode.Parse("""
            {"foregroundPoints":[[120.5,60]],"backgroundPoints":[[4,8]],
             "box":{"x":10,"y":20,"width":160,"height":80},"softEdges":false}
            """)!.AsObject(), 200, 120);
        Assert.Equal(new SelectionPoint(new PointD(120.5, 60)), request.Points[0]);
        Assert.Equal(new SelectionPoint(new PointD(4, 8), false), request.Points[1]);
        Assert.Equal(new SelectionBox(10, 20, 160, 80), request.Box);
        Assert.False(request.SoftEdges);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"backgroundPoints\":[[2,3]]}")]
    [InlineData("{\"foregroundPoints\":[[201,1]]}")]
    [InlineData("{\"foregroundPoints\":[[200,1]]}")]
    [InlineData("{\"foregroundPoints\":[[1]]}")]
    [InlineData("{\"box\":{\"x\":0,\"y\":0,\"width\":0,\"height\":20}}")]
    [InlineData("{\"box\":{\"x\":10,\"y\":0,\"width\":200,\"height\":20}}")]
    public void InvalidPromptsAreRejectedWithoutLoadingAModel(string json) =>
        Assert.Throws<ArgumentException>(() => ObjectSelectionRequest.Parse(JsonNode.Parse(json)!.AsObject(), 200, 120));

    [Fact]
    public void ConstructedIntegerCoordinatesAreNumbers()
    {
        var args = new JsonObject { ["foregroundPoints"] = new JsonArray(new JsonArray(20, 30)) };
        Assert.Equal(new PointD(20, 30), ObjectSelectionRequest.Parse(args, 200, 120).Points[0].Position);
    }
}
