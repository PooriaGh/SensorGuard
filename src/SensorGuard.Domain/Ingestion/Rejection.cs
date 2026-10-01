using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Ingestion;

public sealed record Rejection(int LineNumber, RejectionReason Reason, string Detail);

/// <summary>Outcome of validating one raw line: exactly one of <see cref="Reading"/> or <see cref="Rejection"/>.</summary>
public sealed record ValidationResult
{
    private ValidationResult(Reading? reading, Rejection? rejection)
    {
        Reading = reading;
        Rejection = rejection;
    }

    public Reading? Reading { get; }

    public Rejection? Rejection { get; }

    public bool IsValid => Reading is not null;

    public static ValidationResult Valid(Reading reading) => new(reading, null);

    public static ValidationResult Rejected(Rejection rejection) => new(null, rejection);
}
