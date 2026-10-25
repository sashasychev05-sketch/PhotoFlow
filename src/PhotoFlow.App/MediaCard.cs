using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using PhotoFlow.Infrastructure.Catalog;

namespace PhotoFlow.App;

public sealed class MediaCard(CatalogItem item) : ObservableObject
{
    public CatalogItem Item { get; } = item;
    public string Name => Item.Media.FileName;
    public string Badge => Item.Media.IsRaw ? "RAW" : Item.Media.Extension.TrimStart('.').ToUpperInvariant();
    public string Details => (Item.ImportedPath is not null ? "Импортировано · " : "") +
        (Item.Media.CaptureLocal?.ToString("dd.MM.yyyy") ?? "Дата не известна") + " · " + FormatSize(Item.Media.SizeBytes);
    private bool favorite = item.Favorite;
    private bool selected = item.Selected;
    private ImageSource? preview;
    public string FavoriteGlyph => Favorite ? "★" : "☆";
    public bool Favorite { get => favorite; set { if (SetProperty(ref favorite, value)) { OnPropertyChanged(nameof(FavoriteGlyph)); FlagChanged?.Invoke(this, "favorite", value); } } }
    public bool Selected { get => selected; set { if (SetProperty(ref selected, value)) FlagChanged?.Invoke(this, "selected", value); } }
    public ImageSource? Preview { get => preview; set => SetProperty(ref preview, value); }
    public Action<MediaCard, string, bool>? FlagChanged { get; set; }
    public static string FormatSize(long? bytes) => bytes switch
    {
        null => "размер неизвестен", < 1024 => $"{bytes} Б", < 1048576 => $"{bytes / 1024.0:F0} КБ",
        < 1073741824 => $"{bytes / 1048576.0:F1} МБ", _ => $"{bytes / 1073741824.0:F2} ГБ"
    };
}
