using System.IO;
using System.Text.Json;
using Kroko.App.Services;
using Kroko.App.Tools;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.App.Tests;

/// <summary>
/// La segunda barrera de seguridad: aunque el Brain pida algo peligroso, la App no lo ejecuta
/// sin la aprobación de Pol en la tarjeta.
/// </summary>
public sealed class ExecutorSecurityTests : IDisposable
{
    private readonly string _auditDir = Path.Combine(Path.GetTempPath(), "kroko-audit-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTool _papelera = new("enviar_a_papelera");
    private readonly FakeTool _estado = new("estado_equipo");

    public void Dispose()
    {
        try { Directory.Delete(_auditDir, true); } catch (IOException) { }
    }

    private ToolExecutor Create(KrokoOptions? opt = null)
    {
        var options = Options.Create(opt ?? new KrokoOptions());
        var brain = new BrainConnection(options, NullLogger<BrainConnection>.Instance);
        var audit = new AuditLog(NullLogger<AuditLog>.Instance, _auditDir);
        return new ToolExecutor([_papelera, _estado], brain, audit, options, NullLogger<ToolExecutor>.Instance);
    }

    private static ToolCallMessage Call(string name, string callId, object args) => new()
    {
        CallId = callId,
        Name = name,
        Args = JsonSerializer.SerializeToElement(args),
        Risk = ToolRisk.Read, // aunque el mensaje mienta sobre el riesgo, la App lo recalcula
    };

    [Fact]
    public async Task Peligrosa_sin_aprobacion_se_bloquea()
    {
        var exec = Create();
        var res = await exec.ExecuteAsync(Call("enviar_a_papelera", "c1", new { rutas = new[] { "C:\\x.txt" } }));

        Assert.False(res.Ok);
        Assert.Equal(0, _papelera.Calls);
    }

    [Fact]
    public async Task Peligrosa_aprobada_se_ejecuta_una_sola_vez()
    {
        var exec = Create();
        exec.RecordDecision("c2", "enviar_a_papelera", "Enviar…", approved: true);

        var first = await exec.ExecuteAsync(Call("enviar_a_papelera", "c2", new { rutas = new[] { "C:\\x.txt" } }));
        var replay = await exec.ExecuteAsync(Call("enviar_a_papelera", "c2", new { rutas = new[] { "C:\\x.txt" } }));

        Assert.True(first.Ok);
        Assert.False(replay.Ok); // la aprobación no se puede reutilizar
        Assert.Equal(1, _papelera.Calls);
    }

    [Fact]
    public async Task Rechazada_no_se_ejecuta()
    {
        var exec = Create();
        exec.RecordDecision("c3", "enviar_a_papelera", "Enviar…", approved: false);
        var res = await exec.ExecuteAsync(Call("enviar_a_papelera", "c3", new { rutas = new[] { "C:\\x.txt" } }));
        Assert.False(res.Ok);
        Assert.Equal(0, _papelera.Calls);
    }

    [Fact]
    public async Task Lectura_se_ejecuta_sin_confirmacion_y_se_audita()
    {
        var exec = Create();
        var res = await exec.ExecuteAsync(Call("estado_equipo", "c4", new { }));

        Assert.True(res.Ok);
        Assert.Equal(1, _estado.Calls);
        var line = Assert.Single(File.ReadAllLines(Directory.GetFiles(_auditDir).Single()));
        Assert.Contains("estado_equipo", line);
    }

    [Fact]
    public async Task En_pausa_no_se_ejecuta_nada()
    {
        var exec = Create();
        exec.RecordDecision("c5", "enviar_a_papelera", "Enviar…", approved: true);
        await exec.EmergencyStopAsync();

        Assert.False((await exec.ExecuteAsync(Call("estado_equipo", "c6", new { }))).Ok);
        Assert.False((await exec.ExecuteAsync(Call("enviar_a_papelera", "c5", new { rutas = new[] { "C:\\x" } }))).Ok);
        Assert.Equal(0, _estado.Calls + _papelera.Calls);

        exec.Resume();
        Assert.True((await exec.ExecuteAsync(Call("estado_equipo", "c7", new { }))).Ok);
        Assert.False((await exec.ExecuteAsync(Call("enviar_a_papelera", "c5", new { rutas = new[] { "C:\\x" } }))).Ok); // la pausa borró aprobaciones
    }

    [Fact]
    public async Task Herramienta_desactivada_no_se_ejecuta()
    {
        var exec = Create(new KrokoOptions { DisabledTools = ["estado_equipo"] });
        Assert.False((await exec.ExecuteAsync(Call("estado_equipo", "c8", new { }))).Ok);
        Assert.Equal(0, _estado.Calls);
    }

    private sealed class FakeTool(string name) : ILocalTool
    {
        public int Calls { get; private set; }
        public string Name => name;

        public Task<ToolOutcome> RunAsync(ToolArgs args, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ToolOutcome.Success(new { ok = true }));
        }
    }
}
