using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HermesAsteriskVoip;

public static class Program
{
    private sealed record Stage(string Name, bool Ok, string Detail, long Ms);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 2;
        }

        var json = args.Contains("--json");
        var cfg = PipelineConfig.Load();
        if (Environment.GetEnvironmentVariable("HAV_DEBUG") == "1")
            Console.Error.WriteLine($"[config] home={cfg.Home} lab={cfg.LabTtsUrl} contenedor={cfg.Asterisk.ContainerName}");

        string mode = args[0];
        string? ext = null, text = null;
        bool reuse = false;

        switch (mode)
        {
            case "call":
                if (args.Length < 2) { PrintUsage(); return 2; }
                ext = args[1];
                if (args.Length >= 3) text = args[2];
                reuse = text == "--reuse" || text == "--repetir";
                if (!reuse && string.IsNullOrWhiteSpace(text)) { PrintUsage(); return 2; }
                break;
            case "autotest":
                text = args.Length >= 2 ? args[1] : null;
                if (string.IsNullOrWhiteSpace(text)) { PrintUsage(); return 2; }
                break;
            default:
                PrintUsage();
                return 2;
        }

        var stages = mode == "call"
            ? await RunCallAsync(cfg, ext!, text, reuse)
            : await RunAutotestAsync(cfg, text!);

        return Report(mode, ext, stages, json);
    }

    // ─────────────────────────────────────────────────────────────── call ────
    private static async Task<List<Stage>> RunCallAsync(PipelineConfig cfg, string ext, string? text, bool reuse)
    {
        var stages = new List<Stage>();
        stages.Add(TextStage(text, reuse));

        var audio = await AudioStageAsync(cfg, text, reuse);
        stages.Add(audio.Stage);
        if (!audio.Stage.Ok) return stages;

        stages.Add(AsteriskStage(cfg));

        // Comprobación previa: si la extensión no tiene contacto registrado, el
        // originate tarda 40 s en fallar. Mejor decirlo al instante.
        var contacts = Shell.AsteriskCli(cfg.Asterisk.ContainerName, "pjsip show contacts").Stdout;
        if (!contacts.Contains($"Contact:  {ext}/") && !contacts.Contains($"Contact: {ext}/"))
        {
            stages.Add(new Stage("5 llamada", false,
                $"la extensión {ext} no está registrada (sin contacto en Asterisk) — registra el softphone primero", 0));
            return stages;
        }

        var sw = Stopwatch.StartNew();
        var marca = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        Shell.AsteriskCli(cfg.Asterisk.ContainerName,
            $"channel originate PJSIP/{ext} extension {cfg.Asterisk.PlayExtension}@{cfg.Asterisk.DialContext}");

        var playingToken = $"Playing 'custom/{cfg.SoundName}";
        bool playing = false;
        for (var i = 0; i < cfg.Asterisk.AnswerTimeoutSeconds; i++)
        {
            await Task.Delay(1000);
            var logs = Shell.DockerLogsSince(cfg.Asterisk.ContainerName, marca).Stdout;
            if (logs.Contains(playingToken))
            {
                playing = true;
                break;
            }
            // El softphone se cayó entre la comprobación y la llamada: no esperar.
            if (logs.Contains("Failed to create outgoing session") || logs.Contains("Could not create dialog"))
            {
                stages.Add(new Stage("5 llamada", false,
                    $"Asterisk no pudo marcar {ext}: el endpoint dejó de estar registrado", sw.ElapsedMilliseconds));
                return stages;
            }
        }

        if (!playing)
        {
            Shell.AsteriskCli(cfg.Asterisk.ContainerName, "channel request hangup all");
            stages.Add(new Stage("5 llamada", false,
                $"no descolgó en {cfg.Asterisk.AnswerTimeoutSeconds} s (¿softphone {ext} registrado?)", sw.ElapsedMilliseconds));
            return stages;
        }

        stages.Add(new Stage("5 llamada", true, $"ext {ext} descolgó y empezó a sonar ({sw.ElapsedMilliseconds} ms)", sw.ElapsedMilliseconds));

        // Esperar el final del audio (Hangup en el dialplan) sin bloquear de más.
        var extra = TimeSpan.FromSeconds(audio.Seconds + 15);
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < extra)
        {
            await Task.Delay(1000);
            if (Shell.DockerLogsSince(cfg.Asterisk.ContainerName, marca).Stdout.Contains("Hangup")) break;
        }

        stages.Add(new Stage("6 reproducción", true,
            $"audio de {audio.Seconds:0.##} s entregado a {ext}", waited.ElapsedMilliseconds));
        return stages;
    }

    // ───────────────────────────────────────────────────────────── autotest ──
    private static async Task<List<Stage>> RunAutotestAsync(PipelineConfig cfg, string text)
    {
        var stages = new List<Stage> { TextStage(text, false) };

        var audio = await AudioStageAsync(cfg, text, false);
        stages.Add(audio.Stage);
        if (!audio.Stage.Ok) return stages;

        stages.Add(AsteriskStage(cfg));

        var recording = Path.Combine(cfg.RecordingsDir, cfg.Asterisk.RecordingFile);
        try { if (File.Exists(recording)) File.Delete(recording); } catch { /* sin permiso: seguimos */ }

        var sw = Stopwatch.StartNew();
        Shell.AsteriskCli(cfg.Asterisk.ContainerName,
            $"channel originate Local/{cfg.Asterisk.AutotestExtension} application Wait {Math.Ceiling(audio.Seconds) + 10}");

        for (var i = 0; i < audio.Seconds + 25; i++)
        {
            await Task.Delay(1000);
            if (File.Exists(recording) && new FileInfo(recording).Length > 0) break;
        }

        if (!File.Exists(recording) || new FileInfo(recording).Length == 0)
        {
            stages.Add(new Stage("5 llamada", false, "no se generó la grabación", sw.ElapsedMilliseconds));
            return stages;
        }

        stages.Add(new Stage("5 llamada", true,
            $"canal {cfg.Asterisk.AutotestExtension} contestado y grabado ({sw.ElapsedMilliseconds} ms)", sw.ElapsedMilliseconds));

        // Esperar a que la grabación deje de crecer y no queden canales.
        long prev = -1;
        for (var i = 0; i < audio.Seconds + 30; i++)
        {
            await Task.Delay(1000);
            long cur = new FileInfo(recording).Length;
            var activos = ActiveChannels(cfg);
            if (cur == prev && activos == 0) break;
            prev = cur;
        }

        var vol = await MeanVolumeAsync(cfg, recording);
        var info = Wav.Info(recording);
        if (vol is null)
        {
            stages.Add(new Stage("6 audio real", false, "no se pudo medir el volumen con ffmpeg", 0));
        }
        else if (vol.Value > cfg.Asterisk.SilenceThresholdDb)
        {
            stages.Add(new Stage("6 audio real", true,
                $"{info.Seconds:0.##} s grabados · {vol.Value:0.#} dB (hay voz)", 0));
        }
        else
        {
            stages.Add(new Stage("6 audio real", false,
                $"{info.Seconds:0.##} s · {vol.Value:0.#} dB (por debajo de {cfg.Asterisk.SilenceThresholdDb:0.#} dB: silencio)", 0));
        }
        return stages;
    }

    // ────────────────────────────────────────────────────────────── stages ───
    private static Stage TextStage(string? text, bool reuse) => reuse
        ? new Stage("1 texto", true, "se reutiliza el audio existente", 0)
        : new Stage("1 texto", true, $"{text!.Length} caracteres", 0);

    private sealed record AudioResult(Stage Stage, double Seconds);

    private static async Task<AudioResult> AudioStageAsync(PipelineConfig cfg, string? text, bool reuse)
    {
        Directory.CreateDirectory(cfg.SoundsDir);

        if (reuse || string.IsNullOrWhiteSpace(text))
        {
            if (!File.Exists(cfg.WavPath))
                return new AudioResult(new Stage("3 audio", false, "no hay audio previo y se pidió reutilizar", 0), 0);
            var existing = Wav.Info(cfg.WavPath);
            return new AudioResult(new Stage("3 audio", true, $"{existing.Seconds:0.##} s @{existing.Rate} Hz (existente)", 0), existing.Seconds);
        }

        // ── 2. TTS contra el servicio del lab ────────────────────────────────
        var sw = Stopwatch.StartNew();
        var tmp = Path.Combine(Path.GetTempPath(), $"hav-{Guid.NewGuid():N}.wav");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var form = new MultipartFormDataContent
            {
                { new StringContent(text), "text" },
                { new StringContent(cfg.Voice), "voice" }
            };
            using var resp = await http.PostAsync(cfg.LabTtsUrl, form);
            if (!resp.IsSuccessStatusCode)
                return new AudioResult(new Stage("2 TTS", false, $"el servicio TTS devolvió HTTP {(int)resp.StatusCode}", sw.ElapsedMilliseconds), 0);

            await using (var fs = File.Create(tmp))
                await (await resp.Content.ReadAsStreamAsync()).CopyToAsync(fs);

            if (new FileInfo(tmp).Length == 0)
                return new AudioResult(new Stage("2 TTS", false, "el servicio TTS devolvió un fichero vacío", sw.ElapsedMilliseconds), 0);

            sw.Stop();
            var ttsMs = sw.ElapsedMilliseconds;

            // ── 3. ffmpeg a 8 kHz mono PCM (lo que viaja por RTP) ────────────
            var sw2 = Stopwatch.StartNew();
            var ffmpeg = ResolveFfmpeg(cfg);
            var conv = Shell.Run(ffmpeg, new[]
            {
                "-y", "-loglevel", "error", "-i", tmp,
                "-ar", cfg.SampleRateHz.ToString(CultureInfo.InvariantCulture),
                "-ac", "1", "-c:a", "pcm_s16le", cfg.WavPath
            }, 120_000);

            if (!conv.Ok)
                return new AudioResult(new Stage("3 audio", false, $"ffmpeg falló: {conv.Stderr.Trim()[..Math.Min(160, conv.Stderr.Trim().Length)]}", sw2.ElapsedMilliseconds), 0);

            var info = Wav.Info(cfg.WavPath);
            var stage = new Stage("2+3 TTS+audio", true,
                $"{info.Seconds:0.##} s @{info.Rate} Hz · tts {ttsMs} ms · ffmpeg {sw2.ElapsedMilliseconds} ms",
                ttsMs + sw2.ElapsedMilliseconds);

            return new AudioResult(stage, info.Seconds);
        }
        catch (Exception ex)
        {
            return new AudioResult(new Stage("2 TTS", false, ex.Message, sw.ElapsedMilliseconds), 0);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* temporal */ }
        }
    }

    private static Stage AsteriskStage(PipelineConfig cfg)
    {
        var ps = Shell.Run("docker", new[] { "ps", "--filter", $"name={cfg.Asterisk.ContainerName}", "--format", "{{.Status}}" }, 30_000);
        if (!ps.Ok || !ps.Stdout.Contains("healthy"))
            return new Stage("4 asterisk", false, "contenedor no healthy o inexistente", 0);

        var tr = Shell.AsteriskCli(cfg.Asterisk.ContainerName, "pjsip show transports");
        if (!tr.Stdout.Contains("transport-udp"))
            return new Stage("4 asterisk", false, "el transporte SIP no está cargado", 0);

        return new Stage("4 asterisk", true, "contenedor healthy y transport-udp activo", 0);
    }

    private static int ActiveChannels(PipelineConfig cfg)
    {
        var r = Shell.AsteriskCli(cfg.Asterisk.ContainerName, "core show channels");
        var line = r.Stdout.Split('\n').FirstOrDefault(l => l.Contains("active channel"));
        if (line is null) return 0;
        var digits = new string(line.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static async Task<double?> MeanVolumeAsync(PipelineConfig cfg, string wav)
    {
        var r = Shell.Run(ResolveFfmpeg(cfg),
            new[] { "-hide_banner", "-i", wav, "-af", "volumedetect", "-f", "null", "-" }, 120_000);
        var idx = r.Stderr.IndexOf("mean_volume:", StringComparison.Ordinal);
        if (idx < 0) return null;
        var tail = r.Stderr[idx..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tail.Length >= 2 && double.TryParse(tail[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var db)
            ? db : null;
    }

    private static string ResolveFfmpeg(PipelineConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.FfmpegPath)) return cfg.FfmpegPath;

        var onPath = Shell.Run("bash", new[] { "-lc", "command -v ffmpeg" }, 15_000);
        if (onPath.Ok && onPath.Stdout.Trim().Length > 0) return onPath.Stdout.Trim();

        // ffmpeg que trae Hermes, por si no está en el PATH
        var tools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes", "tools");
        if (Directory.Exists(tools))
        {
            var candidate = Directory.EnumerateDirectories(tools, "ffmpeg-*")
                .Select(d => Path.Combine(d, "bin", "ffmpeg"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null) return candidate;
        }
        return "ffmpeg";
    }

    // ────────────────────────────────────────────────────────────── reporte ──
    private static int Report(string mode, string? ext, List<Stage> stages, bool json)
    {
        var ok = stages.All(s => s.Ok);
        var total = stages.Sum(s => s.Ms);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                mode,
                extension = ext,
                ok,
                totalMs = total,
                stages = stages.Select(s => new { stage = s.Name, ok = s.Ok, detail = s.Detail, ms = s.Ms })
            }, new JsonSerializerOptions { WriteIndented = true }));
            return ok ? 0 : 1;
        }

        Console.WriteLine($"═══ PIPELINE DE VOZ ({mode}) ═══");
        foreach (var s in stages)
            Console.WriteLine($"  {(s.Ok ? "✅" : "❌")} {s.Name} — {s.Detail}");

        Console.WriteLine("──────────────────────────────────────────────");
        if (ok)
        {
            Console.WriteLine($"RESULTADO: ✅ PIPELINE OK — {total} ms");
            return 0;
        }
        var failed = stages.Count(s => !s.Ok);
        Console.WriteLine($"RESULTADO: ❌ {failed} etapa(s) fallida(s) de {stages.Count} — {total} ms");
        return 1;
    }

    private static void PrintUsage() => Console.Error.WriteLine("""
        hav — pipeline de voz Hermes → Pocket-TTS → Asterisk

        uso:
          hav call <extension> "<texto>" [--json]     llama al softphone y le habla
          hav call <extension> --reuse                reutiliza el audio ya generado
          hav autotest "<texto>"                      SIN teléfono: graba y analiza el audio

        variables de entorno (ganan al appsettings.json):
          HAV_HOME       directorio del stack Asterisk (por defecto ~/apps/asterisk)
          HAV_TTS_URL    endpoint del TTS (por defecto http://127.0.0.1:8000/api/tts)
          HAV_VOICE      voz del TTS (por defecto lola)
          HAV_FFMPEG     ruta a ffmpeg (si no está en el PATH)
          HAV_CONTAINER  nombre del contenedor (por defecto alpefe-asterisk)
          HAV_TIMEOUT    segundos de espera al descolgar
          HAV_CONFIG     ruta a un appsettings.json alternativo
        """);
}
