using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// A coalesced editing session — a filter preview, a slider drag, a transform drag — opens one undo step
/// and every later tick joins it. <see cref="EditorDocument.EndTransform"/> closes that step, and a session
/// that ends on the state it started with must leave nothing behind: the Mac's Cancel and Reset discard the
/// preview outright, so a Windows step that undoes to the picture already on screen is a step the user
/// would have to sit through without ever seeing it move.
/// </summary>
public class CoalescedSessionTests
{
    private static Surface Solid(int width, int height, byte red, byte green, byte blue)
    {
        var surface = new Surface(width, height);
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = red; surface.Pixels[i + 1] = green; surface.Pixels[i + 2] = blue; surface.Pixels[i + 3] = 255;
        }
        return surface;
    }

    private static (EditorDocument Document, Guid Layer) Scene()
    {
        var document = EditorDocument.Create(4, 4);
        return (document, document.Snapshot.Manifest.Layers[0].Id);
    }

    private static byte Red(EditorDocument document, Guid layer) => document.Snapshot.Images[layer].Pixels[0];

    [Fact]
    public void APreviewRevertedToTheCapturedPixelsLeavesNoUndoStep()
    {
        var (document, id) = Scene();
        var original = document.Snapshot.Images[id];
        var session = new object();
        Assert.True(document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), session));
        Assert.True(document.CanUndo);
        Assert.True(document.ReplaceLayerImage(id, original, session));       // the Reset path: the very surface it captured
        document.EndTransform(session);
        Assert.False(document.CanUndo);
        Assert.False(document.IsDirty);
        Assert.Equal(original.Pixels[0], Red(document, id));
    }

    [Fact]
    public void AReRenderedCopyOfTheOriginalPixelsIsStillANoOp()
    {
        // A preview that ran the filter again and came back with the same picture is a different surface
        // object holding the same bytes: the user saw nothing change, so the step is of no use to them.
        var (document, id) = Scene();
        var original = document.Snapshot.Images[id];
        var session = new object();
        Assert.True(document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), session));
        var rerendered = Solid(4, 4, original.Pixels[0], original.Pixels[1], original.Pixels[2]);
        Assert.False(ReferenceEquals(rerendered, original));
        Assert.True(document.ReplaceLayerImage(id, rerendered, session));
        document.EndTransform(session);
        Assert.False(document.CanUndo);
        Assert.Equal(original.Pixels[0], Red(document, id));
    }

    [Fact]
    public void ASessionThatEndedDifferentKeepsExactlyOneStep()
    {
        var (document, id) = Scene();
        var original = document.Snapshot.Images[id];
        var session = new object();
        document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), session);
        document.ReplaceLayerImage(id, Solid(4, 4, 0, 0, 255), session);      // a later tick of the same drag
        document.EndTransform(session);
        Assert.True(document.CanUndo);
        Assert.True(document.Undo());
        Assert.True(ReferenceEquals(original, document.Snapshot.Images[id])); // one step back to before the panel
        Assert.False(document.CanUndo);
        Assert.True(document.Redo());
        Assert.Equal((byte)0, Red(document, id));
    }

    [Fact]
    public void ASessionRevertingToTheUndoneStateOpensNoStepAtAll()
    {
        // The panel opened on the state an Undo left behind; the preview went out and back, so nothing is
        // waiting for the user except the redo they already had. Redo itself is not the bargain: opening a
        // panel clears the redo stack the way any change does, and that happens before the session closes.
        var (document, id) = Scene();
        document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0));
        Assert.True(document.Undo());
        var resting = document.Snapshot.Images[id];
        var session = new object();
        document.ReplaceLayerImage(id, Solid(4, 4, 0, 255, 0), session);
        document.ReplaceLayerImage(id, resting, session);
        document.EndTransform(session);
        Assert.False(document.CanUndo);
        Assert.True(ReferenceEquals(resting, document.Snapshot.Images[id]));
    }

    [Fact]
    public void ACancelledSessionDoesNotMakeACleanDocumentLookUnsaved()
    {
        var (document, id) = Scene();
        var original = document.Snapshot.Images[id];
        var session = new object();
        document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), session);
        Assert.True(document.IsDirty);
        document.ReplaceLayerImage(id, original, session);
        document.EndTransform(session);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void ADocumentThatWasAlreadyDirtyStaysDirtyAfterACancelledSession()
    {
        var (document, id) = Scene();
        var original = document.Snapshot.Images[id];
        document.SetOpacity(id, .5);                    // real unsaved work, no session key
        Assert.True(document.IsDirty);
        var session = new object();
        document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), session);
        document.ReplaceLayerImage(id, original, session);
        document.EndTransform(session);
        Assert.True(document.IsDirty);
        Assert.True(document.CanUndo);                  // the opacity step, not the cancelled preview
    }

    [Fact]
    public void LeavingAManifestChangeBehindIsNotACollapse()
    {
        // Pixels back where they started but the layer's opacity moved: the picture did change, so the
        // step has to survive — which is why the manifest is part of the comparison and not just the bytes.
        var (document, id) = Scene();
        var original = document.Snapshot.Images[id];
        var session = new object();
        document.SetOpacity(id, .4, session);
        document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), session);
        document.ReplaceLayerImage(id, original, session);
        document.EndTransform(session);
        Assert.True(document.CanUndo);
        Assert.True(document.Undo());
        Assert.Equal(1, document.Snapshot.Manifest.Layers[0].Opacity ?? 1);   // the layer is back at full opacity
    }

    [Fact]
    public void ClosingSomeoneElsesSessionDoesNotTouchTheirStep()
    {
        // A panel that never wrote anything under this key cannot be the one that opened a step. Closing a
        // foreign key has to leave the open session alone — otherwise a stale key would drop a step another
        // panel is still building, and that panel would then commit into history that no longer exists.
        var (document, id) = Scene();
        var original = document.Snapshot.Images[id];
        var theirs = new object();
        document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), theirs);
        document.ReplaceLayerImage(id, original, theirs);                     // back where they started, still open
        Assert.True(document.CanUndo);
        document.EndTransform(new object());                                 // somebody else's key
        Assert.True(document.CanUndo);
        document.EndTransform(theirs);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void ClosingAKeyThatIsNoLongerTheOpenSessionPopsNothing()
    {
        var (document, id) = Scene();
        var original = document.Snapshot.Images[id];
        var session = new object();
        document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), session);
        document.ReplaceLayerImage(id, Solid(4, 4, 0, 255, 0));      // an unkeyed write takes the session over
        document.EndTransform(session);                               // closing the old key must pop nothing
        Assert.True(document.Undo());
        Assert.Equal((byte)255, Red(document, id));                   // red: the session's own step
        Assert.True(document.Undo());
        Assert.True(ReferenceEquals(original, document.Snapshot.Images[id]));
        Assert.False(document.CanUndo);
    }
}
