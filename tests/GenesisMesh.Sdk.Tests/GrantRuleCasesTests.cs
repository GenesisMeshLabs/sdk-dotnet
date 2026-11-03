using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// The grants' rules and records (1.9.0), beyond the shared suite's vectors: cases generated over the suite's records
/// and judged by the Python reference, with its answers. Envelope parameters read and normalized, envelope problems,
/// use problems at microsecond boundaries, the five records verified with their received digests, and the secret
/// guard. The TypeScript SDK agrees on every case.
/// </summary>
public class GrantRuleCasesTests
{
    private static readonly Lazy<JsonElement> Data = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "grants", "rule_cases.json"))).RootElement);

    public static IEnumerable<object[]> CaseIds() =>
        Data.Value.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()! });

    [Fact]
    public void HoldsTheCases()
    {
        var kinds = Data.Value.GetProperty("cases").EnumerateArray().GroupBy(c => c.GetProperty("kind").GetString()!).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(["env", "param", "rec", "sec", "use"], kinds.Keys.Order(StringComparer.Ordinal));
        Assert.True(kinds.Values.Sum() > 1400);
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void AnswersAsTheReferenceDid(string id)
    {
        var c = Data.Value.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var input = c.GetProperty("input");
        var expected = c.GetProperty("expected");
        string In(string name) => input.GetProperty(name).GetString()!;
        switch (c.GetProperty("kind").GetString())
        {
            case "param":
                {
                    var parameter = Read(In("text")) is { } raw ? GrantRules.Parameter(raw, out _) : null;
                    Assert.Equal(expected.GetProperty("valid").GetBoolean(), parameter is not null);
                    if (parameter is not null) Assert.Equal(expected.GetProperty("normalized").GetString(), Canonical.FromJson(parameter.ToJson()));
                    break;
                }
            case "env":
                {
                    var parameters = Read(In("parameters"));
                    if (expected.TryGetProperty("json", out _))
                    {
                        Assert.Null(parameters);
                        break;
                    }
                    Assert.Equal(Text(expected.GetProperty("problem")), GrantRules.EnvelopeProblem(Read(In("envelope"))!.Value, parameters!.Value));
                    break;
                }
            case "use":
                {
                    var use = JsonSerializer.Deserialize<GrantExecutionEvidence>(In("use"))!;
                    var previous = input.GetProperty("previous");
                    var head = input.GetProperty("token_head");
                    var admitted = input.GetProperty("admitted");
                    var found = GrantRules.UseProblem(use, new UseCheck
                    {
                        Grant = input.GetProperty("grant").ValueKind == JsonValueKind.String ? JsonSerializer.Deserialize<CapabilityGrant>(In("grant")) : null,
                        Previous = previous.ValueKind == JsonValueKind.Object
                            ? new PreviousUse(previous.GetProperty("executed_at").GetString()!, previous.GetProperty("time_token_issued_at").GetString()!)
                            : null,
                        Admitted = admitted.ValueKind == JsonValueKind.Number ? admitted.GetInt32() : null,
                        TokenHead = head.ValueKind == JsonValueKind.Object
                            ? new TokenHead(head.GetProperty("digest").GetString()!, head.GetProperty("executed_at").GetString()!)
                            : null,
                        TokenKeys = [.. input.GetProperty("na_public_keys").EnumerateArray().Select(k => k.GetString()!)],
                        RevokedAt = Text(input.GetProperty("revoked_at")),
                        RecordedAt = Text(input.GetProperty("recorded_at")),
                    });
                    var problem = expected.GetProperty("problem");
                    string[]? reference = problem.ValueKind == JsonValueKind.Null ? null : [problem[0].GetString()!, problem[1].GetString()!];
                    Assert.Equal(reference, found is null ? null : [found.Code, found.Detail]);
                    break;
                }
            case "rec":
                {
                    var record = In("record");
                    string[] keys = [In("key")];
                    var valid = In("model") switch
                    {
                        "CapabilityGrant" => OfflineVerifier.VerifyCapabilityGrant(record, keys).Valid,
                        "TimeToken" => OfflineVerifier.VerifyTimeToken(record, keys),
                        "GrantRevocationRecord" => OfflineVerifier.VerifyGrantRevocation(record, keys),
                        "GrantExecutionEvidence" => OfflineVerifier.VerifyGrantUse(record, keys[0]),
                        _ => OfflineVerifier.VerifyTimeTokenRequest(record, keys[0]),
                    };
                    Assert.Equal(expected.GetProperty("valid").GetBoolean(), valid);
                    Assert.Equal(expected.GetProperty("digest").GetString(), GrantRules.RecordDigest(In("model"), record));
                    break;
                }
            case "sec":
                Assert.Equal(Text(expected.GetProperty("problem")), GrantRules.UseSecretProblem(JsonSerializer.Deserialize<GrantExecutionEvidence>(In("use"))!));
                break;
            default:
                Assert.Fail(id);
                break;
        }
    }

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The value as the SDK reads JSON; null for text it refuses.</summary>
    private static JsonElement? Read(string text)
    {
        try
        {
            using var doc = StrictJsonReader.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (StrictJsonException)
        {
            return null;
        }
    }
}
