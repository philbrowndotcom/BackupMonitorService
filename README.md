# BackupMonitorService

A Windows service that watches local folders and backs them up to Azure Blob Storage when they change.

- Uses Windows file-change notifications. There's no polling, and an idle folder costs nothing.
- Waits for a folder to go quiet before backing it up, so copying in 500 files triggers a single backup.
- Uploads only new or changed files, compared by size and last-modified time, like `azcopy sync`.
- Never deletes anything in Azure. A file deleted locally stays in the backup.
- Uses the Azure Storage SDK directly, so no `azcopy.exe` or batch files are needed.

## How it works

| Step | Where |
|---|---|
| A `FileSystemWatcher` per folder reports creates, deletes, renames and writes. Each change pushes the folder's backup time out by `DebounceSeconds`. | `FolderMonitor.cs` |
| Once a second, any folder that's due and not already backing up starts a backup in the background. Failures are retried after `RetryDelaySeconds`. | `Worker.cs` |
| A backup lists the existing blobs under the folder's prefix, walks the local folder, and uploads files that are missing or changed. | `BlobBackupRunner.cs` |

The change notifications only decide **when** to back up. Each backup compares the whole folder against Azure, so a missed or dropped notification doesn't mean a missed file.

A file counts as unchanged when the blob has the same size and either:
- the file's last-write time matches the `sourcelastwriteutc` metadata this service stores on each upload, or
- the blob has no such metadata (it was uploaded by azcopy or another tool) and is at least as new as the file.

### Gaps and how they're covered

| Situation | Behavior |
|---|---|
| Files changed while the service was stopped | `BackupOnStartup` backs up every folder when the service starts. |
| Notification buffer overflow (huge bursts of changes) | A warning is logged and a backup is scheduled. The full comparison catches everything. |
| Backup fails (network, expired SAS) | An error is logged and the backup is retried after `RetryDelaySeconds`. |
| Folder doesn't exist at startup | It's logged and skipped until the service restarts. Install with `start= delayed-auto` so drives are mounted first. |
| Extra safety net | Set `FullSyncIntervalMinutes` (e.g. `1440`) to back up on a schedule regardless of notifications. |

## Configuration

Settings live under `BackupMonitor` and are layered, with later sources overriding earlier ones:

1. `appsettings.json`: committed to git. Holds the folders and timings, but **no secrets** and no storage account details.
2. `appsettings.Production.json`: **git-ignored**. Holds the container URL and SAS token. The installed service runs as `Production`, so it loads this file.
3. Environment variables, e.g. `BackupMonitor__SasToken`.

```json
"BackupMonitor": {
  "ContainerUrl": "",
  "SasToken": "",
  "DebounceSeconds": 30,
  "RetryDelaySeconds": 300,
  "FullSyncIntervalMinutes": 0,
  "BackupOnStartup": true,
  "MaxParallelUploads": 8,
  "Folders": [
    { "Path": "D:\\Media\\images", "Destination": "Images" },
    { "Path": "D:\\Backup",        "Destination": "Backup" }
  ]
}
```

| Setting | Meaning |
|---|---|
| `ContainerUrl` | Blob container that all folders back up into. |
| `SasToken` | Appended to every folder's URL. The leading `?` is optional. Leave empty to use `DefaultAzureCredential` (e.g. a managed identity). |
| `Folders[].Path` | Local folder to watch. |
| `Folders[].Destination` | Path inside the container, e.g. `Images` → `<ContainerUrl>/Images/...`. Empty means the container root. Case-sensitive. |
| `Folders[].IncludeSubdirectories` | Default `true`. |
| `DebounceSeconds` | Quiet period after the last change before a backup starts. |
| `RetryDelaySeconds` | Wait before retrying a failed backup. |
| `FullSyncIntervalMinutes` | Periodic backup regardless of changes. `0` disables it. |
| `BackupOnStartup` | Back up every folder when the service starts. |
| `MaxParallelUploads` | Concurrent uploads per folder. |

### Container URL and SAS token

`appsettings.Production.json`:

```json
{
  "BackupMonitor": {
    "ContainerUrl": "https://<account>.blob.core.windows.net/<container>",
    "SasToken": "sv=...&sp=...&sig=..."
  }
}
```

- The token needs **List**, **Create** and **Write** on the container (`sp=acwl` or broader). Read and Delete aren't needed.
- If you copy the token from a `.bat` file, change `%%` to `%`. Batch files escape `%` by doubling it; JSON doesn't.
- When the token expires, every backup fails with HTTP 403. Renew it before the `se=` date.

## Testing authentication

`--test-auth` lists each folder's destination using the configured credentials, then exits. Nothing is watched or uploaded.

```powershell
# From the project, using the Production settings (same as the installed service)
dotnet run --launch-profile Production -- --test-auth

# From the install folder
C:\Services\BackupMonitor\BackupMonitorService.exe --test-auth
```

```
Container: https://<account>.blob.core.windows.net/<container>
Auth:      SAS token
OK    https://<account>.blob.core.windows.net/<container>/Images (has existing backups)
OK    https://<account>.blob.core.windows.net/<container>/Backup (has existing backups)
```

It exits with code `1` if any folder fails. A `403 AuthenticationFailed` means the token is invalid, expired, or still contains `%%`.

## Install

From an **Administrator** PowerShell:

```powershell
cd BackupMonitorService
dotnet publish -c Release -r win-x64 -o C:\Services\BackupMonitor
C:\Services\BackupMonitor\BackupMonitorService.exe --test-auth
sc.exe create BackupMonitorService binPath= "C:\Services\BackupMonitor\BackupMonitorService.exe" start= delayed-auto
sc.exe start BackupMonitorService
```

The service runs as LocalSystem. That's fine for local drives. For network shares, use an account with access to the share and UNC paths, not mapped drive letters.

## Deploy changes

```powershell
cd BackupMonitorService
Stop-Service BackupMonitorService
dotnet publish -c Release -r win-x64 -o C:\Services\BackupMonitor
C:\Services\BackupMonitor\BackupMonitorService.exe --test-auth
Start-Service BackupMonitorService
```

- Stop the service first. Its files are locked while it runs.
- Publishing overwrites the config files in the install folder, so make config changes in the project.
- The service reads config only at startup. Restart it after changing settings.

## Uninstall

```powershell
Stop-Service BackupMonitorService
sc.exe delete BackupMonitorService
Remove-Item -Recurse C:\Services\BackupMonitor
```

## Logs

Logs go to **Event Viewer → Windows Logs → Application**, source `BackupMonitorService`. By default only warnings and errors are written. To also see per-backup summaries such as `D:\Backup: 3 uploaded, 41872 unchanged, 0 unreadable`, add this under `Logging` in `appsettings.Production.json`:

```json
"EventLog": { "LogLevel": { "Default": "Information" } }
```

## Development

```powershell
dotnet run
```

`dotnet run` uses the `Development` environment (`Properties/launchSettings.json`), so `appsettings.Production.json` is **not** loaded and no container is configured. To run against real storage locally, use `dotnet run --launch-profile Production`, or set the values with `dotnet user-secrets set "BackupMonitor:ContainerUrl" "..."` (and `BackupMonitor:SasToken`; without a token, backups use `DefaultAzureCredential`).

## Limitations

- Deletions aren't synced. Blobs remain after their source files are deleted.
- A file replaced by a different file of the same size with an *older* modified time (e.g. restored from an archive) isn't detected. `azcopy sync` has the same limit.
- Files that are locked when a backup runs are skipped with a warning. The backup is marked failed and retried.
