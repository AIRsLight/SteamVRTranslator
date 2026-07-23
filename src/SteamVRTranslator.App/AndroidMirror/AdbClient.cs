using System.Diagnostics;
using System.Text;

namespace SteamVRTranslator.App.AndroidMirror;

internal sealed record AndroidDeviceInfo(
    string Serial,
    string State,
    string Model,
    string Product)
{
    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(Model)
        ? $"{Serial} ({State})"
        : $"{Model} · {Serial}{(IsOnline ? string.Empty : $" ({State})")}";
}

internal sealed record AdbCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public void EnsureSuccess(string operation)
    {
        if (ExitCode == 0)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        throw new InvalidOperationException($"{operation}失败：{detail.Trim()}");
    }
}

internal sealed class AdbClient(string executablePath)
{
    public string ExecutablePath { get; } = executablePath;

    public async Task<IReadOnlyList<AndroidDeviceInfo>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["devices", "-l"], cancellationToken);
        result.EnsureSuccess("获取 ADB 设备");
        var devices = new List<AndroidDeviceInfo>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            devices.Add(new AndroidDeviceInfo(
                fields[0],
                fields[1],
                ReadProperty(fields, "model"),
                ReadProperty(fields, "product")));
        }

        return devices;
    }

    public Task<AdbCommandResult> RunForDeviceAsync(
        string serial,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        RunAsync(["-s", serial, .. arguments], cancellationToken);

    public async Task<AdbCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        using var process = CreateProcess(arguments);
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new AdbCommandResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    public Process StartForDevice(
        string serial,
        IReadOnlyList<string> arguments,
        Action<string>? output = null)
    {
        var process = CreateProcess(["-s", serial, .. arguments]);
        if (output is not null)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    output(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    output(e.Data);
                }
            };
        }

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private Process CreateProcess(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = info, EnableRaisingEvents = true };
    }

    private static string ReadProperty(IEnumerable<string> fields, string name)
    {
        var prefix = name + ":";
        return fields.FirstOrDefault(field => field.StartsWith(prefix, StringComparison.Ordinal))?
            [prefix.Length..] ?? string.Empty;
    }
}
