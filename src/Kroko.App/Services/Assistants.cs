using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Kroko.App.Audio;
using Kroko.App.Tools;
using Kroko.App.ViewModels;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Kroko.App.Services;

/// <summary>
/// Salud de la PC (idea «Kroko 2», 18/11/2026): cada 3 minutos mira el disco, la memoria y si alguna app se come el
/// procesador; si algo va mal, la isla avisa con cara cansada y ofrece qué hacer. Cada aviso, como mucho cada 3 horas.
/// Nunca borra nada: «Liberar espacio» abre el Sensor de almacenamiento de Windows.
/// </summary>
public sealed class HealthWatcher
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Quiet = TimeSpan.FromHours(3);
    private readonly IslandViewModel _island;
    private readonly CompanionBehavior _behavior;
    private readonly PanelViewModel _chat;
    private readonly ILogger<HealthWatcher> _log;
    private readonly DispatcherTimer _timer = new() { Interval = Every };
    private readonly Dictionary<string, DateTime> _lastAlert = [];
    private Dictionary<int, TimeSpan> _cpu = [];
    private DateTime _cpuAt;
    private int _memHigh;
    private readonly Dictionary<string, int> _hungry = [];

    public HealthWatcher(IslandViewModel island, CompanionBehavior behavior, PanelViewModel chat, ILogger<HealthWatcher> log)
    {
        _island = island;
        _behavior = behavior;
        _chat = chat;
        _log = log;
        _timer.Tick += (_, _) => _ = Task.Run(Check);
        _timer.Start();
    }

    private void Check()
    {
        try
        {
            // Disco C: lleno
            var c = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            var used = 1 - (double)c.AvailableFreeSpace / c.TotalSize;
            if (used > 0.9)
                Alert("disco", $"Disco {c.Name.TrimEnd('\\')} al {used:P0}", "Liberar espacio", () => ActivityFeed.OpenShell("ms-settings:storagesense"));

            // Memoria alta dos veces seguidas
            var mem = MemoryUse();
            _memHigh = mem > 0.9 ? _memHigh + 1 : 0;
            if (_memHigh >= 2) Alert("memoria", $"Memoria al {mem:P0}", "Ver qué la usa", () => Ask("¿Por qué está lenta mi PC? Revisa estado_equipo y procesos_listar."));

            // Una app con mucho procesador dos veces seguidas
            var now = DateTime.Now;
            var current = new Dictionary<int, TimeSpan>();
            var names = new Dictionary<int, string>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    current[p.Id] = p.TotalProcessorTime;
                    names[p.Id] = p.ProcessName;
                }
                catch (Exception) { } // procesos del sistema sin permiso
                finally { p.Dispose(); }
            }
            if (_cpu.Count > 0)
            {
                var span = (now - _cpuAt).TotalMilliseconds * Environment.ProcessorCount;
                foreach (var (id, t) in current)
                {
                    if (!_cpu.TryGetValue(id, out var before) || names[id] is "Idle" or "System") continue;
                    var share = (t - before).TotalMilliseconds / span;
                    var name = names[id];
                    _hungry[name] = share > 0.35 ? _hungry.GetValueOrDefault(name) + 1 : 0;
                    if (_hungry[name] >= 2)
                        Alert("cpu-" + name, $"{name} está usando {share:P0} del procesador", "Ver", () => Ask($"{name} está usando mucho procesador. ¿Qué es y qué hago?"));
                }
            }
            _cpu = current;
            _cpuAt = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.LogDebug("Salud de la PC: {Error}", ex.Message);
        }
    }

    private void Alert(string key, string text, string action, Action run)
    {
        if (_lastAlert.TryGetValue(key, out var last) && DateTime.Now - last < Quiet) return;
        _lastAlert[key] = DateTime.Now;
        _log.LogInformation("Aviso de salud: {Text}", text);
        Application.Current?.Dispatcher.BeginInvoke(() => _behavior.PlayExpression("sleepy"));
        _island.ShowAlert(text, action, run);
    }

    private void Ask(string text) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        _chat.InputText = text;
        if (_chat.SendCommand.CanExecute(null)) _chat.SendCommand.Execute(null);
    });

    private static double MemoryUse()
    {
        var status = new Native.NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.NativeMethods.MEMORYSTATUSEX>() };
        return Native.NativeMethods.GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad / 100.0 : 0;
    }
}

