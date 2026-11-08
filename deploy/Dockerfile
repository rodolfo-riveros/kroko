# Kroko.Brain para servidor (Fase 5). Construir desde la raíz del repo:
#   docker build -f deploy/Dockerfile -t kroko-brain .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Kroko.Shared/Kroko.Shared.csproj src/Kroko.Shared/
COPY src/Kroko.Brain/Kroko.Brain.csproj src/Kroko.Brain/
RUN dotnet restore src/Kroko.Brain/Kroko.Brain.csproj
COPY src/Kroko.Shared/ src/Kroko.Shared/
COPY src/Kroko.Brain/ src/Kroko.Brain/
RUN dotnet publish src/Kroko.Brain/Kroko.Brain.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Todo lo que Kroko guarda (memoria SQLite, auditoría, logs, permisos de conectores, buzón) va a /data
RUN mkdir -p /data && chown -R app:app /data
# "Urls" (sin prefijo) manda sobre el "Urls" de appsettings.json, que es para la PC (localhost:5080)
ENV ASPNETCORE_URLS=http://+:8080 \
    Urls=http://+:8080 \
    XDG_DATA_HOME=/data \
    KROKO_DISABLE_DOTENV=1 \
    Mcp__Servers__navegador__Enabled=false
VOLUME /data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Kroko.Brain.dll"]
