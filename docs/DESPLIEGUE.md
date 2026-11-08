# Llevar el cerebro de Kroko a un servidor

La App de escritorio se queda en tu PC (es la que tiene las "manos"). Lo que se va al servidor es
**Kroko.Brain**: la IA, la memoria, los recordatorios, el correo, Notion y Telegram. Así Kroko te
avisa en el móvil aunque la PC esté apagada.

## Lo que necesitas

- Un servidor Linux con **Docker** (1 GB de RAM sobra). Gratis: **Oracle Cloud Always Free** (ARM o AMD) o Google Cloud e2-micro.
- Un **dominio** apuntando a la IP del servidor (registro DNS tipo **A**). Gratis: **DuckDNS** (`kroko-tunombre.duckdns.org`).
- Tus claves (Gemini obligatoria; Gmail, Telegram, Trello, Google… opcionales): se ponen después desde la App, en **Configurar Kroko**.

## Paso a paso

1. **Copia la carpeta `deploy` al servidor** (o clona el repo: `git clone https://github.com/rodolfo-riveros/kroko`).
2. **Prepara la configuración:**
   ```bash
   cd deploy
   cp .env.example .env
   nano .env
   ```
   Rellena `KROKO_DOMAIN`, `Agent__Token` (genera uno con `openssl rand -hex 32`) y `Mcp__PublicBaseUrl` (https://tu-dominio).
   Las claves de IA y cuentas NO hace falta ponerlas aquí: se ponen desde la App.
3. **Arráncalo:**
   ```bash
   docker compose up -d
   ```
   Usa la imagen que publica GitHub (`ghcr.io/rodolfo-riveros/kroko-brain`). La primera vez, en GitHub ve a tu perfil →
   **Packages → kroko-brain → Package settings → Change visibility → Public** (o haz `docker login ghcr.io` en el servidor).
   **Watchtower** revisa cada hora y actualiza el cerebro solo cuando subes cambios. Caddy pide el certificado HTTPS solo. Comprueba: `https://tu-dominio/health` debe responder `"status":"ok"`.
4. **En la PC**, abre Kroko → clic derecho → **Ajustes → Cerebro**:
   - Dirección: `https://tu-dominio`
   - Token: el mismo `Agent__Token` del servidor
   - Desmarca "Arrancar el cerebro local"
   - **Guardar y reiniciar Kroko**.
5. **Tus datos y cuentas:** clic derecho → **Configurar Kroko**. Lo que escribas ahí se guarda en el
   volumen `/data` del servidor (archivo `Kroko/secretos.dat`, solo legible por el usuario del contenedor).
6. **Conectores:** clic derecho → Conectores → **Conectar** en Notion. El permiso se abre en el navegador
   de tu PC y vuelve al servidor solo.

## Telegram (avisos al móvil y órdenes desde allí)

1. En Telegram, habla con **@BotFather** → `/newbot` → copia el token.
2. En la PC: clic derecho en Kroko → **Configurar Kroko** → paso **Telegram** → pega el token → **Vincular mi Telegram**.
3. Se abre el chat con tu bot: pulsa **Iniciar** (o mándale el código de 6 cifras). Listo: desde entonces solo tú puedes usarlo.
   No hace falta reiniciar nada: el bot se reinicia solo al cambiar el token.

Puedes escribirle o mandarle **notas de voz**. Lo peligroso te lo pregunta con botones "Sí, hazlo" / "Cancelar".
Lo que toca la PC (abrir programas, archivos…) solo funciona si Kroko está abierto en la PC; si no, te lo dice.
Si no quieres que las aprobaciones de Telegram valgan para acciones en la PC, desactiva
`Kroko:AllowRemoteApprovals` en la App.

## Seguridad

- El cerebro **se niega a arrancar** si escucha fuera de la máquina y no tiene `Agent__Token`.
- Todo lo que guarda (memoria, auditoría, permisos de conectores) va al volumen `kroko-data` (`/data`).
- El conector **navegador** no está disponible en el servidor (necesita un escritorio); el resto sí.
- Copia de seguridad: `docker run --rm -v deploy_kroko-data:/data -v $PWD:/backup alpine tar czf /backup/kroko-data.tgz /data`

## Actualizar

```bash
git pull
cd deploy && docker compose up -d --build
```
