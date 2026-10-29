# hermes-asterisk-voip

Pipeline de voz para LAN: **un agente (Hermes) escribe texto → un TTS lo convierte en voz →
Asterisk llama por VoIP a un softphone → al descolgar, suena el mensaje**.

Prueba de concepto que funciona de punta a punta en una red local, sin nube y sin tocar el router:
la orquestación está en **.NET 10**, la centralita es **Asterisk 22** en Docker (su propio código,
en C, no se modifica) y la síntesis de voz es un servicio HTTP externo (Pocket-TTS).

```
   ┌────────────┐   1. texto     ┌──────────────┐   2. WAV 24 kHz   ┌─────────────┐
   │  Hermes    │ ─────────────► │  Pocket-TTS  │ ─────────────────► │   ffmpeg    │
   │ (o un CLI) │                │  :8000/api   │                    │  8 kHz mono │
   └────────────┘                └──────────────┘                    └──────┬──────┘
                                                                            │ 3. sounds/custom/hermes.wav
                                                                            ▼
   ┌────────────┐   5. RTP       ┌──────────────┐   4. originate    ┌─────────────┐
   │  Softphone │ ◄───────────── │   Asterisk   │ ◄──────────────── │  hav (CLI)  │
   │  (MicroSIP)│   audio        │  22 (Docker) │  channel originate└─────────────┘
   └────────────┘                └──────────────┘
```

## Por qué así

- **El TTS es un servicio HTTP, no una librería**: el pipeline no sabe ni le importa qué motor
  sintetiza. Cambiar de voz o de motor es cambiar una URL.
- **La orquestación es un CLI con código de salida**: cada etapa se verifica y el proceso devuelve
  0 solo si **todas** pasan. Eso lo hace usable a mano, desde un cron o desde un agente.
- **Asterisk intacto**: nada de forks ni módulos. Se configura con sus ficheros (`pjsip.conf`,
  `extensions.conf`) y se controla con su CLI (`asterisk -rx`).
- **Modo `autotest` sin teléfono**: una extensión del dialplan contesta sola, graba lo que suena y
  se analiza el volumen. Así el pipeline se puede validar en CI o en un host sin softphone.

## Etapas y salida real

```
$ hav autotest "Hola Alex. Esta es la prueba automática del pipeline de voz."
═══ PIPELINE DE VOZ (autotest) ═══
  ✅ 1 texto — 63 caracteres
  ✅ 2+3 TTS+audio — 16.64 s @8000 Hz · tts 8521 ms · ffmpeg 33 ms
  ✅ 4 asterisk — contenedor healthy y transport-udp activo
  ✅ 5 llamada — canal 900@prueba contestado y grabado (4089 ms)
  ✅ 6 audio real — 16.62 s grabados · -24.6 dB (hay voz)
──────────────────────────────────────────────
RESULTADO: ✅ PIPELINE OK — 29464 ms
```

Y la llamada real, con el softphone descolgando:

```
$ hav call 100 "Hola, soy Hermes, te llamo desde tu centralita"
  ✅ 5 llamada — ext 100 descolgó y empezó a sonar (4210 ms)
  ✅ 6 reproducción — audio de 23.28 s entregado a 100
```

`hav --json` devuelve lo mismo en JSON, para consumirlo desde otro proceso.

## Puesta en marcha

### 1. Asterisk

```bash
cp config/pjsip.conf.example config/pjsip.conf      # ajusta IPs y extensiones
cp config/ari.conf.example  config/ari.conf         # pon una contraseña
# genera contraseñas SIP y sustitúyelas en pjsip.conf
openssl rand -hex 6     # una por extensión
docker compose up -d
```

El `compose.yaml` usa `network_mode: host` a propósito: con red bridge, Asterisk anunciaría en el
SDP la IP del bridge (172.x) y los softphones devolverían el audio a una dirección inalcanzable →
**audio en una sola dirección**. Con red host ve la IP real del host.

### 2. Softphone

Cualquier softphone SIP (MicroSIP, Zoiper, Linphone) apuntando a:

```
Servidor:  <IP del host>
Puerto:    5060
Transporte: UDP
Usuario / Auth ID: 100
Contraseña: la que pusiste en pjsip.conf
```

Números de prueba del dialplan incluido:

| Marca | Qué hace |
|---|---|
| `100`, `101` | llamar a la otra extensión |
| `600` | eco (te oyes a ti mismo) |
| `700` | `hello-world` de Asterisk |
| `800` | el audio generado por el pipeline |

