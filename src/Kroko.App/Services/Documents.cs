using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Xml;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kroko.App.Services;

/// <summary>Saca el texto de un documento: .txt/.md/.csv/.json, .pdf (PdfPig) y .docx (lee el XML de Word).</summary>
public static class DocumentText
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".csv", ".json", ".pdf", ".docx" };

    public static string Extract(string path, int maxChars = 400_000)
    {
        var text = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => Pdf(path, maxChars),
            ".docx" => Docx(path),
            _ => File.ReadAllText(path),
        };
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    private static string Pdf(string path, int maxChars)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(page.Text);
            if (sb.Length > maxChars) break;
        }
        return sb.ToString();
    }

    private static string Docx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("No es un documento de Word válido.");
        using var stream = entry.Open();
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var sb = new StringBuilder();
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t") sb.Append(xml.ReadElementContentAsString());
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "p") sb.AppendLine();
            else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "tab") sb.Append('\t');
        }
        return sb.ToString();
    }

    /// <summary>Trozos de ~1500 caracteres con un poco de solape, cortando en saltos de línea cuando se puede.</summary>
    public static List<string> Chunk(string text, int size = 1500, int overlap = 200, int max = 60)
    {
        text = string.Join('\n', text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length && chunks.Count < max)
        {
            var end = Math.Min(text.Length, start + size);
            if (end < text.Length)
            {
                var cut = text.LastIndexOf('\n', end, end - start);
                if (cut > start + size / 2) end = cut;
            }
            var piece = text[start..end].Trim();
            if (piece.Length > 30) chunks.Add(piece);
            if (end >= text.Length) break;
            start = Math.Max(end - overlap, start + 1);
        }
        return chunks;
    }
}

public sealed record DocumentHit(string Path, string Name, string Snippet, double Score);

/// <summary>
/// Índice de documentos en %LOCALAPPDATA%\Kroko\documentos.db: trozos de texto con su vector.
/// La búsqueda recorre los vectores en disco (no los guarda en memoria) para no pasar del consumo objetivo.
/// </summary>
public sealed class DocumentIndex
{
    private readonly string _connection;

    public DocumentIndex() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "documentos.db")) { }

    public DocumentIndex(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS files (path TEXT PRIMARY KEY, modified INTEGER NOT NULL, size INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS chunks (
                id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL, ord INTEGER NOT NULL, text TEXT NOT NULL, vector BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_chunks_path ON chunks(path);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connection);
        c.Open();
        return c;
    }

    public Dictionary<string, (long Modified, long Size)> Files()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, modified, size FROM files";
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) d[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
        return d;
    }

    public (int Files, int Chunks) Count()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM files), (SELECT COUNT(*) FROM chunks)";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt32(1));
    }

    /// <summary>Reemplaza los trozos de un archivo (vacío = archivo sin texto útil, queda registrado igual).</summary>
    public void Save(string path, long modified, long size, IReadOnlyList<string> chunks, IReadOnlyList<float[]> vectors)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Delete(c, path);
        using (var f = c.CreateCommand())
        {
            f.CommandText = "INSERT INTO files(path, modified, size) VALUES($p, $m, $s)";
            f.Parameters.AddWithValue("$p", path);
            f.Parameters.AddWithValue("$m", modified);
            f.Parameters.AddWithValue("$s", size);
            f.ExecuteNonQuery();
        }
        for (var i = 0; i < chunks.Count; i++)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO chunks(path, ord, text, vector) VALUES($p, $o, $t, $v)";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$o", i);
            cmd.Parameters.AddWithValue("$t", chunks[i]);
            var bytes = new byte[vectors[i].Length * sizeof(float)];
            Buffer.BlockCopy(vectors[i], 0, bytes, 0, bytes.Length);
            cmd.Parameters.AddWithValue("$v", bytes);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Remove(string path)
    {
        using var c = Open();
        Delete(c, path);
    }

    private static void Delete(SqliteConnection c, string path)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM chunks WHERE path = $p; DELETE FROM files WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Los archivos más parecidos a la consulta (su mejor trozo), con un extra si el nombre coincide.</summary>
    public List<DocumentHit> Search(float[] query, string words, int max = 5)
    {
        var best = new Dictionary<string, (double Score, string Text)>(StringComparer.OrdinalIgnoreCase);
        var terms = words.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 3).ToArray();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, text, vector FROM chunks";
        using var r = cmd.ExecuteReader();
        var v = new float[query.Length];
        while (r.Read())
        {
            var blob = (byte[])r[2];
            if (blob.Length != query.Length * sizeof(float)) continue; // vectores de otro modelo/tamaño
            Buffer.BlockCopy(blob, 0, v, 0, blob.Length);
            double dot = 0;
            for (var i = 0; i < v.Length; i++) dot += v[i] * query[i];
            var path = r.GetString(0);
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var score = dot + (terms.Any(name.Contains) ? 0.05 : 0);
            if (!best.TryGetValue(path, out var b) || score > b.Score) best[path] = (score, r.GetString(1));
        }
        return best.OrderByDescending(kv => kv.Value.Score).Take(max)
            .Select(kv => new DocumentHit(kv.Key, Path.GetFileName(kv.Key),
                kv.Value.Text.Length > 500 ? kv.Value.Text[..500] + "…" : kv.Value.Text, Math.Round(kv.Value.Score, 3)))
            .ToList();
    }
}

