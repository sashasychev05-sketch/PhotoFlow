using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace PhotoFlow.App;

public sealed class MediaCollection(Func<int, Task<IReadOnlyList<MediaCard>>> load) : ObservableCollection<MediaCard>, ISupportIncrementalLoading
{
    private bool loading;
    public bool HasMoreItems { get; private set; } = true;
    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) => AsyncInfo.Run(async cancellation =>
    {
        if (loading || !HasMoreItems) return new LoadMoreItemsResult { Count = 0 };
        loading = true;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var items = await load(Count);
            foreach (var item in items) Add(item);
            HasMoreItems = items.Count == 200;
            return new LoadMoreItemsResult { Count = (uint)items.Count };
        }
        finally { loading = false; }
    });
}
