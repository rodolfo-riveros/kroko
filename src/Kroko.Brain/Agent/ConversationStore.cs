using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Kroko.Brain.Memory;
using Kroko.Brain.Providers;
using Microsoft.Extensions.Options;

namespace Kroko.Brain.Agent;

/// <summary>
/// Historial de cada conversación, guardado en SQLite: sobrevive a reinicios de la App y del Brain.
/// Cuando crece, lo más antiguo se resume (con la IA) y se borra, así Kroko no olvida lo importante.
/// </summary>
public sealed class ConversationStore(
    KrokoDatabase db,
    ConversationSummarizer summarizer,
    IOptionsMonitor<AgentOptions> options,
    ILogger<ConversationStore> log)
{
    private readonly ConcurrentDictionary<string, Conversation> _cache = new();

    /// <summary>Borra todo el historial y los resúmenes (empezar de cero). Los recuerdos se mantienen.</summary>
    public int ClearAll()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM messages; DELETE FROM summaries;";
        var n = cmd.ExecuteNonQuery();
        _cache.Clear();
        log.LogInformation("Historial de conversación borrado");
        return n;
    }

    /// <summary>Borra una conversación concreta (las de las automatizaciones, tras cada ejecución).</summary>
    public void Forget(string conversationId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE conversation = $id; DELETE FROM summaries WHERE conversation = $id;";
        cmd.Parameters.AddWithValue("$id", conversationId);
        cmd.ExecuteNonQuery();
        _cache.TryRemove(conversationId, out _);
    }

    public Conversation Get(string conversationId)
    {
        var key = string.IsNullOrWhiteSpace(conversationId) ? "default" : conversationId;
        return _cache.GetOrAdd(key, k => new Conversation(k, db, summarizer, () => options.CurrentValue.HistoryMaxMessages, log));
    }
}

public sealed class Conversation
{
    private static readonly JsonSerializerOptions Json = new();

    private readonly string _id;
    private readonly KrokoDatabase _db;
    private readonly ConversationSummarizer _summarizer;
    private readonly Func<int> _maxMessages;
    private readonly ILogger _log;
    private readonly List<(long DbId, ChatMessage Message)> _messages = [];
    private readonly Lock _lock = new();

    internal Conversation(string id, KrokoDatabase db, ConversationSummarizer summarizer, Func<int> maxMessages, ILogger log)
    {
        _id = id;
        _db = db;
        _summarizer = summarizer;
        _maxMessages = maxMessages;
        _log = log;
        Load();
    }

    public string Id => _id;

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_lock) return _messages.Select(m => m.Message).ToList();
    }

    /// <summary>Resumen de lo conversado antes (lo que ya se recortó del historial).</summary>
    public string? Summary => _summarizer.Get(_id);

    /// <summary>
    /// Guarda un turno completo (solo si terminó bien, así un fallo nunca deja el historial a medias)
    /// y, si el historial es muy largo, manda a resumir lo más antiguo.
    /// </summary>
    public void Commit(IEnumerable<ChatMessage> turn)
    {
        List<ChatMessage> trimmed = [];
        lock (_lock)
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            foreach (var m in turn)
            {
                // Las capturas de pantalla no se guardan (ocupan mucho y pueden ser privadas)
                var stored = m.ImageBase64 is null ? m : CloneWithoutImage(m);
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO messages(conversation, json, created) VALUES($c, $j, $t); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$c", _id);
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(stored, Json));
                cmd.Parameters.AddWithValue("$t", KrokoDatabase.Now());
                _messages.Add(((long)cmd.ExecuteScalar()!, stored));
            }

            var max = Math.Max(4, _maxMessages());
            if (_messages.Count > max)
            {
                // Se recorta hasta la MITAD de una vez (no de a 2 mensajes): así el resumen con la IA se hace pocas veces
                // y no en casi cada orden. Cortamos en el inicio de un mensaje del usuario para no separar
                // una llamada a herramienta de su resultado.
                var keep = Math.Max(2, max / 2);
                var cut = _messages.Count - keep;
                while (cut < _messages.Count && _messages[cut].Message.Role != ChatRole.User) cut++;
                var removed = _messages.Take(cut).ToList();
                foreach (var (dbId, _) in removed)
                {
                    using var del = c.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM messages WHERE id = $id";
                    del.Parameters.AddWithValue("$id", dbId);
                    del.ExecuteNonQuery();
                }
                _messages.RemoveRange(0, cut);
                trimmed = removed.Select(r => r.Message).ToList();
            }
            tx.Commit();
        }

        if (trimmed.Count > 0) _summarizer.Enqueue(_id, trimmed);
    }

    private void Load()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, json FROM messages WHERE conversation = $c ORDER BY id";
        cmd.Parameters.AddWithValue("$c", _id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            try
            {
                var m = JsonSerializer.Deserialize<ChatMessage>(r.GetString(1), Json);
                if (m is not null) _messages.Add((r.GetInt64(0), m));
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Mensaje del historial ilegible; se ignora");
            }
        }

        // Un historial guardado nunca debe empezar con un resultado de herramienta huérfano
        while (_messages.Count > 0 && _messages[0].Message.Role != ChatRole.User) _messages.RemoveAt(0);
    }

    private static ChatMessage CloneWithoutImage(ChatMessage m) => new()
    {
        Role = m.Role,
        Text = m.Text + " (había una captura de pantalla; no se guardó)",
        ToolCalls = m.ToolCalls,
        ToolCallId = m.ToolCallId,
        ToolName = m.ToolName,
        RawProvider = m.RawProvider,
        Raw = m.Raw,
    };
}

