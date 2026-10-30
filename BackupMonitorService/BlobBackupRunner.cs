using System.Globalization;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace BackupMonitorService
{
    /// <summary>
    /// Syncs a folder to Azure Blob Storage. A file is uploaded when its blob
    /// is missing, or the size or source last-write time (stored in blob metadata) differs.
    /// Blobs without that metadata are compared by size and blob last-modified time instead.
    /// Blobs whose source file was deleted are left in place.
    /// </summary>
    public sealed class BlobBackupRunner(IOptions<BackupMonitorOptions> options, ILogger<BlobBackupRunner> logger)
    {
        private const string LastWriteMetadataKey = "sourcelastwriteutc";

        private readonly Lazy<DefaultAzureCredential> _credential = new(() => new DefaultAzureCredential());

        private BlobContainerClient GetContainer(BackupFolder folder, out string prefix)
        {
            var uri = new BlobUriBuilder(options.Value.GetDestinationUri(folder));
            prefix = uri.BlobName.Trim('/');
            uri.BlobName = "";

            // A SAS token authorizes directly; otherwise use the service's Azure identity.
            return uri.Sas is { Signature.Length: > 0 }
                ? new BlobContainerClient(uri.ToUri())
                : new BlobContainerClient(uri.ToUri(), _credential.Value);
        }

        /// <summary>
        /// Checks that the folder's destination can be listed, which proves the credentials are valid,
        /// without uploading anything. Returns whether any blobs already exist under the folder's prefix.
        /// </summary>
        public async Task<bool> TestAccessAsync(BackupFolder folder, CancellationToken cancellationToken)
        {
            var container = GetContainer(folder, out var prefix);
            await foreach (var page in container.GetBlobsAsync(
                new GetBlobsOptions { Prefix = prefix.Length > 0 ? prefix + "/" : null }, cancellationToken).AsPages(pageSizeHint: 1))
            {
                return page.Values.Count > 0;
            }
            return false;
        }

        /// <summary>Backs up the folder. Throws if the backup fails.</summary>
        public async Task RunAsync(BackupFolder folder, CancellationToken cancellationToken)
        {
            var container = GetContainer(folder, out var prefix);

            var existing = new Dictionary<string, (long Size, string? LastWrite, DateTimeOffset? Modified)>(StringComparer.Ordinal);
            await foreach (var blob in container.GetBlobsAsync(
                new GetBlobsOptions { Traits = BlobTraits.Metadata, Prefix = prefix.Length > 0 ? prefix + "/" : null },
                cancellationToken))
            {
                blob.Metadata.TryGetValue(LastWriteMetadataKey, out var lastWrite);
                existing[blob.Name] = (blob.Properties.ContentLength ?? -1, lastWrite, blob.Properties.LastModified);
            }

            var searchOption = folder.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            int uploaded = 0, skipped = 0, failed = 0;

            await Parallel.ForEachAsync(
                Directory.EnumerateFiles(folder.Path, "*", searchOption),
                new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxParallelUploads, CancellationToken = cancellationToken },
                async (file, ct) =>
                {
                    var relative = Path.GetRelativePath(folder.Path, file).Replace('\\', '/');
                    var blobName = prefix.Length > 0 ? $"{prefix}/{relative}" : relative;
                    var info = new FileInfo(file);
                    var lastWrite = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);

                    if (existing.TryGetValue(blobName, out var current) && current.Size == info.Length &&
                        (current.LastWrite == lastWrite ||
                         // Uploaded by another tool (e.g. azcopy) without our metadata: like azcopy sync,
                         // treat it as current if it was written after the file was last modified.
                         (current.LastWrite is null && current.Modified >= info.LastWriteTimeUtc)))
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }

                    try
                    {
                        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
                        await container.GetBlobClient(blobName).UploadAsync(
                            stream,
                            new BlobUploadOptions { Metadata = new Dictionary<string, string> { [LastWriteMetadataKey] = lastWrite } },
                            ct);
                        Interlocked.Increment(ref uploaded);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Locked or deleted mid-run; it will be picked up by the next backup.
                        logger.LogWarning("Could not read {File}: {Message}", file, ex.Message);
                        Interlocked.Increment(ref failed);
                    }
                });

            logger.LogInformation("{Folder}: {Uploaded} uploaded, {Skipped} unchanged, {Failed} unreadable", folder.Path, uploaded, skipped, failed);

            if (failed > 0)
            {
                throw new IOException($"{failed} file(s) could not be read and were not backed up.");
            }
        }
    }
}
