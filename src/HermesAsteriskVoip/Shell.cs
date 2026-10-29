using System.Diagnostics;
using System.Text;

namespace HermesAsteriskVoip;

/// <summary>Ejecuta procesos externos (docker, ffmpeg) y devuelve stdout/exit code.</summary>
public static class Shell
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr)
    {
        public bool Ok => ExitCode == 0;
    }

    public static Result Run(string file, IEnumerable<string> args, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var eb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) eb.AppendLine(e.Data); };

        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ya murió */ }
                return new Result(-1, sb.ToString(), $"timeout tras {timeoutMs} ms");
            }
            p.WaitForExit(); // drena la salida asíncrona
            return new Result(p.ExitCode, sb.ToString(), eb.ToString());
        }
        catch (Exception ex)
        {
            return new Result(-1, sb.ToString(), ex.Message);
        }
    }

    /// <summary>CLI de Asterisk dentro del contenedor.</summary>
    public static Result AsteriskCli(string container, string command) =>
        Run("docker", new[] { "exec", container, "asterisk", "-rx", command }, 60_000);

    /// <summary>Logs del contenedor desde un instante en RFC3339 UTC.</summary>
    public static Result DockerLogsSince(string container, string rfc3339Utc) =>
        Run("docker", new[] { "logs", "--since", rfc3339Utc, container }, 60_000);
}
