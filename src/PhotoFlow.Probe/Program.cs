using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoFlow.Core;
using PhotoFlow.Infrastructure;
using PhotoFlow.Infrastructure.Wpd;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
return await Probe.RunAsync(args, shutdown.Token);

internal static class Probe
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Help(); return 0; }
            var command = args[0];
            var allowed = command switch
            {
                "devices" => Array.Empty<string>(),
                "scan" => ["folder", "device-index", "device-id", "limit", "output"],
                "thumbnail" or "copy" => ["folder", "device-index", "device-id", "key", "output"],
                _ => throw new ArgumentException("Неизвестная команда. Используйте help.")
            };
            var options = Parse(args.Skip(1).ToArray(), allowed);
            if (command == "devices") return await DevicesAsync(ct);
            await using var source = await CreateSourceAsync(options, ct);
            var warningCount = 0;
            source.Warning += warning =>
            {
                Interlocked.Increment(ref warningCount);
                Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "warning", warning }, Json));
            };
            switch (command)
            {
                case "scan":
                    var limit = options.TryGetValue("limit", out var number) ? int.Parse(number, CultureInfo.InvariantCulture) : int.MaxValue;
                    if (limit <= 0) throw new ArgumentException("Лимит должен быть больше нуля.");
                    TextWriter writer = Console.Out;
                    StreamWriter? file = null;
                    try
                    {
                        if (options.TryGetValue("output", out var output))
                        {
                            file = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                            writer = file;
                        }
                        var timer = Stopwatch.StartNew();
                        var count = 0;
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new
                            { type = "source", source.Id, source.DisplayName, capabilities = await source.GetCapabilitiesAsync(ct) }, Json));
                        await writer.FlushAsync(ct);
                        await foreach (var item in source.EnumerateAsync(ct))
                        {
                            await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "media", item }, Json));
                            if (++count == 1) Console.Error.WriteLine($"Первый объект: {timer.Elapsed.TotalMilliseconds:F0} мс.");
                            if (count % 100 == 0) { Console.Error.WriteLine($"Найдено: {count}"); await writer.FlushAsync(ct); }
                            if (count >= limit) break;
                        }
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new
                            { type = "summary", found = count, elapsedMs = timer.ElapsedMilliseconds,
                              warnings = warningCount, stoppedAtLimit = count >= limit }, Json));
                        Console.Error.WriteLine($"Найдено {count}; предупреждений {warningCount}; {timer.Elapsed.TotalSeconds:F2} с.");
                    }
                    finally { if (file is not null) await file.DisposeAsync(); }
                    return warningCount > 0 ? 3 : 0;

                case "thumbnail":
                    var thumbnailPath = Required(options, "output");
                    if (File.Exists(thumbnailPath)) throw new IOException("Файл назначения уже существует.");
                    var thumbnail = await new ThumbnailService().ReadAsync(source, Required(options, "key"), ct);
                    if (thumbnail.Bytes is null)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(thumbnail, Json));
                        return 4;
                    }
                    await using (var output = new FileStream(thumbnailPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await output.WriteAsync(thumbnail.Bytes, ct);
                    Console.WriteLine(JsonSerializer.Serialize(new { thumbnail.Status, thumbnail.Note, path = Path.GetFullPath(thumbnailPath), bytes = thumbnail.Bytes.Length }, Json));
                    return 0;

                case "copy":
                    var key = Required(options, "key");
                    var destination = Required(options, "output");
                    MediaDescriptor? selected = null;
                    await foreach (var item in source.EnumerateAsync(ct))
                    {
                        if (item.SourceItemKey == key) { selected = item; break; }
                    }
                    if (selected is null) throw new FileNotFoundException("Выбранный медиаобъект не найден. Обновите сканирование.");
                    Console.Error.WriteLine($"Копирование одного выбранного объекта: {selected.FileName}; {selected.SizeBytes?.ToString() ?? "размер неизвестен"} байт.");
                    var transferred = await new SelectedFileTransfer().CopyAsync(source, key, destination, selected.SizeBytes, null, ct);
                    Console.WriteLine(JsonSerializer.Serialize(transferred, Json));
                    return 0;
            }
            return 2;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Операция отменена."); return 130; }
        catch (Exception e)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "error", message = e.Message, hresult = $"0x{e.HResult:X8}" }, Json));
            return 2;
        }
    }

    private static async Task<int> DevicesAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WPD доступен только в Windows.");
        var devices = await WpdDiscovery.DiscoverAsync(ct);
        for (var i = 0; i < devices.Count; i++)
            Console.WriteLine(JsonSerializer.Serialize(new { index = i, device = devices[i] }, Json));
        Console.Error.WriteLine(devices.Count == 0
            ? "WPD-устройства не найдены. Разблокируйте телефон, подтвердите доверие или включите передачу файлов по USB."
            : $"WPD-устройств: {devices.Count}. Индексы действуют только для текущего подключения; используйте PnP ID для точного выбора.");
        return 0;
    }

    private static async Task<IMediaSource> CreateSourceAsync(Dictionary<string, string> options, CancellationToken ct)
    {
        if (new[] { "folder", "device-index", "device-id" }.Count(options.ContainsKey) != 1)
            throw new ArgumentException("Укажите ровно один источник: --folder, --device-index или --device-id.");
        if (options.TryGetValue("folder", out var folder)) return new LocalFolderSource(folder);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WPD доступен только в Windows.");
        if (options.TryGetValue("device-id", out var id)) return new WpdMediaSource(id);
        var index = int.Parse(options["device-index"], CultureInfo.InvariantCulture);
        var devices = await WpdDiscovery.DiscoverAsync(ct);
        if (index < 0 || index >= devices.Count) throw new ArgumentException("Индекс устройства отсутствует. Выполните devices.");
        return new WpdMediaSource(devices[index].PnpId, devices[index].FriendlyName);
    }

    private static Dictionary<string, string> Parse(string[] args, string[] allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(args[i][2..]) ||
                i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Неизвестный параметр или пропущенное значение: {args[i]}");
            if (!result.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException($"Повтор параметра: {args[i]}");
        }
        return result;
    }
    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Укажите --{name}.");

    private static void Help() => Console.WriteLine("""
        PhotoFlow • P0 / 0.1.0 — диагностический прототип, без графического интерфейса

        devices
        scan --folder "D:\Photos" [--limit 100] [--output "scan.ndjson"]
        scan --device-index 0 [--limit 100] [--output "scan.ndjson"]
        scan --device-id "<PnP ID>" [--limit 100]
        thumbnail --device-index 0 --key "<sourceItemKey>" --output "thumb.bin"
        copy --folder "D:\Photos" --key "DCIM/IMG_0001.JPG" --output "D:\Import\photo.jpg"
        copy --device-id "<PnP ID>" --key "<sourceItemKey>" --output "D:\Import\photo.heic"

        Сканирование не читает оригиналы. Миниатюры — только отдельный ресурс,
        без загрузки целого оригинала. copy переносит один явно указанный объект,
        проверяет размер и SHA-256 записанного файла, не перезаписывает назначения.
        Ctrl+C отменяет операцию между вызовами устройства.
        Код выхода: 0 успех; 2 ошибка; 3 неполный скан/предупреждения; 4 нет миниатюры; 130 отмена.
        """);
}
