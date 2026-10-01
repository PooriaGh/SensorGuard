using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace SensorGuard.Api.Tests;

public sealed class AggregationEndpointTests : IClassFixture<AggregationEndpointTests.Fixture>
{
    // D1 temperature, rule: GreaterThan 100 is a violation. 08:00:20 (150) is unacceptable and must never be counted.
    private const string Input = """
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:00:00Z", "value": 70, "seq": 1}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:00:10Z", "value": 72, "seq": 2}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:00:20Z", "value": 150, "seq": 3}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:00:30Z", "value": 74, "seq": 4}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:01:00Z", "value": 80, "seq": 5}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:01:10Z", "value": 82, "seq": 6}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:02:30Z", "value": 90, "seq": 7}
        {"deviceId": "D1", "metric": "temperature", "ts": "2025-06-01T08:03:00Z", "value": 71, "seq": 8}
        not json
        """;

    private const string Rules = """
        [ { "id": "R1", "name": "Too hot", "enabled": true, "metric": "temperature", "operator": "GreaterThan", "value": 100 } ]
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

    public AggregationEndpointTests(Fixture fixture) => _client = fixture.Client;

    private static string Url(string deviceId = "D1", string metric = "temperature", string from = "2025-06-01T08:00:00Z",
        string to = "2025-06-01T08:04:00Z", string bucketSeconds = "60") =>
        $"/api/aggregations?deviceId={deviceId}&metric={metric}&from={System.Uri.EscapeDataString(from)}&to={System.Uri.EscapeDataString(to)}&bucketSeconds={bucketSeconds}";

    private static async Task<JsonDocument> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task Returns_buckets_over_acceptable_readings_only_matching_the_hand_computed_oracle()
    {
        var response = await _client.GetAsync(Url());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = await Json(response);
        var root = doc.RootElement;
        root.GetProperty("deviceId").GetString().ShouldBe("D1");
        root.GetProperty("metric").GetString().ShouldBe("temperature");
        root.GetProperty("from").GetString().ShouldBe("2025-06-01T08:00:00Z");
        root.GetProperty("to").GetString().ShouldBe("2025-06-01T08:04:00Z");
        root.GetProperty("bucketSeconds").GetInt64().ShouldBe(60);
        var buckets = root.GetProperty("buckets").EnumerateArray().ToList();
        buckets.Count.ShouldBe(4);

        // [08:00,08:01): 70, 72, 74 (150 excluded as unacceptable)
        buckets[0].GetProperty("start").GetString().ShouldBe("2025-06-01T08:00:00Z");
        buckets[0].GetProperty("count").GetInt32().ShouldBe(3);
        buckets[0].GetProperty("average").GetDouble().ShouldBe(72.0);
        buckets[0].GetProperty("min").GetDouble().ShouldBe(70);
        buckets[0].GetProperty("max").GetDouble().ShouldBe(74);
        // [08:01,08:02): 80 sits exactly on the boundary and belongs here; 82
        buckets[1].GetProperty("count").GetInt32().ShouldBe(2);
        buckets[1].GetProperty("average").GetDouble().ShouldBe(81.0);
        // [08:02,08:03): 90
        buckets[2].GetProperty("count").GetInt32().ShouldBe(1);
        buckets[2].GetProperty("max").GetDouble().ShouldBe(90);
        // [08:03,08:04): 71 exactly at the start of its bucket
        buckets[3].GetProperty("count").GetInt32().ShouldBe(1);
        buckets[3].GetProperty("min").GetDouble().ShouldBe(71);
    }

    [Fact]
    public async Task Empty_buckets_are_reported_with_count_zero_and_null_statistics()
    {
        var response = await _client.GetAsync(Url(bucketSeconds: "30"));

        using var doc = await Json(response);
        var buckets = doc.RootElement.GetProperty("buckets").EnumerateArray().ToList();
        buckets.Count.ShouldBe(8);
        var empty = buckets[3]; // [08:01:30, 08:02:00): nothing
        empty.GetProperty("count").GetInt32().ShouldBe(0);
        empty.GetProperty("average").ValueKind.ShouldBe(JsonValueKind.Null);
        empty.GetProperty("min").ValueKind.ShouldBe(JsonValueKind.Null);
        empty.GetProperty("max").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_reading_at_to_is_excluded()
    {
        var response = await _client.GetAsync(Url(to: "2025-06-01T08:03:00Z"));

        using var doc = await Json(response);
        doc.RootElement.GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()).ShouldBe(6);
    }

    [Fact]
    public async Task An_unknown_device_with_a_valid_metric_is_200_with_all_buckets_empty()
    {
        var response = await _client.GetAsync(Url(deviceId: "NOPE"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = await Json(response);
        doc.RootElement.GetProperty("buckets").EnumerateArray().All(b => b.GetProperty("count").GetInt32() == 0).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/api/aggregations", "deviceId")]
    [InlineData("/api/aggregations?deviceId=D1&metric=bogus&from=2025-06-01T08:00:00Z&to=2025-06-01T09:00:00Z&bucketSeconds=60", "metric")]
    [InlineData("/api/aggregations?deviceId=D1&metric=temperature&from=nope&to=2025-06-01T09:00:00Z&bucketSeconds=60", "from")]
    [InlineData("/api/aggregations?deviceId=D1&metric=temperature&from=2025-06-01T09:00:00Z&to=2025-06-01T08:00:00Z&bucketSeconds=60", "from")]
    [InlineData("/api/aggregations?deviceId=D1&metric=temperature&from=2025-06-01T08:00:00Z&to=2025-06-01T09:00:00Z&bucketSeconds=0", "bucketSeconds")]
    [InlineData("/api/aggregations?deviceId=D1&metric=temperature&from=2025-06-01T08:00:00Z&to=2025-06-01T09:00:00Z&bucketSeconds=abc", "bucketSeconds")]
    [InlineData("/api/aggregations?deviceId=D1&metric=temperature&from=2025-06-01T08:00:00Z&to=2026-06-01T08:00:00Z&bucketSeconds=1", "bucketSeconds")]
    public async Task Invalid_requests_return_400_problem_details_naming_the_parameter(string url, string parameter)
    {
        var response = await _client.GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using var doc = await Json(response);
        doc.RootElement.GetProperty("status").GetInt32().ShouldBe(400);
        doc.RootElement.GetProperty("errors").TryGetProperty(parameter, out var messages).ShouldBeTrue();
        messages.GetArrayLength().ShouldBeGreaterThan(0);
    }
}
