using System.Threading;
using System.Threading.Tasks;

namespace Wavy.Core.Abstractions;

public interface IToolLocator
{

    Task<string> GetYtDlpPathAsync(CancellationToken ct = default);
}
