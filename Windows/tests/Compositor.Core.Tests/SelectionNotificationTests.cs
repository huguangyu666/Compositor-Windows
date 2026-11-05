using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

public class SelectionNotificationTests
{
    [Fact]
    public void SessionSelectionDoesNotInvalidateTheDocumentComposite()
    {
        var document = EditorDocument.Create(16, 16);
        int contentChanges = 0;
        int selectionChanges = 0;
        document.Changed += () => contentChanges++;
        document.SelectionChanged += () => selectionChanges++;
        var snapshot = document.Snapshot;
        document.SetSelection(Selection.Rectangle(16, 16, 2, 3, 8, 9));
        document.SetSelection(null);
        Assert.Same(snapshot, document.Snapshot);
        Assert.Equal(0, contentChanges);
        Assert.Equal(2, selectionChanges);
    }
}
