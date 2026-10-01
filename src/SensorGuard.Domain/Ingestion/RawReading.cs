namespace SensorGuard.Domain.Ingestion;

/// <summary>The five input fields of one non-blank, well-formed JSON object line, before validation.</summary>
public sealed record RawReading(int LineNumber, Field DeviceId, Field Metric, Field Ts, Field Value, Field Seq);
