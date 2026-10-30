using Azure;
using BackupMonitorService;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "BackupMonitorService");

builder.Services.Configure<BackupMonitorOptions>(builder.Configuration.GetSection(BackupMonitorOptions.SectionName));
builder.Services.AddSingleton<BlobBackupRunner>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// "--test-auth" checks each folder's destination can be reached with the configured credentials,
// then exits without watching or uploading anything.
if (args.Contains("--test-auth", StringComparer.OrdinalIgnoreCase))
{
    var options = host.Services.GetRequiredService<IOptions<BackupMonitorOptions>>().Value;
    var runner = host.Services.GetRequiredService<BlobBackupRunner>();
    var failures = 0;

    Console.WriteLine($"Container: {options.ContainerUrl}");
    Console.WriteLine($"Auth:      {(options.SasToken.Trim().Length > 0 ? "SAS token" : "DefaultAzureCredential")}");

    foreach (var folder in options.Folders)
    {
        var destination = options.GetDisplayDestination(folder);
        try
        {
            var hasBlobs = await runner.TestAccessAsync(folder, CancellationToken.None);
            Console.WriteLine($"OK    {destination} ({(hasBlobs ? "has existing backups" : "empty")})");
        }
        catch (RequestFailedException ex)
        {
            failures++;
            Console.WriteLine($"FAIL  {destination}: HTTP {ex.Status} {ex.ErrorCode}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"FAIL  {destination}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    return failures == 0 ? 0 : 1;
}

host.Run();
return 0;
