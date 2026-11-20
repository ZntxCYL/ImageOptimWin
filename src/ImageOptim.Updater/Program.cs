using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ImageOptim.Updater;

internal static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageOptimWin", "updater.log");

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var opts = ParseArguments(args);
            string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(opts["--install-dir"]));
            string archive = Path.GetFullPath(opts["--archive"]);
            string expectedHash = opts["--sha256"];
            int pid = int.Parse(opts["--parent-pid"]);
            string token = opts["--health-token"];
            string version = opts["--version"];
            if (!Regex.IsMatch(expectedHash, "^[a-fA-F0-9]{64}$") ||
                !Regex.IsMatch(token, "^[a-f0-9]{32}$") ||
                !Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$"))
                throw new InvalidDataException("更新参数格式不正确。");
            if (!File.Exists(archive) || !Directory.Exists(install))
                throw new DirectoryNotFoundException("更新包或应用安装目录不存在。");
            if (Path.GetPathRoot(install) == install)
                throw new InvalidDataException("禁止替换磁盘根目录。");
            if (!File.Exists(Path.Combine(install, "ImageOptim.Win.exe")))
                throw new InvalidDataException("目标目录不是 ImageOptimWin 应用目录。");

            // The detached updater is located in LOCALAPPDATA, not in the installation folder.
            // Never run the copy that resides in a folder about to be swapped.
            string updaterPath = Path.GetFullPath(Environment.ProcessPath ?? "");
            if (updaterPath.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新器必须从临时目录运行，不能覆盖自身。");

            Log($"开始更新至 {version}；正在检查下载包完整性");
            await using (var input = File.OpenRead(archive))
            {
                string sha = Convert.ToHexString(await SHA256.HashDataAsync(input));
                if (!sha.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("更新包 SHA-256 复核失败，已拒绝安装。");
            }

            string parent = Directory.GetParent(install)!.FullName;
            string name = Path.GetFileName(install);
            string id = Guid.NewGuid().ToString("N");
            string stage = Path.Combine(parent, "." + name + ".stage-" + id);
            string backup = Path.Combine(parent, name + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + id[..8]);
            string failed = Path.Combine(parent, "." + name + ".failed-" + id);
            bool backupCreated = false;
            bool newInstalled = false;
            bool healthy = false;
            Process? launched = null;
            bool launchedStarted = false;
            try
            {
                ExtractSafely(archive, stage);
                // Preserve source-independent user preferences, stored under LOCALAPPDATA.
                Log("新版本解压完成；等待旧程序退出");
                await WaitForExitAsync(pid, TimeSpan.FromSeconds(120));
                Directory.Move(install, backup);
                backupCreated = true;
                Directory.Move(stage, install);
                newInstalled = true;

                string healthMarker = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ImageOptimWin", "updates", "health-" + token + ".ok");
                TryDeleteFile(healthMarker);
                launched = new Process();
                launched.StartInfo = new ProcessStartInfo(Path.Combine(install, "ImageOptim.Win.exe"))
                {
                    UseShellExecute = true,
                    WorkingDirectory = install
                };
                launched.StartInfo.ArgumentList.Add("--update-health-token");
                launched.StartInfo.ArgumentList.Add(token);
                if (!launched.Start()) throw new InvalidOperationException("新版本程序启动失败。");
                launchedStarted = true;

                for (int attempt = 0; attempt < 100; attempt++)
                {
                    if (File.Exists(healthMarker)) { healthy = true; break; }
                    if (launched.HasExited) break;
                    await Task.Delay(500);
                }
                if (!healthy) throw new TimeoutException("新版本未能在 50 秒内确认启动，正在回滚。");
                Log("新版本启动成功，旧版本已备份至 " + backup);
                TryDeleteFile(healthMarker);
                return 0;
            }
            catch (Exception failure)
            {
                Log("安装失败，尝试回滚：" + failure);
                if (backupCreated)
                {
                    try
                    {
                        // A newly launched WPF process can keep DLL/EXE files locked.
                        // Stop it before renaming the failed installation directory.
                        if (launchedStarted && launched is { HasExited: false })
                        {
                            launched.Kill(entireProcessTree: true);
                            await launched.WaitForExitAsync();
                        }
                        if (newInstalled && Directory.Exists(install)) Directory.Move(install, failed);
                        if (!Directory.Exists(install)) Directory.Move(backup, install);
                        Log("旧版本目录已恢复。保留故障文件夹：" + failed);
                        TryStartOldVersion(install);
                    }
                    catch (Exception rollbackError)
                    {
                        Log("自动回滚失败：" + rollbackError);
                        throw new AggregateException("安装失败，并且自动回滚失败。请手动恢复备份：" + backup,
                            failure, rollbackError);
                    }
                }
                throw;
            }
            finally
            {
                launched?.Dispose();
                if (!healthy && Directory.Exists(stage))
                {
                    try { Directory.Delete(stage, true); } catch (IOException) { }
                }
            }
        }
        catch (Exception ex)
        {
            Log("更新器退出：" + ex);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("更新参数不完整。");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2) result.Add(args[i], args[i + 1]);
        foreach (var needed in new[] { "--install-dir", "--archive", "--sha256", "--parent-pid", "--health-token", "--version" })
            if (!result.ContainsKey(needed)) throw new ArgumentException("缺少更新参数：" + needed);
        return result;
    }

    private static void ExtractSafely(string archive, string dest)
    {
        Directory.CreateDirectory(dest);
        string prefix = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is < 2 or > 10000) throw new InvalidDataException("更新 ZIP 文件数量异常。");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Contains('\\') || entry.FullName.Contains(':') || entry.FullName.StartsWith('/'))
                throw new InvalidDataException("ZIP 包含非法文件路径。");
            string target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target, dest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP 路径穿越已阻止。");
            total = checked(total + entry.Length);
            if (total > 6_000_000_000L || entry.Length > 2_000_000_000L)
                throw new InvalidDataException("ZIP 解压体积过大。");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
        foreach (string required in new[] { "ImageOptim.Win.exe", "ImageOptim.Updater.exe", "tools/oxipng.exe", "tools/node.exe", "tools/svgo/node_modules/svgo/bin/svgo" })
            if (!File.Exists(Path.Combine(dest, required.Replace('/', Path.DirectorySeparatorChar))))
                throw new InvalidDataException("发布包缺失运行组件：" + required);
    }

    private static async Task WaitForExitAsync(int pid, TimeSpan timeout)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            using var cts = new CancellationTokenSource(timeout);
            try { await parent.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("旧程序未退出，已终止更新。"); }
        }
        catch (ArgumentException) { /* Process has already exited. */ }
    }

    private static void TryStartOldVersion(string install)
    {
        string exe = Path.Combine(install, "ImageOptim.Win.exe");
        if (!File.Exists(exe)) return;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = install });
    }

    private static void TryDeleteFile(string filename)
    {
        try { File.Delete(filename); } catch (IOException) { }
    }

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* Logging failures must not prevent rollback. */ }
    }
}
