using System.IO;
using ImageOptim.Win.Models;

namespace ImageOptim.Win.Services;

/// <summary>Session-scoped originals used only for the Revert command.</summary>
public sealed class BackupManager : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ImageOptimWin", Guid.NewGuid().ToString("N"));

    public void EnsureBackup(ImageJob job)
    {
        if (job.BackupPath is not null && File.Exists(job.BackupPath)) return;
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, Guid.NewGuid().ToString("N") + Path.GetExtension(job.FullPath));
        File.Copy(job.FullPath, destination);
        job.BackupPath = destination;
    }

    public void Revert(ImageJob job)
    {
        if (job.BackupPath is null || !File.Exists(job.BackupPath))
            throw new InvalidOperationException("No session backup is available for this file.");
        var stage = Path.Combine(Path.GetDirectoryName(job.FullPath)!,
            $".imageoptim-restore-{Guid.NewGuid():N}{Path.GetExtension(job.FullPath)}");
        try
        {
            File.Copy(job.BackupPath, stage);
            File.Replace(stage, job.FullPath, null);
            File.Delete(job.BackupPath);
            job.ResetAfterRestore();
        }
        finally { try { if (File.Exists(stage)) File.Delete(stage); } catch (IOException) { } }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Session files can be cleaned by OS. */ }
        catch (UnauthorizedAccessException) { }
    }
}
