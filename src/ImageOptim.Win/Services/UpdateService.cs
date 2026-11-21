using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageOptim.Win.Services;

/// <summary>
/// Checks GitHub Releases with a CNB mirror fallback, verifying the same RSA-SHA256 manifest.
/// The publisher's public key is distributed with the installed application.
/// An unsigned or malformed release can never be installed.
/// </summary>
public sealed class UpdateService : IDisposable
{
    internal const string ArchiveName = "ImageOptimWin-win-x64-Bundled.zip";
    internal const string ManifestName = "update-manifest.json";
    internal const string SignatureName = "update-manifest.sig";
    // Only the owner's public CNB mirror is trusted as a transport alternative.
    // Both sites must present the SAME canonical GitHub-signed update manifest.
    private const string MirrorRepository = "ZntxCYL/ImageOptimWin";
    private const string MirrorHost = "cnb.cool";

    private readonly HttpClient _http;
    private readonly UpdateConfiguration _configuration;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public bool NoStableReleaseFound { get; private set; }

    internal UpdateService(UpdateConfiguration config, HttpMessageHandler? handler = null)
    {
        _configuration = config;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = true });
        _http.Timeout = TimeSpan.FromMinutes(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ImageOptimWin-Updater/" +
            (typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "unknown"));
    }

    public static UpdateService? CreateIfConfigured()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "update-config.json");
        if (!File.Exists(path)) return null;
        var config = JsonSerializer.Deserialize<UpdateConfiguration>(File.ReadAllText(path), JsonOptions);
        if (config is null || string.IsNullOrWhiteSpace(config.Repository) ||
            string.IsNullOrWhiteSpace(config.PublicKeyPem)) return null;
        if (!Regex.IsMatch(config.Repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new InvalidDataException("更新源的仓库格式必须为 OWNER/REPO。");
        using (var rsa = RSA.Create()) rsa.ImportFromPem(config.PublicKeyPem);
        return new UpdateService(config);
    }

    private enum ReleaseSource { GitHub, Cnb }

    private sealed record SourceCheckResult(
        ReleaseSource Source, Version? AdvertisedVersion, AvailableUpdate? Update,
        bool HasStableRelease, Exception? NetworkError = null);

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken token = default)
    {
        NoStableReleaseFound = false;
        var current = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 9, 0);

        // Race both independent, RSA-verified release checks instead of making
        // domestic users wait for the unreachable GitHub host to time out.
        // GitHub wins if it completes first, or within a short grace period
        // after a verified CNB result (so a newer GitHub tag is not missed).
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        var checks = new List<Task<SourceCheckResult>>
        {
            CheckSourceAsync(ReleaseSource.GitHub, current, token, work.Token)
        };
        if (CanUseMirror())
            checks.Add(CheckSourceAsync(ReleaseSource.Cnb, current, token, work.Token));

        var pending = new List<Task<SourceCheckResult>>(checks);
        Exception? lastNetworkError = null;
        try
        {
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                var result = await completed; // Invalid signature/manifest: fail closed.

                if (result.NetworkError is not null)
                {
                    lastNetworkError = result.NetworkError;
                    continue;
                }

                // If CNB responds first, allow a quick GitHub response to
                // override with a newer version. Never wait for its full timeout.
                if (result.Source == ReleaseSource.Cnb && pending.Count > 0)
                {
                    var github = pending[0];
                    var grace = Task.Delay(TimeSpan.FromMilliseconds(350), token);
                    if (await Task.WhenAny(github, grace) == github)
                    {
                        pending.Remove(github);
                        var githubResult = await github;
                        if (githubResult.NetworkError is null &&
                            (githubResult.Update?.Version ?? current) >
                            (result.Update?.Version ?? current))
                            result = githubResult;
                    }
                }

                NoStableReleaseFound = !result.HasStableRelease;
                return result.Update;
            }

            throw new IOException("GitHub 和 CNB 镜像均无法检查更新，请稍后重试。", lastNetworkError);
        }
        finally
        {
            work.Cancel(); // Cancel the losing request before disposing HttpClient.
            try { await Task.WhenAll(checks); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
    }

    private async Task<SourceCheckResult> CheckSourceAsync(
        ReleaseSource source, Version current, CancellationToken userToken, CancellationToken workToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(workToken);
        timeout.CancelAfter(source == ReleaseSource.GitHub
            ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(10));
        try
        {
            string? tag = await ResolveLatestTagAsync(source, timeout.Token);
            if (tag is null)
                return new SourceCheckResult(source, null, null, HasStableRelease: false);
            if (!Version.TryParse(tag[1..], out var latest))
                throw new InvalidDataException("无法解析 Release 版本。");
            if (latest <= current)
                return new SourceCheckResult(source, latest, null, HasStableRelease: true);

            string baseUrl = DownloadBase(source, tag);
            byte[] manifestBytes = await DownloadSmallAsync(baseUrl + ManifestName, 64 * 1024, timeout.Token);
            byte[] signatureBytes = await DownloadSmallAsync(baseUrl + SignatureName, 8 * 1024, timeout.Token);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_configuration.PublicKeyPem);
            if (!rsa.VerifyData(manifestBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("发布清单数字签名验证失败，更新已阻止。");

            var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonOptions)
                ?? throw new InvalidDataException("更新清单内容为空。");
            string sha256 = manifest.Sha256 ?? "";
            if (manifest.Repository != _configuration.Repository || manifest.Tag != tag ||
                manifest.Version != tag[1..] || manifest.Archive != ArchiveName ||
                manifest.Size <= 0 || manifest.Size > 2_000_000_000L ||
                !Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("签名清单与仓库、版本或发布包不匹配。");

            ReleaseSource alternate = source == ReleaseSource.GitHub ? ReleaseSource.Cnb : ReleaseSource.GitHub;
            string? fallbackUrl = CanUseMirror() ? DownloadBase(alternate, tag) + ArchiveName : null;
            var update = new AvailableUpdate(latest, tag, baseUrl + ArchiveName,
                sha256.ToLowerInvariant(), manifest.Size, _configuration.Repository, fallbackUrl);
            return new SourceCheckResult(source, latest, update, HasStableRelease: true);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, userToken))
        {
            return new SourceCheckResult(source, null, null, HasStableRelease: false, ex);
        }
    }

    private bool CanUseMirror() =>
        string.Equals(_configuration.Repository, MirrorRepository, StringComparison.Ordinal);

    private string DownloadBase(ReleaseSource source, string tag) =>
        source == ReleaseSource.GitHub
            ? $"https://github.com/{_configuration.Repository}/releases/download/{tag}/"
            : $"https://{MirrorHost}/{MirrorRepository}/-/releases/download/{tag}/";

    private async Task<string?> ResolveLatestTagAsync(ReleaseSource source, CancellationToken token)
    {
        string endpoint = source == ReleaseSource.GitHub
            ? $"https://github.com/{_configuration.Repository}/releases/latest"
            : $"https://{MirrorHost}/{MirrorRepository}/-/badge/release.link";
        using var response = await _http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureGitHubResponseOk(response, "检查最新版本");
        Uri finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("无法获取最新版本跳转地址。");
        string expectedHost = source == ReleaseSource.GitHub ? "github.com" : MirrorHost;
        string prefix = source == ReleaseSource.GitHub
            ? $"/{_configuration.Repository}/releases/tag/"
            : $"/{MirrorRepository}/-/releases/tag/";
        if (finalUri.Scheme != Uri.UriSchemeHttps ||
            !finalUri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !finalUri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新源未返回预期的正式版本地址。");
        string tag = finalUri.AbsolutePath[prefix.Length..];
        if (!Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+$"))
            throw new InvalidDataException("最新版本标签格式无效。");
        return tag;
    }

    private static bool IsNetworkFailure(Exception ex, CancellationToken userToken) =>
        // Integrity/format failures are terminal even if a base exception is IOException.
        ex is not InvalidDataException && (
            ex is HttpRequestException or IOException ||
            (ex is OperationCanceledException && !userToken.IsCancellationRequested));

    public async Task<string> DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken token)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ImageOptimWin", "updates", update.Tag);
        Directory.CreateDirectory(folder);
        string staged = Path.Combine(folder, "update.zip.part");
        string final = Path.Combine(folder, "update.zip");
        Exception? lastNetworkError = null;
        var urls = new[] { update.DownloadUrl, update.FallbackDownloadUrl }
            .Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string? url in urls)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                progress.Report(0);
                using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                headersTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await _http.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
                EnsureGitHubResponseOk(response, "下载更新包");
                if (response.Content.Headers.ContentLength is long announced && announced != update.Size)
                    throw new InvalidDataException("下载文件大小与签名清单不一致。");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write,
                    FileShare.None, 128 * 1024, true))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long received = 0;
                    var block = new byte[128 * 1024];
                    using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    int count;
                    while (true)
                    {
                        // A hung transfer should eventually try the second mirror.
                        idleTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                        count = await input.ReadAsync(block, idleTimeout.Token);
                        if (count == 0) break;
                        received += count;
                        if (received > update.Size) throw new InvalidDataException("更新包超过清单声明大小。");
                        hash.AppendData(block, 0, count);
                        await output.WriteAsync(block.AsMemory(0, count), token);
                        progress.Report((int)Math.Min(100, received * 100 / update.Size));
                    }
                    await output.FlushAsync(token);
                    if (received != update.Size ||
                        !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                        throw new CryptographicException("更新包 SHA-256 或文件大小校验失败，已阻止安装。");
                }
                File.Move(staged, final, true);
                return final;
            }
            catch (Exception ex) when (IsNetworkFailure(ex, token))
            {
                lastNetworkError = ex;
            }
            finally
            {
                try { File.Delete(staged); } catch (IOException) { }
            }
        }
        throw new IOException("GitHub 与 CNB 更新包下载均失败，请检查网络后重试。", lastNetworkError);
    }

    public static void StartUpdater(string verifiedZip, AvailableUpdate update)
    {
        string installedUpdater = Path.Combine(AppContext.BaseDirectory, "ImageOptim.Updater.exe");
        if (!File.Exists(installedUpdater)) throw new FileNotFoundException("更新器不存在。", installedUpdater);
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ImageOptimWin", "updater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string detachedUpdater = Path.Combine(folder, "ImageOptim.Updater.exe");
        File.Copy(installedUpdater, detachedUpdater);
        string token = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(detachedUpdater)
        {
            UseShellExecute = false,
            WorkingDirectory = folder
        };
        foreach (string arg in new[] {
            "--install-dir", Path.GetFullPath(AppContext.BaseDirectory),
            "--archive", Path.GetFullPath(verifiedZip), "--sha256", update.Sha256,
            "--parent-pid", Environment.ProcessId.ToString(), "--health-token", token,
            "--version", update.Version.ToString(3)
        }) start.ArgumentList.Add(arg);
        _ = Process.Start(start) ?? throw new InvalidOperationException("无法启动独立更新器。");
    }

    public static void ConfirmHealthyStartup(string[] args)
    {
        int index = Array.IndexOf(args, "--update-health-token");
        if (index < 0 || index + 1 >= args.Length) return;
        string token = args[index + 1];
        if (!Regex.IsMatch(token, "^[a-f0-9]{32}$")) return;
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ImageOptimWin", "updates");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "health-" + token + ".ok"), "ready", Encoding.UTF8);
    }

    private async Task<byte[]> DownloadSmallAsync(string url, int maxBytes, CancellationToken token)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        EnsureGitHubResponseOk(response, "下载更新签名文件");
        return await ReadLimitedAsync(response, maxBytes, token);
    }

    private static void EnsureGitHubResponseOk(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"{operation}被服务器限流（HTTP {(int)response.StatusCode}）。",
                null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException($"{operation}失败：发布附件不存在（HTTP 404）。",
                null, response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("远程发布信息超过允许的大小。");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (memory.Length + read > maxBytes) throw new InvalidDataException("远程发布信息过大。");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    public void Dispose() => _http.Dispose();
}

public sealed record AvailableUpdate(Version Version, string Tag, string DownloadUrl, string Sha256, long Size, string Repository, string? FallbackDownloadUrl = null);
public sealed class UpdateConfiguration
{
    public string Repository { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
}
public sealed class UpdateManifest
{
    public string Repository { get; set; } = "";
    public string Version { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Archive { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}
