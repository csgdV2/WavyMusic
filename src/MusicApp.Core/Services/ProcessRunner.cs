using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using MusicApp.Core.Abstractions;
using MusicApp.Core.Models;

namespace MusicApp.Core.Services;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(exePath, args) };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        StartOrThrow(process, exePath);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        process.WaitForExit();

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public async IAsyncEnumerable<string> StreamLinesAsync(
        string exePath,
        IReadOnlyList<string> args,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(exePath, args) };

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var openStreams = 2;
        void OnStreamClosed()
        {
            if (Interlocked.Decrement(ref openStreams) == 0)
                channel.Writer.TryComplete();
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) OnStreamClosed();
            else channel.Writer.TryWrite(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) OnStreamClosed();
            else channel.Writer.TryWrite(e.Data);
        };

        StartOrThrow(process, exePath);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return line;
        }
        finally
        {
            TryKill(process);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string exePath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    private static void StartOrThrow(Process process, string exePath)
    {
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start process: {exePath}");
        }
        catch (Win32Exception ex)
        {

            throw new FileNotFoundException(
                $"Couldn't launch '{exePath}'. Put yt-dlp.exe in the app's Tools folder or on your PATH.",
                exePath, ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {

        }
    }
}