/// <summary>
/// Resume (con la IA, en segundo plano) la parte del historial que se recorta,
/// y guarda el resumen acumulado de cada conversación.
/// </summary>
public sealed class ConversationSummarizer(IServiceProvider services, KrokoDatabase db, ILogger<ConversationSummarizer> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? Get(string conversation)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT text FROM summaries WHERE conversation = $c";
        cmd.Parameters.AddWithValue("$c", conversation);
        return cmd.ExecuteScalar() as string;
    }

    public void Enqueue(string conversation, IReadOnlyList<ChatMessage> removed) =>
        _ = Task.Run(() => SummarizeAsync(conversation, removed));

    internal async Task SummarizeAsync(string conversation, IReadOnlyList<ChatMessage> removed)
    {
        using var _ = Usage.UsageTracker.Scope("resumen"); // en el medidor, aparte del chat
        var transcript = new StringBuilder();
        foreach (var m in removed)
        {
            if (m.Role == ChatRole.User) transcript.AppendLine($"Dueño: {m.Text}");
            else if (m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
                transcript.AppendLine($"Kroko: {ExpressionTag.Extract(m.Text).Text}");
            else if (m.Role == ChatRole.Assistant && m.ToolCalls.Count > 0)
                transcript.AppendLine($"(Kroko usó: {string.Join(", ", m.ToolCalls.Select(t => t.Name))})");
        }
        if (transcript.Length == 0) return;

        await _gate.WaitAsync();
        try
        {
            var previous = Get(conversation);
            var prompt = $"""
                Actualiza el resumen de lo que Pol y Kroko han conversado. Máximo 8 frases en español.
                Conserva solo lo útil para el futuro: decisiones, pendientes, contexto y gustos de Pol.
                Responde solo con el resumen, sin etiquetas.

                Resumen anterior:
                {previous ?? "(ninguno)"}

                Parte nueva de la conversación:
                {transcript}
                """;

            using var scope = services.CreateScope();
            var llm = scope.ServiceProvider.GetRequiredService<IConfiguredLlm>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var res = await llm.ChatAsync("Eres un asistente que resume conversaciones con precisión.",
                [ChatMessage.User(prompt)], [], cts.Token);
            var summary = ExpressionTag.Extract(res.Text).Text;
            if (string.IsNullOrWhiteSpace(summary)) return;

            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO summaries(conversation, text, updated) VALUES($c, $s, $t)
                ON CONFLICT(conversation) DO UPDATE SET text = $s, updated = $t
                """;
            cmd.Parameters.AddWithValue("$c", conversation);
            cmd.Parameters.AddWithValue("$s", summary);
            cmd.Parameters.AddWithValue("$t", KrokoDatabase.Now());
            cmd.ExecuteNonQuery();
            log.LogInformation("Resumen de la conversación actualizado ({Count} mensajes antiguos)", removed.Count);
        }
        catch (Exception ex) when (ex is LlmProviderException or NoLlmConfiguredException or OperationCanceledException)
        {
            // Sin IA disponible: se pierde el detalle de lo antiguo, pero nada se rompe
            log.LogWarning("No se pudo resumir el historial: {Message}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }
}