/// <summary>
/// Indexa en segundo plano las carpetas elegidas: solo archivos nuevos o cambiados, borra los que ya no existen.
/// Empieza un rato después de arrancar y repite cada RescanMinutes. Nunca bloquea la App.
/// </summary>
public sealed class DocumentIndexer(DocumentIndex index, BrainApiClient api, BrainConnection brain, IOptions<KrokoOptions> options,
    ILogger<DocumentIndexer> log) : BackgroundService
{
    private static readonly string[] SkipDirs = ["node_modules", ".git", "bin", "obj", "$recycle.bin", "appdata", ".vs", ".idea"];

    public string Status { get; private set; } = "Aún no se indexó.";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opt = options.Value.Documents;
        if (!opt.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), ct);
            while (!ct.IsCancellationRequested)
            {
                if (brain.IsConnected) await PassAsync(opt, ct);
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, opt.RescanMinutes)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task PassAsync(DocumentSettings opt, CancellationToken ct)
    {
        var known = index.Files();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var todo = new List<FileInfo>();
        foreach (var root in opt.EffectiveFolders.Where(Directory.Exists))
        {
            foreach (var file in Enumerate(root))
            {
                seen.Add(file.FullName);
                if (file.Length > opt.MaxFileMb * 1024L * 1024) continue;
                if (known.TryGetValue(file.FullName, out var k) && k.Modified == file.LastWriteTimeUtc.Ticks && k.Size == file.Length) continue;
                todo.Add(file);
            }
        }
        foreach (var gone in known.Keys.Where(p => !seen.Contains(p))) index.Remove(gone);

        var done = 0;
        foreach (var file in todo.OrderByDescending(f => f.LastWriteTimeUtc).Take(opt.MaxFilesPerPass))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var chunks = DocumentText.Chunk(DocumentText.Extract(file.FullName));
                var vectors = new List<float[]>();
                for (var i = 0; i < chunks.Count; i += 50)
                {
                    var batch = chunks.Skip(i).Take(50).ToList();
                    vectors.AddRange(await api.EmbedAsync(batch, "documento", batch.Select(_ => file.Name).ToList(), ct));
                }
                index.Save(file.FullName, file.LastWriteTimeUtc.Ticks, file.Length, chunks, vectors);
                done++;
                await Task.Delay(200, ct); // con calma: no compite con lo que estés haciendo
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                           or HttpRequestException or XmlException or System.Text.Json.JsonException
                                           or UglyToad.PdfPig.Core.PdfDocumentFormatException)
            {
                log.LogDebug("No se pudo indexar {File}: {Message}", file.Name, ex.Message);
                // Se marca igual (sin trozos) para no reintentarlo en cada pasada mientras no cambie
                if (ex is not HttpRequestException) index.Save(file.FullName, file.LastWriteTimeUtc.Ticks, file.Length, [], []);
            }
        }
        var (files, chunksTotal) = index.Count();
        Status = $"{files} documentos indexados ({chunksTotal} fragmentos).";
        if (done > 0) log.LogInformation("Documentos: {Done} nuevos o cambiados; {Status}", done, Status);
    }

    private static IEnumerable<FileInfo> Enumerate(string root)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs = [], files = [];
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
            foreach (var s in subdirs)
            {
                var name = Path.GetFileName(s).ToLowerInvariant();
                if (!name.StartsWith('.') && !SkipDirs.Contains(name)) stack.Push(s);
            }
            foreach (var f in files)
                if (DocumentText.Extensions.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).StartsWith("~$")) yield return new FileInfo(f);
        }
    }
}