/// <summary>
/// Resumen al prender la PC (idea «Kroko 2»): la primera vez del día que Kroko conecta con su cerebro poco después de
/// encender la PC, te cuenta en menos de 30 segundos el clima, tu agenda, los correos importantes y los pendientes.
/// Se apaga en Configuración → Avisos («Resumen al prender»).
/// </summary>
public sealed class MorningBriefing
{
    public MorningBriefing(BrainConnection brain, PanelViewModel chat, ILogger<MorningBriefing> log)
    {
        brain.ConnectionChanged += connected =>
        {
            if (!connected) return;
            var k = UserSettings.LoadKrokoSection();
            if (k["MorningBrief"]?.GetValue<bool>() == false) return;
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            if (k["MorningBriefDate"]?.GetValue<string>() == today) return;
            if (TimeSpan.FromMilliseconds(Environment.TickCount64) > TimeSpan.FromMinutes(30)) return; // la PC ya llevaba rato prendida
            k["MorningBriefDate"] = today;
            UserSettings.SaveKrokoSection(k);
            log.LogInformation("Resumen al prender la PC");
            Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(4)); // que Kroko termine de despertar
                chat.InputText = "Acabo de prender la PC. Dame mi resumen del día en menos de 30 segundos hablados: clima de mi ciudad, " +
                                 "eventos de hoy, correos importantes y tareas pendientes. Breve y en ese orden.";
                if (chat.SendCommand.CanExecute(null)) chat.SendCommand.Execute(null);
            });
        };
    }
}

/// <summary>Un recordatorio que no va por hora sino por lo que pasa en la PC.</summary>
public sealed record ContextRule(string Id, string Cuando, string App, string Texto, bool Repetir);

