using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Basket.Tests.Support;

/// <summary>
/// Every log event any test host writes, rendered with all its properties, so a test can prove a value never appears.
/// One instance for the whole run: the request-logging middleware writes to Serilog's static logger, which belongs to
/// whichever host was built last, and hosts of parallel test classes overlap. A test therefore looks for a value that is
/// unique to it rather than for "everything my host logged".
/// </summary>
public sealed class LogCapture : ILogEventSink
{
    public static LogCapture Shared { get; } = new();

    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    public void Emit(LogEvent logEvent) =>
        _lines.Enqueue(logEvent.RenderMessage() + " " + string.Join(' ', logEvent.Properties.Select(p => p.Value.ToString())) + " " + logEvent.Exception);
}
