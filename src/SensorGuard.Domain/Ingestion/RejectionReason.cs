namespace SensorGuard.Domain.Ingestion;

/// <summary>Stable reason codes for rejected input lines (spec clarification: invalid-record definition).</summary>
public enum RejectionReason
{
    MalformedJson,
    MissingField,
    InvalidType,
    UnknownMetric,
    InvalidTimestamp,
    InvalidSeq,
    NonFiniteValue,
}
