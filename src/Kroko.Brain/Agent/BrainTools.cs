using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kroko.Brain.Memory;
using Kroko.Brain.Providers;
using Kroko.Shared.Protocol;
using Kroko.Shared.Tools;

namespace Kroko.Brain.Agent;

/// <summary>
/// Herramienta que se ejecuta en el propio Brain (memoria, internet, correo, conectores MCP…).
/// Las de riesgo Dangerous pasan por la tarjeta de confirmación antes de ejecutarse.
/// </summary>
public interface IBrainTool
{
    ToolDefinition Definition { get; }

    ToolRisk RiskFor(JsonElement args) => Definition.Risk;

    /// <summary>Texto de la tarjeta de confirmación.</summary>
    string Describe(JsonElement args) => $"{Definition.Name}: {BrainToolResult.Summarize(args)}";

    Task<ToolExecution> ExecuteAsync(JsonElement args, CancellationToken ct);
}

/// <summary>Herramientas sincrónicas sencillas (memoria, rutinas, buzón).</summary>
public abstract class SyncBrainTool : IBrainTool
{
    public abstract ToolDefinition Definition { get; }

    public abstract ToolExecution Execute(JsonElement args);

    public Task<ToolExecution> ExecuteAsync(JsonElement args, CancellationToken ct) => Task.FromResult(Execute(args));
}

/// <summary>Fuente de herramientas que cambian en marcha (servidores MCP que se conectan o caen).</summary>
public interface IToolSource
{
    IEnumerable<IBrainTool> CurrentTools { get; }
}

public static class BrainToolResult
{
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ToolExecution Ok(object data) =>
        new(new JsonObject { ["ok"] = true, ["data"] = JsonSerializer.SerializeToNode(data, Readable) }.ToJsonString(Readable));

    public static ToolExecution Fail(string error) =>
        new(new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(Readable));

    public static ToolDefinition Define(string name, string description, string schema, ToolRisk risk = ToolRisk.Write) =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone(), risk);

    /// <summary>"clave: valor · clave: valor" (corto) para tarjetas de confirmación y auditoría.</summary>
    public static string Summarize(JsonElement args, int max = 300)
    {
        if (args.ValueKind != JsonValueKind.Object) return args.ValueKind == JsonValueKind.Undefined ? "" : args.GetRawText();
        var parts = args.EnumerateObject().Select(p => p.Name + ": " + (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText()));
        var text = string.Join(" · ", parts);
        return text.Length <= max ? text : text[..max] + "…";
    }

    public static IReadOnlyList<string> List(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => e.GetString()?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList()
            : [];
}

// ---------------------------------------------------------------- memoria

public sealed class RecordarTool(MemoryStore memory) : SyncBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("recordar",
        "Guarda para siempre algo útil sobre tu dueño: preferencias, datos, personas, hábitos o correcciones que te hace. " +
        "Úsalo cuando tu dueño te diga algo que convenga recordar o te corrija. Nunca guardes contraseñas ni datos bancarios.",
        """{"type":"object","properties":{"contenido":{"type":"string","description":"Frase corta y autosuficiente, p. ej. 'Su navegador favorito es Brave'"},"categoria":{"type":"string","enum":["preferencia","dato","persona","habito","otro"]}},"required":["contenido"]}""");

    public override ToolExecution Execute(JsonElement args)
    {
        var content = ToolCatalog.Str(args, "contenido").Trim();
        if (content.Length == 0) return BrainToolResult.Fail("Falta el contenido.");
        if (content.Length > 500) return BrainToolResult.Fail("Demasiado largo: resúmelo en una frase.");
        if (SensitiveData.LooksSensitive(content))
            return BrainToolResult.Fail("Eso parece una contraseña o un dato bancario: por seguridad no lo guardo.");
        var id = memory.Remember(content, ToolCatalog.Str(args, "categoria") is { Length: > 0 } c ? c : "otro");
        return BrainToolResult.Ok(new { guardado = content, id });
    }
}

public sealed class ActualizarRecuerdoTool(MemoryStore memory) : SyncBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("actualizar_recuerdo",
        "Corrige un recuerdo existente (por su número #id) cuando tu dueño te dice que algo cambió.",
        """{"type":"object","properties":{"id":{"type":"integer"},"contenido":{"type":"string"}},"required":["id","contenido"]}""");

    public override ToolExecution Execute(JsonElement args)
    {
        var content = ToolCatalog.Str(args, "contenido");
        if (SensitiveData.LooksSensitive(content))
            return BrainToolResult.Fail("Eso parece una contraseña o un dato bancario: por seguridad no lo guardo.");
        return memory.Update(ToolCatalog.Int(args, "id"), content)
            ? BrainToolResult.Ok(new { actualizado = content })
            : BrainToolResult.Fail("No existe ese recuerdo.");
    }
}

