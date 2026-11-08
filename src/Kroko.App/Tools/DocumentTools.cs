using System.IO;
using System.Net.Http;
using Kroko.App.Services;

namespace Kroko.App.Tools;

public sealed class BuscarEnDocumentosTool(DocumentIndex index, BrainApiClient api, DocumentIndexer indexer) : ILocalTool
{
    public string Name => "buscar_en_documentos";

    public async Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var question = args.String("pregunta").Trim();
        if (question.Length == 0) throw new ToolException("¿Qué busco?");
        var (files, _) = index.Count();
        if (files == 0) return ToolOutcome.Fail("Todavía no indexé tus documentos (empieza unos minutos después de abrir Kroko).");
        float[] query;
        try
        {
            query = (await api.EmbedAsync([question], "consulta", null, ct))[0];
        }
        catch (HttpRequestException ex)
        {
            return ToolOutcome.Fail("No pude preparar la búsqueda: " + ex.Message);
        }
        var max = args.Raw.TryGetProperty("cuantos", out _) ? args.Int("cuantos", 1, 10) : 5;
        var hits = await Task.Run(() => index.Search(query, question, max), ct);
        return ToolOutcome.Success(new
        {
            resultados = hits.Select(h => new { archivo = h.Name, ruta = h.Path, parecido = h.Score, fragmento = h.Snippet }),
            indice = indexer.Status,
        });
    }
}

public sealed class LeerDocumentoTool : ILocalTool
{
    private const int MaxChars = 20_000;

    public string Name => "leer_documento";

    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var path = PathResolver.Resolve(args.String("ruta"));
        if (!File.Exists(path)) throw new ToolException("No encontré ese archivo.");
        if (!DocumentText.Extensions.Contains(Path.GetExtension(path)))
            throw new ToolException("Solo sé leer Word (.docx), PDF y archivos de texto.");
        var text = DocumentText.Extract(path, MaxChars + 1);
        return Task.FromResult(ToolOutcome.Success(new
        {
            archivo = Path.GetFileName(path),
            texto = text.Length > MaxChars ? text[..MaxChars] + "…(recortado)" : text,
        }));
    }
}
