using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SteamVRTranslator.App.Localization;

namespace SteamVRTranslator.App.Output;

internal enum VbCablePackageState { Missing, Invalid, Ready }
internal sealed record VbCableDownloadProgress(long BytesDownloaded, long TotalBytes, bool Verifying = false);
internal sealed record VbCablePackage(Uri Url, long Size, string Sha256);

/// <summary>Downloads the unmodified official package. Only an explicit install action elevates.</summary>
internal sealed class VbCableInstallation : IDisposable
{
    public const string WebsiteUrl = "https://vb-audio.com/Cable/";
    public const string LicenseUrl = "https://vb-audio.com/Services/licensing.htm";
    public const string SetupFileName = "VBCABLE_Setup_x64.exe";
    public const string CatalogFileName = "vbaudio_cable64_win10.cat";
    internal static readonly VbCablePackage OfficialPackage = new(
        new Uri("https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip"),
        1318877, "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly VbCablePackage _package;
    private readonly Func<string, bool> _verifySignature;
    private readonly SemaphoreSlim _operation = new(1, 1);
    public string PackageDirectory { get; }
    public string ArchivePath => Path.Combine(PackageDirectory, "VBCABLE_Driver_Pack45.zip");

    public VbCableInstallation(string? applicationDirectory = null)
        : this(applicationDirectory ?? AppContext.BaseDirectory, null, OfficialPackage, AuthenticodeSignature.IsTrusted) { }

    internal VbCableInstallation(string rootDirectory, HttpClient? client, VbCablePackage package, Func<string, bool> verifySignature)
    {
        PackageDirectory = Path.GetFullPath(Path.Combine(rootDirectory, "drivers", "vb-cable"));
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        if (_ownsClient) _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SteamVRTranslator", "1.0"));
        _package = package;
        _verifySignature = verifySignature;
    }

    public VbCablePackageState GetPackageState()
    {
        if (!File.Exists(ArchivePath)) return VbCablePackageState.Missing;
        try
        {
            using var stream = File.OpenRead(ArchivePath);
            return stream.Length == _package.Size && Convert.ToHexString(SHA256.HashData(stream)).Equals(_package.Sha256, StringComparison.OrdinalIgnoreCase)
                ? VbCablePackageState.Ready : VbCablePackageState.Invalid;
        }
        catch (IOException) { return VbCablePackageState.Invalid; }
        catch (UnauthorizedAccessException) { return VbCablePackageState.Invalid; }
    }

    public async Task DownloadAsync(IProgress<VbCableDownloadProgress>? progress, CancellationToken token)
    {
        if (!await _operation.WaitAsync(0, token)) throw new InvalidOperationException(AppLocalization.Text("Download.AlreadyRunning"));
        var temporary = ArchivePath + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            if (await Task.Run(GetPackageState, token) == VbCablePackageState.Ready)
            {
                progress?.Report(new(_package.Size, _package.Size));
                return;
            }
            Directory.CreateDirectory(PackageDirectory);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var response = await _client.GetAsync(_package.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is { } length && length != _package.Size)
                        throw new InvalidDataException(AppLocalization.Format("Download.FileSizeMismatch", "VB-CABLE", _package.Size, length));
                    await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    {
                        var buffer = new byte[65536];
                        long downloaded = 0;
                        progress?.Report(new(0, _package.Size));
                        int count;
                        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            downloaded += count;
                            if (downloaded > _package.Size) throw new InvalidDataException(AppLocalization.Format("Download.FileSizeMismatch", "VB-CABLE", _package.Size, downloaded));
                            await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                            progress?.Report(new(downloaded, _package.Size));
                        }
                    }
                    progress?.Report(new(_package.Size, _package.Size, true));
                    await using (var stream = File.OpenRead(temporary)) await VerifyArchiveAsync(stream, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, ArchivePath, overwrite: true);
                    progress?.Report(new(_package.Size, _package.Size));
                    return;
                }
                catch (HttpRequestException) when (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(attempt), token).ConfigureAwait(false); }
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            finally { _operation.Release(); }
        }
    }

    internal async Task<string> PrepareSetupAsync(CancellationToken token)
    {
        // Reopen and hash the complete archive for each install. Extract to a fresh
        // directory, so an old or altered extracted executable can never be reused.
        await using var source = File.OpenRead(ArchivePath);
        await VerifyArchiveAsync(source, token).ConfigureAwait(false);
        source.Position = 0;
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var directory = Path.Combine(PackageDirectory, "setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtractPackage(zip, directory, token);
            foreach (var file in new[] { SetupFileName, CatalogFileName, "vbMmeCable64_win10.inf", "vbaudio_cable64_win10.sys", "readme.txt" })
                if (!File.Exists(Path.Combine(directory, file))) throw new InvalidDataException(AppLocalization.Text("Voice.Mic.Package.Invalid"));
            if (!_verifySignature(Path.Combine(directory, SetupFileName)) || !_verifySignature(Path.Combine(directory, CatalogFileName)))
                throw new InvalidDataException(AppLocalization.Text("Voice.Mic.SignatureFailed"));
            return directory;
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    internal static void ExtractPackage(ZipArchive zip, string directory, CancellationToken token)
    {
        if (zip.Entries.Count is 0 or > 128 || zip.Entries.Sum(entry => entry.Length) > 32 * 1024 * 1024)
            throw new InvalidDataException("Invalid VB-CABLE archive size.");
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (entry.FullName.Contains(':') || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !paths.Add(path))
                throw new InvalidDataException("Invalid VB-CABLE archive path.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    private async Task VerifyArchiveAsync(Stream source, CancellationToken token)
    {
        if (source.Length != _package.Size) throw new InvalidDataException(AppLocalization.Format("Download.FileSizeMismatch", "VB-CABLE", _package.Size, source.Length));
        if (!Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false)).Equals(_package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(AppLocalization.Format("Download.HashMismatch", "VB-CABLE"));
    }

    public async Task RunAsync(bool install)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(AppLocalization.Text("Voice.Mic.Platform"));
        if (!await _operation.WaitAsync(0)) throw new InvalidOperationException(AppLocalization.Text("Download.AlreadyRunning"));
        try
        {
            var directory = await Task.Run(() => PrepareSetupAsync(CancellationToken.None));
            if (install) AudioDefaultRestoration.Save(PackageDirectory);
            Process started;
            try
            {
                started = Process.Start(CreateStartInfo(directory, install)) ??
                    throw new InvalidOperationException(AppLocalization.Text("Voice.Mic.SetupFailed"));
            }
            catch { if (install) AudioDefaultRestoration.Clear(PackageDirectory); throw; }
            using var process = started;
            await process.WaitForExitAsync();
            if (process.ExitCode is not (0 or 3010 or 1641)) throw new Win32Exception(process.ExitCode);
            // Keep the extracted files: the vendor setup can delegate work to another
            // process. Device status, not this exit code, determines whether cues enable.
        }
        finally { _operation.Release(); }
    }

    internal static ProcessStartInfo CreateStartInfo(string directory, bool install)
    {
        var start = new ProcessStartInfo(Path.Combine(directory, SetupFileName))
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = directory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(install ? "-i" : "-u");
        start.ArgumentList.Add("-h");
        return start;
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
