using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Infrastructure.Files;
using Shouldly;
using Xunit;

namespace SensorGuard.Infrastructure.Tests.Files;

public sealed class JsonlReadingSourceTests : IDisposable
{
    private const string Valid =
        "{\"deviceId\": \"PUMP-01\", \"metric\": \"temperature\", \"ts\": \"2025-06-01T08:33:00Z\", \"value\": 67.21, \"seq\": 1199}";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sensorguard-tests-" + Guid.NewGuid().ToString("N"));

    public JsonlReadingSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<List<RawLine>> ReadAsync(byte[] content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllBytesAsync(path, content);
        var lines = new List<RawLine>();
        await foreach (var line in new JsonlReadingSource(path).ReadAsync(default))
        {
            lines.Add(line);
        }

        return lines;
    }

    private Task<List<RawLine>> ReadTextAsync(string text, bool bom = false)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom);
        return ReadAsync(encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
    }

    [Fact]
    public async Task Reads_a_valid_line_into_raw_fields_with_a_one_based_line_number()
    {
        var lines = await ReadTextAsync(Valid + "\n");

        var line = lines.Single();
        line.LineNumber.ShouldBe(1);
        line.IsBlank.ShouldBeFalse();
        line.ParseFailure.ShouldBeNull();
        var raw = line.Parsed!;
        raw.DeviceId.Kind.ShouldBe(FieldKind.String);
        raw.DeviceId.Text.ShouldBe("PUMP-01");
        raw.Metric.Text.ShouldBe("temperature");
        raw.Ts.Text.ShouldBe("2025-06-01T08:33:00Z");
        raw.Value.Kind.ShouldBe(FieldKind.Number);
        raw.Value.Number.ShouldBe(67.21);
        raw.Seq.IsIntegerToken.ShouldBeTrue();
        raw.Seq.Integer.ShouldBe(1199);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Tolerates_lf_and_crlf_and_a_missing_trailing_newline(string eol)
    {
        var lines = await ReadTextAsync(Valid + eol + Valid);

        lines.Count.ShouldBe(2);
        lines.All(l => l.Parsed is not null).ShouldBeTrue();
        lines.Select(l => l.LineNumber).ShouldBe(new[] { 1, 2 });
    }

    [Fact]
    public async Task Tolerates_a_utf8_bom()
    {
        var lines = await ReadTextAsync(Valid + "\n", bom: true);

        lines.Single().Parsed.ShouldNotBeNull();
    }

    [Fact]
    public async Task Flags_blank_and_whitespace_only_lines_and_keeps_numbering()
    {
        var lines = await ReadTextAsync(Valid + "\n\n   \n" + Valid + "\n");

        lines.Select(l => l.IsBlank).ShouldBe(new[] { false, true, true, false });
        lines.Select(l => l.LineNumber).ShouldBe(new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task An_empty_file_yields_no_lines()
    {
        (await ReadAsync(Array.Empty<byte>())).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"deviceId\": \"PUMP-01\", \"metric\": \"temperature\", \"ts\": \"2025-06-01")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public async Task Malformed_or_non_object_lines_are_MalformedJson_not_exceptions(string text)
    {
        var lines = await ReadTextAsync(text + "\n" + Valid + "\n");

        lines[0].ParseFailure.ShouldBe(RejectionReason.MalformedJson);
        lines[0].Parsed.ShouldBeNull();
        lines[1].Parsed.ShouldNotBeNull();
    }

    [Fact]
    public async Task Maps_json_kinds_to_field_kinds()
    {
        var json = "{\"deviceId\": null, \"metric\": \"vibration\", \"ts\": 5, \"value\": \"12\", \"extra\": true}";

        var raw = (await ReadTextAsync(json)).Single().Parsed!;

        raw.DeviceId.Kind.ShouldBe(FieldKind.Null);
        raw.Metric.Kind.ShouldBe(FieldKind.String);
        raw.Ts.Kind.ShouldBe(FieldKind.Number);
        raw.Value.Kind.ShouldBe(FieldKind.String);
        raw.Value.Text.ShouldBe("12");
        raw.Seq.Kind.ShouldBe(FieldKind.Missing);
    }

    [Fact]
    public async Task An_object_array_or_boolean_valued_field_is_the_Other_kind()
    {
        var json = "{\"deviceId\": {\"a\": 1}, \"metric\": [1], \"ts\": true, \"value\": 1, \"seq\": 1}";

        var raw = (await ReadTextAsync(json)).Single().Parsed!;

        raw.DeviceId.Kind.ShouldBe(FieldKind.Other);
        raw.Metric.Kind.ShouldBe(FieldKind.Other);
        raw.Ts.Kind.ShouldBe(FieldKind.Other);
    }

    [Fact]
    public async Task A_number_beyond_double_range_becomes_a_non_finite_number_field()
    {
        var json = "{\"deviceId\": \"d\", \"metric\": \"pressure\", \"ts\": \"2025-06-01T08:00:00Z\", \"value\": 1e999, \"seq\": 1}";

        var raw = (await ReadTextAsync(json)).Single().Parsed!;

        raw.Value.Kind.ShouldBe(FieldKind.Number);
        double.IsFinite(raw.Value.Number!.Value).ShouldBeFalse();
    }

    [Theory]
    [InlineData("5.0")]
    [InlineData("5e0")]
    [InlineData("1.5")]
    public async Task A_seq_with_a_fraction_or_exponent_is_not_an_integer_token(string seq)
    {
        var json = "{\"deviceId\": \"d\", \"metric\": \"pressure\", \"ts\": \"2025-06-01T08:00:00Z\", \"value\": 1, \"seq\": " + seq + "}";

        var raw = (await ReadTextAsync(json)).Single().Parsed!;

        raw.Seq.IsIntegerToken.ShouldBeFalse();
    }

    [Fact]
    public async Task A_seq_above_long_range_is_an_integer_token_without_a_long_value()
    {
        var json = "{\"deviceId\": \"d\", \"metric\": \"pressure\", \"ts\": \"2025-06-01T08:00:00Z\", \"value\": 1, \"seq\": 99999999999999999999}";

        var raw = (await ReadTextAsync(json)).Single().Parsed!;

        raw.Seq.IsIntegerToken.ShouldBeTrue();
        raw.Seq.Integer.ShouldBeNull();
        raw.Seq.Number.ShouldNotBeNull();
    }
}
