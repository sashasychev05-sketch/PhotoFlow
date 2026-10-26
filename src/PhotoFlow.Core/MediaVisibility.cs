namespace PhotoFlow.Core;

/// <summary>Exclude service storage before descending into folders or requesting previews.</summary>
public static class MediaVisibility
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Android", "cache", "caches", "thumbnails", "thumbs", "trash", "trashed",
        "recycle", "recycler", "recycle bin", "$RECYCLE.BIN", "System Volume Information",
        "lost+found", "recently deleted", "deleted", "корзина", "недавно удаленные",
        "недавно удалённые", "кэш", "кеш"
    };

    public static bool IsExcluded(string relativePath) => relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
        .Any(part => part.StartsWith('.') || Excluded.Contains(part));
}
