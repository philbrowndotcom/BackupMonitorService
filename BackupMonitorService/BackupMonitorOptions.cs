namespace BackupMonitorService
{
    public sealed class BackupMonitorOptions
    {
        public const string SectionName = "BackupMonitor";

        /// <summary>Blob container that all folders back up into, e.g. https://account.blob.core.windows.net/container</summary>
        public string ContainerUrl { get; set; } = "";

        /// <summary>
        /// SAS token appended to every folder's destination, with or without the leading '?'.
        /// Leave empty to authenticate with DefaultAzureCredential instead.
        /// </summary>
        public string SasToken { get; set; } = "";

        /// <summary>How long a folder must be quiet after a change before a backup starts.</summary>
        public int DebounceSeconds { get; set; } = 30;

        /// <summary>How long to wait before retrying a failed backup.</summary>
        public int RetryDelaySeconds { get; set; } = 300;

        /// <summary>Backs up every folder on this interval even without detected changes. 0 disables.</summary>
        public int FullSyncIntervalMinutes { get; set; } = 0;

        /// <summary>Backs up every folder when the service starts, to catch changes made while it was stopped.</summary>
        public bool BackupOnStartup { get; set; } = true;

        /// <summary>Maximum parallel uploads per folder.</summary>
        public int MaxParallelUploads { get; set; } = 8;

        public List<BackupFolder> Folders { get; set; } = [];

        /// <summary>Full destination URL for a folder, including the SAS token. Don't log this.</summary>
        public Uri GetDestinationUri(BackupFolder folder)
        {
            var url = $"{ContainerUrl.TrimEnd('/')}/{folder.Destination.Trim('/')}";
            var sas = SasToken.Trim().TrimStart('?');
            return new Uri(sas.Length > 0 ? $"{url}?{sas}" : url);
        }

        /// <summary>Destination URL without the SAS token, for logging.</summary>
        public string GetDisplayDestination(BackupFolder folder) =>
            $"{ContainerUrl.TrimEnd('/')}/{folder.Destination.Trim('/')}";
    }

    public sealed class BackupFolder
    {
        /// <summary>Local folder to monitor.</summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// Path inside the container to back up to, e.g. "Images". Empty backs up to the container root.
        /// </summary>
        public string Destination { get; set; } = "";

        public bool IncludeSubdirectories { get; set; } = true;
    }
}
