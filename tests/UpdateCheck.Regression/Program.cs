using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ImageOptim.Win.Services;

internal static class Program
{
    private static async Task Main()
    {
        await TestCnbAnswersWithoutWaitingForBlockedGitHub();
        await TestFastGitHubCanOverrideAnOlderMirror();
        await TestHttp403FallsBack();
        await TestAlreadyCurrentAnswersQuickly();
        await TestInvalidMirrorSignatureFailsClosed();
        Console.WriteLine("[PASS] Fast dual-source update-check regression tests");
    }

    private static UpdateService CreateService(RSA rsa, FakeReleaseHandler handler) =>
        new(new UpdateConfiguration {
            Repository = "ZntxCYL/ImageOptimWin",
            PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem()
        }, handler);

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception("Update-check regression failed: " + message);
    }

    private static async Task TestCnbAnswersWithoutWaitingForBlockedGitHub()
    {
        using var rsa = RSA.Create(2048);
        using var handler = new FakeReleaseHandler(rsa) {
            GitHubDelay = Timeout.InfiniteTimeSpan,
            CnbDelay = TimeSpan.FromMilliseconds(20)
        };
        using var service = CreateService(rsa, handler);
        var watch = Stopwatch.StartNew();
        var update = await service.CheckAsync();
        watch.Stop();
        Assert(update is not null && update.Version.ToString(3) == "0.9.6", "CNB did not detect a newer signed release");
        Assert(update!.DownloadUrl.StartsWith("https://cnb.cool/", StringComparison.Ordinal), "CNB must be the fast primary source");
        Assert(update!.FallbackDownloadUrl?.StartsWith("https://github.com/", StringComparison.Ordinal) == true,
            "GitHub should remain the fallback archive source");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(3), "Blocked GitHub delayed a valid CNB result");
        Console.WriteLine("CNB first / blocked GitHub: " + watch.ElapsedMilliseconds + "ms");
    }

    private static async Task TestFastGitHubCanOverrideAnOlderMirror()
    {
        using var rsa = RSA.Create(2048);
        using var handler = new FakeReleaseHandler(rsa) {
            GitHubTag = "v0.9.7",
            CnbTag = "v0.9.6",
            GitHubDelay = TimeSpan.FromMilliseconds(80),
            CnbDelay = TimeSpan.FromMilliseconds(20)
        };
        using var service = CreateService(rsa, handler);
        var update = await service.CheckAsync();
        Assert(update is not null && update.Version.ToString(3) == "0.9.7", "Fast GitHub latest tag was ignored");
        Assert(update!.DownloadUrl.StartsWith("https://github.com/", StringComparison.Ordinal), "GitHub should win for newer tags");
        Console.WriteLine("Fast GitHub newer than CNB: PASS");
    }

    private static async Task TestHttp403FallsBack()
    {
        using var rsa = RSA.Create(2048);
        using var handler = new FakeReleaseHandler(rsa) { GitHubForbidden = true };
        using var service = CreateService(rsa, handler);
        var update = await service.CheckAsync();
        Assert(update?.Tag == "v0.9.6", "CNB fallback failed after GitHub 403");
        Console.WriteLine("GitHub HTTP 403 fallback: PASS");
    }

    private static async Task TestAlreadyCurrentAnswersQuickly()
    {
        using var rsa = RSA.Create(2048);
        using var handler = new FakeReleaseHandler(rsa) {
            GitHubDelay = Timeout.InfiniteTimeSpan,
            CnbTag = "v0.9.5"
        };
        using var service = CreateService(rsa, handler);
        var watch = Stopwatch.StartNew();
        var update = await service.CheckAsync();
        watch.Stop();
        Assert(update is null && !service.NoStableReleaseFound, "Current release incorrectly treated as missing");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(3), "No-update result waited for blocked GitHub");
        Console.WriteLine("Already-current CNB / blocked GitHub: " + watch.ElapsedMilliseconds + "ms");
    }

    private static async Task TestInvalidMirrorSignatureFailsClosed()
    {
        using var rsa = RSA.Create(2048);
        using var handler = new FakeReleaseHandler(rsa) {
            GitHubDelay = Timeout.InfiniteTimeSpan,
            CorruptCnbSignature = true
        };
        using var service = CreateService(rsa, handler);
        try
        {
            await service.CheckAsync();
            throw new Exception("Invalid RSA signature was accepted");
        }
        catch (CryptographicException) { }
        Console.WriteLine("Corrupt CNB signature rejected: PASS");
    }

    private sealed class FakeReleaseHandler : HttpMessageHandler
    {
        private readonly RSA _key;
        public TimeSpan GitHubDelay { get; init; } = TimeSpan.FromMilliseconds(60);
        public TimeSpan CnbDelay { get; init; } = TimeSpan.FromMilliseconds(15);
        public string GitHubTag { get; init; } = "v0.9.6";
        public string CnbTag { get; init; } = "v0.9.6";
        public bool GitHubForbidden { get; init; }
        public bool CorruptCnbSignature { get; init; }

        public FakeReleaseHandler(RSA key) => _key = key;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            bool isGitHub = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
            var tag = isGitHub ? GitHubTag : CnbTag;
            await Task.Delay(isGitHub ? GitHubDelay : CnbDelay, token);
            if (isGitHub && GitHubForbidden)
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request };

            if (uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal) ||
                uri.AbsolutePath.EndsWith("/badge/release.link", StringComparison.Ordinal))
            {
                var finalUri = isGitHub
                    ? $"https://github.com/ZntxCYL/ImageOptimWin/releases/tag/{tag}"
                    : $"https://cnb.cool/ZntxCYL/ImageOptimWin/-/releases/tag/{tag}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri),
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
            }

            var manifest = JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest {
                Repository = "ZntxCYL/ImageOptimWin",
                Version = tag[1..],
                Tag = tag,
                Archive = "ImageOptimWin-win-x64-Bundled.zip",
                Sha256 = new string('a', 64),
                Size = 12345
            });
            byte[] contents;
            if (uri.AbsolutePath.EndsWith("/update-manifest.json", StringComparison.Ordinal))
                contents = manifest;
            else if (uri.AbsolutePath.EndsWith("/update-manifest.sig", StringComparison.Ordinal))
            {
                contents = _key.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (!isGitHub && CorruptCnbSignature)
                    contents[0] ^= 0xff;
            }
            else
                throw new Exception("Unexpected test request: " + uri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) {
                RequestMessage = request,
                Content = new ByteArrayContent(contents)
            };
        }
    }
}
