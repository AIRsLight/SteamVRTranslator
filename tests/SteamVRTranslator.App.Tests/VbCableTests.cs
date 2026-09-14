using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using SteamVRTranslator.App.Output;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class VbCableTests
{
    [Theory]
    [InlineData(@"ROOT\MEDIA\0001")]
    [InlineData(@"{1}.root\media\0001")]
    public void ControllerIdentityCanRouteCuesWithoutActivatingUnsupportedTopology(string controllerId)
    {
        const string adapter = @"ROOT\MEDIA\0001";
        var identity = VbCableDevice.ResolveAdapter([adapter],
        [
            new("controller", () => controllerId, IsController: true),
            new("topology", () => throw new InvalidOperationException("Topology must not be needed."))
        ], _ => throw new InvalidOperationException("No parent lookup needed."));
        Assert.Equal(adapter, identity);
        Assert.True(VbCableDevice.SelectEndpoints([adapter],
        [
            new("render", identity!, DataFlow.Render, true, 1),
            new("capture", identity!, DataFlow.Capture, true, 3)
        ]).Ready);
    }

    [Fact]
    public void EndpointParentLinksRecoverWhenControllerPropertyIsUnavailable()
    {
        const string adapter = @"ROOT\MEDIA\0001";
        var diagnostics = new List<string>();
        var identity = VbCableDevice.ResolveAdapter([adapter],
        [
            new("controller", () => throw new COMException("Property unavailable", unchecked((int)0x80004002))),
            new("endpoint", () => "endpoint", true),
            new("topology", () => throw new InvalidOperationException("Parent lookup should resolve first."))
        ], id => id switch { "endpoint" => "child", "child" => adapter, _ => null }, diagnostics.Add);
        Assert.Equal(adapter, identity);
        Assert.Contains(diagnostics, line => line.Contains("80004002"));
    }

    [Fact]
    public void MissingPropertiesCanStillUseTopologyAndLookupFailuresAreReported()
    {
        const string adapter = @"ROOT\MEDIA\0001";
        var identity = VbCableDevice.ResolveAdapter([adapter],
        [new("controller", () => null), new("endpoint", () => "Unknown", true), new("topology", () => adapter)], _ => null);
        Assert.Equal(adapter, identity);
        var diagnostics = new List<string>();
        Assert.Null(VbCableDevice.ResolveAdapter([adapter],
            [new("topology", () => throw new InvalidCastException("No IDeviceTopology"))], _ => null, diagnostics.Add));
        Assert.Contains(diagnostics, line => line.Contains("No IDeviceTopology"));
    }

    [Theory]
    [InlineData(@"{1}.ROOT\STEAMSTREAMINGSPEAKERS\0000")]
    [InlineData(@"{1}.ROOT\MEDIA\0000")]
    [InlineData(@"{1}.HDAUDIO\FUNC_01&VEN_10EC\0001")]
    public void KnownUnrelatedControllerNeverActivatesItsTopology(string controller)
    {
        var topologyRead = false;
        var identity = VbCableDevice.ResolveAdapter([@"ROOT\MEDIA\0001"],
        [
            new("controller", () => controller, IsController: true),
            new("topology", () => { topologyRead = true; throw new NullReferenceException("ConnectorCount"); })
        ], _ => null);
        Assert.Null(identity);
        Assert.False(topologyRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FaultyUnrelatedTopologyCannotDiscardUsableCableEndpoints(bool faultAfterCable)
    {
        const string adapter = @"ROOT\MEDIA\0001";
        var diagnostics = new List<string>();
        var devices = new List<(string Id, DataFlow Flow, VbCableIdentitySource Source)>
        {
            ("render", DataFlow.Render, new("controller", () => "{1}." + adapter, IsController: true)),
            ("capture", DataFlow.Capture, new("controller", () => "{1}." + adapter, IsController: true))
        };
        devices.Insert(faultAfterCable ? devices.Count : 0,
            ("unrelated", DataFlow.Render, new("topology", () => throw new NullReferenceException("ConnectorCount"))));
        var endpoints = new List<VbCableEndpoint>();
        foreach (var device in devices)
        {
            var identity = VbCableDevice.ResolveAdapter([adapter], [device.Source], _ => null, diagnostics.Add);
            if (identity is not null) endpoints.Add(new(device.Id, identity, device.Flow, true, 1));
        }
        var status = VbCableDevice.SelectEndpoints([adapter], endpoints);
        Assert.True(status.Ready);
        Assert.Equal("render", status.PlaybackDeviceId);
        Assert.Equal("capture", status.RecordingDeviceId);
        Assert.Contains(diagnostics, line => line.Contains("ConnectorCount"));
    }

    [Fact]
    public void UnknownControllerFormatStillAllowsFallbackIdentityLookup()
    {
        const string adapter = @"ROOT\MEDIA\0001";
        Assert.Equal(adapter, VbCableDevice.ResolveAdapter([adapter],
            [new("controller", () => "unknown-format", IsController: true), new("topology", () => adapter)], _ => null));
    }

    [Theory]
    [InlineData(@"{1}.ROOT\MEDIA\00010")]
    [InlineData(@"{1}.ROOT\MEDIA\0002")]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    public void SimilarIdsAndRenamedPhysicalDevicesCannotMatchCable(string identity)
    {
        Assert.Null(VbCableDevice.ResolveAdapter([@"ROOT\MEDIA\0001"], [new("controller", () => identity)], _ => null));
    }

    [Fact]
    public void CyclicAndUnboundedParentLinksStopWithoutMatchingUnrelatedDevices()
    {
        var reads = 0;
        Assert.Null(VbCableDevice.ResolveAdapter(["cable"], [new("endpoint", () => "one", true)],
            id => { reads++; return id == "one" ? "two" : "one"; }));
        Assert.Equal(2, reads);
        reads = 0;
        Assert.Null(VbCableDevice.ResolveAdapter(["cable"], [new("endpoint", () => "one", true)],
            _ => (++reads).ToString()));
        Assert.Equal(16, reads);
    }

    [Fact]
    public void AutomaticRoutingRequiresACompletePairOnOneCableAdapter()
    {
        string[] adapters = ["cable"];
        var render = new VbCableEndpoint("render", "cable", DataFlow.Render, true, 1);
        var capture = new VbCableEndpoint("capture", "cable", DataFlow.Capture, true, 3);
        Assert.False(VbCableDevice.SelectEndpoints([], [render, capture]).Ready);
        Assert.False(VbCableDevice.SelectEndpoints(adapters, [render]).Ready);
        Assert.False(VbCableDevice.SelectEndpoints(adapters, [render, capture with { Active = false }]).Ready);
        Assert.False(VbCableDevice.SelectEndpoints(adapters, [render, capture with { AdapterInstanceId = "other" }]).Ready);
        var status = VbCableDevice.SelectEndpoints(adapters, [render, capture]);
        Assert.True(status.Ready);
        Assert.Equal("render", status.PlaybackDeviceId);
        Assert.Equal("capture", status.RecordingDeviceId);
    }

    [Fact]
    public void SpeakerPinIsPreferredOverSixteenChannelPinAndUnrelatedSpeakersAreIgnored()
    {
        var status = VbCableDevice.SelectEndpoints(["cable"],
        [
            new("speakers", "physical", DataFlow.Render, true, 1),
            new("sixteen", "cable", DataFlow.Render, true, 2),
            new("renamed-cable", "CABLE", DataFlow.Render, true, 1),
            new("recording", "cable", DataFlow.Capture, true, 3)
        ]);
        Assert.True(status.Ready);
        Assert.Equal("renamed-cable", status.PlaybackDeviceId);
        Assert.DoesNotContain("speakers", status.DeviceIds);
        Assert.Contains("sixteen", status.DeviceIds);
    }

    [Fact]
    public void AmbiguousMultipleCablesAreNotChosenArbitrarily()
    {
        var devices = new[] { "one", "two" }.SelectMany(id => new[]
        {
            new VbCableEndpoint(id + "-render", id, DataFlow.Render, true, 1),
            new VbCableEndpoint(id + "-capture", id, DataFlow.Capture, true, 3)
        });
        var status = VbCableDevice.SelectEndpoints(["one", "two"], devices);
        Assert.True(status.Installed);
        Assert.False(status.Ready);
    }

    [Fact]
    public async Task DownloadVerifiesAndReusesArchiveThenExtractsFreshOfficialFiles()
    {
        using var fixture = new PackageFixture();
        using var service = fixture.CreateService();
        var progress = new List<VbCableDownloadProgress>();
        Assert.Equal(VbCablePackageState.Missing, service.GetPackageState());
        await service.DownloadAsync(new InlineProgress(progress.Add), CancellationToken.None);
        Assert.Equal(VbCablePackageState.Ready, service.GetPackageState());
        Assert.Contains(progress, value => value.Verifying);
        Assert.Equal(fixture.Bytes.Length, progress[^1].BytesDownloaded);
        await service.DownloadAsync(null, CancellationToken.None);
        Assert.Equal(1, fixture.Requests);
        var first = await service.PrepareSetupAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(first, VbCableInstallation.SetupFileName), "tampered cached installer");
        var second = await service.PrepareSetupAsync(CancellationToken.None);
        Assert.NotEqual(first, second);
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(second, VbCableInstallation.SetupFileName)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorruptDownloadsNeverBecomeInstallable(bool wrongSize)
    {
        using var fixture = new PackageFixture();
        var corrupt = fixture.Bytes.ToArray();
        if (wrongSize) corrupt = corrupt[..^1]; else corrupt[10] ^= 1;
        fixture.Response = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt) });
        using var service = fixture.CreateService();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(null, CancellationToken.None));
        Assert.Equal(VbCablePackageState.Missing, service.GetPackageState());
        Assert.Empty(Directory.GetFiles(service.PackageDirectory));
    }

    [Fact]
    public async Task CancelledDownloadCleansTemporaryFileAndCanRetry()
    {
        using var fixture = new PackageFixture();
        using var service = fixture.CreateService();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(
            new InlineProgress(value => { if (value.BytesDownloaded > 0) cancellation.Cancel(); }), cancellation.Token));
        Assert.False(File.Exists(service.ArchivePath));
        Assert.Empty(Directory.GetFiles(service.PackageDirectory));
        await service.DownloadAsync(null, CancellationToken.None);
        Assert.Equal(VbCablePackageState.Ready, service.GetPackageState());
    }

    [Fact]
    public async Task ConcurrentRequestsAreRejectedAndCancellationReleasesTheOperation()
    {
        using var fixture = new PackageFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        };
        using var service = fixture.CreateService();
        using var cancellation = new CancellationTokenSource();
        var first = service.DownloadAsync(null, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(null, CancellationToken.None));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task ChangedArchiveOrUntrustedSignaturePreventsInstallerPreparation()
    {
        using var fixture = new PackageFixture();
        using var service = fixture.CreateService(verifySignature: _ => false);
        await service.DownloadAsync(null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareSetupAsync(CancellationToken.None));
        Assert.Empty(Directory.GetDirectories(service.PackageDirectory));
        File.WriteAllText(service.ArchivePath, "replaced archive");
        Assert.Equal(VbCablePackageState.Invalid, service.GetPackageState());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareSetupAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("setup.exe:payload")]
    public async Task ArchiveEntriesCannotEscapeExtractionDirectory(string maliciousName)
    {
        using var fixture = new PackageFixture(maliciousName);
        using var service = fixture.CreateService();
        await service.DownloadAsync(null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareSetupAsync(CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(service.PackageDirectory, "escape.exe")));
        Assert.Empty(Directory.GetDirectories(service.PackageDirectory));
    }

    [Fact]
    public async Task ServerErrorCanRetryWithoutLeavingPartialPackage()
    {
        using var fixture = new PackageFixture();
        fixture.Response = _ => Task.FromResult(new HttpResponseMessage(fixture.Requests == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new ByteArrayContent(fixture.Bytes) });
        using var service = fixture.CreateService();
        await service.DownloadAsync(null, CancellationToken.None);
        Assert.Equal(2, fixture.Requests);
        Assert.Equal(VbCablePackageState.Ready, service.GetPackageState());
    }

    [Fact]
    public void InstallerUsesExplicitActionAndRequestsElevation()
    {
        var install = VbCableInstallation.CreateStartInfo("C:/package", true);
        Assert.Equal("runas", install.Verb);
        Assert.True(install.UseShellExecute);
        Assert.Equal(new[] { "-i", "-h" }, install.ArgumentList);
        Assert.Equal(new[] { "-u", "-h" }, VbCableInstallation.CreateStartInfo("C:/package", false).ArgumentList);
    }

    [Fact]
    public void DefaultRestorationOnlyUndoesChangesToCableAndPreservesExplicitUserChoices()
    {
        string[] cable = ["cable-render", "cable-capture"];
        Assert.True(AudioDefaultRestoration.ShouldRestore("headphones", "cable-render", true, cable));
        Assert.True(AudioDefaultRestoration.ShouldRestore("microphone", "cable-capture", true, cable));
        Assert.False(AudioDefaultRestoration.ShouldRestore("headphones", "new-speakers", true, cable));
        Assert.False(AudioDefaultRestoration.ShouldRestore("headphones", "cable-render", false, cable));
        Assert.False(AudioDefaultRestoration.ShouldRestore("cable-render", "cable-capture", true, cable));
        Assert.False(AudioDefaultRestoration.ShouldRestore("headphones", "headphones", true, cable));
    }

    private sealed class InlineProgress(Action<VbCableDownloadProgress> action) : IProgress<VbCableDownloadProgress>
    {
        public void Report(VbCableDownloadProgress value) => action(value);
    }

    private sealed class PackageFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "vb-cable-tests-" + Guid.NewGuid().ToString("N"));
        public byte[] Bytes { get; }
        public int Requests { get; private set; }
        public Func<CancellationToken, Task<HttpResponseMessage>> Response { get; set; }
        private readonly HttpClient _client;
        public PackageFixture(string? extraEntry = null)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                foreach (var name in new[] { VbCableInstallation.SetupFileName, VbCableInstallation.CatalogFileName, "vbMmeCable64_win10.inf", "vbaudio_cable64_win10.sys", "readme.txt" }.Concat(extraEntry is null ? [] : new[] { extraEntry }))
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write("fixture");
                }
            }
            Bytes = buffer.ToArray();
            Response = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
            _client = new HttpClient(new Handler(async token => { Requests++; return await Response(token); }));
        }
        public VbCableInstallation CreateService(Func<string, bool>? verifySignature = null) => new(DirectoryPath, _client,
            new VbCablePackage(new Uri("https://example.invalid/cable.zip"), Bytes.Length, Convert.ToHexString(SHA256.HashData(Bytes))), verifySignature ?? (_ => true));
        public void Dispose()
        {
            _client.Dispose();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
        private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(token);
        }
    }
}
