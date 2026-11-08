#!/usr/bin/env bash
# Varias personas en el mismo servidor, cada una con SU cerebro de Kroko (memoria, claves y cuentas aisladas).
# El token de cada persona es su "cuenta": sin él nadie entra a su cerebro.
#
#   ./persona.sh nueva <nombre> <dominio>   crea el cerebro de <nombre> en <nombre>.<dominio> (p. ej. ana kroko-pol.duckdns.org)
#   ./persona.sh lista                      quién hay, en qué puerto y si está encendido
#   ./persona.sh datos <nombre>             la dirección y el token que esa persona pone en Kroko
#   ./persona.sh logs <nombre>              ver qué pasa en su cerebro (Ctrl+C para salir)
#   ./persona.sh parar|iniciar <nombre>     apagar o encender su cerebro (sus datos se quedan)
#   ./persona.sh actualizar                 bajar la última versión del cerebro para todos ya mismo
#   ./persona.sh borrar <nombre>            quitar su cerebro (pregunta si borrar también sus datos)
set -euo pipefail

cd "$(dirname "$0")"
DIR=personas
COMPOSE=docker-compose.persona.yml
FIRST_PORT=8091

die() { echo "✗ $*" >&2; exit 1; }
need_name() {
  [[ "${1:-}" =~ ^[a-z][a-z0-9-]{1,23}$ ]] || die "Nombre inválido: usa minúsculas, números o guiones (ej. ana, luis, pol)."
}
env_of() { echo "$DIR/$1.env"; }
get() { grep -E "^$2=" "$(env_of "$1")" | head -1 | cut -d= -f2-; }
dc() { # dc <nombre> <args…>
  local n=$1; shift
  KROKO_PERSONA=$n KROKO_PORT=$(get "$n" KROKO_PORT) docker compose -p "kroko-$n" -f "$COMPOSE" "$@"
}

