using System.Text.Json;

namespace HermesAsteriskVoip;

/// <summary>Configuración del pipeline (appsettings.json + variables HAV_*).</summary>
public sealed class PipelineConfig
{
    /// <summary>Raíz del stack; por defecto ~/apps/asterisk del usuario actual.</summary>
    public static string DefaultHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "apps", "asterisk");

    public string Home { get; init; } = DefaultHome;
    public string LabTtsUrl { get; init; } = "http://127.0.0.1:8000/api/tts";
    public string Voice { get; init; } = "lola";
    public string FfmpegPath { get; init; } = "";
    public string SoundName { get; init; } = "hermes";
    public int SampleRateHz { get; init; } = 8000;
    public AsteriskConfig Asterisk { get; init; } = new();

    public string SoundsDir => Path.Combine(Home, "sounds", "custom");
    public string WavPath => Path.Combine(SoundsDir, SoundName + ".wav");
    public string LogsDir => Path.Combine(Home, "logs");
    public string RecordingsDir => Path.Combine(Home, "spool", "monitor");

    public sealed class AsteriskConfig
    {
        public string ContainerName { get; init; } = "alpefe-asterisk";
        public string DialContext { get; init; } = "hermes";
        public string PlayExtension { get; init; } = "play";
        public string AutotestExtension { get; init; } = "900@prueba";
        public int AnswerTimeoutSeconds { get; init; } = 40;
        public string RecordingFile { get; init; } = "grabacion.wav";
        public double SilenceThresholdDb { get; init; } = -50.0;
    }

    public static PipelineConfig Load(string? explicitPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        var envPath = Environment.GetEnvironmentVariable("HAV_CONFIG");
        if (!string.IsNullOrWhiteSpace(envPath)) candidates.Add(envPath);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        candidates.Add("appsettings.json");

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Pipeline", out var p)) continue;
            var cfg = new PipelineConfig
            {
                Home = HomeOrDefault(Str(p, "Home", "")),
                LabTtsUrl = Str(p, "LabTtsUrl", "http://127.0.0.1:8000/api/tts"),
                Voice = Str(p, "Voice", "lola"),
                FfmpegPath = Str(p, "FfmpegPath", ""),
                SoundName = Str(p, "SoundName", "hermes"),
                SampleRateHz = Num(p, "SampleRateHz", 8000),
                Asterisk = ReadAsterisk(p)
            };
            return cfg.ApplyEnvironment();
        }
        return new PipelineConfig().ApplyEnvironment();
    }

    /// <summary>Las variables de entorno ganan al fichero: útil en CI o en otras máquinas.</summary>
    private PipelineConfig ApplyEnvironment() => new()
    {
        Home = HomeOrDefault(Env("HAV_HOME") ?? Home),
        LabTtsUrl = Env("HAV_TTS_URL") ?? LabTtsUrl,
        Voice = Env("HAV_VOICE") ?? Voice,
        FfmpegPath = Env("HAV_FFMPEG") ?? FfmpegPath,
        SoundName = Env("HAV_SOUND") ?? SoundName,
        SampleRateHz = SampleRateHz,
        Asterisk = new AsteriskConfig
        {
            ContainerName = Env("HAV_CONTAINER") ?? Asterisk.ContainerName,
            DialContext = Asterisk.DialContext,
            PlayExtension = Asterisk.PlayExtension,
            AutotestExtension = Asterisk.AutotestExtension,
            AnswerTimeoutSeconds = int.TryParse(Env("HAV_TIMEOUT"), out var t) ? t : Asterisk.AnswerTimeoutSeconds,
            RecordingFile = Asterisk.RecordingFile,
            SilenceThresholdDb = Asterisk.SilenceThresholdDb
        }
    };

    private static AsteriskConfig ReadAsterisk(JsonElement parent)
    {
        if (!parent.TryGetProperty("Asterisk", out var a)) return new AsteriskConfig();
        return new AsteriskConfig
        {
            ContainerName = Str(a, "ContainerName", "alpefe-asterisk"),
            DialContext = Str(a, "DialContext", "hermes"),
            PlayExtension = Str(a, "PlayExtension", "play"),
            AutotestExtension = Str(a, "AutotestExtension", "900@prueba"),
            AnswerTimeoutSeconds = Num(a, "AnswerTimeoutSeconds", 40),
            RecordingFile = Str(a, "RecordingFile", "grabacion.wav"),
            SilenceThresholdDb = a.TryGetProperty("SilenceThresholdDb", out var s) && s.TryGetDouble(out var d) ? d : -50.0
        };
    }

    /// <summary>Ruta vacía o relativa se resuelve contra el home del usuario.</summary>
    private static string HomeOrDefault(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultHome;
        return Path.IsPathRooted(value) ? value : Path.Combine(DefaultHome, value);
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
    private static string Str(JsonElement e, string name, string fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
    private static int Num(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;
}
