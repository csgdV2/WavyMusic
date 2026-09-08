using MusicApp.Core.Models;

namespace MusicApp.Core.Abstractions;

public interface IProcessRunner
{

    Task<ProcessResult> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        CancellationToken ct = default);

    IAsyncEnumerable<string> StreamLinesAsync(
        string exePath,
        IReadOnlyList<string> args,
        CancellationToken ct = default);
}
