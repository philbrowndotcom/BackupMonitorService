namespace BackupMonitorService
{
    /// <summary>
    /// Watches one folder and tracks when it is next due for backup. Each change pushes the due time
    /// out by the debounce period, so a burst of writes results in a single backup once things settle.
    /// </summary>
    internal sealed class FolderMonitor : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly TimeSpan _debounce;
        private readonly ILogger _logger;

        // UTC ticks when a backup is due; 0 when nothing is pending.
        private long _dueTicks;

        public FolderMonitor(BackupFolder folder, TimeSpan debounce, ILogger logger)
        {
            Folder = folder;
            _debounce = debounce;
            _logger = logger;

            _watcher = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = folder.IncludeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }

        public BackupFolder Folder { get; }

        public DateTime LastBackupStartUtc { get; private set; } = DateTime.UtcNow;

        public Task? CurrentBackup { get; set; }

        public void ScheduleBackup(TimeSpan delay) =>
            Interlocked.Exchange(ref _dueTicks, (DateTime.UtcNow + delay).Ticks);

        public bool IsDue(DateTime nowUtc, TimeSpan? fullSyncInterval)
        {
            if (CurrentBackup is { IsCompleted: false })
            {
                return false;
            }

            var due = Interlocked.Read(ref _dueTicks);
            if (due != 0 && nowUtc.Ticks >= due)
            {
                return true;
            }

            return fullSyncInterval is { } interval && nowUtc - LastBackupStartUtc >= interval;
        }

        /// <summary>
        /// Clears the pending flag at the start of a backup. Changes that arrive while the backup is
        /// running set it again, so they get picked up by a follow-up backup.
        /// </summary>
        public void MarkBackupStarted(DateTime nowUtc)
        {
            Interlocked.Exchange(ref _dueTicks, 0);
            LastBackupStartUtc = nowUtc;
        }

        private void OnChanged(object sender, FileSystemEventArgs e) => ScheduleBackup(_debounce);

        private void OnError(object sender, ErrorEventArgs e)
        {
            // Usually a buffer overflow from a large burst of changes; individual events were lost,
            // but a backup is a full sync so scheduling one covers them.
            _logger.LogWarning(e.GetException(), "File watcher error for {Folder}", Folder.Path);
            ScheduleBackup(_debounce);
        }

        public void Dispose() => _watcher.Dispose();
    }
}