port_in_use() {
  local p=$1
  grep -qsx "KROKO_PORT=$p" "$DIR"/*.env 2>/dev/null && return 0
  if command -v ss >/dev/null; then ss -tln | grep -q ":$p\b" && return 0; fi
  if command -v netstat >/dev/null; then netstat -an 2>/dev/null | grep -qE "[:.]$p\s.*LISTEN" && return 0; fi
  return 1
}
free_port() {
  local p=$FIRST_PORT
  while port_in_use "$p"; do p=$((p + 1)); done
  echo "$p"
}
new_token() {
  if command -v openssl >/dev/null; then openssl rand -hex 32; else head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n'; fi
}

ensure_watchtower() {
  [[ -n "${KROKO_SIN_WATCHTOWER:-}" ]] && return 0 # pruebas
  docker compose -p kroko-watchtower -f docker-compose.watchtower.yml up -d >/dev/null
}

print_access() {
  local n=$1
  echo
  echo "  Persona:   $n"
  echo "  Dirección: https://$(get "$n" KROKO_DOMAIN)"
  echo "  Token:     $(get "$n" Agent__Token)"
  echo
  echo "  En su PC: instalar Kroko → Ajustes → Cerebro → pegar dirección y token → Comprobar → Guardar y reiniciar."
  echo "  Luego: clic derecho → Configurar Kroko → SU clave de Gemini (https://aistudio.google.com/apikey)."
  echo "  Pásale el token por un canal privado (no por un grupo)."
}

write_nginx() {
  local n=$1 domain port
  domain=$(get "$n" KROKO_DOMAIN); port=$(get "$n" KROKO_PORT)
  cat > "$DIR/$n.nginx.conf" <<EOF
# Cerebro de Kroko de "$n" → copia a /etc/nginx/sites-available/kroko-$n y actívalo (ver docs/VPS-PROPIO.md)
server {
    listen 80;
    server_name $domain;
    client_max_body_size 20m;

    location / {
        proxy_pass http://127.0.0.1:$port;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
        proxy_buffering off;
    }
}
EOF
}

cmd=${1:-}
case "$cmd" in
  nueva)
    n=${2:-}; base=${3:-}
    need_name "$n"
    [[ -n "$base" ]] || die "Falta el dominio. Ej.: ./persona.sh nueva $n kroko-pol.duckdns.org"
    [[ -e "$(env_of "$n")" ]] && die "$n ya existe. Mira sus datos con: ./persona.sh datos $n"
    mkdir -p "$DIR"; chmod 700 "$DIR"
    port=$(free_port)
    domain="$n.$base"
    umask 077
    cat > "$(env_of "$n")" <<EOF
# Cerebro de Kroko de "$n". NUNCA subas este archivo a git (contiene su token).
KROKO_PERSONA=$n
KROKO_PORT=$port
KROKO_DOMAIN=$domain
Agent__Token=$(new_token)
Mcp__PublicBaseUrl=https://$domain
# Sus claves (Gemini, Gmail, Telegram…) las pone ÉL/ELLA desde Configurar Kroko: se guardan cifradas en su volumen.
EOF
    chmod 600 "$(env_of "$n")"
    write_nginx "$n"
    ensure_watchtower
    dc "$n" up -d
    echo -n "Arrancando el cerebro de $n"
    for _ in $(seq 1 30); do
      if curl -fsS "http://127.0.0.1:$port/health" >/dev/null 2>&1; then echo " ✓"; break; fi
      echo -n "."; sleep 2
    done
    curl -fsS "http://127.0.0.1:$port/health" >/dev/null 2>&1 || echo " (aún no responde: ./persona.sh logs $n)"
    print_access "$n"
    echo
    echo "  Falta (una vez): que $domain apunte a este servidor y su HTTPS."
    echo "    Nginx:  sudo cp $DIR/$n.nginx.conf /etc/nginx/sites-available/kroko-$n"
    echo "            sudo ln -s /etc/nginx/sites-available/kroko-$n /etc/nginx/sites-enabled/"
    echo "            sudo nginx -t && sudo systemctl reload nginx && sudo certbot --nginx -d $domain"
    echo "    Caddy:  $domain { reverse_proxy 127.0.0.1:$port }"
    ;;

  lista)
    shopt -s nullglob
    files=("$DIR"/*.env)
    [[ ${#files[@]} -gt 0 ]] || { echo "Todavía no hay nadie. Crea la primera: ./persona.sh nueva <nombre> <dominio>"; exit 0; }
    printf "%-14s %-6s %-40s %s\n" PERSONA PUERTO DIRECCIÓN ESTADO
    for f in "${files[@]}"; do
      n=$(basename "$f" .env)
      port=$(get "$n" KROKO_PORT)
      state=$(curl -fsS -m 3 "http://127.0.0.1:$port/health" >/dev/null 2>&1 && echo encendido || echo apagado)
      printf "%-14s %-6s %-40s %s\n" "$n" "$port" "https://$(get "$n" KROKO_DOMAIN)" "$state"
    done
    ;;

  datos)
    n=${2:-}; need_name "$n"; [[ -e "$(env_of "$n")" ]] || die "No existe $n."
    print_access "$n"
    ;;

  logs)
    n=${2:-}; need_name "$n"; [[ -e "$(env_of "$n")" ]] || die "No existe $n."
    dc "$n" logs -f --tail 100 brain
    ;;

  parar|iniciar)
    n=${2:-}; need_name "$n"; [[ -e "$(env_of "$n")" ]] || die "No existe $n."
    if [[ $cmd == parar ]]; then dc "$n" stop; else dc "$n" up -d; fi
    ;;

  actualizar)
    shopt -s nullglob
    for f in "$DIR"/*.env; do n=$(basename "$f" .env); dc "$n" pull -q && dc "$n" up -d; done
    echo "✓ Todos los cerebros con la última versión."
    ;;

  borrar)
    n=${2:-}; need_name "$n"; [[ -e "$(env_of "$n")" ]] || die "No existe $n."
    read -r -p "¿Borrar también TODOS los datos de $n (memoria, claves, conversación)? No se pueden recuperar. Escribe su nombre para borrarlos, o Enter para conservarlos: " answer
    if [[ "$answer" == "$n" ]]; then
      dc "$n" down -v
      rm -f "$(env_of "$n")" "$DIR/$n.nginx.conf"
      echo "✓ Cerebro y datos de $n borrados."
    else
      dc "$n" down
      echo "✓ Cerebro de $n detenido. Sus datos siguen guardados (vuelve con: ./persona.sh iniciar $n)."
    fi
    ;;

  *)
    sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
    ;;
esac
