using SensorGuard.Application.Ports;

namespace SensorGuard.Api;

public sealed class ConsoleReportSink : IReportSink
{
    public void Write(string text) => Console.Out.Write(text);
}
