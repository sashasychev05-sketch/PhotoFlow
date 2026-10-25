using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoFlow.Core;
using PhotoFlow.Infrastructure;
using PhotoFlow.Infrastructure.Catalog;
using PhotoFlow.Infrastructure.Wpd;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace PhotoFlow.App;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly CatalogStore store;
    private readonly string dataDirectory;
    private readonly SemaphoreSlim previewSlots = new(4);
    private readonly Dictionary<string, IMediaSource> previewSources = [];
    private readonly ConditionalWeakTable<GridViewItem, CancellationTokenSource> previewTokens = new();
    private readonly LinkedList<MediaCard> previewLru = new();
    private readonly Dictionary<MediaCard, LinkedListNode<MediaCard>> previewNodes = [];
    private CancellationTokenSource? operation;
    private CancellationTokenSource? pageCancellation;
    private CancellationTokenSource? searchCancellation;
    private MediaCollection? cards;
    private CatalogSource? currentSource;
    private bool favoritesOnly;
    private bool initialized;
    private bool busy;
    private int anchor;
    private int queryVersion;
    private Task pendingFlags = Task.CompletedTask;
    private TaskCompletionSource<bool>? busyCompletion;
    private bool closingReady;

    public MainWindow()
    {
        InitializeComponent();
        dataDirectory = Environment.GetEnvironmentVariable("PHOTOFLOW_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoFlow");
        store = new CatalogStore(Path.Combine(dataDirectory, "catalog.db"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1450, 940));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "PhotoFlow.ico"));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        Root.Loaded += async (_, _) => { initialized = true; await ReloadAsync(); await LoadSourcesAsync(); };
        AppWindow.Closing += async (_, e) =>
        {
            if (closingReady || (!busy && pendingFlags.IsCompleted)) return;
            e.Cancel = true; operation?.Cancel();
            StatusLabel.Text = "Завершаем запись и сохраняем отметки…";
            await Task.WhenAll(pendingFlags, busyCompletion?.Task ?? Task.CompletedTask);
            closingReady = true; Close();
        };
        Closed += async (_, _) =>
        {
            lifetime.Cancel(); operation?.Cancel(); pageCancellation?.Cancel();
            foreach (var source in previewSources.Values) { try { await source.DisposeAsync(); } catch { /* App is closing. */ } }
            store.Dispose();
        };
    }

    private CatalogQuery Query => new(currentSource?.Id, favoritesOnly, Search.Text,
        TypeFilter.SelectedIndex switch { 1 => "photo", 2 => "video", 3 => "raw", _ => "all" },
        SortFilter.SelectedIndex switch { 1 => "name", 2 => "size", 3 => "type", _ => "date" });

    private async Task ReloadAsync()
    {
        if (!initialized) return;
        var version = ++queryVersion;
        pageCancellation?.Cancel();
        pageCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = pageCancellation.Token;
        var query = Query;
        cards = new MediaCollection(async offset =>
        {
            try
            {
                var result = await store.QueryAsync(query, offset, 200, token);
                return result.Select(item => new MediaCard(item) { FlagChanged = FlagChanged }).ToArray();
            }
            catch (OperationCanceledException) { return []; }
            catch (Exception e) { StatusLabel.Text = "Не удалось прочитать каталог: " + e.Message; return []; }
        });
        Gallery.ItemsSource = cards;
        try
        {
            var count = await store.CountAsync(query, token);
            if (version != queryVersion) return;
            CountLabel.Text = $"{count:N0} кадров · {currentSource?.Label ?? "все источники"}";
            ViewTitle.Text = favoritesOnly ? "Избранное" : currentSource?.Label ?? "Все кадры";
            EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Gallery.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            await UpdateSelectionAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadSourcesAsync()
    {
        var sources = await store.SourcesAsync(lifetime.Token);
        SourceButtons.Children.Clear();
        foreach (var source in sources)
        {
            var button = new Button { Style = (Style)Application.Current.Resources["NavButton"],
                Content = (source.Kind == "local" ? "▧  " : "▯  ") + source.Label, Tag = source,
                MaxWidth = 190 };
            button.Click += async (_, _) => { if (busy) return; currentSource = source; favoritesOnly = false; await ReloadAsync(); };
            SourceButtons.Children.Add(button);
        }
    }

    private void FlagChanged(MediaCard card, string flag, bool value)
    {
        var previous = pendingFlags;
        pendingFlags = PersistFlagAsync(previous, card, flag, value);
    }

    private async Task PersistFlagAsync(Task previous, MediaCard card, string flag, bool value)
    {
        try
        {
            await previous;
            await store.SetFlagAsync(card.Item.Id, flag, value, lifetime.Token);
            await UpdateSelectionAsync();
            if (flag == "favorite" && favoritesOnly && !value) await ReloadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { StatusLabel.Text = "Не удалось сохранить отметку: " + e.Message; }
    }

    private async Task UpdateSelectionAsync()
    {
        var summary = await store.SelectionAsync(currentSource?.Id, lifetime.Token);
        SelectionLabel.Text = summary.Count == 0 ? "Выберите то, что хочется сохранить" : $"Выбрано {summary.Count:N0} · {MediaCard.FormatSize(summary.Bytes)}";
        SelectionDetails.Text = summary.Count == 0 ? "Галочка — импортировать. Звезда — добавить в избранное." :
            "Весь источник, включая скрытое фильтром" + (summary.UnknownSizes > 0 ? $" · размер {summary.UnknownSizes} файлов неизвестен" : " · оригиналы остаются на месте");
        ImportButton.IsEnabled = summary.Count > 0 && !busy;
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await GuardAsync(async () => { if (await PickFolderAsync() is { } folder) await IndexAsync(new LocalFolderSource(folder), "local", folder, Path.GetFileName(folder)); });
    }

    private async Task IndexAsync(IMediaSource source, string kind, string location, string label)
    {
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        busyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        busy = true; Progress.Visibility = Visibility.Visible; ImportButton.IsEnabled = false; StopButton.Visibility = Visibility.Visible;
        var count = 0; var warnings = 0;
        source.Warning += _ => Interlocked.Increment(ref warnings);
        try
        {
            await using (source)
            {
                currentSource = new(source.Id, label, kind, location); favoritesOnly = false;
                var batch = new List<MediaDescriptor>(32);
                await foreach (var item in source.EnumerateAsync(operation.Token))
                {
                    batch.Add(item); count++;
                    if (batch.Count < 32) continue;
                    await store.UpsertAsync(currentSource, batch, operation.Token); batch.Clear();
                    if (count == 32) await ReloadAsync();
                    StatusLabel.Text = $"Сканирование · найдено {count:N0} · оригиналы не копируются";
                }
                await store.UpsertAsync(currentSource, batch, operation.Token);
            }
            StatusLabel.Text = $"Найдено {count:N0} кадров." + (warnings > 0 ? $" Есть недоступные объекты: {warnings}. Список может быть неполным." : " Выберите любимое — импортируйте только выбранное.");
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Сканирование остановлено. Уже найденные кадры сохранены в каталоге."; }
        finally
        {
            busy = false; Progress.Visibility = Visibility.Collapsed; StopButton.Visibility = Visibility.Collapsed;
            await ReloadAsync(); await LoadSourcesAsync(); operation.Dispose(); operation = null;
            busyCompletion.TrySetResult(true);
        }
    }

    private async void Devices_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await GuardAsync(async () =>
        {
            var devices = await WpdDiscovery.DiscoverAsync(lifetime.Token);
            var list = new StackPanel { Spacing = 10 };
            list.Children.Add(new TextBlock { Text = "Подключите телефон по USB и разблокируйте его. На iPhone подтвердите доверие; на Android включите передачу файлов.", TextWrapping = TextWrapping.Wrap, MaxWidth = 430 });
            var choices = new ComboBox { PlaceholderText = "Выберите устройство", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var device in devices) choices.Items.Add(device.FriendlyName.Length == 0 ? "USB-устройство" : device.FriendlyName);
            if (devices.Count > 0) choices.SelectedIndex = 0;
            list.Children.Add(devices.Count == 0 ? new TextBlock { Text = "Устройства пока не найдены. Проверьте подключение и попробуйте ещё раз.", TextWrapping = TextWrapping.Wrap, MaxWidth = 430 } : choices);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Сначала подключение, потом отбор", Content = list,
                PrimaryButtonText = "Открыть устройство", CloseButtonText = "Закрыть", IsPrimaryButtonEnabled = devices.Count > 0, DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedIndex >= 0)
            {
                var device = devices[choices.SelectedIndex];
                await IndexAsync(new WpdMediaSource(device.PnpId, device.FriendlyName), "wpd", device.PnpId,
                    string.IsNullOrWhiteSpace(device.FriendlyName) ? "USB-устройство" : device.FriendlyName);
            }
        });
    }

    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await GuardAsync(async () =>
        {
            var demo = Path.Combine(AppContext.BaseDirectory, "Assets", "Demo");
            if (!Directory.Exists(demo)) throw new DirectoryNotFoundException("Демонстрационные иллюстрации не найдены.");
            await IndexAsync(new LocalFolderSource(demo), "local", demo, "Демонстрация");
            StatusLabel.Text = "Демонстрационные иллюстрации PhotoFlow · это не личные фотографии.";
        });
    }

    private async void Library_Click(object sender, RoutedEventArgs e) { if (busy) return; currentSource = null; favoritesOnly = false; await GuardAsync(ReloadAsync); }
    private async void Favorites_Click(object sender, RoutedEventArgs e) { if (busy) return; currentSource = null; favoritesOnly = true; await GuardAsync(ReloadAsync); }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await GuardAsync(async () =>
        {
            if (currentSource is not { } source) { await ReloadAsync(); return; }
            await IndexAsync(CreateSource(source.Kind, source.Location), source.Kind, source.Location, source.Label);
        });
    }

    private async void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!initialized) return;
        searchCancellation?.Cancel(); searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try { await Task.Delay(250, searchCancellation.Token); await ReloadAsync(); } catch (OperationCanceledException) { }
    }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (initialized) await GuardAsync(ReloadAsync); }

    private void Gallery_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MediaCard card || cards is null) return;
        var index = cards.IndexOf(card);
        if (KeyDown(VirtualKey.Shift))
        { for (var i = Math.Min(anchor, index); i <= Math.Max(anchor, index) && i < cards.Count; i++) cards[i].Selected = true; }
        else { card.Selected = !card.Selected; anchor = index; }
    }

    private async void SelectAll_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => SelectMatchesAsync(true));
    private async void Clear_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => SelectMatchesAsync(false));
    private async Task SelectMatchesAsync(bool selected)
    {
        await pendingFlags;
        await store.SetAllSelectedAsync(selected ? Query : new(currentSource?.Id), selected, lifetime.Token);
        await ReloadAsync();
        StatusLabel.Text = selected ? "Выбраны все совпадения с фильтром, включая ещё не показанные карточки." : "Выбор в источнике снят.";
    }

    private static bool KeyDown(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (KeyDown(VirtualKey.Control) && e.Key == VirtualKey.A)
        { e.Handled = true; await GuardAsync(() => SelectMatchesAsync(!KeyDown(VirtualKey.Shift))); }
        if (e.Key == VirtualKey.F && cards is { Count: > 0 } && anchor < cards.Count)
        { cards[anchor].Favorite = !cards[anchor].Favorite; e.Handled = true; }
    }

    private async void Gallery_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not GridViewItem container) return;
        if (previewTokens.TryGetValue(container, out var previous)) { previous.Cancel(); previous.Dispose(); previewTokens.Remove(container); }
        if (args.InRecycleQueue || args.Item is not MediaCard card || card.Preview is not null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        previewTokens.Add(container, cancellation);
        try
        {
            await previewSlots.WaitAsync(cancellation.Token);
            try
            {
                var bitmap = await PreviewAsync(card, false, cancellation.Token);
                if (!cancellation.IsCancellationRequested && bitmap is not null)
                {
                    card.Preview = bitmap;
                    if (previewNodes.Remove(card, out var existing)) previewLru.Remove(existing);
                    previewNodes[card] = previewLru.AddLast(card);
                    while (previewLru.Count > 64)
                    {
                        var oldest = previewLru.First!.Value; oldest.Preview = null;
                        previewLru.RemoveFirst(); previewNodes.Remove(oldest);
                    }
                }
            }
            finally { previewSlots.Release(); }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Offline/unsupported resources use the visible placeholder. */ }
    }

    private async Task<ImageSource?> PreviewAsync(MediaCard card, bool full, CancellationToken ct)
    {
        var item = card.Item;
        var image = new BitmapImage { DecodePixelWidth = full ? 1800 : 480, DecodePixelHeight = full ? 1200 : 320 };
        if (item.SourceKind == "local" || item.ImportedPath is { } imported && File.Exists(imported))
        {
            var path = item.ImportedPath is { } copied && File.Exists(copied) ? copied : Path.Combine(item.Location, item.Media.SourceItemKey);
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
            if (full)
            { using var stream = await file.OpenReadAsync().AsTask(ct); await image.SetSourceAsync(stream).AsTask(ct); }
            else
            {
                using var stream = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, 480, ThumbnailOptions.UseCurrentScale).AsTask(ct);
                if (stream is null || stream.Size == 0) return null;
                await image.SetSourceAsync(stream).AsTask(ct);
            }
        }
        else
        {
            if (!previewSources.TryGetValue(item.SourceId, out var source))
                previewSources[item.SourceId] = source = new WpdMediaSource(item.Location);
            var preview = await new ThumbnailService().ReadAsync(source, item.Media.SourceItemKey, ct);
            if (preview.Bytes is null) return null;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(preview.Bytes.AsBuffer()).AsTask(ct); stream.Seek(0);
            await image.SetSourceAsync(stream).AsTask(ct);
        }
        return image;
    }

    private async void Card_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not MediaCard card) return;
        await GuardAsync(async () =>
        {
            var image = new Image { Source = card.Preview, Stretch = Stretch.Uniform, MaxHeight = 580, MinWidth = 500 };
            var text = new TextBlock { Text = $"{card.Details}\nДата: {card.Item.Media.CaptureSource} · уверенность: {card.Item.Media.CaptureConfidence}" +
                (card.Item.SourceKind == "wpd" ? "\nДля USB используется только отдельная миниатюра. Оригинал не скачивается для просмотра." : ""), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var panel = new StackPanel { Spacing = 14 }; panel.Children.Add(image); panel.Children.Add(text);
            try { image.Source = await PreviewAsync(card, true, lifetime.Token) ?? card.Preview; } catch { text.Text += "\nПолный просмотр недоступен: файл offline или не поддерживается кодеком Windows."; }
            if (image.Source is null) text.Text += "\nПревью недоступно.";
            await new ContentDialog { XamlRoot = Root.XamlRoot, Title = card.Name, Content = panel, CloseButtonText = "Закрыть" }.ShowAsync();
        });
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await GuardAsync(async () =>
        {
            await pendingFlags;
            var selected = await store.SelectedAsync(currentSource?.Id, lifetime.Token);
            if (selected.Count == 0 || await PickFolderAsync() is not { } destination) return;
            var knownBytes = selected.Sum(x => x.Media.SizeBytes ?? 0);
            var freeBytes = new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
            if (knownBytes > freeBytes) throw new IOException("Недостаточно свободного места для выбранного набора.");
            var volumeLabel = (selected.Any(x => x.Media.SizeBytes is null) ? "не менее " : "") + MediaCard.FormatSize(knownBytes);
            var preview = new TextBlock { Text = $"Файлов: {selected.Count:N0}\nОбъём: {volumeLabel}\nСвободно: {MediaCard.FormatSize(freeBytes)}\nПапка: {destination}\n\nСуществующие имена получат числовой суффикс. Оригиналы остаются на месте.\n\n" +
                string.Join("\n", selected.Take(8).Select(x => x.Media.FileName)) + (selected.Count > 8 ? $"\n…ещё {selected.Count - 8}" : ""), TextWrapping = TextWrapping.Wrap, MaxWidth = 480 };
            if (await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Импортировать выбранное?", Content = preview, PrimaryButtonText = "Импортировать", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary }.ShowAsync() != ContentDialogResult.Primary) return;
            operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); busy = true; ImportButton.IsEnabled = false;
            busyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Progress.Visibility = Visibility.Visible; StopButton.Visibility = Visibility.Visible;
            var done = 0;
            try
            {
                foreach (var item in selected)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    await using var source = CreateSource(item.SourceKind, item.Location);
                    var target = UniqueDestination(destination, item.Media.FileName);
                    StatusLabel.Text = $"Импорт {done + 1} из {selected.Count} · {item.Media.FileName}";
                    var result = await new SelectedFileTransfer().CopyAsync(source, item.Media.SourceItemKey, target, item.Media.SizeBytes, null, operation.Token);
                    // Once a file is committed, persist its completion even if cancellation arrived just after the move.
                    await store.MarkImportedAsync(item.Id, result, lifetime.Token); done++;
                }
                StatusLabel.Text = $"Готово: импортировано {done} файлов. Оригиналы сохранены.";
            }
            catch (OperationCanceledException) { StatusLabel.Text = $"Остановлено. Импортировано {done}; оставшиеся файлы по-прежнему выбраны."; }
            finally
            {
                busy = false; Progress.Visibility = Visibility.Collapsed; StopButton.Visibility = Visibility.Collapsed;
                operation.Dispose(); operation = null; await ReloadAsync();
                busyCompletion.TrySetResult(true);
            }
        });
    }

    private static IMediaSource CreateSource(string kind, string location) => kind == "local" ? new LocalFolderSource(location) : new WpdMediaSource(location);
    private static string UniqueDestination(string folder, string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(Path.GetFileName(name).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (name.Length == 0) name = "media";
        var stem = Path.GetFileNameWithoutExtension(name); var extension = Path.GetExtension(name);
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem.ToUpperInvariant())) stem = "_" + stem;
        var path = Path.Combine(folder, stem + extension);
        for (var index = 1; File.Exists(path) || Directory.Exists(path); index++) path = Path.Combine(folder, $"{stem} ({index}){extension}");
        return path;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
    private async void About_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "PhotoFlow 0.2", CloseButtonText = "Понятно",
            Content = "Сначала выберите любимое. Потом импортируйте.\n\nВсе отметки и каталог хранятся локально. Звезда не меняет избранное телефона. PhotoFlow не загружает ваши фото и каталог в облако.\n\nCtrl+A — все совпадения\nCtrl+Shift+A — снять выбор\nShift+клик — диапазон показанных карточек\nF — избранное последнего выбранного кадра\nДвойной клик — просмотр\n\nWinUI 3 · CommunityToolkit · SQLite\nЛицензии и статус USB-проверок — в документации проекта." }.ShowAsync());
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            StatusLabel.Text = "Операция не завершена. " + e.Message;
            if (!lifetime.IsCancellationRequested) await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Не удалось завершить", Content = e.Message, CloseButtonText = "Понятно" }.ShowAsync();
        }
    }
}
