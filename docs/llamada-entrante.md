# Llamada entrante: que Hermes conteste al teléfono

> Estado: **diseño**. Hoy el pipeline es unidireccional (agente → TTS → Asterisk → softphone → audio).
> Esto plantea la dirección inversa: **Alex marca → Asterisk contesta → conversación con Hermes**.

## 1. Qué cambia respecto a lo que ya funciona

| | Hoy (outbound) | Objetivo (inbound) |
|---|---|---|
| Quién inicia | `hav` vía `channel originate` | Alex, marcando una extensión |
| Audio | fichero WAV fijo, `Playback` | flujo continuo, ida y vuelta |
| Turnos | uno solo | bucle: escuchar → pensar → hablar |
| Media | `Playback(custom/hermes)` | PCM crudo por socket (AudioSocket) |
| Interrupción | no existe | barge-in: si hablas, Hermes calla |

El `Playback` del dialplan no sirve para esto: es *half-duplex* y bloquea el canal. Para conversación
real hace falta un canal de audio bidireccional que un proceso externo controle: **AudioSocket**.

## 2. Arquitectura propuesta

```
   Softphone (móvil/LAN)                         host polaper (192.168.1.223)
 ┌────────────────────┐   SIP 5060/udp   ┌──────────────────────────────────────────┐
 │  Zoiper/Linphone   │ ◄──────────────► │  alpefe-asterisk (Asterisk 22, host net) │
 │  marca 1000        │   RTP 10000-     │                                          │
 └────────────────────┘   10199/udp      │  [from-internal]                         │
                                          │   1000 → Answer()                        │
                                          │          AudioSocket(<uuid>,127.0.0.1:9092)
                                          └──────────────────┬───────────────────────┘
                                                             │ TCP, slin 8 kHz mono
                                                             │ 320 B / 20 ms
                                          ┌──────────────────▼───────────────────────┐
                                          │  havd (.NET 10, el mismo stack del CLI)  │
                                          │                                          │
                                          │  VAD ──► STT ──► Hermes ──► TTS ──► cola │
                                          │   ▲        (frases)              │       │
                                          │   └──────── barge-in ◄───────────┘       │
                                          └──────────────────┬───────────────────────┘
                                                             │ HTTP (127.0.0.1)
                                    ┌────────────────────────┼──────────────────────┐
                                    │                        │                      │
                              whisper.cpp /          Hermes agent             Piper / Pocket-TTS
                              faster-whisper         (`hermes -z`, o API)     (WAV → PCM 8 k)
```

### Por qué AudioSocket y no ARI

- **ARI** obliga a un websocket suscrito y a manejar canales/bridges; ya nos mordió (canal que se
  cuelga si no hay app Stasis escuchando). Es la vía "oficial" pero paga un precio alto en complejidad
  para lo que queremos.
- **AudioSocket** es un módulo `extended` que **ya está cargado** en la imagen (`app_audiosocket`,
  `res_audiosocket`, `chan_audiosocket`). El dialplan hace una línea y todo el control vive en tu
  proceso: frames de audio crudo en ambas direcciones. Es lo que usan la mayoría de agentes de voz
  sobre Asterisk.
- **`Record()` + AGI en bucle** es la opción más simple de todas, pero mete pausas de ~1 s por turno
  al abrir/cerrar ficheros y **no permite barge-in**. Sirve como fase 0 para validar el resto de la
  cadena.

## 3. Protocolo AudioSocket (lo justo para implementarlo)

Cada mensaje: **3 bytes de cabecera** (1 byte tipo + 2 bytes big-endian de longitud) + payload.

| Tipo | Byte | Payload |
|---|---|---|
| HANGUP | `0x00` | vacío (`0x00 0x00 0x00`) — cierra la llamada |
| UUID | `0x01` | 16 bytes, el UUID que pasaste en el dialplan |
| DTMF | `0x03` | 1 byte ASCII |
| **AUDIO** | `0x10` | **slin 16-bit, 8 kHz, mono, 320 bytes = 20 ms** |
| ERROR | `0xff` | 1 byte de código |

`app_audiosocket` trabaja en **slin 8 kHz**: el TTS (24 kHz) hay que remuestrear con ffmpeg a
`-ar 8000 -ac 1 -f s16le` y trocear en bloques de exactamente 320 bytes. Ni un byte más: si el
troceado no cuadra, el audio suena a robot con hipo.

## 4. Cambios concretos en Asterisk

`config/extensions.conf` — extensión nueva, sin tocar las existentes:

```
; 1000: Hermes contesta y conversa
exten => 1000,1,Answer()
 same => n,Set(UUID=${SHELL(uuidgen | tr -d '\n')})
 same => n,AudioSocket(${UUID},127.0.0.1:9092)
 same => n,Hangup()

; 1001: misma cosa pero con voz de calidad (slin16) si algún día hace falta
```

