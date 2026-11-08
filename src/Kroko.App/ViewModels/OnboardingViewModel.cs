using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kroko.App.Face;
using Kroko.App.Services;
using Microsoft.Extensions.Logging;

namespace Kroko.App.ViewModels;

public sealed record TimeZoneChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class OnboardingStep(int index, string title) : ObservableObject
{
    public int Index { get; } = index;
    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }
}

/// <summary>
/// Asistente "Configurar Kroko": la primera vez que Kroko se instala pregunta quién es su dueño y le ayuda a
/// conectar sus cuentas (IA, Gmail, Telegram, conectores). Todo se puede saltar y repetir más tarde.
/// Las claves viajan una sola vez al Brain, que las guarda cifradas; aquí nunca se muestran ni se guardan.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject, IDisposable
{
    public const int WelcomeStep = 0, ProfileStep = 1, AiStep = 2, MailStep = 3, TelegramStep = 4, ExtrasStep = 5, DoneStep = 6;

    private readonly BrainApiClient _api;
    private readonly WindowService _windows;
    private readonly BrainConnection _brain;
    private readonly ILogger<OnboardingViewModel> _log;
    private readonly DispatcherTimer _telegramPoll;

    public OnboardingViewModel(BrainApiClient api, WindowService windows, BrainConnection brain, ExpressionLibrary library,
        ILogger<OnboardingViewModel> log)
    {
        _api = api;
        _windows = windows;
        _brain = brain;
        _log = log;
        Face = new FaceAnimator(library);

        string[] titles = ["Bienvenida", "Sobre ti", "Cerebro (IA)", "Correo", "Telegram", "Tus apps", "¡Listo!"];
        for (var i = 0; i < titles.Length; i++) Steps.Add(new OnboardingStep(i, titles[i]));

        TimeZones = BuildTimeZones();
        SelectedTimeZone = DetectTimeZone();
        Country = RegionInfo.CurrentRegion.NativeName is { Length: > 0 } c ? Capitalize(c) : "";
        Accent = AccentFor(RegionInfo.CurrentRegion.TwoLetterISORegionName);

        _telegramPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _telegramPoll.Tick += async (_, _) => await PollTelegramAsync();

        GoTo(WelcomeStep);
    }

    /// <summary>La ventana se cierra (lo pide el ViewModel al terminar o con "Más tarde").</summary>
    public event Action? CloseRequested;

    public FaceAnimator Face { get; }

    public ObservableCollection<OnboardingStep> Steps { get; } = [];

    public IReadOnlyList<TimeZoneChoice> TimeZones { get; }

    public ObservableCollection<string> Accents { get; } =
        ["peruano", "mexicano", "argentino", "colombiano", "chileno", "venezolano", "ecuatoriano", "boliviano",
         "uruguayo", "español de España", "latinoamericano neutro"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(NextText), nameof(IsWelcome), nameof(IsProfile), nameof(IsAi),
        nameof(IsMail), nameof(IsTelegram), nameof(IsExtras), nameof(IsDone))]
    public partial int CurrentStep { get; set; }

    public bool IsWelcome => CurrentStep == WelcomeStep;
    public bool IsProfile => CurrentStep == ProfileStep;
    public bool IsAi => CurrentStep == AiStep;
    public bool IsMail => CurrentStep == MailStep;
    public bool IsTelegram => CurrentStep == TelegramStep;
    public bool IsExtras => CurrentStep == ExtrasStep;
    public bool IsDone => CurrentStep == DoneStep;

    public bool CanGoBack => CurrentStep > WelcomeStep && CurrentStep < DoneStep;

    public string NextText => CurrentStep switch
    {
        WelcomeStep => "Empecemos",
        DoneStep => "Terminar",
        _ => "Siguiente",
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Mensaje del paso actual (verde si salió bien, rojo si no).</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    [ObservableProperty]
    public partial bool BrainOnline { get; set; }

    // ------------------------------------------------------------ paso: sobre ti

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Greeting))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Greeting))]
    public partial string CallMe { get; set; } = "";

    [ObservableProperty]
    public partial string City { get; set; } = "";

    [ObservableProperty]
    public partial string Country { get; set; } = "";

    [ObservableProperty]
    public partial TimeZoneChoice? SelectedTimeZone { get; set; }

    [ObservableProperty]
    public partial string Accent { get; set; } = "";

    [ObservableProperty]
    public partial string About { get; set; } = "";

    public string Greeting
    {
        get
        {
            var nick = CallMe.Trim().Length > 0 ? CallMe.Trim() : Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrEmpty(nick) ? "¡Todo listo!" : $"¡Todo listo, {nick}!";
        }
    }

    // ------------------------------------------------------------ paso: cerebro

    [ObservableProperty]
    public partial string GeminiKey { get; set; } = "";

    [ObservableProperty]
    public partial string DeepSeekKey { get; set; } = "";

    [ObservableProperty]
    public partial string TavilyKey { get; set; } = "";

    [ObservableProperty]
    public partial string GeminiState { get; set; } = "";

    [ObservableProperty]
    public partial string DeepSeekState { get; set; } = "";

    [ObservableProperty]
    public partial string TavilyState { get; set; } = "";

    // ------------------------------------------------------------ paso: correo

    [ObservableProperty]
    public partial string MailAddress { get; set; } = "";

    [ObservableProperty]
    public partial string MailPassword { get; set; } = "";

    [ObservableProperty]
    public partial string MailState { get; set; } = "";

    // ------------------------------------------------------------ paso: Telegram

    [ObservableProperty]
    public partial string TelegramToken { get; set; } = "";

    [ObservableProperty]
    public partial string TelegramState { get; set; } = "";

    [ObservableProperty]
    public partial string PairingCode { get; set; } = "";

    [ObservableProperty]
    public partial string PairingLink { get; set; } = "";

    [ObservableProperty]
    public partial bool TelegramLinked { get; set; }

    // ------------------------------------------------------------ servidor (cerebro en la nube)

    [ObservableProperty]
    public partial bool ShowServerSetup { get; set; }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "https://";

    [ObservableProperty]
    public partial string ServerToken { get; set; } = "";

    /// <summary>Guarda la dirección y el token del cerebro y reinicia Kroko para conectarse.</summary>
    [RelayCommand]
    private void SaveServer()
    {
        var url = ServerUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            Fail("Escribe la dirección completa, por ejemplo https://kroko-tunombre.duckdns.org");
            return;
        }
        if (!uri.IsLoopback && uri.Scheme != "https")
        {
            Fail("Un cerebro en internet tiene que usar https (con candado).");
            return;
        }
        if (!uri.IsLoopback && ServerToken.Trim().Length < 16)
        {
            Fail("Falta el token del servidor (el valor de Agent__Token en su .env).");
            return;
        }
        var k = UserSettings.LoadKrokoSection();
        k["BrainUrl"] = url;
        k["BrainToken"] = ServerToken.Trim();
        k["LaunchLocalBrain"] = uri.IsLoopback;
        UserSettings.SaveKrokoSection(k);
        AppRestart.Now();
    }

    // ------------------------------------------------------------ paso: tus apps (Trello, Google)

    [ObservableProperty]
    public partial string TrelloKey { get; set; } = "";

    [ObservableProperty]
    public partial string TrelloToken { get; set; } = "";

    [ObservableProperty]
    public partial string TrelloState { get; set; } = "";

    [ObservableProperty]
    public partial string GoogleClientId { get; set; } = "";

    [ObservableProperty]
    public partial string GoogleClientSecret { get; set; } = "";

    [ObservableProperty]
    public partial string GoogleState { get; set; } = "";

    [ObservableProperty]
    public partial bool GoogleConnected { get; set; }

    private DispatcherTimer? _googlePoll;

    private void ApplyGoogle(GoogleStatusDto g)
    {
        GoogleConnected = g.Conectado;
        GoogleState = g switch
        {
            { Conectado: true } => $"✓ Conectado{(g.Correo is { } c ? " con " + c : "")}: Tasks, Calendar y Drive.",
            { Cliente: true } => "Cliente de Google listo. Pulsa «Conectar mi cuenta de Google».",
            _ => "Necesita un cliente OAuth de tu proyecto de Google Cloud (pasos abajo).",
        };
    }

    /// <summary>Abre la página de Trello para crear el token (necesita la clave).</summary>
    [RelayCommand]
    private void OpenTrelloToken()
    {
        var key = TrelloKey.Trim();
        if (key.Length < 20)
        {
            Fail("Primero pega tu clave de Trello (la de 32 caracteres) y luego pulsa aquí.");
            return;
        }
        OpenUrl($"https://trello.com/1/authorize?expiration=never&scope=read,write&response_type=token&name=Kroko&key={Uri.EscapeDataString(key)}");
    }

    [RelayCommand]
    private async Task SaveTrelloAsync()
    {
        IsBusy = true;
        try
        {
            var values = new Dictionary<string, string>();
            if (TrelloKey.Trim().Length > 0) values["Trello:ApiKey"] = TrelloKey.Trim();
            if (TrelloToken.Trim().Length > 0) values["Trello:Token"] = TrelloToken.Trim();
            if (values.Count > 0 && await _api.SaveSecretsAsync(values) is { } error)
            {
                Fail(error);
                return;
            }
            TrelloKey = TrelloToken = "";
            var r = await _api.TestAsync("trello");
            if (r.Ok) Ok(r.Mensaje);
            else Fail(r.Mensaje);
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Fail("Mi cerebro no responde ahora mismo.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectGoogleAsync()
    {
        IsBusy = true;
        try
        {
            var values = new Dictionary<string, string>();
            if (GoogleClientId.Trim().Length > 0) values["Google:ClientId"] = GoogleClientId.Trim();
            if (GoogleClientSecret.Trim().Length > 0) values["Google:ClientSecret"] = GoogleClientSecret.Trim();
            if (values.Count > 0 && await _api.SaveSecretsAsync(values) is { } error)
            {
                Fail(error);
                return;
            }
            GoogleClientId = GoogleClientSecret = "";
            var (url, err) = await _api.StartGoogleConnectAsync();
            if (url is null)
            {
                Fail(err ?? "No pude empezar la conexión con Google.");
                return;
            }
            OpenUrl(url);
            Ok("Se abrió Google en tu navegador: elige tu cuenta y pulsa «Permitir». Te espero aquí…");

            // Se comprueba cada 2 s durante 3 minutos si ya dio permiso
            var until = DateTime.Now.AddMinutes(3);
            _googlePoll?.Stop();
            _googlePoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _googlePoll.Tick += async (_, _) =>
            {
                if (DateTime.Now > until) _googlePoll?.Stop();
                try
                {
                    var g = await _api.GetGoogleStatusAsync();
                    if (!g.Conectado) return;
                    _googlePoll?.Stop();
                    ApplyGoogle(g);
                    Ok("¡Google conectado! Ya puedo ver tus tareas, tu calendario y tu Drive.");
                    Face.Play("love");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
            };
            _googlePoll.Start();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Fail("Mi cerebro no responde ahora mismo.");
        }
        finally
        {
            IsBusy = false;
        }
    }


    public ObservableCollection<string> Summary { get; } = [];

    // ------------------------------------------------------------ carga inicial

    public async Task StartAsync()
    {
        Face.Play("waking");
        await RefreshAsync(loadProfile: true);
    }

    private async Task RefreshAsync(bool loadProfile = false)
    {
        try
        {
            if (loadProfile)
            {
                var profile = await _api.GetProfileAsync();
                if (profile.Completo) ApplyProfile(profile.Perfil);
            }
            var status = await _api.GetSetupAsync();
            ApplyStatus(status);
            BrainOnline = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            BrainOnline = false;
            _log.LogInformation("Asistente: el cerebro aún no responde ({Message})", ex.Message);
        }
    }

    private void ApplyProfile(ProfileDto p)
    {
        Name = p.Nombre;
        CallMe = p.ComoLlamarte;
        City = p.Ciudad;
        if (p.Pais.Length > 0) Country = p.Pais;
        if (p.Acento.Length > 0)
        {
            if (!Accents.Contains(p.Acento)) Accents.Add(p.Acento);
            Accent = p.Acento;
        }
        About = p.SobreMi;
        if (p.ZonaHoraria.Length > 0 && FindTimeZone(p.ZonaHoraria) is { } tz) SelectedTimeZone = tz;
    }

    private void ApplyStatus(SetupStatus s)
    {
        string Source(string key) => s.Fuentes.TryGetValue(key, out var src) && src != "falta" ? $" (desde {src})" : "";
        GeminiState = s.Gemini ? "✓ Ya tengo una clave de Gemini" + Source("gemini") + ". Escribe otra solo si quieres cambiarla." : "Falta: sin esta clave no puedo pensar.";
        DeepSeekState = s.Deepseek ? "✓ Configurada" + Source("deepseek") : "Opcional: respaldo si Gemini falla.";
        TavilyState = s.Tavily ? "✓ Configurada" + Source("tavily") : "Opcional: mejora las búsquedas y la lectura de páginas.";
        if (s.Correo.Configurado)
        {
            MailState = $"✓ Conectado con {s.Correo.Direccion}. Escribe otra contraseña solo si quieres cambiarla.";
            if (MailAddress.Length == 0) MailAddress = s.Correo.Direccion;
        }
        else
        {
            MailState = "Opcional: así puedo leer, buscar y (con tu permiso) enviar correos.";
        }
        ApplyTelegram(s.Telegram);
        TrelloState = s.Trello ? "✓ Trello conectado. Escribe otra clave solo si quieres cambiarla." : "Tableros, listas y tarjetas.";
        ApplyGoogle(s.Google);
    }

    private void ApplyTelegram(TelegramStatusDto t)
    {
        TelegramLinked = t.Vinculado;
        TelegramState = t switch
        {
            { Vinculado: true } => $"✓ Vinculado con {t.Bot}. Ya puedes escribirme desde el móvil.",
            { Conectado: true } => $"Bot {t.Bot} conectado. Falta vincular tu chat (paso 2).",
            { Token: true } => "Tengo un token, pero el bot no conecta: revisa que esté bien copiado.",
            _ => "Opcional: para hablarme desde el móvil y recibir avisos.",
        };
    }

    // ------------------------------------------------------------ navegación

    /// <summary>Solo para --snapshot: muestra un paso sin validar.</summary>
    internal void Preview(int step) => GoTo(step);

    private void GoTo(int step)
    {
        CurrentStep = step;
        foreach (var s in Steps)
        {
            s.IsCurrent = s.Index == step;
            if (s.Index < step) s.IsDone = true;
        }
        Message = "";
        Face.Play(step switch
        {
            WelcomeStep => "happy",
            ProfileStep => "listening",
            AiStep => "thinking",
            MailStep => "focus",
            TelegramStep => "idea",
            ExtrasStep => "confident",
            _ => "success",
        });
        if (step == TelegramStep) _telegramPoll.Start();
        else _telegramPoll.Stop();
        if (step == DoneStep) BuildSummary();
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack) GoTo(CurrentStep - 1);
    }

    [RelayCommand]
    private void Skip()
    {
        if (CurrentStep is > ProfileStep and < DoneStep) GoTo(CurrentStep + 1);
    }

    [RelayCommand]
    private void Later() => CloseRequested?.Invoke();

    private bool CanNext() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync()
    {
        IsBusy = true;
        try
        {
            var ok = CurrentStep switch
            {
                WelcomeStep => await CheckBrainAsync(),
                ProfileStep => await SaveProfileAsync(),
                AiStep => await SaveAiAsync(),
                MailStep => await SaveMailAsync(),
                TelegramStep => await SaveTelegramTokenAsync(onlyIfTyped: true),
                DoneStep => Finish(),
                _ => true,
            };
            if (ok && CurrentStep < DoneStep) GoTo(CurrentStep + 1);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> CheckBrainAsync()
    {
        await RefreshAsync(loadProfile: true);
        if (BrainOnline) return true;
        ShowServerSetup = true;
        Fail(_brain.IsConnected
            ? "Mi cerebro no contesta a la configuración. Revisa el token en Ajustes → Cerebro."
            : "Mi cerebro todavía está arrancando. Espera unos segundos y vuelve a intentarlo.");
        return false;
    }

    private async Task<bool> SaveProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Fail("¿Cómo te llamas? Es lo único obligatorio.");
            return false;
        }
        var error = await _api.SaveProfileAsync(new ProfileDto
        {
            Nombre = Name.Trim(),
            ComoLlamarte = CallMe.Trim(),
            Ciudad = City.Trim(),
            Pais = Country.Trim(),
            ZonaHoraria = SelectedTimeZone?.Id ?? "",
            Acento = Accent.Trim(),
            SobreMi = About.Trim(),
        });
        if (error is not null)
        {
            Fail(error);
            return false;
        }
        return true;
    }

    private async Task<bool> SaveAiAsync()
    {
        var values = new Dictionary<string, string>();
        if (GeminiKey.Trim().Length > 0) values["Llm:Gemini:ApiKey"] = GeminiKey.Trim();
        if (DeepSeekKey.Trim().Length > 0) values["Llm:DeepSeek:ApiKey"] = DeepSeekKey.Trim();
        if (TavilyKey.Trim().Length > 0) values["Web:TavilyApiKey"] = TavilyKey.Trim();

        if (values.Count > 0 && await _api.SaveSecretsAsync(values) is { } error)
        {
            Fail(error);
            return false;
        }
        GeminiKey = DeepSeekKey = TavilyKey = "";

        var status = await _api.GetSetupAsync();
        if (!status.Gemini && !status.Deepseek)
        {
            ApplyStatus(status);
            Fail("Necesito al menos la clave de Gemini (o la de DeepSeek) para poder pensar.");
            return false;
        }

        // Se prueba lo que haya: si una clave falla, se avisa y se queda en este paso
        var problems = new List<string>();
        foreach (var (name, has) in new[] { ("gemini", status.Gemini), ("deepseek", status.Deepseek), ("tavily", status.Tavily) })
        {
            if (!has) continue;
            var r = await _api.TestAsync(name);
            if (!r.Ok) problems.Add($"{Pretty(name)}: {r.Mensaje}");
        }
        ApplyStatus(status);
        if (problems.Count > 0)
        {
            Fail(string.Join("\n", problems));
            return false;
        }
        return true;
    }

    private async Task<bool> SaveMailAsync()
    {
        var address = MailAddress.Trim();
        var password = MailPassword.Replace(" ", "").Trim();
        if (address.Length == 0 && password.Length == 0) return true; // se salta

        if (address.Length > 0 && !address.Contains('@'))
        {
            Fail("Ese correo no parece válido.");
            return false;
        }
        var values = new Dictionary<string, string>();
        if (address.Length > 0) values["Email:Address"] = address;
        if (password.Length > 0) values["Email:AppPassword"] = password;
        if (await _api.SaveSecretsAsync(values) is { } error)
        {
            Fail(error);
            return false;
        }
        MailPassword = "";

        Say("Probando tu Gmail…");
        var r = await _api.TestAsync("correo");
        await RefreshAsync();
        if (!r.Ok)
        {
            Fail(r.Mensaje + "\nSi el problema sigue, crea una contraseña de aplicación nueva.");
            return false;
        }
        return true;
    }

    [RelayCommand]
    private async Task TestMailAsync()
    {
        IsBusy = true;
        try
        {
            if (await SaveMailAsync()) Ok("¡Gmail conectado!");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> SaveTelegramTokenAsync(bool onlyIfTyped)
    {
        var token = TelegramToken.Trim();
        if (token.Length == 0) return onlyIfTyped;
        if (!token.Contains(':'))
        {
            Fail("Ese token no parece de BotFather (tiene la forma 123456789:ABC…).");
            return false;
        }
        if (await _api.SaveSecretsAsync(new Dictionary<string, string> { ["Telegram:BotToken"] = token }) is { } error)
        {
            Fail(error);
            return false;
        }
        TelegramToken = "";
        return true;
    }

    [RelayCommand]
    private async Task LinkTelegramAsync()
    {
        IsBusy = true;
        try
        {
            if (!await SaveTelegramTokenAsync(onlyIfTyped: true)) return;

            // El bot tarda unos segundos en reiniciarse con el token nuevo
            Say("Conectando con tu bot…");
            (PairingDto? Pairing, string? Error) result = (null, null);
            for (var i = 0; i < 8; i++)
            {
                result = await _api.StartTelegramPairingAsync();
                if (result.Pairing is not null) break;
                await Task.Delay(1500);
            }
            if (result.Pairing is not { } p)
            {
                Fail(result.Error ?? "No pude conectar el bot.");
                await RefreshAsync();
                return;
            }
            PairingCode = p.Codigo;
            PairingLink = p.Enlace;
            Ok($"Abre {p.Bot} en Telegram y pulsa «Iniciar» (o mándale el código {p.Codigo}). Caduca en 10 minutos.");
            OpenUrl(p.Enlace);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Fail("Mi cerebro no responde ahora mismo.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PollTelegramAsync()
    {
        if (PairingCode.Length == 0 || TelegramLinked) return;
        try
        {
            var t = await _api.GetTelegramStatusAsync();
            if (!t.Vinculado) return;
            ApplyTelegram(t);
            PairingCode = PairingLink = "";
            Ok("¡Vinculado! Ya te escribí en Telegram.");
            Face.Play("love");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // se reintenta en el siguiente tic
        }
    }

    [RelayCommand]
    private void OpenPairingLink()
    {
        if (PairingLink.Length > 0) OpenUrl(PairingLink);
    }

    [RelayCommand]
    private void OpenConnectors() => _windows.ShowConnectors();

    [RelayCommand]
    private static void OpenLink(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)) OpenUrl(url);
    }

    private void BuildSummary()
    {
        Summary.Clear();
        _ = Task.Run(async () =>
        {
            try
            {
                var s = await _api.GetSetupAsync();
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    Summary.Add((s.PerfilCompleto ? "✓ " : "· ") + "Sé quién eres");
                    Summary.Add((s.Gemini || s.Deepseek ? "✓ " : "· ") + "Cerebro de IA" + (s.Tavily ? " + búsqueda Tavily" : ""));
                    Summary.Add((s.Correo.Configurado ? "✓ Gmail: " + s.Correo.Direccion : "· Gmail: sin conectar"));
                    Summary.Add(s.Telegram.Vinculado ? "✓ Telegram vinculado" : "· Telegram: sin vincular");
                    Summary.Add(s.Trello ? "✓ Trello conectado" : "· Trello: sin conectar");
                    Summary.Add(s.Google.Conectado ? "✓ Google: " + (s.Google.Correo ?? "conectado") : "· Google (Tasks, Calendar, Drive): sin conectar");
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _log.LogDebug(ex, "No se pudo armar el resumen del asistente");
            }
        });
    }

    private bool Finish()
    {
        CloseRequested?.Invoke();
        return false;
    }

    // ------------------------------------------------------------ utilidades

    private void Fail(string text)
    {
        MessageIsError = true;
        Message = text;
        Face.Play("oops");
    }

    private void Ok(string text)
    {
        MessageIsError = false;
        Message = text;
    }

    private void Say(string text)
    {
        MessageIsError = false;
        Message = text;
    }

    private static string Pretty(string name) => name switch
    {
        "gemini" => "Gemini",
        "deepseek" => "DeepSeek",
        "tavily" => "Tavily",
        _ => name,
    };

    private static void OpenUrl(string url)
    {
        // Solo enlaces web: nunca se abre otra cosa desde aquí
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Serilog.Log.Warning("No se pudo abrir {Url}: {Message}", url, ex.Message);
        }
    }

    private static IReadOnlyList<TimeZoneChoice> BuildTimeZones() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(z => new TimeZoneChoice(TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : z.Id, z.DisplayName))
            .DistinctBy(z => z.Id)
            .ToList();

    private TimeZoneChoice? DetectTimeZone()
    {
        var local = TimeZoneInfo.Local;
        var id = TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, RegionInfo.CurrentRegion.TwoLetterISORegionName, out var iana) ? iana : local.Id;
        return FindTimeZone(id) ?? TimeZones.FirstOrDefault(z => z.Label == local.DisplayName);
    }

    private TimeZoneChoice? FindTimeZone(string id)
    {
        var exact = TimeZones.FirstOrDefault(z => z.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        // Otra ciudad con la misma zona (America/Lima vs America/Bogota): vale la de la lista
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
            return TimeZones.FirstOrDefault(z => z.Label == tz.DisplayName);
        return null;
    }

    private static string AccentFor(string region) => region.ToUpperInvariant() switch
    {
        "PE" => "peruano",
        "MX" => "mexicano",
        "AR" => "argentino",
        "CO" => "colombiano",
        "CL" => "chileno",
        "VE" => "venezolano",
        "EC" => "ecuatoriano",
        "BO" => "boliviano",
        "UY" => "uruguayo",
        "ES" => "español de España",
        _ => "latinoamericano neutro",
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    public void Dispose()
    {
        _telegramPoll.Stop();
        _googlePoll?.Stop();
    }
}
