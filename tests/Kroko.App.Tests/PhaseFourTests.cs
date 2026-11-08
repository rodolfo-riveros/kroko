using System.IO;
using System.Text.Json;
using FlaUI.Core.WindowsAPI;
using Kroko.App.Services;
using Kroko.App.Tools;
using Kroko.App.ViewModels;
using Kroko.Shared.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kroko.App.Tests;

public sealed class PhaseFourTests : IDisposable
{
    private readonly string _auditDir = Path.Combine(Path.GetTempPath(), "kroko-audit-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_auditDir, true); } catch (IOException) { }
    }

    [Fact]
    public void Atajos_de_teclado()
    {
        Assert.Equal([VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S], TeclasTool.Parse("Ctrl+S"));
        Assert.Equal([VirtualKeyShort.F5], TeclasTool.Parse("F5"));
        Assert.Equal([VirtualKeyShort.ALT, VirtualKeyShort.TAB], TeclasTool.Parse("Alt+Tab"));
        Assert.Throws<ToolException>(() => TeclasTool.Parse("Ctrl+Patata"));
    }

    [Fact]
    public void Alias_y_scripts_se_leen_de_texto()
    {
        var obj = SettingsViewModel.ParsePairs("excel = C:\\Office\\excel.exe\n\nmal formado\nbackup = \"D:\\scripts\\backup.ps1\"");
        Assert.Equal(2, obj.Count);
        Assert.Equal("D:\\scripts\\backup.ps1", obj["backup"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Aprobacion_desde_Telegram_segun_ajuste(bool allow, bool shouldRun)
    {
        var options = Options.Create(new KrokoOptions { AllowRemoteApprovals = allow });
        var brain = new BrainConnection(options, NullLogger<BrainConnection>.Instance);
        var audit = new AuditLog(NullLogger<AuditLog>.Instance, _auditDir);
        var tool = new Counting("enviar_a_papelera");
        var exec = new ToolExecutor([tool], brain, audit, options, NullLogger<ToolExecutor>.Instance);

        var res = await exec.ExecuteAsync(new ToolCallMessage
        {
            CallId = "t1",
            Name = "enviar_a_papelera",
            Args = JsonSerializer.SerializeToElement(new { rutas = new[] { "C:\\x.txt" } }),
            ApprovedRemotely = "Telegram",
        });

        Assert.Equal(shouldRun, res.Ok);
        Assert.Equal(shouldRun ? 1 : 0, tool.Calls);
    }

    private sealed class Counting(string name) : ILocalTool
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
