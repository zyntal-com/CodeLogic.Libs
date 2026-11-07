using CodeLogic.Core.Logging;

namespace SocialConnect.Tests;

/// <summary>Captures every log line so tests can assert what reaches the log (and what must not).</summary>
internal sealed class TestLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public string All => string.Join('\n', Lines);

    public void Trace(string message) => Lines.Add("TRACE " + message);
    public void Debug(string message) => Lines.Add("DEBUG " + message);
    public void Info(string message) => Lines.Add("INFO " + message);
    public void Warning(string message) => Lines.Add("WARN " + message);
    public void Error(string message, Exception? exception = null) => Lines.Add("ERROR " + message + (exception is null ? "" : " | " + exception));
    public void Critical(string message, Exception? exception = null) => Lines.Add("CRIT " + message + (exception is null ? "" : " | " + exception));
}
