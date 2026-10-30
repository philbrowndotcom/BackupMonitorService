using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace BackupMonitorService
{
    public class Worker(IOptions<BackupMonitorOptions> options, BlobBackupRunner runner, ILogger<Worker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var opts = options.Value;
            var debounce = TimeSpan.FromSeconds(opts.DebounceSeconds);
            var retryDelay = TimeSpan.FromSeconds(opts.RetryDelaySeconds);
            TimeSpan? fullSyncInterval = opts.FullSyncIntervalMinutes > 0 ? TimeSpan.FromMinutes(opts.FullSyncIntervalMinutes) : null;

            if (!Uri.TryCreate(opts.ContainerUrl, UriKind.Absolute, out _))
            {
                logger.LogError("{Section}:ContainerUrl is missing or invalid; nothing to back up", BackupMonitorOptions.SectionName);
                return;
            }

            var monitors = new List<FolderMonitor>();
            foreach (var folder in opts.Folders)
            {
                if (string.IsNullOrWhiteSpace(folder.Path))
                {
                    logger.LogError("Skipping folder entry with no Path");
                    continue;
                }
                if (!Directory.Exists(folder.Path))
                {
                    logger.LogError("Skipping {Folder}: folder does not exist", folder.Path);
                    continue;
                }

                var monitor = new FolderMonitor(folder, debounce, logger);
                if (opts.BackupOnStartup)
                {
                    monitor.ScheduleBackup(TimeSpan.Zero);
                }
                monitors.Add(monitor);
                logger.LogInformation("Monitoring {Folder} -> {Destination}", folder.Path, opts.GetDisplayDestination(folder));
            }

            if (monitors.Count == 0)
            {
                logger.LogWarning("No valid folders configured in {Section}:Folders; nothing to monitor", BackupMonitorOptions.SectionName);
                return;
            }

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    var now = DateTime.UtcNow;
                    foreach (var monitor in monitors)
                    {
                        if (monitor.IsDue(now, fullSyncInterval))
                        {
                            monitor.MarkBackupStarted(now);
                            monitor.CurrentBackup = RunBackupAsync(monitor, retryDelay, stoppingToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            finally
            {
                await Task.WhenAll(monitors.Select(m => m.CurrentBackup ?? Task.CompletedTask));
                monitors.ForEach(m => m.Dispose());
            }
        }

        private async Task RunBackupAsync(FolderMonitor monitor, TimeSpan retryDelay, CancellationToken stoppingToken)
        {
            // Run off the timer loop so a long backup doesn't hold up other folders.
            await Task.Yield();

            var folder = monitor.Folder;
            var stopwatch = Stopwatch.StartNew();
            logger.LogInformation("Backing up {Folder} -> {Destination}", folder.Path, options.Value.GetDisplayDestination(folder));

            try
            {
                await runner.RunAsync(folder, stoppingToken);
                logger.LogInformation("Backup of {Folder} completed in {Elapsed}", folder.Path, stopwatch.Elapsed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Backup of {Folder} cancelled by service shutdown", folder.Path);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup of {Folder} failed; retrying in {RetryDelay}", folder.Path, retryDelay);
                monitor.ScheduleBackup(retryDelay);
            }
        }
    }
}