`config/pjsip.conf` — **quitar `media_address` fijo** del transport/endpoint cuando haya clientes
fuera de la LAN (ver trampa 1). Para móvil por VPN/Tailscale, el SDP debe anunciar la IP de la
interfaz por la que entra la señalización, no `192.168.1.223`.

Registro del endpoint nuevo: si Alex quiere marcar **desde el móvil**, ese móvil es una extensión más
(p. ej. `102`), con su propio `aor` y `context=from-internal`. No se reutiliza la 100 o la llamada se
la hace a sí misma.

## 5. El daemon (`havd`)

Un servicio systemd de usuario (`havd.service`, enabled + linger, como `vaultd`), escuchando en
`127.0.0.1:9092`. Un hilo por llamada. Máquina de estados:

```
IDLE ──frame audio──► ESCUCHANDO ──silencio 700 ms──► TRANSCRIBIENDO
                          ▲                                  │
                          │                                  ▼
                     barge-in                           PENSANDO (Hermes)
                          │                                  │
                          │                                  ▼
                     HABLANDO ◄──── primera frase TTS ◄── GENERANDO (streaming)
                          │
                     fin de cola / HANGUP ──► ESCUCHANDO
```

Reglas de diseño:

1. **Nunca bloquees la lectura del socket.** Un `Task` lee frames; otro escribe de una cola. Si el
   bucle de lectura se bloquea esperando al LLM, se pierde el barge-in.
2. **VAD por energía primero** (RMS por ventana de 20 ms, umbral calibrado con la primera llamada),
   Silero ONNX después si hace falta precisión. Con auriculares el umbral es trivial; con altavoz no
   (ver trampa 4).
3. **Endpointing**: 600–800 ms de silencio cierran el turno. Menos = cortas al usuario; más = se
   siente lento.
4. **Streaming por frases**: en cuanto el LLM tiene una frase cerrada (`.`/`?`/`,` con ≥ 8 palabras),
   se manda al TTS y se encola el PCM. La primera frase debe sonar antes de que el LLM termine.
5. **Barge-in**: mientras `HABLANDO`, si el VAD detecta voz sostenida > 250 ms, se vacía la cola de
   salida (y se corta el TTS en curso) y se pasa a `ESCUCHANDO`.
5b. **Relleno anti-silencio**: si el pensamiento tarda > 1,5 s, reproducir un "mmm…" / "déjame ver"
   pregrabado. Es gratis y cambia la percepción por completo.
6. **Timeout**: 8 s de silencio → "¿sigues ahí?"; 15 s → HANGUP (`0x00 0x00 0x00`).
7. **Colgar por DTMF** (`*`): llega como frame `0x03`, se atiende al momento.

Consistencia con el CLI actual: `havd` debe exponer un `--selftest` que haga la llamada a sí mismo
(extensión 900 ya existe) y devuelva 0/1 con timings por etapa, igual que `hav autotest`. Sin eso, no
hay forma de saber si sigue vivo sin llamar.

## 6. STT y TTS (local, CPU, 4 cores / 14 GB)

| Pieza | Recomendación | Por qué |
|---|---|---|
| STT | **whisper.cpp** (`small` o `base`, quantizado) o `faster-whisper` int8 | sin Python en el camino caliente si se usa whisper.cpp; español decente; ~1 s por turno de 3-5 s de audio a medir |
| VAD | energía + **Silero ONNX** (opcional) | evita mandar silencio al STT |
| TTS | **Piper** (`es_ES-davefx-medium`) para el bucle; Pocket-TTS para calidad | Piper va a RTF muy bajo en CPU; Pocket-TTS medido en este repo: 8,5 s para 16,6 s de audio (RTF ≈ 0,5) — demasiado lento para conversar |

Presupuesto de latencia por turno (objetivo < 2,5 s hasta la primera palabra):

```
fin de habla → endpointing 0,7 s → STT 0,8 s → LLM 1ª frase 0,6 s → TTS 1ª frase 0,3 s
                                                                    ≈ 2,4 s
```

Todo lo que pase de ahí se percibe como "está pensando". El `hermes -z` medido hoy tarda **4,8 s**
(arranque de proceso incluido), así que para el bucle hace falta una de estas dos:

- **A**: `hermes -z --resume voz` por turno → simple, mantiene contexto entre llamadas, pero ~4-5 s.
- **B**: un pequeño wrapper HTTP que mantenga una sesión de agente caliente y exponga
  `POST /turn {text}` → `text/event-stream` con las frases. Es lo que da latencia conversacional.
  `hermes proxy` (OpenAI-compatible) sirve solo si no necesitas herramientas.

Recomendación: empezar con **A** para validar la cadena completa, y migrar a **B** cuando duela.

## 7. Llamar desde fuera de casa

