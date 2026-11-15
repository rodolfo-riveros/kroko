namespace Kroko.Brain.Productivity;

/// <summary>
/// Avisos automáticos que el dueño eligió en el panel (14/11/2026): se guardan en la tabla kv y mandan sobre
/// appsettings. Sin elección guardada, valen los valores por defecto (reuniones y correos importantes sí;
/// tareas vencidas y resumen del día, no).
/// </summary>
public static class NoticePreferences
{
    public static bool? Get(ProductivityStore store, string key) => store.GetValue("avisos." + key) switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    public static void Set(ProductivityStore store, string key, bool on) => store.SetValue("avisos." + key, on ? "1" : "0");

    public static int MeetingMinutes(ProductivityStore s, ProactiveOptions o) => Get(s, "reuniones") switch
    {
        false => 0,
        true => o.MeetingMinutesBefore > 0 ? o.MeetingMinutesBefore : 10,
        null => o.MeetingMinutesBefore,
    };

    public static bool Emails(ProductivityStore s, ProactiveOptions o) => Get(s, "correos") ?? o.ImportantEmails;

    public static int OverdueHour(ProductivityStore s, ProactiveOptions o) => Get(s, "vencidas") switch
    {
        false => -1,
        true => o.OverdueHour >= 0 ? o.OverdueHour : 10,
        null => o.OverdueHour,
    };

    public static int SummaryHour(ProductivityStore s, ProductivityOptions o) => Get(s, "resumen") switch
    {
        false => -1,
        true => o.DailySummaryHour >= 0 ? o.DailySummaryHour : 8,
        null => o.DailySummaryHour,
    };
}