public sealed class OlvidarTool(MemoryStore memory) : SyncBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("olvidar",
        "Borra un recuerdo (por su número #id) cuando tu dueño te pide que lo olvides.",
        """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}""");

    public override ToolExecution Execute(JsonElement args) =>
        memory.Forget(ToolCatalog.Int(args, "id"))
            ? BrainToolResult.Ok(new { olvidado = true })
            : BrainToolResult.Fail("No existe ese recuerdo.");
}

// ---------------------------------------------------------------- rutinas

public sealed class CrearRutinaTool(RoutineStore routines) : SyncBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("crear_rutina",
        "Guarda (o reemplaza) una rutina que tu dueño te enseña: un nombre, las frases con las que la activará y los pasos " +
        "que debes hacer con tus herramientas. Ej.: 'modo estudio' → ['abrir Notion', 'silenciar el sonido'].",
        """{"type":"object","properties":{"nombre":{"type":"string"},"frases":{"type":"array","items":{"type":"string"},"description":"Frases que la activan"},"pasos":{"type":"array","items":{"type":"string"},"description":"Pasos en orden, en lenguaje natural"}},"required":["nombre","pasos"]}""");

    public override ToolExecution Execute(JsonElement args)
    {
        var name = ToolCatalog.Str(args, "nombre").Trim();
        var steps = BrainToolResult.List(args, "pasos");
        if (name.Length == 0 || steps.Count == 0) return BrainToolResult.Fail("Una rutina necesita nombre y al menos un paso.");
        if (steps.Count > 20) return BrainToolResult.Fail("Demasiados pasos (máximo 20).");
        var triggers = BrainToolResult.List(args, "frases");
        if (triggers.Count == 0) triggers = [name];
        var id = routines.Save(name, triggers, steps);
        return BrainToolResult.Ok(new { rutina = name, id, frases = triggers, pasos = steps.Count });
    }
}

public sealed class BorrarRutinaTool(RoutineStore routines) : SyncBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("borrar_rutina",
        "Borra una rutina por su nombre.",
        """{"type":"object","properties":{"nombre":{"type":"string"}},"required":["nombre"]}""");

    public override ToolExecution Execute(JsonElement args) =>
        routines.Delete(ToolCatalog.Str(args, "nombre"))
            ? BrainToolResult.Ok(new { borrada = true })
            : BrainToolResult.Fail("No hay ninguna rutina con ese nombre.");
}

// ---------------------------------------------------------------- buzón de mejoras

public sealed class ProponerMejoraTool(SuggestionStore suggestions) : SyncBrainTool
{
    public override ToolDefinition Definition { get; } = BrainToolResult.Define("proponer_mejora",
        "Anota en tu buzón de mejoras algo que tu dueño te pidió y no pudiste hacer (te falta una herramienta), " +
        "o un fallo que tuviste. Tu dueño y su desarrollador lo revisan para darte esa capacidad.",
        """{"type":"object","properties":{"titulo":{"type":"string","description":"Qué capacidad falta, en pocas palabras"},"detalle":{"type":"string","description":"Qué pidió tu dueño exactamente y por qué no se pudo"},"tipo":{"type":"string","enum":["idea","fallo"]}},"required":["titulo","detalle"]}""");

    public override ToolExecution Execute(JsonElement args)
    {
        var title = ToolCatalog.Str(args, "titulo").Trim();
        if (title.Length == 0) return BrainToolResult.Fail("Falta el título.");
        var id = suggestions.Add(ToolCatalog.Str(args, "tipo"), title, ToolCatalog.Str(args, "detalle"));
        return BrainToolResult.Ok(new { anotada = title, id });
    }
}


// ---------------------------------------------------------------- confirmación explícita

/// <summary>
/// Para pasos irreversibles que el sistema no puede detectar solo (comprar, publicar o enviar un
/// formulario en el navegador…): muestra la tarjeta "¿Lo hago?" con la descripción que dé la IA.
/// </summary>
public sealed class ConfirmarAccionTool : IBrainTool
{
    public ToolDefinition Definition { get; } = BrainToolResult.Define("confirmar_accion",
        "Pide a tu dueño que confirme con la tarjeta en pantalla ANTES de un paso irreversible que no tenga confirmación propia: " +
        "comprar, pagar, publicar, enviar un formulario, aceptar términos, borrar algo en una web… Si devuelve error, NO lo hagas.",
        """{"type":"object","properties":{"accion":{"type":"string","description":"Qué vas a hacer exactamente, en una o dos frases"}},"required":["accion"]}""",
        ToolRisk.Dangerous);

    public string Describe(JsonElement args) => ToolCatalog.Str(args, "accion");

    public Task<ToolExecution> ExecuteAsync(JsonElement args, CancellationToken ct) =>
        Task.FromResult(BrainToolResult.Ok(new { aprobado = true, mensaje = "Tu dueño aprobó. Puedes hacerlo." }));
}
