# Third-party notices

PhotoFlow-authored code is MIT licensed. Dependencies and redistributed runtimes retain their own licenses; PhotoFlow's MIT license does not replace those terms.

| Component | Use | License / notice |
|---|---|---|
| Microsoft WinUI Gallery | Adapted GridView / image DataTemplate pattern in MainWindow.xaml | MIT; [license](third_party/licenses/WinUI-Gallery-MIT.txt). Source revision and link in docs/REUSE.md |
| CommunityToolkit.Mvvm 8.4.0 | ObservableObject in MediaCard | MIT; [license](third_party/licenses/communitytoolkit.mvvm-License.md), [notices](third_party/licenses/communitytoolkit.mvvm-ThirdPartyNotices.txt) |
| Microsoft.Data.Sqlite 10.0.13 | Local catalog | MIT; [license](third_party/licenses/Microsoft.Data.Sqlite-MIT.txt) |
| SQLitePCLRaw 2.1.13 | SQLite native binding | Apache-2.0; [license](third_party/licenses/SQLitePCLRaw-Apache-2.0.txt). Copyright Eric Sink and contributors |
| SQLite | Embedded database engine | Public domain; [SQLite copyright statement](https://www.sqlite.org/copyright.html) |
| Microsoft Windows App SDK 1.8.260921001 and its runtime modules | WinUI desktop runtime | [Microsoft software license terms](third_party/licenses/microsoft.windowsappsdk-license.txt), [third-party notices](third_party/licenses/microsoft.windowsappsdk-NOTICE.txt). Only NuGet-binplaced distributable files are included in the application bundle |
| .NET 10 | Application runtime, installed separately | [dotnet/runtime licenses](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| xUnit v3 4.0.2 | Development tests only | [Apache-2.0](https://github.com/xunit/xunit/blob/main/LICENSE) |

The Gallery pattern was adapted to PhotoFlow's card layout and SQL loading; this is not a fork of WinUI Gallery. The OpenPhotoCull, PicView, MediaDevices and metadata-extractor repositories were reviewed as documented in REUSE; their application source/assets are not included.

Demo JPEGs, the camera mark and banner are original procedural PhotoFlow artwork, with no personal photos or EXIF. Fluent icon glyphs and the Segoe UI font are provided by the user's Windows installation and are not copied into this repository.
