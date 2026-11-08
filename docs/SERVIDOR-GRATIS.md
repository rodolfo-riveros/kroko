# El cerebro de Kroko en un servidor gratis (paso a paso)

Tiempo: ~1 hora la primera vez. Costo: S/ 0. Necesitarás una tarjeta solo para verificar tu identidad en Oracle
(no cobra mientras uses lo gratuito).

## Cuentas que necesitas

| Cuenta | Para qué | Dónde |
|---|---|---|
| GitHub | Tu código y las versiones de Kroko (ya la tienes) | https://github.com |
| Oracle Cloud | El servidor gratis donde vive el cerebro | https://www.oracle.com/cloud/free/ |
| DuckDNS | Una dirección gratis para tu servidor (`kroko-tunombre.duckdns.org`) | https://www.duckdns.org |
| Google AI Studio | La clave de Gemini (el cerebro piensa con ella) | https://aistudio.google.com/apikey |
| Telegram (opcional) | Hablarle a Kroko desde el móvil | App de Telegram → @BotFather |

---

## 1. Oracle Cloud: crear la cuenta

1. Entra a https://www.oracle.com/cloud/free/ → **Start for free**.
2. Llena tus datos. **Región de inicio (Home Region):** elige una cercana y con capacidad, por ejemplo
   **Brazil East (São Paulo)** o **Chile Central (Santiago)**. No se puede cambiar después.
3. Verifica tu tarjeta (hacen un cargo temporal de prueba que devuelven).
4. Te dan 30 días de prueba con 300 USD. No te preocupes: lo que crearemos es **"Always Free"** y sigue gratis
   después de los 30 días.
5. **Recomendado:** cuando la cuenta esté lista, ve a **Facturación → Actualizar a Pay As You Go**. Lo gratuito sigue
   costando 0, y Oracle ya no te quitará la máquina por "poco uso".

## 2. Crear la máquina (gratis para siempre)

1. Menú ☰ → **Compute → Instances → Create instance**.
2. **Name:** `kroko`.
3. **Image:** *Change image* → **Canonical Ubuntu 24.04** (o la Ubuntu más nueva).
4. **Shape:** *Change shape* → **Ampere** → **VM.Standard.A1.Flex** → **1 OCPU y 6 GB** de memoria.
   Debe decir **"Always Free-eligible"**. (El límite gratis total es 2 OCPU y 12 GB: no te pases.)
5. **Networking:** deja que cree una red nueva con **IP pública** (Assign a public IPv4 address: Sí).
6. **SSH keys:** *Generate a key pair for me* → **Save private key** (guárdala bien, es tu llave para entrar).
7. **Create**. Cuando diga *Running*, copia la **Public IP address** (por ejemplo `140.238.10.20`).

> Si sale "Out of capacity", prueba más tarde o elige otro *Availability Domain*. Es común en las máquinas gratis.

## 3. Abrir los puertos 80 y 443 en Oracle

1. En tu instancia → **Primary VNIC → Subnet** → **Security Lists** → la lista por defecto.
2. **Add Ingress Rules** (dos reglas):
   - Source CIDR `0.0.0.0/0`, protocolo TCP, **Destination Port 80**.
   - Source CIDR `0.0.0.0/0`, protocolo TCP, **Destination Port 443**.

## 4. Tu dirección gratis con DuckDNS

1. Entra a https://www.duckdns.org e inicia sesión con GitHub o Google.
2. Escribe un nombre, por ejemplo `kroko-rodolfo` → **add domain**.
3. En **current ip** pega la **IP pública** de Oracle → **update ip**.
4. Tu dirección será `kroko-rodolfo.duckdns.org`.

## 5. Entrar al servidor e instalar Docker

En tu PC (PowerShell), con la llave que guardaste:

```powershell
ssh -i C:\ruta\a\tu-llave.key ubuntu@TU_IP_PUBLICA
```

Ya dentro del servidor:

```bash
# Docker
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker ubuntu
exit
```

Vuelve a entrar (`ssh …` otra vez) y abre los puertos también dentro de Ubuntu (las imágenes de Oracle los bloquean):

```bash
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 80 -j ACCEPT
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 443 -j ACCEPT
sudo netfilter-persistent save
```

## 6. Instalar el cerebro de Kroko

```bash
git clone https://github.com/rodolfo-riveros/kroko.git
cd kroko/deploy
cp .env.example .env
openssl rand -hex 32        # copia este resultado: es tu TOKEN
nano .env
```

En `nano` deja así (con tus datos) y guarda con **Ctrl+O, Enter, Ctrl+X**:

```
KROKO_DOMAIN=kroko-rodolfo.duckdns.org
Agent__Token=EL_TOKEN_QUE_GENERASTE
Mcp__PublicBaseUrl=https://kroko-rodolfo.duckdns.org
```

> La imagen del cerebro la publica GitHub automáticamente. La primera vez, en GitHub: tu perfil → **Packages →
> kroko-brain → Package settings → Change visibility → Public**.

Arranca:

```bash
docker compose up -d
docker compose logs -f brain      # Ctrl+C para salir de los registros
```

Prueba en tu navegador: `https://kroko-rodolfo.duckdns.org/health` → debe decir `"status":"ok"` con candado.

## 7. Conectar tu PC

1. En Kroko: clic derecho → **Ajustes → Cerebro**:
   - Dirección: `https://kroko-rodolfo.duckdns.org`
   - Token: el mismo `Agent__Token`
   - Apaga "Arrancar el cerebro en esta PC" → **Guardar y reiniciar Kroko**.
2. Clic derecho → **Configurar Kroko**: tu nombre, la **clave de Gemini** (https://aistudio.google.com/apikey),
   Gmail, Telegram y tus apps. Todo se guarda **cifrado en el servidor**, nunca en GitHub.

## 8. Telegram (opcional)

1. En Telegram busca **@BotFather** → `/newbot` → ponle nombre y usuario (que termine en `bot`) → copia el token.
2. Configurar Kroko → **Telegram** → pega el token → **Vincular mi Telegram** → en el chat pulsa **Iniciar**.

## Actualizaciones

- **Cerebro:** cada vez que subes cambios a GitHub, se publica una imagen nueva y **Watchtower** la instala sola
  en el servidor (revisa cada hora).
- **App de la PC:** crea una versión con `git tag v1.0.1` y `git push origin v1.0.1`; Kroko se actualiza solo.

## Si algo falla

| Problema | Solución |
|---|---|
| `/health` no abre | Revisa los pasos 3 y 5 (puertos en Oracle y en Ubuntu) y que DuckDNS tenga la IP correcta |
| Sin candado / error de certificado | Espera 1–2 minutos; Caddy lo pide solo. Mira `docker compose logs caddy` |
| `docker compose up` no baja la imagen | Haz público el paquete `kroko-brain` en GitHub (paso 6) |
| El cerebro se niega a arrancar | Falta `Agent__Token` en `.env` (por seguridad no arranca sin token) |
| Ver qué pasa | `docker compose logs -f brain` |