El repo ya avisa: SIP/RTP son UDP y **el túnel de Cloudflare no los transporta**. Tres caminos:

| Opción | Cómo | Veredicto |
|---|---|---|
| **VPN (Tailscale o WireGuard)** | móvil con la VPN → softphone apunta a `100.x.y.z` (o la IP del túnel WG) puerto 5060 | ✅ **Recomendada**. Cero puertos abiertos, cifrado, sobrevive a CGNAT, y el RTP también va por el túnel. |
| **WebRTC + WSS** | SIP.js en una página servida por `cloudflared`; señalización por 443 | ⚠️ La señalización pasa, el **media SRTP/UDP no** → haría falta un TURN (coturn) con `turns:443`. Complejo para lo que aporta. |
| **Abrir 5060/udp + RTP en el router** | port forward | ❌ Fraude telefónico en horas. Solo con fail2ban + allowlist del operador, y aun así no. |

Con Tailscale, además, el móvil **se queda registrado** y entonces el sentido outbound (`hav call`)
también funciona estés donde estés: Hermes te llama al móvil. Los dos sentidos salen gratis.

## 8. Trampas (previsibles, con la causa)

1. **`media_address=192.168.1.223` mata el audio por VPN.** El SDP anuncia una IP que el móvil remoto
   no alcanza → audio en una sola dirección. Quitarlo del endpoint o poner un endpoint/transport
   distinto para clientes remotos.
2. **Formato**: `app_audiosocket` = slin 8 kHz. Si mandas 24 kHz "porque suena mejor", se oye a
   velocidad de chipmunk. Resamplear y trocear a 320 B.
3. **El socket TCP es la llamada.** Si el proceso cierra el socket, Asterisk cuelga. Al revés: si el
   daemon no está escuchando en 9092, `AudioSocket()` falla y la llamada muere en silencio (mirar el
   log, no el softphone).
4. **Eco del altavoz**: el VAD oye a Hermes por el altavoz y se interrumpe a sí mismo. Con auriculares
   no pasa. Con altavoz: half-duplex al principio (no escuchar mientras habla) o AEC de verdad.
5. **El contenedor no ve `127.0.0.1` como tú crees**: con `network_mode: host` sí lo ve, pero si algún
   día se cambia a bridge, `127.0.0.1` dentro del contenedor es el propio contenedor → usar
   `host.docker.internal` o la IP del host.
6. **`dialplan reload` + bind-mount**: la trampa 2 del README sigue vigente. Reiniciar el contenedor
   tras editar `extensions.conf` si el editor reescribió el inodo.
7. **Logs en UTC** en el contenedor, host en CET. Para correlacionar turnos, timestamps UTC.
8. **`Record()` + AGI** parece más simple, pero cada `RECORD FILE` abre y cierra fichero: 300-800 ms
   por turno y sin barge-in. Vale para la fase 0, no para el producto.
9. **No metas el audio de la llamada en `sounds/custom/`**: nombre fijo y compartido entre llamadas
   concurrentes. Buffers en memoria o `/tmp` por UUID.

## 9. Plan por fases (cada una con su verificación)

| Fase | Qué | Verificación (criterio de "hecho") |
|---|---|---|
| **0** · 1 h | Extensión `1000` con `Record` + `Playback` + script AGI que llama a `hermes -z` | Marcas 1000, hablas, oyes una respuesta coherente. Sin barge-in, con pausas. |
| **1** · 1 tarde | `havd` con AudioSocket: VAD por energía + STT + Hermes + TTS, sin barge-in | Conversación de 3 turnos seguidos; log con `stt_ms`, `llm_ms`, `tts_ms` por turno. |
| **2** | Barge-in + TTS por frases + relleno | Interrumpir a media frase funciona; primer audio < 2,5 s. |
| **3** | Tailscale + extensión móvil `102` | Llamada desde datos móviles, sin abrir puertos. |
| **4** | `havd --selftest` + `havd.service` enabled | `systemctl --user status havd` verde y selftest exit 0 tras reiniciar el host. |

## 10. Lo que ya está verificado en este host

- Asterisk **22.11.0** sano, `transport-udp` activo, ARI en `127.0.0.1:8088`.
- Módulos presentes y en ejecución: `app_audiosocket`, `chan_audiosocket`, `res_audiosocket`,
  `app_stasis`, `res_speech`, `chan_rtp`.
- `hermes -z "..."`: **4,8 s** de pared (incluye arranque de proceso).
- Pocket-TTS vivo en `:8000` (responde 422 a un POST vacío, que es lo correcto).
- Host: 4 cores, 14 GB RAM (9 GB disponibles). Suficiente para whisper `small` + Piper.

---

Siguiente paso cuando toque: fase 0 (una hora, sin dependencias nuevas) y medir los tres tiempos
reales antes de decidir si el STT va en C# (sherpa-onnx) o por proceso (whisper.cpp).
