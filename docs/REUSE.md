# Что нашли на GitHub и что переиспользовали

Проверка источников: 25 октября 2026. Заимствования отделены от референсов; похожий README не считается доказательством работы USB.

| Проект | Что полезно | Решение |
|---|---|---|
| [Microsoft WinUI Gallery](https://github.com/microsoft/WinUI-Gallery) | GridView, ItemsWrapGrid, шаблоны изображений, Fluent controls | Адаптирован паттерн виртуализированной сетки и Image/DataTemplate. MIT, текст сохранён в third_party/licenses |
| [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) | ObservableObject, уведомления об изменениях UI | Подключён CommunityToolkit.Mvvm 8.4.0 как зависимость. MIT |
| [dotnet/efcore](https://github.com/dotnet/efcore) | Microsoft.Data.Sqlite | Подключена версия 10.0.13, собственные схема и SQL-запросы. MIT; SQLitePCLRaw и SQLite сохраняют собственные уведомления |
| [OpenPhotoCull](https://github.com/zwoodard/OpenPhotoCull) | Отбор с виртуальной сеткой, фильтрами и массовыми действиями | Изучен PhotoGrid.tsx и рабочий процесс. Исходники React/Rust не переносились; интерфейс PhotoFlow написан для WinUI |
| [bassman2/MediaDevices](https://github.com/bassman2/MediaDevices) | Готовая обёртка WPD/MTP/PTP, MIT | Кандидат для дополнительного адаптера и сравнения. Не подключена: текущий транспорт уже проверяется через IMediaSource |
| [PicView](https://github.com/Ruben2776/PicView) | Просмотр, кодеки, навигация по большим наборам | Референс возможностей. GPL-3.0, код и ресурсы не копировались |
| [ios-photo-importer-windows](https://github.com/MrLionware/ios-photo-importer-windows) | WinUI, SQLite, организация импорта | Только архитектурное сравнение. README указывает UnsupportedWpdTransport по умолчанию; готовый USB-транспорт из него не заимствован |
| [metadata-extractor-dotnet](https://github.com/drewnoakes/metadata-extractor-dotnet) | EXIF/QuickTime | Кандидат для следующего этапа метаданных, пока не подключён |

Адаптированный пример WinUI Gallery: [`GridViewPage.xaml`](https://github.com/microsoft/WinUI-Gallery/blob/14061eeac21c38ae99f302edf3d9136d4f652c4c/WinUIGallery/Samples/GridView/GridViewPage.xaml). Проверенный срез OpenPhotoCull: `8824c83b8b2dce461e1d33f0ac77e0d7168d5053`.

Галерея, SQL-каталог, правила отбора, WPD-планирование и перенос PhotoFlow реализованы в этом проекте. Вместо копирования целых приложений подключены необходимые библиотеки и небольшой UI-паттерн с атрибуцией. Фото и иконки чужих приложений не переносились: используются системные Fluent-глифы и собственные процедурные иллюстрации.
