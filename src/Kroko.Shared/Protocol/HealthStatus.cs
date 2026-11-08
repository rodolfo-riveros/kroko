namespace Kroko.Shared.Protocol;

/// <summary>
/// Respuesta del endpoint /health del Brain. La App la usa para saber si el cerebro está vivo.
/// </summary>
public sealed record HealthStatus(string Status, string Service, string Version, DateTimeOffset Time);
