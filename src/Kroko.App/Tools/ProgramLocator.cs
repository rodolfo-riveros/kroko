using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Kroko.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace Kroko.App.Tools;

/// <summary>
/// Encuentra cómo abrir un programa por su nombre. Orden de búsqueda:
/// 1) alias configurados, 2) nombres comunes, 3) "App Paths" del registro,
/// 4) apps del menú Inicio (incluye las de la Tienda), 5) accesos directos .lnk.
/// </summary>
public sealed class ProgramLocator(IOptions<KrokoOptions> options, ILogger<ProgramLocator> log)
{
    public sealed record Match(string DisplayName, string Target, bool IsShellApp);

    private static readonly Dictionary<string, string> Common = new()
    {
        ["calculadora"] = "calc.exe",
        ["calculator"] = "calc.exe",
        ["bloc de notas"] = "notepad.exe",
        ["notepad"] = "notepad.exe",
        ["explorador"] = "explorer.exe",
        ["explorador de archivos"] = "explorer.exe",
        ["paint"] = "mspaint.exe",
        ["configuracion"] = "ms-settings:",
        ["ajustes de windows"] = "ms-settings:",
        ["administrador de tareas"] = "taskmgr.exe",
        ["cmd"] = "cmd.exe",
        ["simbolo del sistema"] = "cmd.exe",
        ["terminal"] = "wt.exe",
        ["powershell"] = "powershell.exe",
        ["excel"] = "excel.exe",
        ["word"] = "winword.exe",
        ["powerpoint"] = "powerpnt.exe",
        ["outlook"] = "outlook.exe",
        ["chrome"] = "chrome.exe",
        ["google chrome"] = "chrome.exe",
        ["edge"] = "msedge.exe",
        ["firefox"] = "firefox.exe",
        ["brave"] = "brave.exe",
    };

    /// <summary>Nombres de proceso habituales (para cerrar programas).</summary>
    public static readonly Dictionary<string, string[]> ProcessNames = new()
    {
        ["excel"] = ["EXCEL"],
        ["word"] = ["WINWORD"],
        ["powerpoint"] = ["POWERPNT"],
        ["outlook"] = ["OUTLOOK", "olk"],
        ["bloc de notas"] = ["notepad"],
        ["notepad"] = ["notepad"],
        ["calculadora"] = ["CalculatorApp", "calc"],
        ["chrome"] = ["chrome"],
        ["google chrome"] = ["chrome"],
        ["edge"] = ["msedge"],
        ["navegador"] = ["chrome", "msedge", "firefox", "brave"],
        ["spotify"] = ["Spotify"],
        ["paint"] = ["mspaint"],
        ["visual studio code"] = ["Code"],
        ["vscode"] = ["Code"],
    };

    private List<(string Name, string AppId)>? _startApps;
    private DateTime _startAppsLoaded;

    public async Task<Match?> FindAsync(string name, CancellationToken ct)
    {
        var key = Normalize(name);

        var alias = options.Value.ProgramAliases.FirstOrDefault(a => Normalize(a.Key) == key);
        if (!string.IsNullOrWhiteSpace(alias.Value)) return new Match(alias.Key, alias.Value, false);

        if (Common.TryGetValue(key, out var exe) && (exe.Contains(':') || ResolveAppPath(exe) is not null || IsOnPath(exe)))
            return new Match(name, ResolveAppPath(exe) ?? exe, false);

        if (ResolveAppPath(key.Replace(" ", "") + ".exe") is { } appPath) return new Match(name, appPath, false);

        var apps = await GetStartAppsAsync(ct);
        var best = apps
            .Select(a => (a, score: Score(key, Normalize(a.Name))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.a.Name.Length)
            .FirstOrDefault();
        if (best.score > 0) return new Match(best.a.Name, best.a.AppId, true);

        return FindShortcut(key);
    }

    /// <summary>Nombres parecidos para sugerir cuando no se encuentra.</summary>
    public async Task<IReadOnlyList<string>> SuggestAsync(string name, CancellationToken ct)
    {
        var key = Normalize(name);
        var apps = await GetStartAppsAsync(ct);
        return apps
            .Select(a => (a.Name, d: Distance(key, Normalize(a.Name))))
            .OrderBy(x => x.d)
            .Take(3)
            .Select(x => x.Name)
            .ToList();
    }

    public static void Launch(Match m)
    {
        var psi = m.IsShellApp
            ? new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{m.Target}") { UseShellExecute = true }
            : new ProcessStartInfo(m.Target) { UseShellExecute = true };
        Process.Start(psi)?.Dispose();
    }

    // ---------------------------------------------------------------- fuentes

    private static string? ResolveAppPath(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (k?.GetValue(null) is string path)
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private static bool IsOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Any(d => File.Exists(Path.Combine(d.Trim(), exe)));

    /// <summary>Apps del menú Inicio (escritorio y Tienda) vía Get-StartApps. Se guarda 10 minutos.</summary>
    private async Task<List<(string Name, string AppId)>> GetStartAppsAsync(CancellationToken ct)
    {
        if (_startApps is not null && DateTime.Now - _startAppsLoaded < TimeSpan.FromMinutes(10)) return _startApps;

        var list = new List<(string, string)>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var json = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            foreach (var e in items)
            {
                var n = e.GetProperty("Name").GetString();
                var id = e.GetProperty("AppID").GetString();
                if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(id) && !IsUninstaller(n)) list.Add((n, id));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or KeyNotFoundException)
        {
            log.LogWarning(ex, "No se pudo leer la lista de apps del menú Inicio");
        }

        _startApps = list;
        _startAppsLoaded = DateTime.Now;
        return list;
    }

    private static Match? FindShortcut(string key)
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var best = dirs.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.lnk", opts))
            .Where(f => !IsUninstaller(Path.GetFileNameWithoutExtension(f)))
            .Select(f => (f, score: Score(key, Normalize(Path.GetFileNameWithoutExtension(f)))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .FirstOrDefault();
        return best.score > 0 ? new Match(Path.GetFileNameWithoutExtension(best.f), best.f, false) : null;
    }

    // ---------------------------------------------------------------- comparación de nombres

    public static string Normalize(string s)
    {
        var decomposed = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return string.Join(' ', sb.ToString().Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>0 = no coincide; más alto = mejor.</summary>
    public static int Score(string query, string candidate)
    {
        if (candidate == query) return 100;
        if (candidate.StartsWith(query + " ", StringComparison.Ordinal)) return 85;
        if (candidate.EndsWith(" " + query, StringComparison.Ordinal)) return 80;
        if (candidate.Split(' ').Contains(query)) return 75;
        if (query.Length >= 3 && candidate.StartsWith(query, StringComparison.Ordinal)) return 65;
        if (query.Length >= 4 && candidate.Contains(query, StringComparison.Ordinal)) return 55;
        var words = query.Split(' ');
        if (words.Length > 1 && words.All(w => candidate.Contains(w, StringComparison.Ordinal))) return 50;
        return 0;
    }

    private static bool IsUninstaller(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("desinstal", StringComparison.OrdinalIgnoreCase);

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
