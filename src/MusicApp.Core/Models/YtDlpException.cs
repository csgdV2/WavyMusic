namespace MusicApp.Core.Models;

public sealed class YtDlpException : Exception
{
    public YtDlpException(string message) : base(message) { }

    public YtDlpException(string message, Exception inner) : base(message, inner) { }
}
