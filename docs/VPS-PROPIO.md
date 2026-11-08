# El cerebro de Kroko en tu propio VPS (con los puertos 80/443 ya ocupados)

Si tu VPS ya tiene una web (Nginx, Apache, un panel como CloudPanel, aaPanel, Plesk…), **no hace falta liberar
los puertos**. El cerebro se queda escondido dentro del servidor en un puerto interno (por defecto `8090`, solo
`127.0.0.1`) y tu servidor web actual le pasa el tráfico con HTTPS, igual que a tus otras webs.

```
Kroko (tu PC) ──HTTPS/WebSocket──► Nginx/Apache del VPS (443) ──► 127.0.0.1:8090 ──► contenedor del cerebro
```

Necesitas: acceso SSH con `sudo`, Docker, y un **subdominio** (ej. `kroko.tudominio.com`) o uno gratis de DuckDNS.

---

## 1. Ver qué ocupa los puertos y elegir uno libre

```bash
sudo ss -tlnp | grep -E ':(80|443)\s'     # te dice si es nginx, apache2, caddy, traefik, docker-proxy…
sudo ss -tln | grep ':8090'               # si no sale nada, 8090 está libre
```

Si `8090` está ocupado, elige otro (8091, 8095…) y úsalo en todos los pasos siguientes.

## 2. Docker (si aún no lo tienes)

```bash
docker --version || (curl -fsSL https://get.docker.com | sudo sh)
sudo usermod -aG docker $USER      # luego sal (exit) y vuelve a entrar por SSH
```

## 3. El subdominio

En el DNS de tu dominio crea un registro **A**: `kroko` → la IP de tu VPS.
(Sin dominio: crea uno en https://www.duckdns.org y pon la IP del VPS.)

## 4. Arrancar el cerebro

```bash
git clone https://github.com/rodolfo-riveros/kroko.git
cd kroko/deploy
cp .env.example .env
openssl rand -hex 32        # copia el resultado: es tu TOKEN
nano .env
```

Deja así (con tus datos) y guarda con **Ctrl+O, Enter, Ctrl+X**:

```
KROKO_DOMAIN=kroko.tudominio.com
KROKO_PORT=8090
Agent__Token=EL_TOKEN_QUE_GENERASTE
Mcp__PublicBaseUrl=https://kroko.tudominio.com
```

```bash
docker compose -f docker-compose.vps.yml up -d
curl http://127.0.0.1:8090/health        # debe responder {"status":"ok",…}
```

> Usa siempre `-f docker-compose.vps.yml`. El `docker-compose.yml` normal trae Caddy y chocaría con tus puertos.

## 5. Conectar tu servidor web (elige el tuyo)

Lo importante en cualquiera: pasar el **WebSocket** (`/ws`), porque la App habla con el cerebro por ahí.

### Opción A — Nginx

```bash
sudo nano /etc/nginx/sites-available/kroko
```

```nginx
server {
    listen 80;
    server_name kroko.tudominio.com;

    client_max_body_size 20m;

    location / {
        proxy_pass http://127.0.0.1:8090;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 3600s;     # conversaciones largas por WebSocket
        proxy_send_timeout 3600s;
        proxy_buffering off;
    }
}
```

```bash
sudo ln -s /etc/nginx/sites-available/kroko /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d kroko.tudominio.com      # pone el HTTPS y lo renueva solo
```

### Opción B — Apache

```bash
sudo a2enmod proxy proxy_http proxy_wstunnel headers rewrite
sudo nano /etc/apache2/sites-available/kroko.conf
```

```apache
<VirtualHost *:80>
    ServerName kroko.tudominio.com
    ProxyPreserveHost On
    ProxyTimeout 3600
    ProxyPass        / http://127.0.0.1:8090/ upgrade=websocket
    ProxyPassReverse / http://127.0.0.1:8090/
    RequestHeader set X-Forwarded-Proto "https" env=HTTPS
</VirtualHost>
```

```bash
sudo a2ensite kroko && sudo apache2ctl configtest && sudo systemctl reload apache2
sudo apt install -y certbot python3-certbot-apache
sudo certbot --apache -d kroko.tudominio.com
```

(`upgrade=websocket` necesita Apache 2.4.47 o más nuevo: compruébalo con `apache2 -v`.)

### Opción C — Un panel (CloudPanel, aaPanel, Plesk, HestiaCP…)

Crea un sitio **"Reverse proxy"** para `kroko.tudominio.com` hacia `http://127.0.0.1:8090`, activa
**WebSocket** si el panel lo pregunta y emite el certificado **Let's Encrypt** desde el mismo panel.

### Opción D — Ya usas Caddy o Traefik

- **Caddy:** añade a tu Caddyfile `kroko.tudominio.com { reverse_proxy 127.0.0.1:8090 }` y recarga.
- **Traefik:** quita la línea `ports:` del cerebro, conéctalo a la red de Traefik y ponle las etiquetas de router
  con tu `certresolver`.

## 6. Probar y conectar la App

1. En el navegador: `https://kroko.tudominio.com/health` → `"status":"ok"` con candado.
2. En Kroko: clic derecho → **Ajustes → Cerebro** → dirección `https://kroko.tudominio.com` y el **token** →
   **Guardar y reiniciar Kroko**. (La App instalada te lo pide sola la primera vez.)
3. Clic derecho → **Configurar Kroko**: tu nombre, clave de Gemini, Gmail, Telegram y tus apps (se guardan cifradas
   en el servidor).

## Si algo falla

| Problema | Solución |
|---|---|
| `curl 127.0.0.1:8090/health` no responde | `docker compose -f docker-compose.vps.yml logs brain`. Si dice que falta el token, revisa `Agent__Token` en `.env` |
| `/health` abre pero la App queda "sin conexión" | Falta pasar el WebSocket: revisa `Upgrade`/`Connection` (Nginx) o `upgrade=websocket` / `proxy_wstunnel` (Apache) |
| Error 502 Bad Gateway | El puerto de `proxy_pass` no coincide con `KROKO_PORT`, o el contenedor está detenido |
| Se corta a los 60 s | Faltan los `proxy_read_timeout` / `ProxyTimeout` de arriba |
| Certbot falla | El registro A del subdominio aún no apunta al VPS (espera unos minutos) o el puerto 80 está cerrado en el firewall |
| Usas Cloudflare con nube naranja | Activa **WebSockets** en Cloudflare → Network, y SSL en modo **Full (strict)** |

Actualizar: el cerebro se actualiza solo (Watchtower revisa cada hora). Para forzarlo:
`docker compose -f docker-compose.vps.yml pull && docker compose -f docker-compose.vps.yml up -d`.
