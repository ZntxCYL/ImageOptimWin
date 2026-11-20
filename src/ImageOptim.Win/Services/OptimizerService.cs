using System.Diagnostics;
using System.IO;
using System.Xml;
using ImageOptim.Win.Models;

namespace ImageOptim.Win.Services;

public sealed record OptimizationResult(long OriginalBytes, long FinalBytes, string EngineName)
{
    public bool Improved => FinalBytes < OriginalBytes;
}

public sealed record OptimizationProgress(int FinishedStages, int TotalStages, string CurrentEngine)
{
    public int Percent => TotalStages <= 0 ? 0 : (int)Math.Round(100.0 * FinishedStages / TotalStages);
}

internal sealed record ToolInvocation(string Name, string Executable, string OutputFile, string[] Arguments);

/// <summary>
/// Runs cross-platform open-source command-line optimizers on Windows.
/// Every tool writes to a candidate path: original files are never passed as outputs.
/// The smallest successful candidate replaces the original atomically.
/// </summary>
public static class OptimizerService
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".svg" };

    public static bool IsSupported(string path) => Supported.Contains(Path.GetExtension(path));

    public static async Task<OptimizationResult> OptimizeAsync(
        ImageJob job, AppSettings settings, BackupManager backups, CancellationToken token,
        IProgress<OptimizationProgress>? progress = null)
    {
        var source = job.FullPath;
        if (!File.Exists(source)) throw new FileNotFoundException("File no longer exists.", source);
        var original = new FileInfo(source).Length;
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            throw new IOException("Source is read-only. Make it writable before optimizing.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Symbolic links and reparse-point files are not modified.");

        var writeTime = File.GetLastWriteTimeUtc(source);
        var creationTime = File.GetCreationTimeUtc(source);
        var workdir = Path.Combine(Path.GetTempPath(), "ImageOptimWin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);
        try
        {
            var runs = CreateInvocations(source, workdir, settings);
            if (runs.Count == 0)
                throw new InvalidOperationException($"No enabled optimizer found for {Path.GetExtension(source)}. " +
                    "The matching bundled optimizer is missing. Re-publish with .\\publish.ps1 to bundle all engines.");

            string? bestOutput = null;
            string bestEngine = "";
            long bestSize = original;
            int completed = 0;
            var errors = new List<string>();
            var finishedStages = 0;
            progress?.Report(new OptimizationProgress(0, runs.Count, "准备压缩"));

            foreach (var run in runs)
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(run.OutputFile)!);
                progress?.Report(new OptimizationProgress(finishedStages, runs.Count, $"正在使用 {run.Name}"));
                try
                {
                    var (exitCode, stderr) = await ExecuteAsync(run, token).ConfigureAwait(false);
                    if (exitCode != 0)
                    {
                        errors.Add($"{run.Name}: {ShortError(stderr, exitCode)}");
                        continue;
                    }
                    if (!File.Exists(run.OutputFile)) { completed++; continue; } // Some tools skip unchanged files.
                    if (!LooksLikeExpectedFormat(run.OutputFile, Path.GetExtension(source)))
                    {
                        errors.Add($"{run.Name}: produced an invalid output signature");
                        continue;
                    }
                    completed++;
                    var size = new FileInfo(run.OutputFile).Length;
                    if (size > 0 && size < bestSize)
                    {
                        bestSize = size;
                        bestOutput = run.OutputFile;
                        bestEngine = run.Name;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    errors.Add($"{run.Name}: {ex.Message}");
                }
                finally
                {
                    finishedStages++;
                    progress?.Report(new OptimizationProgress(finishedStages, runs.Count, $"已检查 {run.Name}"));
                }
            }

            if (completed == 0)
                throw new InvalidOperationException("All optimizers failed. " + string.Join(" | ", errors.Take(3)));

            if (bestOutput is null)
                return new OptimizationResult(original, original, "No reduction");

            token.ThrowIfCancellationRequested();
            // Keep a recoverable session copy BEFORE changing the source.
            backups.EnsureBackup(job);

            // File.Replace requires source and target to live on the same volume.
            var stage = Path.Combine(Path.GetDirectoryName(source)!,
                $".imageoptim-stage-{Guid.NewGuid():N}{Path.GetExtension(source)}");
            try
            {
                File.Copy(bestOutput, stage);
                token.ThrowIfCancellationRequested();
                // If source changed while the optimizer ran, refuse to replace it.
                if (!File.Exists(source) || new FileInfo(source).Length != original ||
                    File.GetLastWriteTimeUtc(source) != writeTime)
                    throw new IOException("Source file changed during optimization. Nothing was overwritten.");
                File.Replace(stage, source, null, ignoreMetadataErrors: true);
                if (settings.PreserveTimestamps)
                {
                    // Timestamp restoration is best effort; replacement already succeeded.
                    try { File.SetLastWriteTimeUtc(source, writeTime); File.SetCreationTimeUtc(source, creationTime); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            finally { try { if (File.Exists(stage)) File.Delete(stage); } catch (IOException) { } }
            return new OptimizationResult(original, new FileInfo(source).Length, bestEngine);
        }
        finally
        {
            try { Directory.Delete(workdir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static List<ToolInvocation> CreateInvocations(string input, string workspace, AppSettings s)
    {
        var result = new List<ToolInvocation>();
        var ext = Path.GetExtension(input).ToLowerInvariant();
        var level = Math.Clamp(s.OptimizationLevel, 1, 3);
        var oxipngLevel = level switch { 1 => "2", 2 => "4", _ => "6" };

        void Add(string name, string executable, string output, params string[] args)
        {
            var path = ToolResolver.Find(executable, s);
            if (path is not null) result.Add(new ToolInvocation(name, path, output, args));
        }

        if (ext == ".png")
        {
            if (s.UseOxipng)
            {
                var output = Path.Combine(workspace, "oxipng", "optimized.png");
                var args = new List<string> { "-o", oxipngLevel };
                if (s.StripPngMetadata) args.AddRange(new[] { "--strip", "safe" });
                args.AddRange(new[] { "--out", output, input });
                Add("OxiPNG", "oxipng.exe", output, args.ToArray());
            }
            if (s.EnableLossy && s.UsePngquant)
            {
                var output = Path.Combine(workspace, "pngquant", "optimized.png");
                var maxQuality = Math.Clamp(s.PngQuality, 1, 100);
                var minQuality = Math.Max(0, maxQuality - 20);
                Add("pngquant (lossy)", "pngquant.exe", output,
                    "--force", "--quality", $"{minQuality}-{maxQuality}", "--output", output, input);
            }
        }
        else if (ext is ".jpg" or ".jpeg")
        {
            if (s.UseJpegtran && !s.EnableLossy)
            {
                var output = Path.Combine(workspace, "jpegtran", "optimized" + ext);
                var args = new List<string> { "-copy", s.StripJpegMetadata ? "none" : "all", "-optimize" };
                if (level == 3) args.Add("-progressive");
                args.AddRange(new[] { "-outfile", output, input });
                Add("jpegtran", "jpegtran.exe", output, args.ToArray());
            }
            if (s.UseJpegoptim)
            {
                var destFolder = Path.Combine(workspace, "jpegoptim");
                var output = Path.Combine(destFolder, Path.GetFileName(input));
                var args = new List<string> { "--dest=" + destFolder };
                if (s.StripJpegMetadata) args.Add("--strip-all");
                if (s.EnableLossy) args.Add("--max=" + Math.Clamp(s.JpegQuality, 1, 100));
                args.Add(input);
                Add(s.EnableLossy ? "jpegoptim (lossy)" : "jpegoptim", "jpegoptim.exe", output, args.ToArray());
            }
        }
        else if (ext == ".gif" && s.UseGifsicle)
        {
            var output = Path.Combine(workspace, "gifsicle", "optimized.gif");
            Add("Gifsicle", "gifsicle.exe", output, "-O3", input, "-o", output);
        }
        else if (ext == ".svg" && s.UseSvgo)
        {
            // SVGO + its Node runtime are shipped with the published Windows ZIP.
            // Run Node directly rather than assuming npm or npx is installed.
            var node = ToolResolver.Find("node.exe", s);
            var script = ToolResolver.FindEmbeddedAsset(
                Path.Combine("svgo", "node_modules", "svgo", "bin", "svgo"), s);
            if (node is not null && script is not null)
            {
                var output = Path.Combine(workspace, "svgo", "optimized.svg");
                result.Add(new ToolInvocation("SVGO", node, output,
                    new[] { script, "--input", input, "--output", output, "--multipass" }));
            }
        }
        return result;
    }

    private static async Task<(int Code, string Error)> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
    {
        var psi = new ProcessStartInfo(invocation.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(invocation.OutputFile)!
        };
        foreach (var arg in invocation.Arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new IOException($"Could not start {invocation.Name}.");
        var stderr = process.StandardError.ReadToEndAsync(token);
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(stderr, stdout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, await stderr.ConfigureAwait(false));
    }

    // Reject clearly corrupted/mismatched results before replacing an original.
    // These checks are deliberately lightweight and are not full image-decode tests.
    private static bool LooksLikeExpectedFormat(string path, string extension)
    {
        try
        {
            if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = XmlReader.Create(path, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });
                reader.MoveToContent();
                return reader.NodeType == XmlNodeType.Element && reader.LocalName == "svg";
            }

            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            var count = stream.Read(header);
            return extension.ToLowerInvariant() switch
            {
                ".png" => count >= 8 && header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                ".jpg" or ".jpeg" => count >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
                ".gif" => count >= 6 && ("GIF87a"u8.SequenceEqual(header[..6]) || "GIF89a"u8.SequenceEqual(header[..6])),
                _ => false
            };
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException) { return false; }
    }

    private static string ShortError(string stderr, int code)
    {
        var message = stderr.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length > 180) message = message[..180] + "…";
        return string.IsNullOrEmpty(message) ? $"exit code {code}" : message;
    }
}
