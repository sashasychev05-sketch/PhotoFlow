using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoFlow.Core;

namespace PhotoFlow.Infrastructure.Catalog;

public sealed record CatalogSource(string Id, string Label, string Kind, string Location);
public sealed record CatalogQuery(string? SourceId = null, bool FavoritesOnly = false,
    string Search = "", string Type = "all", string Sort = "date", bool OnlyNew = false);
public sealed record CatalogItem(long Id, string SourceId, string SourceKind, string Location,
    MediaDescriptor Media, bool Favorite, bool Selected, string? ImportedPath);
public sealed record SelectionSummary(long Count, long Bytes, long UnknownSizes);

/// <summary>SQL queries cover the full catalog. UI pages are only a view of that catalog.</summary>
public sealed class CatalogStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    public CatalogStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = full, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS sources(id TEXT PRIMARY KEY,label TEXT NOT NULL,kind TEXT NOT NULL,location TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS media(
              id INTEGER PRIMARY KEY, source_id TEXT NOT NULL REFERENCES sources(id), identity TEXT NOT NULL,
              file_name TEXT NOT NULL, search_name TEXT NOT NULL, extension TEXT NOT NULL, capture_local TEXT, size_bytes INTEGER,
              kind TEXT NOT NULL, is_raw INTEGER NOT NULL, metadata TEXT NOT NULL,
              favorite INTEGER NOT NULL DEFAULT 0, selected INTEGER NOT NULL DEFAULT 0,
              imported_path TEXT, content_sha256 TEXT, imported_utc TEXT,
              UNIQUE(source_id,identity));
            CREATE INDEX IF NOT EXISTS idx_catalog_date ON media(source_id,capture_local DESC,id DESC);
            CREATE INDEX IF NOT EXISTS idx_catalog_name ON media(source_id,file_name,id);
            CREATE INDEX IF NOT EXISTS idx_catalog_flags ON media(source_id,favorite,selected,id);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    private Task<T> Run<T>(Func<T> action, CancellationToken ct) => Task.Run(() =>
    { ct.ThrowIfCancellationRequested(); lock (gate) { ct.ThrowIfCancellationRequested(); return action(); } }, ct);

    public Task UpsertAsync(CatalogSource source, IReadOnlyList<MediaDescriptor> items, CancellationToken ct) => Run(() =>
    {
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO sources VALUES($id,$label,$kind,$location) ON CONFLICT(id) DO UPDATE SET label=$label,location=$location";
            command.Parameters.AddWithValue("$id", source.Id); command.Parameters.AddWithValue("$label", source.Label);
            command.Parameters.AddWithValue("$kind", source.Kind); command.Parameters.AddWithValue("$location", source.Location);
            command.ExecuteNonQuery();
        }
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO media(source_id,identity,file_name,search_name,extension,capture_local,size_bytes,kind,is_raw,metadata)
                VALUES($source,$identity,$name,$searchName,$extension,$capture,$size,$kind,$raw,$metadata)
                ON CONFLICT(source_id,identity) DO UPDATE SET file_name=$name,extension=$extension,capture_local=$capture,
                search_name=$searchName,size_bytes=$size,kind=$kind,is_raw=$raw,
                favorite=CASE WHEN json_extract(media.metadata,'$.StableFingerprint')=json_extract($metadata,'$.StableFingerprint') OR $identity LIKE 'persistent:%' THEN favorite ELSE 0 END,
                selected=CASE WHEN json_extract(media.metadata,'$.StableFingerprint')=json_extract($metadata,'$.StableFingerprint') OR $identity LIKE 'persistent:%' THEN selected ELSE 0 END,
                imported_path=CASE WHEN json_extract(media.metadata,'$.StableFingerprint')=json_extract($metadata,'$.StableFingerprint') OR $identity LIKE 'persistent:%' THEN imported_path ELSE NULL END,
                content_sha256=CASE WHEN json_extract(media.metadata,'$.StableFingerprint')=json_extract($metadata,'$.StableFingerprint') OR $identity LIKE 'persistent:%' THEN content_sha256 ELSE NULL END,
                imported_utc=CASE WHEN json_extract(media.metadata,'$.StableFingerprint')=json_extract($metadata,'$.StableFingerprint') OR $identity LIKE 'persistent:%' THEN imported_utc ELSE NULL END,
                metadata=$metadata;
                """;
            command.Parameters.AddWithValue("$source", source.Id);
            command.Parameters.AddWithValue("$identity", item.PersistentId is { Length: > 0 } persistent ? "persistent:" + persistent : "path:" + item.RelativePath);
            command.Parameters.AddWithValue("$name", item.FileName); command.Parameters.AddWithValue("$extension", item.Extension);
            command.Parameters.AddWithValue("$searchName", item.FileName.ToUpperInvariant());
            command.Parameters.AddWithValue("$capture", (object?)item.CaptureLocal?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            command.Parameters.AddWithValue("$size", (object?)item.SizeBytes ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", item.Kind.ToString()); command.Parameters.AddWithValue("$raw", item.IsRaw ? 1 : 0);
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(item));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }, ct);

    private static string Where(SqliteCommand command, CatalogQuery query)
    {
        command.Parameters.AddWithValue("$source", (object?)query.SourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$favorite", query.FavoritesOnly ? 1 : 0);
        command.Parameters.AddWithValue("$new", query.OnlyNew ? 1 : 0);
        command.Parameters.AddWithValue("$type", query.Type);
        command.Parameters.AddWithValue("$search", "%" + query.Search.ToUpperInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        return """
            WHERE ($source IS NULL OR m.source_id=$source) AND ($favorite=0 OR m.favorite=1)
            AND ($new=0 OR m.imported_path IS NULL) AND m.search_name LIKE $search ESCAPE '\'
            AND ($type='all' OR ($type='raw' AND m.is_raw=1) OR ($type='photo' AND m.kind='Image') OR ($type='video' AND m.kind='Video'))
            """;
    }

    public Task<IReadOnlyList<CatalogItem>> QueryAsync(CatalogQuery query, int offset, int limit, CancellationToken ct) => Run<IReadOnlyList<CatalogItem>>(() =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var command = connection.CreateCommand();
        var where = Where(command, query);
        var sort = query.Sort switch { "name" => "m.file_name COLLATE NOCASE", "size" => "m.size_bytes DESC", "type" => "m.extension", _ => "m.capture_local DESC" };
        command.CommandText = "SELECT m.id,m.source_id,s.kind,s.location,m.metadata,m.favorite,m.selected,m.imported_path FROM media m JOIN sources s ON s.id=m.source_id "
            + where + $" ORDER BY {sort},m.id DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", limit); command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var result = new List<CatalogItem>();
        while (reader.Read()) { ct.ThrowIfCancellationRequested(); result.Add(Read(reader)); }
        return result;
    }, ct);

    private static CatalogItem Read(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        JsonSerializer.Deserialize<MediaDescriptor>(reader.GetString(4))!, reader.GetBoolean(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7));

    public Task<long> CountAsync(CatalogQuery query, CancellationToken ct) => Run(() =>
    {
        using var command = connection.CreateCommand();
        var where = Where(command, query); command.CommandText = "SELECT COUNT(*) FROM media m " + where;
        return (long)command.ExecuteScalar()!;
    }, ct);

    public Task SetAllSelectedAsync(CatalogQuery query, bool selected, CancellationToken ct) => Run(() =>
    {
        using var command = connection.CreateCommand();
        var where = Where(command, query); command.Parameters.AddWithValue("$selected", selected ? 1 : 0);
        command.CommandText = "UPDATE media AS m SET selected=$selected " + where;
        return command.ExecuteNonQuery();
    }, ct);

    public Task SetFlagAsync(long id, string flag, bool value, CancellationToken ct) => Run(() =>
    {
        if (flag is not ("favorite" or "selected")) throw new ArgumentException("Unknown flag.", nameof(flag));
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE media SET {flag}=$value WHERE id=$id";
        command.Parameters.AddWithValue("$value", value ? 1 : 0); command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery();
    }, ct);

    public Task<SelectionSummary> SelectionAsync(string? sourceId, CancellationToken ct) => Run(() =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(size_bytes),0),COALESCE(SUM(size_bytes IS NULL),0) FROM media WHERE selected=1 AND ($source IS NULL OR source_id=$source)";
        command.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
        using var reader = command.ExecuteReader(); reader.Read();
        return new SelectionSummary(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }, ct);

    public Task<IReadOnlyList<CatalogItem>> SelectedAsync(string? sourceId, CancellationToken ct) => Run<IReadOnlyList<CatalogItem>>(() =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT m.id,m.source_id,s.kind,s.location,m.metadata,m.favorite,m.selected,m.imported_path FROM media m JOIN sources s ON s.id=m.source_id WHERE m.selected=1 AND ($source IS NULL OR m.source_id=$source) ORDER BY m.id";
        command.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
        using var reader = command.ExecuteReader(); var result = new List<CatalogItem>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }, ct);

    public Task MarkImportedAsync(long id, TransferResult result, CancellationToken ct) => Run(() =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE media SET imported_path=$path,content_sha256=$sha,imported_utc=$date,selected=0 WHERE id=$id";
        command.Parameters.AddWithValue("$path", result.Path); command.Parameters.AddWithValue("$sha", result.Sha256);
        command.Parameters.AddWithValue("$date", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery();
    }, ct);

    public Task<IReadOnlyList<CatalogSource>> SourcesAsync(CancellationToken ct) => Run<IReadOnlyList<CatalogSource>>(() =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT id,label,kind,location FROM sources ORDER BY label";
        using var reader = command.ExecuteReader(); var result = new List<CatalogSource>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }, ct);

    public void Dispose() { lock (gate) connection.Dispose(); }
}
