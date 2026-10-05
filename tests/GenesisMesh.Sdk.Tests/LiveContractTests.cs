using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Live contract check: every client method against a running Network Authority, each
/// typed result compared with the JSON the NA sent. Runs when GM_E2E_PYTHON names a Python
/// with the genesis-mesh core installed (scripts/contract_na.py starts a disposable NA):
/// <code>GM_E2E_PYTHON=python dotnet test --filter Category=Live</code>
/// CI runs it in its own job, against the core branch of the same name or main.
/// </summary>
[Trait("Category", "Live")]
public class LiveContractTests
{
    private sealed class Recorder : DelegatingHandler
    {
        public JsonObject? Json;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath == "/sovereign.json") return response;
            var text = await response.Content.ReadAsStringAsync(ct);
            try { Json = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { Json = null; }
            response.Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json");
            return response;
        }
    }

    private static string ContractScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var script = Path.Combine(dir.FullName, "scripts", "contract_na.py");
            if (File.Exists(script)) return script;
        }
        throw new FileNotFoundException("scripts/contract_na.py not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task EveryCallReturnsWhatTheNASent()
    {
        var python = Environment.GetEnvironmentVariable("GM_E2E_PYTHON");
        if (string.IsNullOrEmpty(python)) return; // not configured here; CI runs it in its own job

        using var na = Process.Start(new ProcessStartInfo(python, ContractScript()) { RedirectStandardOutput = true })!;
        try
        {
            var line = await na.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("the NA did not start");
            var inputs = JsonNode.Parse(line)!.AsObject();
            string baseUrl = (string)inputs["baseUrl"]!, seed = (string)inputs["seed"]!, keyId = (string)inputs["keyId"]!;
            string naKey = (string)inputs["naPublicKey"]!, network = (string)inputs["networkName"]!;
            var proofInput = JsonSerializer.Deserialize<object>(inputs["justificationProof"]!.ToJsonString());

            var recorder = new Recorder { InnerHandler = new HttpClientHandler() };
            using var gm = new GenesisMeshClient(new ClientOptions
            {
                BaseUrl = baseUrl, SigningKey = seed, KeyId = keyId, HttpHandler = recorder,
            });
            var now = DateTimeOffset.UtcNow;
            string Iso(TimeSpan d) => now.Add(d).ToString("yyyy-MM-ddTHH:mm:ssK");
            static Dictionary<string, object?> D(params (string, object?)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);
            object? Raw(JsonObject response) => JsonSerializer.Deserialize<object>(response.ToJsonString());

            // The NA's response to the call just made, checked against the typed result: every
            // key the NA sent is modeled, and every required property was sent.
            JsonObject Check<T>(string name, T typed)
            {
                var raw = recorder.Json ?? throw new InvalidOperationException(name + ": no JSON response");
                if (typed is null) return raw;
                var declared = ContractTypesTests.JsonFields(typed.GetType());
                Assert.True(raw.All(k => declared.ContainsKey(k.Key)),
                    $"{name}: unmodeled keys {string.Join(", ", raw.Select(k => k.Key).Where(k => !declared.ContainsKey(k)))}");
                if (typed is not VerifyResult)
                {
                    var missing = declared.Where(f => f.Value && !raw.ContainsKey(f.Key)).Select(f => f.Key).ToList();
                    Assert.True(missing.Count == 0, $"{name}: declared but not sent: {string.Join(", ", missing)}");
                }
                return raw;
            }
            void Accepted(string name, VerifyResult result) =>
                Assert.True(result.Valid && result.Accepted, $"{name}: the NA answered {recorder.Json?.ToJsonString()}");

            var offer = await gm.Agreement.Offer(new CapabilityOffer
            {
                ResponderSovereignId = "sovereign-b", Capabilities = ["read", "write"], ValidFrom = Iso(TimeSpan.Zero),
                ValidUntil = Iso(TimeSpan.FromHours(24)), ExpiresAt = Iso(TimeSpan.FromHours(1)),
            });
            var rawOffer = Check("agreement_offer", offer);
            Check("agreement_counter", await gm.Agreement.Counter(D(("offer", Raw(rawOffer)), ("capabilities", new[] { "read" }),
                ("scope", new Dictionary<string, object?>()), ("valid_from", Iso(TimeSpan.Zero)), ("valid_until", Iso(TimeSpan.FromHours(12))))));
            var rawAgreement = Check("agreement_accept", await gm.Agreement.Accept(offer));
            var agreementCheck = await gm.Agreement.Verify(D(("agreement", Raw(rawAgreement))));
            Check("agreement_verify", agreementCheck);
            Accepted("agreement_verify", agreementCheck);

            var decision = await gm.Boundary.Decide(D(("agreement", Raw(rawAgreement)), ("requested_capability", "read")));
            var rawDecision = Check("boundary_decide", decision);
            Assert.Equal((bool)rawDecision["authorized"]!, decision.Authorized);
            var decisionCheck = await gm.Boundary.Verify(D(("decision", Raw(rawDecision))));
            Check("boundary_verify", decisionCheck);
            Accepted("boundary_verify", decisionCheck);

            var evidence = await gm.Evidence.Build(new TrustDecision
            {
                SourceSovereignId = network, TargetSovereignId = "sovereign-b", Verdict = "allow",
                Reason = "direct recognition", Trusted = true, HopCount = 1,
            });
            var rawEvidence = Check("evidence_build", evidence);
            Assert.True(evidence.Trusted);
            Assert.Equal(1, evidence.HopCount);
            var evidenceCheck = await gm.Evidence.Verify(D(("evidence", Raw(rawEvidence))));
            Check("evidence_verify", evidenceCheck);
            Accepted("evidence_verify", evidenceCheck);

            var rawCommitment = Check("disclosure_commit",
                await gm.Disclosure.Commit(D(("capabilities", new[] { "read", "write" }), ("agreement", Raw(rawAgreement)))));
            var rawProof = Check("disclosure_prove", await gm.Disclosure.Prove(D(("capability", "read"),
                ("capabilities", new[] { "read", "write" }), ("commitment", Raw(rawCommitment)), ("prover_sovereign_id", "sovereign-b"))));
            var proofCheck = await gm.Disclosure.Verify(D(("proof", Raw(rawProof)), ("commitment", Raw(rawCommitment))));
            Check("disclosure_verify", proofCheck);
            Accepted("disclosure_verify", proofCheck);

            var rawPolicy = Check("data_usage_policy", await gm.DataUsage.CreatePolicy(D(("licensee_sovereign_id", "sovereign-b"),
                ("allowed_source_ids", new[] { "src-1" }), ("allowed_access_types", new[] { "read" }),
                ("valid_from", Iso(TimeSpan.Zero)), ("valid_until", Iso(TimeSpan.FromHours(720))))));
            Check("data_usage_get_policy", await gm.DataUsage.GetPolicy());
            var rawIntent = Check("data_usage_intent", await gm.DataUsage.CreateIntent(D(("sources", new[] { D(("source_id", "src-1"),
                ("source_type", "public"), ("owner_sovereign_id", network), ("classification_tags", Array.Empty<string>())) }),
                ("access_types", new[] { "read" }), ("decision_id", "dec-001"))));
            var usageCheck = await gm.DataUsage.Verify(D(("intent", Raw(rawIntent)), ("policy", Raw(rawPolicy))));
            Check("data_usage_verify", usageCheck);
            Accepted("data_usage_verify", usageCheck);

            var rawVote = Check("consensus_vote",
                await gm.Consensus.Vote(D(("justification_proof", proofInput), ("vote", true), ("reason", "contract"))));
            var rawConsensus = Check("consensus_proof", await gm.Consensus.Proof(D(("justification_proof", proofInput),
                ("votes", new[] { Raw(rawVote) }), ("required_threshold", 1), ("validator_sovereign_ids", new[] { network }))));
            var consensusCheck = await gm.Consensus.Verify(D(("proof", Raw(rawConsensus)),
                ("validator_public_keys", new Dictionary<string, object?> { [network] = naKey })));
            Check("consensus_verify", consensusCheck);
            Assert.True(consensusCheck.Valid);

            var attestation = await gm.Attestation.Issue(D(("subject_id", "vendor-contract"), ("roles", new[] { "role:client" })));
            Check("attestation_issue", attestation);
            Assert.Equal("vendor-contract", attestation.SubjectId);
        }
        finally
        {
            na.Kill(entireProcessTree: true);
        }
    }
}
