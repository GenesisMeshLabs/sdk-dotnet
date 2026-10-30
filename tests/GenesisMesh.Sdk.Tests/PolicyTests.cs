using System.Net;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>The boundary policy lifecycle (v0.58) and policy-aware evaluation.</summary>
public class PolicyTests
{
    private static string Captured(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "contract", "na_responses.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty(name).GetRawText();
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A client whose NA answers <paramref name="body"/>, recording each request's method, path and body.</summary>
    private static GenesisMeshClient Client(List<(string Method, string Path, string? Body)> sent, int status, string body, bool signed = true) =>
        new(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = signed ? TestHelpers.ZeroSeedB64 : null,
            KeyId = "ops",
            Audience = "TEST",
            HttpHandler = new FuncHandler(req =>
            {
                sent.Add((req.Method.Method, req.RequestUri!.PathAndQuery, req.Content?.ReadAsStringAsync().Result));
                return Json(status, body);
            }),
        });

    private static BoundaryPolicyIntent Intent() => new()
    {
        PolicyId = "sp-secret-policy",
        Description = "vendor secrets",
        ValidFrom = "2026-10-30T00:00:00Z",
        ValidUntil = "2026-11-30T00:00:00Z",
        Selector = new PolicySelector { ParentKinds = ["attestation"], Capabilities = ["sp-secret.*"] },
        Gates =
        [
            new GateSpec
            {
                GateId = "max-lifetime",
                GateType = "max_value.v1",
                Order = 0,
                Config = new Dictionary<string, object?> { ["path"] = "request_parameters.lifetime_days", ["max"] = 90 },
            },
        ],
    };

