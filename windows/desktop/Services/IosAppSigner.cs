using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CaboCam.Services;

public sealed class IosSignException : Exception
{
    public IosSignException(string message, bool needsLogin = false) : base(message) => NeedsLogin = needsLogin;

    /// <summary>The saved Apple ID session is missing or no longer valid.</summary>
    public bool NeedsLogin { get; }
}

public sealed record IpaRelease(string Tag, string FileName, string DownloadUrl, long Size);

/// <summary>
/// Signs and installs the CaboCam iOS app with the user's Apple ID by driving
/// <c>plumesign.exe</c> (CLI of the Impactor project, shipped next to CaboCam.exe).
///
/// plumesign keeps the Apple session (tokens, not the password) in
/// %AppData%\PlumeImpactor\accounts.json. The password is only written to the child's stdin
/// during login and is never stored by CaboCam. Installation goes through the Apple Mobile
/// Device Service (usbmuxd), the same driver CaboCam uses for video.
/// </summary>
public static class IosAppSigner
{
    public const string IpaAssetSuffix = ".ipa";
    public static readonly TimeSpan FreeProfileLifetime = TimeSpan.FromDays(7);

    private static readonly HttpClient Http = CreateHttp();
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex Progress = new(@"Installation progress:\s*(\d+)", RegexOptions.Compiled);

    public static string PlumesignPath => Path.Combine(AppContext.BaseDirectory, "plumesign.exe");
    public static bool IsAvailable => File.Exists(PlumesignPath);

    public static string CacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaboCam", "ios");

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CaboCam", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    // =============================================================================
    // .ipa from GitHub Releases
    // =============================================================================

    public static async Task<IpaRelease> GetLatestReleaseAsync(string repo, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new IosSignException($"Nenhum Release publicado em github.com/{repo}. Crie uma tag v* para o workflow gerar o .ipa.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "?";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(IpaAssetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            return new IpaRelease(tag, name, asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64());
        }
        throw new IosSignException($"O Release {tag} de {repo} não tem um arquivo .ipa.");
    }