### 3. TTS

Cualquier servicio que acepte `POST` multipart con `text` y `voice` y devuelva un WAV. El de esta
prueba es [Pocket-TTS](https://github.com/kyutai-labs/pocket-tts) servido con FastAPI en
`:8000/api/tts`. Si usas otro, apunta `HAV_TTS_URL` a él.

### 4. El CLI

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet build src/HermesAsteriskVoip -c Release
dotnet run --project src/HermesAsteriskVoip -- call 100 "Hola"
# o el binario:
./src/HermesAsteriskVoip/bin/Release/net10.0/hav call 100 "Hola"
```

## Configuración

`appsettings.json` (sin secretos) + variables de entorno que lo sobrescriben:

| Variable | Por defecto | Qué hace |
|---|---|---|
| `HAV_HOME` | `~/apps/asterisk` | directorio con `sounds/custom`, `logs`, `spool` |
| `HAV_TTS_URL` | `http://127.0.0.1:8000/api/tts` | endpoint del TTS |
| `HAV_VOICE` | `lola` | voz solicitada al TTS |
| `HAV_FFMPEG` | autodetectado | ruta al binario de ffmpeg |
| `HAV_CONTAINER` | `alpefe-asterisk` | contenedor de Asterisk |
| `HAV_TIMEOUT` | `40` | segundos de espera al descolgar |
| `HAV_CONFIG` | — | `appsettings.json` alternativo |
| `HAV_DEBUG` | — | `1` imprime la configuración efectiva |

## Cómo se verifica (y por qué así)

| Etapa | Verificación real |
|---|---|
| TTS | HTTP 200 + fichero no vacío |
| audio | cabecera RIFF leída en el propio código: duración y frecuencia |
| Asterisk | `docker ps` con `healthy` + `pjsip show transports` con `transport-udp` |
| llamada | el log del contenedor contiene `Playing 'custom/hermes` |
| audio real (autotest) | `ffmpeg volumedetect` sobre la grabación: **voz ≈ -25 dB, silencio ≈ -91 dB** |

Nada de "debería funcionar": cada etapa se comprueba contra el sistema real.

## Trampas conocidas

1. **Los canales creados por ARI se cuelgan solos.** `POST /ari/channels?app=<x>` mete el canal en
   Stasis; si no hay un websocket suscrito a esa app, Asterisk lo tira
   (`Stasis app 'x' doesn't exist`). Por eso el pipeline origina con
   `asterisk -rx "channel originate …"`, que va directo al dialplan.
2. **Editar un `.conf` montado con bind-mount no llega al contenedor** si el editor reescribe el
   fichero con rename: cambia el inode y el mount sigue viendo el viejo. `dialplan reload` responde
   OK y el contexto "no existe". Solución: reiniciar el contenedor.
3. **`media_address` es opción de *endpoint*, no de *transport*.** En `type=transport` da
   `Could not find option suitable for category 'transport-udp' named 'media_address'` y el
   transporte entero no carga (nada escuchando en 5060).
4. **`pjsip reload` no crea transportes**: se construyen al cargar el módulo. Reiniciar.
5. **`docker logs --since` no acepta epoch** (`1793…s`): hay que pasar RFC3339 UTC. Con epoch, el
   `grep` no encuentra nada y el pipeline reporta un falso "no descolgó".
6. **No montar `/var/lib/asterisk` entero**: oculta los sonidos core de la imagen y
   `Playback(hello-world)` queda mudo. Montar solo `sounds/custom`.
7. Los logs del contenedor van en **UTC**; el host puede estar en otra zona.

## Seguridad

- Esto es **LAN-only**. SIP y RTP son UDP y un túnel tipo Cloudflare (TCP/HTTP) no los transporta:
  para exponerlo a internet hay que abrir `5060/udp` y el rango RTP en el router **y** protegerlo
  (allowlist del operador, contraseñas fuertes, fail2ban). Un 5060 abierto sin más recibe intentos
  de fraude telefónico en horas.
- Los ficheros con contraseñas (`config/pjsip.conf`, `config/ari.conf`) están en `.gitignore`.
  El repo solo trae `.example`.
- El audio del TTS se escribe en `sounds/custom/` con un nombre fijo: no lo alimentes con datos de
  terceros sin querer.

## Licencia

MIT.
