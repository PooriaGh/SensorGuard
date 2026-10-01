using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace SensorGuard.Api.Tests;

/// <summary>The optional read-only endpoints that expose alerts and the unacceptable readings as separate outputs (FR-012).</summary>
public sealed class ReadModelEndpointTests : IClassFixture<ReadModelEndpointTests.Fixture>
{
    private const string Input = """
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:00:00Z", "value": 70, "seq": 1}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:00:10Z", "value": 71, "seq": 2}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:00:20Z", "value": 85, "seq": 3}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:00:30Z", "value": 86, "seq": 4}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:00:40Z", "value": 87, "seq": 5}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:00:50Z", "value": 88, "seq": 6}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:01:00Z", "value": 89, "seq": 7}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:01:10Z", "value": 70, "seq": 8}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:03:00Z", "value": 150, "seq": 9}
        {"deviceId": "D2", "metric": "temperature", "ts": "2025-06-01T08:00:00Z", "value": 60, "seq": 10}
        """;

    private const string Rules = """
        [
          { "id": "R1", "name": "Too hot", "enabled": true, "metric": "temperature", "operator": "GreaterThan", "value": 100 },
          { "id": "S1", "name": "Sustained heat", "enabled": true, "metric": "temperature", "operator": "SustainedAbove", "threshold": 80, "durationSeconds": 30 }
        ]
        """;

    public sealed class Fixture : System.IDisposable
    {
        public Fixture()
        {
            Factory = new ApiFactory(Input, Rules);
            Client = Factory.CreateClient();
        }

        public ApiFactory Factory { get; }

        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private readonly HttpClient _client;

    public ReadModelEndpointTests(Fixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task Alerts_lists_one_alert_per_sustained_episode_with_all_fields()
    {
        var response = await _client.GetAsync("/api/alerts");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var alert = doc.RootElement.EnumerateArray().Single();
        alert.GetProperty("ruleId").GetString().ShouldBe("S1");
        alert.GetProperty("ruleName").GetString().ShouldBe("Sustained heat");
        alert.GetProperty("deviceId").GetString().ShouldBe("D1");
        alert.GetProperty("metric").GetString().ShouldBe("temperature");
        alert.GetProperty("startTs").GetString().ShouldBe("2025-06-01T08:00:20Z");
        alert.GetProperty("endTs").GetString().ShouldBe("2025-06-01T08:01:10Z");
        alert.GetProperty("peakValue").GetDouble().ShouldBe(89);
        alert.GetProperty("openAtEndOfData").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Unacceptable_readings_are_listed_with_their_violated_rules_and_reasons()
    {
        var response = await _client.GetAsync("/api/readings/unacceptable");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var readings = doc.RootElement.EnumerateArray().ToList();
        readings.Count.ShouldBe(6); // 5 readings in the sustained episode + the 150 reading
        var hot = readings.Single(r => r.GetProperty("value").GetDouble() == 150);
        hot.GetProperty("violations").EnumerateArray().Select(v => v.GetProperty("ruleId").GetString()).ShouldBe(new[] { "R1" });
        var sustained = readings.First(r => r.GetProperty("seq").GetInt64() == 3);
        sustained.GetProperty("ts").GetString().ShouldBe("2025-06-01T08:00:20Z");
        sustained.GetProperty("violations")[0].GetProperty("reason").GetString()
            .ShouldBe("above 80 for 30s+ (episode started 2025-06-01T08:00:20Z)");
    }

    [Fact]
    public async Task Unacceptable_readings_can_be_filtered_by_device_and_metric()
    {
        var response = await _client.GetAsync("/api/readings/unacceptable?deviceId=D2&metric=temperature");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("/api/readings/unacceptable?deviceId=D1")]
    [InlineData("/api/readings/unacceptable?metric=temperature")]
    [InlineData("/api/readings/unacceptable?deviceId=D1&metric=bogus")]
    public async Task A_half_given_or_unknown_filter_is_a_400(string url)
    {
        var response = await _client.GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
