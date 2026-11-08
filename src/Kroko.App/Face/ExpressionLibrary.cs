using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Kroko.App.Face;

/// <summary>
/// Carga las expresiones desde expressions.json y las recarga en caliente al guardar el archivo.
/// Prioridad: %LOCALAPPDATA%\Kroko\expressions.json (tu copia editable) y si no existe, la que viene con la App.
/// </summary>
public sealed class ExpressionLibrary : IDisposable
{
    public const string FallbackKey = "idle";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<ExpressionLibrary> _log;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly DispatcherTimer _reloadDebounce;
    private Dictionary<string, Expression> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<Expression> _all = [];

    public ExpressionLibrary(ILogger<ExpressionLibrary> log)
    {
        _log = log;
        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            Load();
        };

        Load();
        Watch(Path.GetDirectoryName(DefaultPath)!);
        Watch(Path.GetDirectoryName(UserPath)!);
    }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Face", "expressions.json");

    public static string UserPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "expressions.json");

    public string ActivePath => File.Exists(UserPath) ? UserPath : DefaultPath;

    /// <summary>Se lanza (en el hilo de la interfaz) cada vez que se recargan las expresiones.</summary>
    public event Action? Changed;

    public IReadOnlyList<Expression> All => _all;

    public Expression Get(string key) =>
        _byKey.TryGetValue(key, out var e) ? e
        : _byKey.TryGetValue(FallbackKey, out var idle) ? idle
        : Expression.Build(new ExpressionSpec { Key = FallbackKey, Name = "Reposo" });

    public bool Contains(string key) => _byKey.ContainsKey(key);

    /// <summary>Crea tu copia editable (si no existe) y devuelve su ruta.</summary>
    public string EnsureUserCopy()
    {
        if (!File.Exists(UserPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserPath)!);
            File.Copy(DefaultPath, UserPath);
            _log.LogInformation("Copia editable de expresiones creada en {Path}", UserPath);
        }
        return UserPath;
    }

    public void Load()
    {
        var path = ActivePath;
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<ExpressionFile>(json, JsonOptions)
                       ?? throw new InvalidDataException("Archivo vacío");

            var list = new List<Expression>();
            var map = new Dictionary<string, Expression>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in file.Expressions.Where(s => !string.IsNullOrWhiteSpace(s.Key)))
            {
                var exp = Expression.Build(spec);
                map[exp.Key] = exp;
                list.Add(exp);
            }

            _byKey = map;
            _all = list;
            _log.LogInformation("{Count} expresiones cargadas desde {Path}", list.Count, path);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            // Si el JSON tiene un error mientras lo editas, Kroko sigue con las expresiones anteriores.
            _log.LogWarning(ex, "No se pudieron cargar las expresiones de {Path}; se mantienen las anteriores", path);
        }
    }

    private void Watch(string dir)
    {
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var w = new FileSystemWatcher(dir, "expressions.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler h = (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        });
        w.Changed += h;
        w.Created += h;
        w.Deleted += h;
        w.Renamed += (s, e) => h(s, e);
        _watchers.Add(w);
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _reloadDebounce.Stop();
    }
}
