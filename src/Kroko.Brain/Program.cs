using System.Reflection;
using Kroko.Brain.Agent;
using Kroko.Brain.Config;
using Kroko.Brain.Email;
using Kroko.Brain.Mcp;
using Kroko.Brain.Memory;
using Kroko.Brain.Owner;
using Kroko.Brain.Productivity;
using Kroko.Brain.Providers;
using Kroko.Brain.Tts;
using Kroko.Brain.Web;
using Kroko.Shared;
using Kroko.Shared.Protocol;
using Serilog;

// Logger de arranque: captura errores incluso antes de leer la configuración.
// Es un logger normal (no "bootstrap"): AddSerilog lo reemplaza sin congelarlo, así también
// funciona si se crean varios Brains en el mismo proceso (pruebas de integración).
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    // Las API keys viven en user-secrets. Se cargan siempre (no solo en Development),
    // porque la App lanza el Brain local sin variables de entorno de desarrollo.
    builder.Configuration.AddUserSecrets<Program>(optional: true);

    // El .env del dueño (con nombre o con la clave suelta). Después se vuelven a añadir las variables de
    // entorno y la línea de comandos para que, en un servidor, siempre manden sobre el archivo.
    string? dotEnvFile = null;
    if (Environment.GetEnvironmentVariable("KROKO_DISABLE_DOTENV") is not "1" && DotEnv.Find(AppContext.BaseDirectory) is { } envFile)
    {
        builder.Configuration.AddInMemoryCollection(DotEnv.Parse(File.ReadAllLines(envFile)));
        dotEnvFile = envFile;
    }

    // Lo que el dueño escribe en "Configurar Kroko" (cifrado). Las variables de entorno siguen mandando.
    var secretStore = new SecretStore(SecretStore.DefaultPath());
    ((IConfigurationBuilder)builder.Configuration).Add(new SecretConfigurationSource(secretStore));
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
    builder.Services.AddSingleton(secretStore);

    var logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kroko", "logs");

    builder.Services.AddSerilog((services, cfg) => cfg
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.Conditional(_ => builder.Configuration.GetValue("Logging:WriteToFile", true),
            w => w.File(Path.Combine(logDir, "brain-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)));

    builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.Section));
    builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));

    var timeout = TimeSpan.FromSeconds(Math.Max(5, builder.Configuration.GetValue($"{LlmOptions.Section}:TimeoutSeconds", 60)));
    builder.Services.AddHttpClient<GeminiProvider>(c => c.Timeout = timeout);
    builder.Services.AddHttpClient<DeepSeekProvider>(c => c.Timeout = timeout);
    builder.Services.AddTransient<ILlmProvider>(sp => sp.GetRequiredService<GeminiProvider>());
    builder.Services.AddTransient<ILlmProvider>(sp => sp.GetRequiredService<DeepSeekProvider>());
    builder.Services.AddTransient<IConfiguredLlm, FallbackLlmProvider>();

    builder.Services.AddSingleton<Kroko.Brain.Owner.OwnerProfileStore>();
    builder.Services.AddSingleton<TimeProvider, Kroko.Brain.Owner.OwnerTimeProvider>();
    builder.Services.Configure<MemoryOptions>(builder.Configuration.GetSection(MemoryOptions.Section));
    builder.Services.AddSingleton<KrokoDatabase>();
    builder.Services.AddSingleton<MemoryStore>();
    builder.Services.AddSingleton<RoutineStore>();
    builder.Services.AddSingleton<SuggestionStore>();
    builder.Services.AddSingleton<ConversationSummarizer>();
    builder.Services.AddSingleton<ConversationStore>();
    builder.Services.AddSingleton<SystemPrompt>();

    // Herramientas: las del Brain (memoria, rutinas, buzón) + las de la App (manos)
    builder.Services.AddSingleton<IBrainTool, RecordarTool>();
    builder.Services.AddSingleton<IBrainTool, ActualizarRecuerdoTool>();
    builder.Services.AddSingleton<IBrainTool, OlvidarTool>();
    builder.Services.AddSingleton<IBrainTool, CrearRutinaTool>();
    builder.Services.AddSingleton<IBrainTool, BorrarRutinaTool>();
    builder.Services.AddSingleton<IBrainTool, ProponerMejoraTool>();
    builder.Services.AddSingleton<IBrainTool, Kroko.Brain.Owner.PerfilActualizarTool>();

    // Automatizaciones programadas con IA
    // Cuenta de Google (Tasks, Calendar, Drive). Un solo objeto: guarda los permisos pendientes y el token de acceso
    builder.Services.Configure<Kroko.Brain.Apps.GoogleOptions>(builder.Configuration.GetSection(Kroko.Brain.Apps.GoogleOptions.Section));
    builder.Services.AddHttpClient("google", c => c.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<Kroko.Brain.Apps.GoogleAccount>(sp,
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("google")));
    foreach (var t in new[] { typeof(Kroko.Brain.Apps.GoogleTareasListarTool), typeof(Kroko.Brain.Apps.GoogleTareaCrearTool), typeof(Kroko.Brain.Apps.GoogleTareaCompletarTool),
                 typeof(Kroko.Brain.Apps.GoogleTareaBorrarTool), typeof(Kroko.Brain.Apps.GoogleCalendarioEventosTool), typeof(Kroko.Brain.Apps.GoogleCalendarioCrearTool),
                 typeof(Kroko.Brain.Apps.GoogleDriveBuscarTool), typeof(Kroko.Brain.Apps.GoogleDriveLeerTool), typeof(Kroko.Brain.Apps.GoogleContactosBuscarTool) })
        builder.Services.AddSingleton(typeof(IBrainTool), t);

    // Notas y agenda en Notion (una herramienta que hace todos los pasos por código)
    builder.Services.AddSingleton<Kroko.Brain.Apps.INotionCalls, Kroko.Brain.Apps.McpNotionCalls>();
    builder.Services.AddSingleton<Kroko.Brain.Apps.NotionNotebook>();
    builder.Services.AddSingleton<IBrainTool, Kroko.Brain.Apps.GuardarEnNotionTool>();

    // Apps de tareas: Trello
    builder.Services.Configure<Kroko.Brain.Apps.TrelloOptions>(builder.Configuration.GetSection(Kroko.Brain.Apps.TrelloOptions.Section));
    builder.Services.AddHttpClient<Kroko.Brain.Apps.TrelloClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
    foreach (var t in new[] { typeof(Kroko.Brain.Apps.TrelloTablerosTool), typeof(Kroko.Brain.Apps.TrelloTarjetasTool), typeof(Kroko.Brain.Apps.TrelloCrearTarjetaTool),
                 typeof(Kroko.Brain.Apps.TrelloMoverTarjetaTool), typeof(Kroko.Brain.Apps.TrelloComentarTool), typeof(Kroko.Brain.Apps.TrelloArchivarTool) })
        builder.Services.AddSingleton(typeof(IBrainTool), t);

    builder.Services.AddSingleton<Kroko.Brain.Automation.AutomationStore>();
    builder.Services.AddSingleton<Kroko.Brain.Automation.AutomationRunner>();
    builder.Services.AddHostedService<Kroko.Brain.Automation.AutomationScheduler>();
    builder.Services.AddSingleton<IBrainTool, Kroko.Brain.Automation.AutomatizacionCrearTool>();
    builder.Services.AddSingleton<IBrainTool, Kroko.Brain.Automation.AutomatizacionesListarTool>();
    builder.Services.AddSingleton<IBrainTool, Kroko.Brain.Automation.AutomatizacionCambiarTool>();
    builder.Services.AddSingleton<IBrainTool, Kroko.Brain.Automation.AutomatizacionProbarTool>();
    // Internet (búsqueda de Google vía Gemini + lectura de páginas)
    builder.Services.Configure<WebOptions>(builder.Configuration.GetSection(WebOptions.Section));
    builder.Services.AddHttpClient<TavilyClient>(c => c.Timeout = TimeSpan.FromSeconds(40));
    builder.Services.AddHttpClient<BuscarEnInternetTool>(c => c.Timeout = TimeSpan.FromSeconds(60));
    builder.Services.AddHttpClient<LeerPaginaTool>(c => c.Timeout = TimeSpan.FromSeconds(20))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddTransient<IBrainTool>(sp => sp.GetRequiredService<BuscarEnInternetTool>());
    builder.Services.AddTransient<IBrainTool>(sp => sp.GetRequiredService<LeerPaginaTool>());
    builder.Services.AddHttpClient<EmbeddingService>(c => c.Timeout = TimeSpan.FromSeconds(60));

    // Correo (Gmail por IMAP/SMTP con contraseña de aplicación)
    builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));
    builder.Services.AddSingleton<EmailService>();
    builder.Services.AddSingleton<IBrainTool, CorreoCarpetasTool>();
    builder.Services.AddSingleton<IBrainTool, CorreoBuscarTool>();
    builder.Services.AddSingleton<IBrainTool, CorreoLeerTool>();
    builder.Services.AddSingleton<IBrainTool, CorreoMarcarTool>();
    builder.Services.AddSingleton<IBrainTool, CorreoMoverTool>();
    builder.Services.AddSingleton<IBrainTool, CorreoPapeleraTool>();
    builder.Services.AddSingleton<IBrainTool, CorreoEnviarTool>();
    builder.Services.AddSingleton<IBrainTool, CorreoResponderTool>();

    // Conectores MCP (navegador, Notion, Google…) y confirmación explícita
    builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.Section));
    builder.Services.AddSingleton<McpHub>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<McpHub>());
    builder.Services.AddSingleton<IToolSource>(sp => sp.GetRequiredService<McpHub>());
    builder.Services.AddSingleton<IBrainTool, ConfirmarAccionTool>();

    // Voz: transcripción y síntesis con Gemini (sección 6)
    builder.Services.Configure<VoiceOptions>(builder.Configuration.GetSection(VoiceOptions.Section));
    builder.Services.AddHttpClient<GeminiSpeech>(c => c.Timeout = TimeSpan.FromSeconds(60));
    builder.Services.AddTransient<ITtsProvider>(sp => sp.GetRequiredService<GeminiSpeech>());
    builder.Services.AddTransient<ISpeechToText>(sp => sp.GetRequiredService<GeminiSpeech>());

    // Productividad (Fase 2): recordatorios, tareas, hábitos, resumen del día y avisos
    builder.Services.Configure<ProductivityOptions>(builder.Configuration.GetSection(ProductivityOptions.Section));
    builder.Services.AddSingleton<ProductivityStore>();
    builder.Services.AddSingleton<AppConnections>();
    builder.Services.AddSingleton<INotifier, AppNotifier>();
    builder.Services.AddHostedService<ReminderScheduler>();
    builder.Services.Configure<ProactiveOptions>(builder.Configuration.GetSection(ProactiveOptions.Section));
    builder.Services.AddHostedService<ProactiveService>();
    builder.Services.AddHttpClient("weather", c => c.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddSingleton<MyDayService>();
    foreach (var t in new[] { typeof(RecordatorioCrearTool), typeof(RecordatoriosListarTool), typeof(RecordatorioBorrarTool),
                 typeof(TareaCrearTool), typeof(TareasListarTool), typeof(TareaCompletarTool), typeof(TareaBorrarTool),
                 typeof(HabitoCrearTool), typeof(HabitoMarcarTool), typeof(HabitosListarTool), typeof(HabitoBorrarTool),
                 typeof(ResumenDelDiaTool) })
        builder.Services.AddSingleton(typeof(IBrainTool), t);

    // Telegram (Fase 5)
    builder.Services.Configure<Kroko.Brain.Telegram.TelegramOptions>(builder.Configuration.GetSection(Kroko.Brain.Telegram.TelegramOptions.Section));
    builder.Services.AddSingleton<Kroko.Brain.Telegram.TelegramBot>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Kroko.Brain.Telegram.TelegramBot>());
    builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<Kroko.Brain.Telegram.TelegramBot>());
    builder.Services.AddSingleton<ITelegramDelivery>(sp => sp.GetRequiredService<Kroko.Brain.Telegram.TelegramBot>());

    builder.Services.AddSingleton<BrainAuditLog>();
    builder.Services.AddSingleton<AppToolGateway>();
    builder.Services.AddTransient<IToolGateway, CompositeToolGateway>();
    builder.Services.AddTransient<AgentLoop>();
    builder.Services.AddTransient<SessionHandler>();
    builder.Services.AddHostedService<ParentProcessWatcher>();

    // Seguridad del modo servidor (Fase 5): escuchar fuera de esta PC SIN token sería dejar la puerta abierta
    var urls = builder.Configuration["urls"] ?? builder.Configuration["Urls"] ?? builder.Configuration["ASPNETCORE_URLS"] ?? "";
    var token = builder.Configuration["Agent:Token"];
    if (string.IsNullOrWhiteSpace(token) && ServerSafety.ExposesOutsideThisPc(urls))
    {
        Log.Fatal("El cerebro escucha en {Urls} sin token. Define Agent__Token (o KROKO_TOKEN en el .env) antes de exponerlo.", urls);
        return;
    }

    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = KrokoProtocol.HeartbeatInterval });

    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    app.MapGet(KrokoDefaults.HealthPath, () =>
        Results.Ok(new HealthStatus("ok", "Kroko.Brain", version, DateTimeOffset.Now)));

    // Qué proveedores de IA tienen key (sin mostrar las keys)
    app.MapGet("/health/llm", (IConfiguredLlm llm) => Results.Ok(new
    {
        providers = llm.ConfiguredProviders,
        // De dónde salió cada clave (nunca el valor): ayuda a diagnosticar "no tengo API key"
        fuentes = KeySources.Describe(app.Configuration),
    }));

    app.MapMemoryApi();
    app.MapConnectorsApi();
    app.MapSetupApi();
    Kroko.Brain.Apps.GoogleApi.MapGoogleApi(app);
    app.MapMyDayApi();
    app.MapEmbeddingApi();

    app.Map(KrokoProtocol.WebSocketPath, (HttpContext ctx, SessionHandler handler) => handler.HandleAsync(ctx));

    Log.Information("Kroko.Brain {Version} arrancando", version);
    if (dotEnvFile is not null) Log.Information("Claves leídas de {File} (sin mostrar valores)", dotEnvFile);
    foreach (var (name, source) in KeySources.Describe(app.Configuration))
    {
        if (source == "falta") Log.Warning("{Name}: SIN API key (ponla en Configurar Kroko, el .env o user-secrets)", name);
        else Log.Information("{Name}: API key encontrada en {Source}", name, source);
    }
    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Kroko.Brain se cerró por un error inesperado");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Visible para las pruebas de integración.</summary>
public partial class Program;
