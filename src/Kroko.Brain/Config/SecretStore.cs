using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kroko.Brain.Config;

/// <summary>
/// Claves y cuentas que el dueño escribe en el asistente "Configurar Kroko" (API keys, Gmail, Telegram).
/// Se guardan en secretos.dat, cifrado con DPAPI del usuario de Windows (en Linux/Docker: archivo con
/// permisos 600 dentro del volumen /data). Funciona como fuente de configuración: al guardar, todas las
/// opciones (IOptionsMonitor) se recargan sin reiniciar.
/// Prioridad: appsettings &lt; user-secrets &lt; .env &lt; este archivo &lt; variables de entorno / línea de comandos.
/// </summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = "Kroko.SecretStore.v1"u8.ToArray();

    /// <summary>Únicas claves que se pueden escribir desde la API (nada de tokens del Brain ni rutas).</summary>
    public static readonly IReadOnlySet<string> WritableKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Llm:Gemini:ApiKey",
        "Llm:DeepSeek:ApiKey",
        "Web:TavilyApiKey",
        "Email:Address",
        "Email:AppPassword",
        "Telegram:BotToken",
        "Trello:ApiKey",
        "Trello:Token",
        "Google:ClientId",
        "Google:ClientSecret",
    };

    private readonly Lock _lock = new();
    private Dictionary<string, string> _values;

    public SecretStore(string path)
    {
        FilePath = path;
        _values = Read(path);
    }

    public string FilePath { get; }

    /// <summary>Se dispara tras guardar; la fuente de configuración se recarga con esto.</summary>
    public event Action? Changed;

    /// <summary>Ruta por defecto: junto a los datos de Kroko (en Docker, /data/Kroko). KROKO_SECRETS_FILE la cambia (pruebas).</summary>
    public static string DefaultPath()
    {
        if (Environment.GetEnvironmentVariable("KROKO_SECRETS_FILE") is { Length: > 0 } custom)
            return Environment.ExpandEnvironmentVariables(custom);
        return Path.Combine(Memory.MemoryOptions.DefaultFolder, "secretos.dat");
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_lock) return new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);
    }

    public bool Has(string key)
    {
        lock (_lock) return _values.ContainsKey(key);
    }

    /// <summary>
    /// Aplica cambios: valor con texto = guardar, cadena vacía = borrar, null = no tocar.
    /// Lanza ArgumentException si alguna clave no está permitida (salvo internal = true).
    /// </summary>
    public void Update(IReadOnlyDictionary<string, string?> changes, bool @internal = false)
    {
        if (!@internal && changes.Keys.FirstOrDefault(k => !WritableKeys.Contains(k)) is { } bad)
            throw new ArgumentException($"La clave \"{bad}\" no se puede configurar desde aquí.");

        lock (_lock)
        {
            var next = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in changes)
            {
                if (value is null) continue;
                var v = value.Trim();
                if (v.Length == 0) next.Remove(key);
                else next[key] = v;
            }
            Write(FilePath, next);
            _values = next;
        }
        Changed?.Invoke();
    }

    /// <summary>Reemplaza todas las claves que empiezan por un prefijo (para listas: Telegram:AllowedChatIds:0, :1…).</summary>
    public void ReplaceList(string prefix, IEnumerable<string> items)
    {
        lock (_lock)
        {
            var next = _values.Where(kv => !kv.Key.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var item in items) next[$"{prefix}:{i++}"] = item;
            Write(FilePath, next);
            _values = next;
        }
        Changed?.Invoke();
    }

    private static Dictionary<string, string> Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            var raw = File.ReadAllBytes(path);
            var json = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser) : raw;
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            return new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Archivo de otro usuario de Windows o dañado: se empieza de cero (no se borra, por si acaso).
            Serilog.Log.Warning("No se pudo leer {Path} ({Message}); los datos se volverán a pedir", path, ex.Message);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Write(string path, Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values));
        var data = OperatingSystem.IsWindows() ? ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser) : json;

        // Escritura atómica: primero un temporal, luego se reemplaza
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Fuente de configuración que lee el SecretStore y se recarga cuando cambia.</summary>
public sealed class SecretConfigurationSource(SecretStore store) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new SecretConfigurationProvider(store);
}

public sealed class SecretConfigurationProvider : ConfigurationProvider
{
    private readonly SecretStore _store;

    public SecretConfigurationProvider(SecretStore store)
    {
        _store = store;
        _store.Changed += () =>
        {
            Load();
            OnReload();
        };
    }

    public override void Load() =>
        Data = new Dictionary<string, string?>(_store.Snapshot().Select(kv => KeyValuePair.Create(kv.Key, (string?)kv.Value)),
            StringComparer.OrdinalIgnoreCase);
}
