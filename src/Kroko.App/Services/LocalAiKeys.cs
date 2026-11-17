using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace Kroko.App.Services;

/// <summary>
/// Las claves de IA del dueño viven SOLO en su PC (decidido por Pol, 15/11/2026: «el cerebro no puede tener la API de
/// Gemini»). Se guardan cifradas con Windows (DPAPI, solo su usuario puede leerlas) en %LOCALAPPDATA%\Kroko\claves-ia.dat
/// y se le prestan al cerebro cada vez que la App se conecta; el cerebro las tiene en memoria y las olvida al desconectarse.
/// </summary>
public sealed class LocalAiKeys
{
    private static readonly byte[] Entropy = "Kroko.LocalAiKeys.v1"u8.ToArray();
    private readonly BrainConnection _brain;
    private readonly ILogger<LocalAiKeys> _log;
    private readonly Lock _lock = new();
    private readonly LocalAiService? _localAi;

    public LocalAiKeys(BrainConnection brain, ILogger<LocalAiKeys> log, LocalAiService localAi) : this(brain, log, DefaultPath, localAi) { }

    internal LocalAiKeys(BrainConnection brain, ILogger<LocalAiKeys> log, string path, LocalAiService? localAi = null)
    {
        _localAi = localAi;
        // Cuando la IA de la PC se enciende o se apaga, el cerebro se entera al momento
        if (localAi is not null) localAi.StateChanged += () => _ = SendAsync();
        _brain = brain;
        _log = log;
        FilePath = path;
        // Cada vez que se conecta (o reconecta) al cerebro, le presta las claves
        _brain.ConnectionChanged += connected =>
        {
            if (connected) _ = SendAsync();
        };
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "claves-ia.dat");

    public string FilePath { get; }

    public sealed record Keys(List<string> Gemini, string? DeepSeek)
    {
        public bool HasAny => Gemini.Count > 0 || !string.IsNullOrEmpty(DeepSeek);
    }

    public Keys Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new([], null);
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<Keys>(plain) ?? new([], null);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
            {
                _log.LogWarning("No pude leer las claves de IA guardadas en esta PC ({Message})", ex.Message);
                return new([], null);
            }
        }
    }

    /// <summary>
    /// Cambia las claves: un texto = guardar esa posición, "" = borrarla, null = no tocar. gemini tiene 4 posiciones
    /// (principal y 3 extra). Devuelve lo que quedó guardado.
    /// </summary>
    public Keys Update(IReadOnlyList<string?> gemini, string? deepSeek)
    {
        lock (_lock)
        {
            var current = Load();
            var slots = Enumerable.Range(0, 4).Select(i => current.Gemini.ElementAtOrDefault(i)).ToArray();
            for (var i = 0; i < Math.Min(4, gemini.Count); i++)
                if (gemini[i] is { } value) slots[i] = value.Trim().Length == 0 ? null : value.Trim();
            var ds = deepSeek is null ? current.DeepSeek : deepSeek.Trim().Length == 0 ? null : deepSeek.Trim();
            var next = new Keys([.. slots.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k!).Distinct()], ds);

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(next), Entropy, DataProtectionScope.CurrentUser);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, FilePath, overwrite: true);
            return next;
        }
    }

    /// <summary>Presta las claves al cerebro conectado (solo viven allí en memoria).</summary>
    public async Task<bool> SendAsync()
    {
        var keys = Load();
        var local = _localAi is { IsReady: true } ai ? ai.ModelName : null;
        if ((!keys.HasAny && local is null) || !_brain.IsConnected) return false;
        var sent = await _brain.SendAsync(new CredentialsMessage { Gemini = keys.Gemini, DeepSeek = keys.DeepSeek, LocalModel = local });
        if (sent) _log.LogInformation("Claves de IA prestadas al cerebro ({Gemini} de Gemini)", keys.Gemini.Count);
        return sent;
    }
}