/// <summary>
/// Recordatorios por contexto (idea «Kroko 2»): «cuando abra Excel, recuérdame actualizar la hoja de ventas». Disparadores:
/// al abrir una app, al conectar audífonos (cambia la salida de audio) y al prender la PC. Se guardan en esta PC
/// (%LOCALAPPDATA%\Kroko\recordatorios-contexto.json) y avisan en la isla con +10 min / Hecho.
/// </summary>
public sealed class ContextReminders
{
    private readonly string _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "recordatorios-contexto.json");
    private readonly IslandViewModel _island;
    private readonly ISoundService _sounds;
    private readonly ILogger<ContextReminders> _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private string? _output;
    private readonly Lock _gate = new();

    public ContextReminders(IslandViewModel island, ISoundService sounds, ILogger<ContextReminders> log)
    {
        _island = island;
        _sounds = sounds;
        _log = log;
        Rules = Load();
        _running = RunningApps();
        _output = DefaultOutput();
        _timer.Tick += (_, _) => _ = Task.Run(Check);
        _timer.Start();
        if (TimeSpan.FromMilliseconds(Environment.TickCount64) < TimeSpan.FromMinutes(15)) Fire("inicio", "");
    }

    public List<ContextRule> Rules { get; private set; }

    public ContextRule Add(string cuando, string app, string texto, bool repetir)
    {
        var rule = new ContextRule(Guid.NewGuid().ToString("N")[..8], cuando, app.Trim(), texto.Trim(), repetir);
        lock (_gate)
        {
            Rules.Add(rule);
            Save();
        }
        return rule;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var n = Rules.RemoveAll(r => r.Id == id);
            if (n > 0) Save();
            return n > 0;
        }
    }

    private void Check()
    {
        try
        {
            var now = RunningApps();
            foreach (var opened in now.Except(_running)) Fire("app", opened);
            _running = now;
            var output = DefaultOutput();
            if (output is not null && _output is not null && output != _output) Fire("audifonos", "");
            _output = output;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.LogDebug("Recordatorios por contexto: {Error}", ex.Message);
        }
    }

    /// <summary>¿Coincide la regla con la app que se abrió? «Excel» ↔ EXCEL, «Word» ↔ WINWORD, «Chrome» ↔ chrome…</summary>
    internal static bool Matches(ContextRule rule, string cuando, string process)
    {
        if (!rule.Cuando.Equals(cuando, StringComparison.OrdinalIgnoreCase)) return false;
        if (cuando != "app") return true;
        var want = rule.App.ToLowerInvariant().Replace(".exe", "").Trim();
        var have = process.ToLowerInvariant();
        string[] aliases = want switch
        {
            "word" => ["winword"],
            "excel" => ["excel"],
            "powerpoint" => ["powerpnt"],
            "outlook" => ["outlook", "olk"],
            "teams" => ["ms-teams", "teams"],
            "edge" => ["msedge"],
            "vs code" or "visual studio code" or "code" => ["code"],
            _ => [want.Replace(" ", "")],
        };
        return aliases.Any(a => have == a || (a.Length >= 4 && have.StartsWith(a, StringComparison.Ordinal)));
    }

    private void Fire(string cuando, string process)
    {
        List<ContextRule> hits;
        lock (_gate) hits = [.. Rules.Where(r => Matches(r, cuando, process))];
        foreach (var rule in hits)
        {
            _log.LogInformation("Recordatorio por contexto ({When}): {Text}", cuando, rule.Texto);
            var prefix = cuando switch { "app" => $"Abriste {process}: ", "audifonos" => "Conectaste audio: ", _ => "" };
            Application.Current?.Dispatcher.BeginInvoke(() => _sounds.Play("notify"));
            _island.ShowAlert(prefix + rule.Texto);
            if (!rule.Repetir) Remove(rule.Id);
        }
    }

    private static HashSet<string> RunningApps()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.MainWindowHandle != 0) set.Add(p.ProcessName); }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return set;
    }

    private static string? DefaultOutput()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.ID;
        }
        catch (Exception) { return null; }
    }

    private List<ContextRule> Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<List<ContextRule>>(File.ReadAllText(_file)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(Rules));
    }
}

/// <summary>«Cuando abra Excel, recuérdame…»: crear, ver o borrar recordatorios por contexto.</summary>
public sealed class RecordatorioContextoTool(IServiceProvider services) : ILocalTool
{
    public string Name => "recordatorio_contexto";

    // Se busca al usarla (no al crearla): ContextReminders depende de la isla, y la isla del chat, que usa las herramientas
    public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var reminders = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ContextReminders>(services);
        switch (args.String("accion"))
        {
            case "crear":
                var cuando = args.String("cuando");
                if (cuando is not ("app" or "audifonos" or "inicio")) throw new ToolException("cuando debe ser app, audifonos o inicio.");
                var app = args.OptionalString("app") ?? "";
                if (cuando == "app" && app.Trim().Length == 0) throw new ToolException("Dime qué app (Excel, Word, Chrome…).");
                var rule = reminders.Add(cuando, app, args.String("texto"), args.OptionalString("repetir") != "no");
                return Task.FromResult(ToolOutcome.Success(new { creado = rule }));
            case "listar":
                return Task.FromResult(ToolOutcome.Success(new { recordatorios = reminders.Rules }));
            case "borrar":
                return Task.FromResult(reminders.Remove(args.String("id"))
                    ? ToolOutcome.Success(new { borrado = true })
                    : ToolOutcome.Fail("No encuentro ese recordatorio."));
            default:
                throw new ToolException("accion debe ser crear, listar o borrar.");
        }
    }
}
