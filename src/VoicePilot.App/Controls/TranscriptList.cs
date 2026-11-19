using Microsoft.UI.Xaml.Controls;

namespace VoicePilot.App.Controls;

public sealed class TranscriptList : ListView
{
    public TranscriptList()
    {
        SelectionMode = ListViewSelectionMode.None;
        IsItemClickEnabled = false;
        Items.VectorChanged += (_, _) =>
        {
            if (Items.Count > 0)
            {
                _ = DispatcherQueue.TryEnqueue(() => ScrollIntoView(Items[Items.Count - 1]));
            }
        };
    }
}