    // ── Policies ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_SendsTheIntent_AndReadsTheIssues()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 200, Captured("policy_validate"));
        var result = await gm.Policy.Validate(Intent());
        Assert.True(result.Valid);
        Assert.Equal(1, result.NextVersion);
        var (method, path, body) = Assert.Single(sent);
        Assert.Equal(("POST", "/admin/boundary-policies/validate"), (method, path));
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("sp-secret-policy", doc.RootElement.GetProperty("policy_id").GetString());
        Assert.Equal(90, doc.RootElement.GetProperty("gates")[0].GetProperty("config").GetProperty("max").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("remediation", out _));
    }

    [Fact]
    public async Task Publish_ReturnsTheSignedPolicy()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 201, Captured("policy_publish"));
        var policy = await gm.Policy.Publish(Intent());
        Assert.Equal(("sp-secret-policy", 1), (policy.PolicyId, policy.Version));
        Assert.Equal("enforce", policy.Gates[0].Mode);
        Assert.Equal(["sp-secret.*"], policy.Selector.Capabilities!);
        Assert.Equal(JsonValueKind.Object, policy.Signature.ValueKind);
        Assert.Equal("/admin/boundary-policies", sent.Single().Item2);
    }

    [Fact]
    public async Task Publish_OfAnInvalidIntent_IsABadRequest_WithTheIssues()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 400, """{"error":{"code":"boundary_policy_invalid","details":{"issues":[{"code":"unknown_gate_type","gate_id":"bogus","message":"x"}],"valid":false},"message":"boundary policy failed validation"}}""");
        var ex = await Assert.ThrowsAsync<BadRequestException>(() => gm.Policy.Publish(Intent()));
        Assert.Equal("boundary_policy_invalid", ex.Code);
        Assert.Equal("unknown_gate_type", ex.Details.GetProperty("issues")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task List_ReturnsEveryVersion()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 200, $$"""{"policies":[{{Captured("policy_list_item")}}]}""");
        var versions = await gm.Policy.List();
        Assert.True(Assert.Single(versions) is { PolicyId: "sp-secret-policy", Version: 1, IntegrityOk: true });
        Assert.Equal(("GET", "/admin/boundary-policies"), (sent[0].Item1, sent[0].Item2));
    }

    [Fact]
    public async Task ActiveAndHistory_ReadTheirRoutes()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 200, Captured("policy_active"));
        var active = await gm.Policy.Active();
        Assert.True(active.PolicySetHealthy);
        Assert.Equal("sp-secret-policy", active.Active.Single().Policy.PolicyId);
        Assert.Contains("max_value.v1", active.RegistryGateTypes);

        var historySent = new List<(string, string, string?)>();
        using var history = Client(historySent, 200, Captured("policy_history"));
        var versions = await history.Policy.History("sp-secret-policy");
        Assert.Equal([2, 1], versions.Versions.Select(v => v.Version));
        Assert.NotNull(versions.Versions[0].Policy);
        Assert.Equal("/admin/boundary-policies/sp-secret-policy/history", historySent[0].Item2);
    }

    [Fact]
    public async Task ActivateAndDeactivate_SendTheVersion()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 200, Captured("policy_activate"));
        var activation = await gm.Policy.Activate("sp-secret-policy", 1);
        Assert.True(activation.Active);
        await gm.Policy.Deactivate("sp-secret-policy", 2);
        Assert.Equal(
            [("/admin/boundary-policies/sp-secret-policy/activate", """{"version":1}"""),
             ("/admin/boundary-policies/sp-secret-policy/deactivate", """{"version":2}""")],
            sent.Select(s => (s.Item2, s.Item3)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    public async Task APolicyIdThatIsNotOnePathSegment_IsRefusedBeforeSending(string policyId)
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 200, "{}");
        await Assert.ThrowsAsync<ArgumentException>(() => gm.Policy.History(policyId));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.Policy.Activate(policyId, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.Policy.Deactivate(policyId, 1));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Verify_SendsThePolicyAsSigned_AndTheKeysWhenGiven()
    {
        var policy = JsonSerializer.Deserialize<BoundaryPolicy>(Captured("policy_publish"), Auth.SerializerOptions)!;
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 200, Captured("policy_verify"), signed: false);
        Assert.True((await gm.Policy.Verify(policy)).Valid);
        await gm.Policy.Verify(policy, ["KEY"]);
        Assert.Equal("/boundary-policies/verify", sent[0].Item2);
        using var first = JsonDocument.Parse(sent[0].Item3!);
        Assert.Equal(Canonical.FromJson(Captured("policy_publish")), Canonical.Of(first.RootElement.GetProperty("policy")));
        Assert.False(first.RootElement.TryGetProperty("issuer_public_keys", out _));
        using var second = JsonDocument.Parse(sent[1].Item3!);
        Assert.Equal("KEY", second.RootElement.GetProperty("issuer_public_keys")[0].GetString());
    }

    [Fact]
    public async Task AdminPolicyRoutes_NeedASigningKey()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 200, "{}", signed: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gm.Policy.Validate(Intent()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gm.Policy.Publish(Intent()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gm.Policy.List());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gm.Policy.Active());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gm.Policy.History("p"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gm.Policy.Activate("p", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gm.Policy.Deactivate("p", 1));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task AStandardKey_IsForbiddenToPublish()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 403, """{"error":{"code":"insufficient_operator_tier","message":"x"}}""");
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => gm.Policy.Publish(Intent()));
        Assert.Equal("insufficient_operator_tier", ex.Code);
    }

    // ── Evaluation ────────────────────────────────────────────────────────────

    private static EvaluateRequest Request(string capability = "sp-secret.create") => new()
    {
        AttestationId = "att-1",
        RequestedCapability = capability,
        Context = new EvaluationContext
        {
            RequestParameters = new Dictionary<string, object?> { ["app_id"] = "app-billing", ["lifetime_days"] = 30 },
            Attributes = new Dictionary<string, object?> { ["owner"] = "team-a" },
        },
    };

    [Fact]
    public async Task Evaluate_ReturnsTheSignedDecisionAndItsProof()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 201, Captured("boundary_evaluate"));
        var evaluation = await gm.Boundary.Evaluate(Request());
        Assert.True(evaluation.Decision.Authorized);
        Assert.Null(evaluation.Decision.DenialReason);
        Assert.Equal(JsonValueKind.Object, evaluation.Decision.PolicyBinding.ValueKind);
        Assert.Equal(JsonValueKind.Object, evaluation.JustificationProof.ValueKind);
        var (method, path, body) = sent.Single();
        Assert.Equal(("POST", "/admin/boundary/evaluate"), (method, path));
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("att-1", doc.RootElement.GetProperty("attestation_id").GetString());
        Assert.False(doc.RootElement.TryGetProperty("agreement", out _));
        Assert.Equal(30, doc.RootElement.GetProperty("context").GetProperty("request_parameters").GetProperty("lifetime_days").GetInt32());
    }

    [Fact]
    public async Task Evaluate_SendsAnAgreementBasis_WithoutAnAttestation()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 201, Captured("boundary_evaluate"));
        var agreement = JsonSerializer.Deserialize<AgreementRecord>(Captured("agreement_accept"), Auth.SerializerOptions)!;
        await gm.Boundary.Evaluate(new EvaluateRequest { Agreement = agreement, RequestedCapability = "read" });
        using var doc = JsonDocument.Parse(sent.Single().Item3!);
        Assert.Equal(Canonical.FromJson(Captured("agreement_accept")), Canonical.Of(doc.RootElement.GetProperty("agreement")));
        Assert.False(doc.RootElement.TryGetProperty("attestation_id", out _));
    }

    [Fact]
    public async Task Evaluate_ADenial_IsASignedDecision()
    {
        var denied = "{\"decision\":" + Captured("boundary_evaluate_denied_decision") + ",\"justification_proof\":{}}";
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 201, denied);
        var evaluation = await gm.Boundary.Evaluate(Request());
        Assert.False(evaluation.Decision.Authorized);
        Assert.StartsWith("policy gate 'sp-secret-policy/app' failed", evaluation.Decision.DenialReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_NeedsExactlyOneBasis_AndSendsNothingOtherwise()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 201, "{}");
        await Assert.ThrowsAsync<ArgumentException>(() => gm.Boundary.Evaluate(new EvaluateRequest { RequestedCapability = "read" }));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.Boundary.Evaluate(
            new EvaluateRequest { RequestedCapability = "read", AttestationId = "a", Agreement = new { } }));
        Assert.Empty(sent);
    }

    private sealed class CountingSigner : ISigner
    {
        public int Calls;
        public string KeyId => "ops";
        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new byte[64]);
        }
    }

    [Theory]
    [InlineData("client_secret", "x")]
    [InlineData("note", "-----BEGIN PRIVATE KEY-----")]
    [InlineData("token", "x")]
    public async Task Evaluate_RefusesSecretMaterialInTheContext_BeforeSigningOrSending(string field, string value)
    {
        var signer = new CountingSigner();
        var sent = 0;
        using var gm = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            Audience = "TEST",
            Signer = signer,
            HttpHandler = new FuncHandler(_ => { sent++; return Json(201, "{}"); }),
        });
        var request = Request();
        request.Context!.Attributes = new Dictionary<string, object?> { ["nested"] = new Dictionary<string, object?> { [field] = value } };
        var ex = await Assert.ThrowsAsync<SecretMaterialException>(() => gm.Boundary.Evaluate(request));
        Assert.Equal("evidence_secret_material", ex.Code);
        Assert.Equal((0, 0), (signer.Calls, sent));
    }

    [Fact]
    public async Task Evaluate_NeedsASigningKey()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 201, "{}", signed: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gm.Boundary.Evaluate(Request()));
    }

    [Fact]
    public async Task Evaluate_WithPoliciesRequired_AndAnUnknownAttestation_MapsTheRefusal()
    {
        var sent = new List<(string, string, string?)>();
        using var gm = Client(sent, 404, """{"error":{"code":"attestation_not_found","message":"x"}}""");
        var ex = await Assert.ThrowsAsync<NotFoundException>(() => gm.Boundary.Evaluate(Request()));
        Assert.Equal("attestation_not_found", ex.Code);
    }
}

