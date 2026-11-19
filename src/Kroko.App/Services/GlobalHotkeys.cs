using System.Windows.Interop;
using Kroko.App.Native;
using Microsoft.Extensions.Logging;

namespace Kroko.App.Services;

/// <summary>
/// Atajos globales de Kroko 2 (18/11/2026): barra de comandos, portapapeles, dictado y burbuja de texto seleccionado.
/// Una ventana invisible solo de mensajes; cada atajo se registra con su texto ("Ctrl+Shift+Space") y su acción.
/// Si otro programa ya usa uno, se avisa en el registro y Kroko sigue funcionando sin él.
/// </summary>
public sealed class GlobalHotkeys(ILogger<GlobalHotkeys> log) : IDisposable
{
    private HwndSource? _source;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 0x5000;

    public bool Register(string hotkey, Action action, string what)
    {
        _source ??= CreateSource();
        if (!VoiceController.TryParseHotkey(hotkey, out var mods, out var vk))
        {
            log.LogWarning("Atajo no válido para {What}: {Hotkey}", what, hotkey);
            return false;
        }
        var id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, mods | NativeMethods.MOD_NOREPEAT, vk))
        {
            log.LogWarning("No se pudo registrar {Hotkey} para {What} (otro programa lo usa)", hotkey, what);
            return false;
        }
        _actions[id] = action;
        log.LogInformation("{What}: {Hotkey}", what, hotkey);
        return true;
    }

    private HwndSource CreateSource()
    {
        var source = new HwndSource(new HwndSourceParameters("KrokoHotkeys2") { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(WndProc);
        return source;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _actions.TryGetValue((int)wParam, out var action))
        {
            handled = true;
            action();
        }
        return 0;
    }

    public void Dispose()
    {
        if (_source is null) return;
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }
}
