using System.Text.Json;
using Kroko.Brain.Agent;
using Kroko.Brain.Providers;
using Kroko.Shared.Tools;

namespace Kroko.Brain.Owner;

/// <summary>
/// Kroko actualiza el perfil de su dueño conversando ("llámame Rodo", "me mudé a Cusco").
/// Solo cambia los campos que se le pasan.
/// </summary>
public sealed class PerfilActualizarTool(OwnerProfileStore profiles) : SyncBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("perfil_actualizar",
        "Actualiza el perfil de tu dueño cuando te dice su nombre, cómo quiere que lo llames, dónde vive, " +
        "su zona horaria o con qué acento quiere que hables. Pasa solo los campos que cambian. " +
        "Para otros datos (gustos, personas, trabajo) usa recordar.",
        """
        {"type":"object","properties":{
          "nombre":{"type":"string","description":"Nombre completo"},
          "como_llamarte":{"type":"string","description":"Cómo quiere que lo llames"},
          "ciudad":{"type":"string"},
          "pais":{"type":"string"},
          "zona_horaria":{"type":"string","description":"Id IANA, p. ej. America/Lima, America/Mexico_City, Europe/Madrid"},
          "acento":{"type":"string","description":"peruano, mexicano, argentino, colombiano, chileno, español de España, neutro…"}
        }}
        """);

    public override ToolExecution Execute(JsonElement args)
    {
        var p = profiles.Get();
        string Pick(string name, string current) => ToolCatalog.Str(args, name) is { Length: > 0 } v ? v.Trim() : current;

        var updated = p with
        {
            Name = Pick("nombre", p.Name),
            CallMe = Pick("como_llamarte", p.CallMe),
            City = Pick("ciudad", p.City),
            Country = Pick("pais", p.Country),
            TimeZone = Pick("zona_horaria", p.TimeZone),
            Accent = Pick("acento", p.Accent),
        };
        if (updated == p) return ToolConfirmation.Fail("No me pasaste ningún dato nuevo del perfil.");

        try
        {
            updated = profiles.Save(updated);
        }
        catch (ArgumentException ex)
        {
            return ToolConfirmation.Fail(ex.Message + " Usa un id IANA como America/Lima.");
        }
        return BrainToolResult.Ok(new
        {
            nombre = updated.Name,
            como_llamarte = updated.Nickname,
            ubicacion = updated.Location,
            zona_horaria = updated.TimeZone,
            acento = updated.AccentOrDefault,
        });
    }
}
