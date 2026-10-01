using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Ingestion;

namespace SensorGuard.Infrastructure.Files;

/// <summary>
/// Streams a JSON Lines file line by line (the file extension is irrelevant). Tolerates a UTF-8 BOM, LF or CRLF
/// line endings and a missing trailing newline. Parse problems become <see cref="RawLine"/> failures, never exceptions.
/// </summary>
public sealed class JsonlReadingSource : IReadingSource
{
    private readonly string _path;

    public JsonlReadingSource(string path) => _path = path;

    public async IAsyncEnumerable<RawLine> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(_path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            yield return string.IsNullOrWhiteSpace(line) ? RawLine.Blank(lineNumber) : Parse(lineNumber, line);
        }
    }

    private static RawLine Parse(int lineNumber, string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return RawLine.Failed(lineNumber, RejectionReason.MalformedJson);
            }

            return RawLine.Ok(new RawReading(
                lineNumber,
                ToField(root, "deviceId"),
                ToField(root, "metric"),
                ToField(root, "ts"),
                ToField(root, "value"),
                ToField(root, "seq")));
        }
        catch (JsonException)
        {
            return RawLine.Failed(lineNumber, RejectionReason.MalformedJson);
        }
    }

    private static Field ToField(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var element))
        {
            return Field.Missing;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Null => Field.Null,
            JsonValueKind.String => Field.Str(element.GetString() ?? string.Empty),
            JsonValueKind.Number => ToNumberField(element),
            _ => Field.Other,
        };
    }

    private static Field ToNumberField(JsonElement element)
    {
        var raw = element.GetRawText();
        var isIntegerToken = IsPlainInteger(raw);
        if (isIntegerToken && long.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var integer))
        {
            return Field.Int(integer);
        }

        var number = element.TryGetDouble(out var parsed)
            ? parsed
            : raw.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;

        return isIntegerToken ? Field.OversizedInteger(number) : Field.Num(number);
    }

    private static bool IsPlainInteger(string rawNumber)
    {
        var start = rawNumber.StartsWith('-') ? 1 : 0;
        if (rawNumber.Length <= start)
        {
            return false;
        }

        for (var i = start; i < rawNumber.Length; i++)
        {
            if (rawNumber[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