/// <summary>The metadata guard (the reference's check_metadata_only), as the TypeScript SDK tests it.</summary>
public class EvidenceMetadataTests
{
    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("""{"password":"x"}""", "field 'password' is not allowed in evidence metadata")]
    [InlineData("""{"nested":{"access_token":"x"}}""", "field 'nested.access_token' is not allowed in evidence metadata")]
    [InlineData("""{"list":[{"privateKey":"x"}]}""", "field 'list.0.privateKey' is not allowed in evidence metadata")]
    [InlineData("""{"note":"-----BEGIN PRIVATE KEY-----"}""", "field 'note' contains a PEM block")]
    [InlineData("""{"note":"eyJabc.def.ghi"}""", "field 'note' looks like key or token material")]
    [InlineData("""{"Connection-String":"x"}""", "field 'Connection-String' is not allowed in evidence metadata")]
    public void SecretMaterial_IsRefused_WithTheReferencesReason(string metadata, string reason) =>
        Assert.Equal(reason, EvidenceMetadata.CheckMetadataOnly(J(metadata)));

    [Fact]
    public void ALongOpaqueValue_IsKeyMaterial_ButAShorterOneIsNot()
    {
        Assert.NotNull(EvidenceMetadata.CheckMetadataOnly(J($$"""{"note":"{{new string('x', 120)}}"}""")));
        Assert.NotNull(EvidenceMetadata.CheckMetadataOnly(J($$"""{"note":"{{new string('x', 120)}}\n"}""")));
        Assert.Null(EvidenceMetadata.CheckMetadataOnly(J($$"""{"note":"{{new string('x', 119)}}"}""")));
    }

    [Fact]
    public void TheDetail_TheSize_AndOrdinaryIdentifiers()
    {
        Assert.Equal("field 'outcome_detail' contains a PEM block",
            EvidenceMetadata.CheckMetadataOnly(J("{}"), "-----BEGIN PRIVATE KEY-----"));
        Assert.Contains("limit", EvidenceMetadata.CheckMetadataOnly(J($$"""{"note":"{{string.Concat(Enumerable.Repeat("é ", 6000))}}"}""")));
        Assert.Null(EvidenceMetadata.CheckMetadataOnly(J("""{"secret_version":"v1","owner":"Zoë","expires_at":"2027-01-01"}""")));
        Assert.Null(EvidenceMetadata.CheckMetadataOnly(default));
    }

    [Fact]
    public void TheSize_CountsNonAsciiAsUtf8_NotAsEscapes()
    {
        // {"execution_parameters":{"n":"<s>"},"outcome_detail":null}: 55 bytes around s; é is 2 bytes in UTF-8, 6 escaped.
        var fits = new string('é', (EvidenceMetadata.MaxMetadataBytes - 55) / 2);
        Assert.Null(EvidenceMetadata.CheckMetadataOnly(J($$"""{"n":"{{fits}}"}""")));
        Assert.Contains("over the 16384-byte limit", EvidenceMetadata.CheckMetadataOnly(J($$"""{"n":"{{fits}}éé"}""")));
        // An escaped backslash followed by "u" stays as it is.
        Assert.Null(EvidenceMetadata.CheckMetadataOnly(J("""{"n":"\\u00e9 and é"}""")));
    }
}
