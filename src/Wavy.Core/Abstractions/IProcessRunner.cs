using Wavy.Core.Models;

namespace Wavy.Core.Abstractions;

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
