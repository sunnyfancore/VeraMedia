using System.Diagnostics;
using System.Net.Sockets;

namespace VeraMedia.Api.Services;

public interface ILibreOfficeService
{
    Task<string> ConvertToPdfAsync(string inputPath, string outputDir, CancellationToken ct);
}

public sealed class LibreOfficeListenerService : BackgroundService, ILibreOfficeService
{
    private readonly ILogger<LibreOfficeListenerService> _logger;
    private readonly SemaphoreSlim _conversionLock = new(1, 1);
    private Process? _listenerProcess;
    private bool _ready;
    private readonly object _processLock = new();

    public LibreOfficeListenerService(ILogger<LibreOfficeListenerService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartListenerAsync(stoppingToken);
                _ready = true;
                _logger.LogInformation("LibreOffice listener ready");

                // Monitor: wait for process exit or cancellation
                var tcs = new TaskCompletionSource();
                stoppingToken.Register(() => tcs.TrySetResult());

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _listenerProcess!.WaitForExitAsync(stoppingToken);
                    }
                    catch { }
                    tcs.TrySetResult();
                }, stoppingToken);

                await tcs.Task;

                if (stoppingToken.IsCancellationRequested)
                {
                    KillProcess();
                    return;
                }

                // Process exited unexpectedly
                _logger.LogWarning("LibreOffice listener exited unexpectedly, restarting...");
                _ready = false;
                await Task.Delay(2000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                KillProcess();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LibreOffice listener error, restarting in 5s...");
                _ready = false;
                try { await Task.Delay(5000, stoppingToken); } catch { return; }
            }
        }
    }

    private async Task StartListenerAsync(CancellationToken ct)
    {
        KillProcess();

        var binary = ResolveBinary();
        if (binary is null)
        {
            _logger.LogWarning("LibreOffice binary not found, conversions will not be available");
            return;
        }

        var profileDir = Path.Combine(Path.GetTempPath(), "lo-listener-profile");
        Directory.CreateDirectory(profileDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.EnvironmentVariables["SAL_NO_XINITTHREADS"] = "1";
        startInfo.EnvironmentVariables["HOME"] = "/tmp";
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--norestore");
        startInfo.ArgumentList.Add("--invisible");
        startInfo.ArgumentList.Add("--nodefault");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--nofirststartwizard");
        startInfo.ArgumentList.Add($"-env:UserInstallation=file://{profileDir}");
        startInfo.ArgumentList.Add("--accept=socket,host=127.0.0.1,port=2002;urp;");

        lock (_processLock)
        {
            _listenerProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        }

        _listenerProcess.Start();
        _logger.LogInformation("LibreOffice listener started (PID {Pid})", _listenerProcess.Id);

        // Wait for listener to be ready (port 2002 connectable)
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", 2002, ct);
                return; // Connected - listener is ready
            }
            catch
            {
                await Task.Delay(500, ct);
            }
        }

        throw new InvalidOperationException("LibreOffice listener did not become ready within 30s");
    }

    public async Task<string> ConvertToPdfAsync(string inputPath, string outputDir, CancellationToken ct)
    {
        await _conversionLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(outputDir);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            var binary = ResolveBinary() ?? throw new InvalidOperationException("LibreOffice 不可用。");

            var profileDir = Path.Combine(Path.GetTempPath(), "lo-listener-profile");

            var startInfo = new ProcessStartInfo
            {
                FileName = binary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.EnvironmentVariables["SAL_NO_XINITTHREADS"] = "1";
            startInfo.EnvironmentVariables["HOME"] = "/tmp";
            startInfo.ArgumentList.Add($"-env:UserInstallation=file://{profileDir}");
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--nologo");
            startInfo.ArgumentList.Add("--nofirststartwizard");
            startInfo.ArgumentList.Add("--convert-to");
            startInfo.ArgumentList.Add("pdf");
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(outputDir);
            startInfo.ArgumentList.Add(inputPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动转换进程。");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("PDF 转换超时。");
            }

            var stderr = (await stderrTask).Trim();
            var stdout = (await stdoutTask).Trim();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"PDF 转换失败 (exit {process.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
            }

            var pdf = Directory.GetFiles(outputDir, "*.pdf").FirstOrDefault()
                ?? throw new InvalidOperationException("PDF 导出失败，未生成文件。");

            return pdf;
        }
        finally
        {
            _conversionLock.Release();
        }
    }

    private void KillProcess()
    {
        lock (_processLock)
        {
            if (_listenerProcess is null || _listenerProcess.HasExited) return;
            try { _listenerProcess.Kill(entireProcessTree: true); } catch { }
            _listenerProcess = null;
        }
    }

    private static string? ResolveBinary()
    {
        foreach (var name in new[] { "libreoffice", "soffice" })
        {
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in paths)
            {
                var fullPath = Path.Combine(dir, OperatingSystem.IsWindows() ? $"{name}.exe" : name);
                if (File.Exists(fullPath)) return fullPath;
            }
        }
        return null;
    }

    public override void Dispose()
    {
        KillProcess();
        _conversionLock.Dispose();
        base.Dispose();
    }
}
