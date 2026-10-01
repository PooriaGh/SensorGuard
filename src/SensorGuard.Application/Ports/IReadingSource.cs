using System.Collections.Generic;
using System.Threading;
using SensorGuard.Domain.Ingestion;

namespace SensorGuard.Application.Ports;

/// <summary>
/// One physical line of input after format-level parsing. Exactly one of: blank, a parse failure
/// (not valid JSON or not a JSON object), or a parsed set of raw fields.
/// </summary>
public sealed record RawLine(int LineNumber, bool IsBlank, RawReading? Parsed, RejectionReason? ParseFailure)
{
    public static RawLine Blank(int lineNumber) => new(lineNumber, true, null, null);

    public static RawLine Failed(int lineNumber, RejectionReason reason) => new(lineNumber, false, null, reason);

    public static RawLine Ok(RawReading raw) => new(raw.LineNumber, false, raw, null);
}

/// <summary>Port for reading input; a new input source is a new adapter implementing this interface.</summary>
public interface IReadingSource
{
    IAsyncEnumerable<RawLine> ReadAsync(CancellationToken cancellationToken);
}
