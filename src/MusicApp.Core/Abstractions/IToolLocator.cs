using System.Threading;
using System.Threading.Tasks;

namespace MusicApp.Core.Abstractions;

public interface IToolLocator
{

    Task<string> GetYtDlpPathAsync(CancellationToken ct = default);
}
