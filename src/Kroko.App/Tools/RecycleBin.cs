using System.IO;
using Kroko.App.Native;

namespace Kroko.App.Tools;

/// <summary>Mandar a la Papelera (nunca borrar para siempre). Para deshacer lo que Kroko creó (18/11/2026).</summary>
public static class RecycleBin
{
    public static bool Send(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        PathGuard.EnsureWritable(path);
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw new ToolException("Está en una unidad sin Papelera; no lo borro para no perderlo para siempre.");
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc = NativeMethods.FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT | NativeMethods.FOF_NOERRORUI,
        };
        return NativeMethods.SHFileOperation(ref op) == 0 && !File.Exists(path) && !Directory.Exists(path);
    }
}
