using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tedd.Defrag.Update;

public sealed record AvailableRelease(
    Version Version,
    string DisplayVersion,
    string AssetName,
    Uri DownloadUri,
    Uri ChecksumUri,
    Uri ReleasePageUri);

public static class ReleaseUpdater
{
    private const string Repository = "tedd/Tedd.Defrag";
    private const long MaximumArchiveBytes = 1_073_741_824;
    private const long MaximumExtractedBytes = 1_610_612_736;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly HttpClient Http = CreateHttpClient();
    private static Task<AvailableRelease?>? _pendingCheck;

    public static string DisplayVersion => ReadManifest()?.Version
        ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static Version CurrentVersion => ParseVersion(DisplayVersion) ?? new Version(0, 0, 0);

    public static bool IsPackaged => ReadManifest() is not null
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "Tedd.Defrag.Cli.exe"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "Tedd.Defrag.Desktop.exe"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "Tedd.Defrag.Worker.exe"));

    public static Task<AvailableRelease?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPackaged) return Task.FromResult<AvailableRelease?>(null);
        lock (Http)
            _pendingCheck ??= FetchLatestReleaseAsync(CancellationToken.None);
        return _pendingCheck.WaitAsync(cancellationToken);
    }

    public static bool IsNewerVersion(string current, string candidate)
    {
        Version? currentVersion = ParseVersion(current), candidateVersion = ParseVersion(candidate);
        return currentVersion is not null && candidateVersion is not null && candidateVersion > currentVersion;
    }

    public static async Task LaunchUpdateAsync(AvailableRelease release, string launcherName, IReadOnlyList<string> launchArguments, CancellationToken cancellationToken = default)
    {
        if (!IsPackaged) throw new InvalidOperationException("Self-update is available only in a release distribution.");
        if (Path.GetFileName(launcherName) != launcherName) throw new ArgumentException("The launcher must be a file in the installation directory.", nameof(launcherName));

        string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string updateDirectory = Path.Combine(localRoot, "Tedd.Defrag", "updates", release.DisplayVersion + "-" + RuntimeId());
        Directory.CreateDirectory(updateDirectory);
        string archivePath = Path.Combine(updateDirectory, release.AssetName);
        string partialPath = archivePath + ".download";

        string expectedHash = await DownloadChecksumAsync(release.ChecksumUri, cancellationToken);
        await DownloadFileAsync(release.DownloadUri, partialPath, cancellationToken);
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(partialPath), cancellationToken));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
        {
            File.Delete(partialPath);
            throw new InvalidDataException("The downloaded release did not match its published SHA-256 checksum.");
        }
        File.Move(partialPath, archivePath, true);

        string updaterSource = Path.Combine(AppContext.BaseDirectory, "Tedd.Defrag.Cli.exe");
        string updaterPath = Path.Combine(updateDirectory, "Tedd.Defrag.Updater.exe");
        File.Copy(updaterSource, updaterPath, true);
        var request = new UpdateRequest(
            Environment.ProcessId,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
            archivePath,
            expectedHash,
            release.DisplayVersion,
            launcherName,
            [.. launchArguments]);
        string requestPath = Path.Combine(updateDirectory, "request.json");
        string requestTemp = requestPath + ".tmp";
        await File.WriteAllTextAsync(requestTemp, JsonSerializer.Serialize(request, Json), cancellationToken);
        File.Move(requestTemp, requestPath, true);

        var start = new ProcessStartInfo(updaterPath) { UseShellExecute = false, WorkingDirectory = updateDirectory };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(requestPath);
        Process.Start(start)?.Dispose();
    }

    public static async Task<int> ApplyUpdateAsync(string requestPath)
    {
        try
        {
            string fullRequestPath = Path.GetFullPath(requestPath);
            Environment.CurrentDirectory = Path.GetDirectoryName(fullRequestPath)!;
            var request = JsonSerializer.Deserialize<UpdateRequest>(await File.ReadAllTextAsync(fullRequestPath), Json)
                ?? throw new InvalidDataException("The update request is invalid.");
            ValidateUpdateRequest(request);
            await WaitForProcessAsync(request.ParentProcessId, TimeSpan.FromMinutes(2));
            await WaitForInstallationProcessesAsync(request.TargetDirectory, TimeSpan.FromSeconds(30));
            await VerifyArchiveAsync(request.ArchivePath, request.ExpectedSha256);
            ApplyArchive(request);
            return 0;
        }
        catch (Exception exception)
        {
            WriteLog(exception.ToString());
            return 1;
        }
    }

    private static async Task<AvailableRelease?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, Json, cancellationToken)
            ?? throw new InvalidDataException("GitHub returned an invalid release response.");
        if (release.Draft || release.Prerelease || !IsNewerVersion(DisplayVersion, release.TagName)) return null;

        string assetName = $"Tedd.Defrag-{RuntimeId()}.zip";
        GitHubAsset? package = release.Assets.FirstOrDefault(asset => asset.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase));
        GitHubAsset? checksum = release.Assets.FirstOrDefault(asset => asset.Name.Equals(assetName + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (package is null || checksum is null) return null;
        Uri downloadUri = TrustedGitHubUri(package.BrowserDownloadUrl);
        Uri checksumUri = TrustedGitHubUri(checksum.BrowserDownloadUrl);
        return new(ParseVersion(release.TagName)!, release.TagName.TrimStart('v', 'V'), assetName, downloadUri, checksumUri, new Uri(release.HtmlUrl));
    }

    private static async Task<string> DownloadChecksumAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 4096) throw new InvalidDataException("The checksum response is unexpectedly large.");
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        string hash = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("The release checksum is invalid.");
        return hash.ToUpperInvariant();
    }

    private static async Task DownloadFileAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumArchiveBytes) throw new InvalidDataException("The release archive is unexpectedly large.");
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[1024 * 128];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaximumArchiveBytes) throw new InvalidDataException("The release archive exceeded the download limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
    }

    private static async Task VerifyArchiveAsync(string archivePath, string expectedHash)
    {
        await using var stream = File.OpenRead(archivePath);
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
            throw new InvalidDataException("The staged release no longer matches its SHA-256 checksum.");
    }

    private static void ApplyArchive(UpdateRequest request)
    {
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.TargetDirectory));
        string parent = Directory.GetParent(target)?.FullName ?? throw new InvalidOperationException("The installation directory cannot be a drive root.");
        string operationId = Guid.NewGuid().ToString("N");
        string staged = Path.Combine(parent, $".{Path.GetFileName(target)}.update-{operationId}");
        string backup = Path.Combine(parent, $".{Path.GetFileName(target)}.previous-{operationId}");
        Directory.CreateDirectory(staged);
        try
        {
            ExtractArchive(request.ArchivePath, staged);
            ValidateStagedRelease(staged, request.Version, request.LauncherName);
            Directory.Move(target, backup);
            try
            {
                Directory.Move(staged, target);
                string launcher = Path.Combine(target, request.LauncherName);
                var start = new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = target };
                foreach (string argument in request.LaunchArguments) start.ArgumentList.Add(argument);
                Process.Start(start)?.Dispose();
            }
            catch
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(backup, target);
                throw;
            }
            try { Directory.Delete(backup, true); }
            catch (Exception exception) { WriteLog("Update succeeded, but the previous installation could not be removed: " + exception.Message); }
        }
        finally
        {
            if (Directory.Exists(staged))
                try { Directory.Delete(staged, true); } catch { }
        }
    }

    private static void ExtractArchive(string archivePath, string stagedDirectory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedDirectory)) + Path.DirectorySeparatorChar;
        long total = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            total = checked(total + entry.Length);
            if (total > MaximumExtractedBytes) throw new InvalidDataException("The release archive expands beyond the safety limit.");
            string destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The release archive contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static void ValidateStagedRelease(string staged, string expectedVersion, string launcherName)
    {
        foreach (string file in new[] { "Tedd.Defrag.Cli.exe", "Tedd.Defrag.Desktop.exe", "Tedd.Defrag.Worker.exe", launcherName, "release-manifest.json" })
            if (!File.Exists(Path.Combine(staged, file))) throw new InvalidDataException($"The release is missing {file}.");
        ReleaseManifest? manifest = ReadManifest(Path.Combine(staged, "release-manifest.json"));
        if (manifest is null || !string.Equals(manifest.Version, expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The release manifest version does not match the requested update.");
    }

    private static void ValidateUpdateRequest(UpdateRequest request)
    {
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.TargetDirectory));
        if (Directory.GetParent(target) is null || !Directory.Exists(target)) throw new InvalidOperationException("The installation directory is invalid.");
        if (!File.Exists(Path.Combine(target, "release-manifest.json"))) throw new InvalidOperationException("The target is not a release distribution.");
        if (!File.Exists(request.ArchivePath)) throw new FileNotFoundException("The staged release archive was not found.", request.ArchivePath);
        if (request.ExpectedSha256.Length != 64 || !request.ExpectedSha256.All(Uri.IsHexDigit)) throw new InvalidDataException("The expected checksum is invalid.");
        if (Path.GetFileName(request.LauncherName) != request.LauncherName) throw new InvalidDataException("The launcher name is invalid.");
    }

    private static async Task WaitForProcessAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            using var cancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (ArgumentException) { }
    }

    private static async Task WaitForInstallationProcessesAsync(string directory, TimeSpan timeout)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            bool found = false;
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (path is not null && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                    }
                    catch { }
                }
            }
            if (!found) return;
            await Task.Delay(500);
        }
        throw new IOException("An application process is still using the installation directory.");
    }

    private static ReleaseManifest? ReadManifest() => ReadManifest(Path.Combine(AppContext.BaseDirectory, "release-manifest.json"));

    private static ReleaseManifest? ReadManifest(string path)
    {
        try { return JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(path), Json); }
        catch { return null; }
    }

    private static Version? ParseVersion(string value)
    {
        string numeric = value.Trim().TrimStart('v', 'V').Split('+')[0].Split('-')[0];
        return Version.TryParse(numeric, out Version? version) ? version : null;
    }

    private static string RuntimeId() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException("Release updates are available for Windows x64 and ARM64.")
    };

    private static Uri TrustedGitHubUri(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub returned an untrusted release URL.");
        return uri;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tedd.Defrag/" + DisplayVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static void WriteLog(string message)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedd.Defrag");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "update.log"), $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private sealed record ReleaseManifest(string Version, string Runtime, string AssetName);
    private sealed record UpdateRequest(int ParentProcessId, string TargetDirectory, string ArchivePath, string ExpectedSha256, string Version, string LauncherName, string[] LaunchArguments);
    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        bool Draft,
        bool Prerelease,
        IReadOnlyList<GitHubAsset> Assets);
    private sealed record GitHubAsset(string Name, [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
