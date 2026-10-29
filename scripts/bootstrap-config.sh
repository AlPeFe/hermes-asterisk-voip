#!/usr/bin/env bash
# bootstrap-config.sh — genera la configuración local de Asterisk a partir de los
# ficheros .example, con contraseñas nuevas y la IP del host ya puesta.
#
#   ./scripts/bootstrap-config.sh [IP_DEL_HOST]
#
# Deja las contraseñas en credentials.txt (0600). NO versiones nada de esto:
# ya está en .gitignore.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
IP=${1:-$(ip -4 -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1)}

[ -n "$IP" ] || { echo "no pude detectar la IP; pásala como argumento" >&2; exit 1; }

P100=$(openssl rand -hex 6)
P101=$(openssl rand -hex 6)
PARI=$(openssl rand -hex 12)

mkdir -p "$ROOT/config" "$ROOT/sounds/custom" "$ROOT/logs" "$ROOT/spool/monitor"

# pjsip.conf: una contraseña por extensión
sed -e "s|<HOST_LAN_IP>|$IP|g" "$ROOT/config/pjsip.conf.example" > "$ROOT/config/pjsip.conf"
python3 - "$ROOT/config/pjsip.conf" "$P100" "$P101" <<'PY'
import sys, pathlib
path, p100, p101 = sys.argv[1], sys.argv[2], sys.argv[3]
p = pathlib.Path(path)
text = p.read_text()
lines = text.splitlines()
seen = 0
for i, line in enumerate(lines):
    if line.startswith('password=CHANGE_ME_'):
        lines[i] = 'password=' + (p100 if seen == 0 else p101)
        seen += 1
p.write_text("\n".join(lines) + "\n")
if seen != 2:
    raise SystemExit(f"esperaba 2 contraseñas, encontré {seen}")
PY

sed -e "s|CHANGE_ME_ARI|$PARI|g" "$ROOT/config/ari.conf.example" > "$ROOT/config/ari.conf"

umask 077
cat > "$ROOT/credentials.txt" <<EOF
# Credenciales locales — generadas $(date '+%F %T') por scripts/bootstrap-config.sh
# NO versionar (está en .gitignore).
extension 100: usuario 100   contraseña $P100
extension 101: usuario 101   contraseña $P101
ARI:           usuario hermes contraseña $PARI

Softphone: servidor $IP  puerto 5060  UDP  usuario 100  contraseña (arriba)
EOF
chmod 600 "$ROOT/credentials.txt" "$ROOT/config/pjsip.conf" "$ROOT/config/ari.conf"

echo "✅ Configuración creada para la IP $IP"
echo "   config/pjsip.conf  config/ari.conf  credentials.txt (0600)"
echo "   siguiente: docker compose up -d"