    /// <summary>Downloads the release .ipa into the local cache (reused if already there).</summary>
    public static async Task<string> DownloadAsync(IpaRelease release, IProgress<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDirectory);
        var safeTag = string.Concat(release.Tag.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(CacheDirectory, $"CaboCam-{safeTag}.ipa");
        if (File.Exists(path) && new FileInfo(path).Length == release.Size) return path;

        log?.Report($"Baixando {release.FileName} ({release.Size / 1024.0 / 1024.0:0.0} MB)…");
        var tmp = path + ".part";
        using (var resp = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);

        // Keep only the newest download.
        foreach (var old in Directory.EnumerateFiles(CacheDirectory, "CaboCam-*.ipa"))
            if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) TryDelete(old);
        return path;
    }

    // =============================================================================
    // plumesign
    // =============================================================================

    /// <summary>E-mail of the Apple ID selected in plumesign, or null when not logged in.</summary>
    public static async Task<string?> GetSelectedAccountAsync(CancellationToken ct)
    {
        var result = await RunAsync(new[] { "account", "list" }, null, null, ct).ConfigureAwait(false);
        foreach (var line in result.Lines)
        {
            // " [First] email@example.com (selected)"
            if (!line.Contains("(selected)")) continue;
            var m = Regex.Match(line, @"\]\s*(\S+@\S+)\s*\(selected\)");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// Logs in to the Apple ID. <paramref name="askCode"/> is called when Apple asks for the
    /// two-factor code; returning null cancels the login.
    /// </summary>
    public static async Task LoginAsync(string email, string password, Func<string, Task<string?>> askCode,
                                        IProgress<string>? log, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Process? proc = null;

        async Task OnLine(string line)
        {
            if (proc is null) return;
            if (line.Contains("Enter 2FA code", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Enter the code", StringComparison.OrdinalIgnoreCase))
            {
                var code = await askCode(line).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(code)) { cts.Cancel(); return; }
                await proc.StandardInput.WriteLineAsync(code.Trim()).ConfigureAwait(false);
                await proc.StandardInput.FlushAsync().ConfigureAwait(false);
            }
        }

        var result = await RunAsync(new[] { "account", "login", "-u", email }, p =>
        {
            proc = p;
            // plumesign reads the password from stdin when -p is omitted (keeps it off the command line).
            p.StandardInput.WriteLine(password);
            p.StandardInput.Flush();
        }, (line) => { log?.Report(line); return OnLine(line); }, cts.Token).ConfigureAwait(false);

        if (ct.IsCancellationRequested || cts.IsCancellationRequested)
            throw new OperationCanceledException("Login cancelado.");
        if (result.ExitCode != 0 || !result.Text.Contains("Successfully logged in", StringComparison.OrdinalIgnoreCase))
            throw new IosSignException("Falha no login do Apple ID: " + result.LastError());
    }

    public static async Task LogoutAsync(CancellationToken ct) =>
        await RunAsync(new[] { "account", "logout" }, null, null, ct).ConfigureAwait(false);

    /// <summary>Registers the device, signs the .ipa with the saved Apple ID and installs it over USB.</summary>
    public static async Task SignAndInstallAsync(string ipaPath, string udid, IProgress<string>? log,
                                                 IProgress<int>? percent, CancellationToken ct)
    {
        var result = await RunAsync(
            new[] { "sign", "--package", ipaPath, "--apple-id", "--register-and-install", "--udid", udid },
            p => p.StandardInput.Close(), // any unexpected interactive prompt fails instead of hanging
            line =>
            {
                var m = Progress.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var pct)) percent?.Report(pct);
                else log?.Report(line);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);

        if (result.ExitCode == 0 && result.Text.Contains("Installation complete", StringComparison.OrdinalIgnoreCase))
            return;

        var text = result.Text;
        bool needsLogin = text.Contains("No account selected", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("login first", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("401", StringComparison.Ordinal)
                          || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("session", StringComparison.OrdinalIgnoreCase) && text.Contains("expired", StringComparison.OrdinalIgnoreCase);
        if (text.Contains("not a terminal", StringComparison.OrdinalIgnoreCase) || text.Contains("IO error", StringComparison.OrdinalIgnoreCase))
            throw new IosSignException("O plumesign pediu uma escolha interativa (por exemplo, mais de um Team na conta). " +
                                       "Rode o plumesign num terminal uma vez para resolver. Detalhe: " + result.LastError());
        throw new IosSignException("Falha ao assinar/instalar: " + result.LastError(), needsLogin);
    }

    // =============================================================================
    // Process plumbing
    // =============================================================================

    private sealed record RunResult(int ExitCode, List<string> Lines)
    {
        public string Text => string.Join("\n", Lines);

        public string LastError()
        {
            var err = Lines.LastOrDefault(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                                             || l.Contains("ERROR", StringComparison.Ordinal))
                      ?? Lines.LastOrDefault(l => l.Length > 0);
            return string.IsNullOrEmpty(err) ? $"código de saída {ExitCode}" : err;
        }
    }

    private static async Task<RunResult> RunAsync(IEnumerable<string> args, Action<Process>? onStarted,
                                                  Func<string, Task>? onLine, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new IosSignException($"plumesign.exe não encontrado em {AppContext.BaseDirectory}. Reinstale o CaboCam.");

        var psi = new ProcessStartInfo(PlumesignPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["RUST_LOG"] = "info";
        psi.Environment["RUST_LOG_STYLE"] = "never";
        psi.Environment["NO_COLOR"] = "1";

        var lines = new List<string>();
        var gate = new object();
        // Lines are handled one at a time so a 2FA prompt can await the user.
        var queue = new SemaphoreSlim(1, 1);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var pending = new List<Task>();

        void Handle(string? raw)
        {
            if (raw is null) return;
            var line = Ansi.Replace(raw, "").TrimEnd();
            lock (gate)
            {
                lines.Add(line);
                if (onLine is null) return;
                pending.Add(Task.Run(async () =>
                {
                    await queue.WaitAsync().ConfigureAwait(false);
                    try { await onLine(line).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Error("plumesign: erro ao tratar saída", ex); }
                    finally { queue.Release(); }
                }));
            }
        }

        proc.OutputDataReceived += (_, e) => Handle(e.Data);
        proc.ErrorDataReceived += (_, e) => Handle(e.Data);

        Log.Info("plumesign " + string.Join(' ', args.Select(a => a.Contains('@') ? "<apple-id>" : a)));
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            onStarted?.Invoke(proc);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        Task[] toWait;
        lock (gate) toWait = pending.ToArray();
        await Task.WhenAll(toWait).ConfigureAwait(false);

        List<string> snapshot;
        lock (gate) snapshot = lines.ToList();
        foreach (var l in snapshot) Log.Info("plumesign> " + l);
        Log.Info($"plumesign saiu com código {proc.ExitCode}");
        return new RunResult(proc.ExitCode, snapshot);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}
