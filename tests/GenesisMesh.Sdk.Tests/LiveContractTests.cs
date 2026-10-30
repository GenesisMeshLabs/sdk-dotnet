using System.Diagnostics;
using System.Globalization;
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
/// CI runs it in its own job, against the core branch of the same name or main, with
/// <c>GM_E2E_REQUIRED=1</c>: then a run without <c>GM_E2E_PYTHON</c> fails rather than passes.
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
        if (string.IsNullOrEmpty(python))
        {
            // 1.8.0: where the live check is required (CI sets GM_E2E_REQUIRED=1), an unconfigured run fails
            // instead of passing green without an NA.
            if (Environment.GetEnvironmentVariable("GM_E2E_REQUIRED") == "1")
                throw new InvalidOperationException("GM_E2E_REQUIRED=1 but GM_E2E_PYTHON is not set: no NA to check against");
            return; // not configured here; CI runs it in its own job
        }

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
                BaseUrl = baseUrl,
                SigningKey = seed,
                KeyId = keyId,
                HttpHandler = recorder,
            });
            var now = DateTimeOffset.UtcNow;
            string Iso(TimeSpan d) => now.Add(d).ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture);
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
                ResponderSovereignId = "sovereign-b",
                Capabilities = ["read", "write"],
                ValidFrom = Iso(TimeSpan.Zero),
                ValidUntil = Iso(TimeSpan.FromHours(24)),
                ExpiresAt = Iso(TimeSpan.FromHours(1)),
            });
            var rawOffer = Check("agreement_offer", offer);
            var counter = await gm.Agreement.Counter(D(("offer", Raw(rawOffer)), ("capabilities", new[] { "read" }),
                ("scope", new Dictionary<string, object?>()), ("valid_from", Iso(TimeSpan.Zero)), ("valid_until", Iso(TimeSpan.FromHours(12)))));
            var rawCounter = Check("agreement_counter", counter);
            var counterAgreement = Check("agreement_accept_counter", await gm.Agreement.AcceptCounter(Raw(rawCounter)!, Raw(rawOffer)!));
            Assert.Equal(new JsonArray("read").ToJsonString(), counterAgreement["agreed_terms"]!["capabilities"]!.ToJsonString());
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
                SourceSovereignId = network,
                TargetSovereignId = "sovereign-b",
                Verdict = "allow",
                Reason = "direct recognition",
                Trusted = true,
                HopCount = 1,
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

            var policy = await gm.DataUsage.CreatePolicy(D(("licensee_sovereign_id", "sovereign-b"),
                ("allowed_source_ids", new[] { "src-1" }), ("allowed_access_types", new[] { "read" }),
                ("valid_from", Iso(TimeSpan.Zero)), ("valid_until", Iso(TimeSpan.FromHours(720)))));
            var rawPolicy = Check("data_usage_policy", policy);
            Check("data_usage_get_policy", await gm.DataUsage.GetPolicy());
            var intent = await gm.DataUsage.CreateIntent(D(("sources", new[] { D(("source_id", "src-1"),
                ("source_type", "public"), ("owner_sovereign_id", network), ("classification_tags", Array.Empty<string>())) }),
                ("access_types", new[] { "read" }), ("decision_id", "dec-001")));
            var rawIntent = Check("data_usage_intent", intent);
            var usageCheck = await gm.DataUsage.Verify(D(("intent", Raw(rawIntent)), ("policy", Raw(rawPolicy))));
            Check("data_usage_verify", usageCheck);
            Accepted("data_usage_verify", usageCheck);
            // 1.8.0: the typed records, passed back as they are, keep the form the NA signed.
            Accepted("data_usage_verify_typed", await gm.DataUsage.Verify(D(("intent", intent), ("policy", policy))));

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

            Check("health_liveness", await gm.Health.Liveness());
            var readiness = await gm.Health.Readiness();
            Check("health_readiness", readiness);
            Assert.True(readiness.Ready);
            Check("health_status", await gm.Health.Status());
            Assert.True((await gm.Health.Endpoints()).Single() is { Reachable: true, Ready: true });

            Check("na_key_history", await gm.NA.KeyHistory());
            var history = await gm.NA.VerifiedKeyHistory(naKey);
            Assert.Equal(naKey, history.Keys.Single().PublicKey);

            // 1.9.0: the policy lifecycle and policy-aware evaluation.
            var policyIntent = new BoundaryPolicyIntent
            {
                PolicyId = "contract-policy",
                Description = "contract",
                ValidFrom = Iso(TimeSpan.FromMinutes(-5)),
                ValidUntil = Iso(TimeSpan.FromHours(24)),
                Selector = new PolicySelector { ParentKinds = ["attestation"], Capabilities = ["contract.*"] },
                Gates =
                [
                    new GateSpec
                    {
                        GateId = "max-days",
                        GateType = "max_value.v1",
                        Order = 0,
                        Config = new Dictionary<string, object?> { ["path"] = "request_parameters.days", ["max"] = 90 },
                    },
                ],
            };
            Assert.True(Check("policy_validate", await gm.Policy.Validate(policyIntent))["valid"]!.GetValue<bool>());
            var boundaryPolicy = await gm.Policy.Publish(policyIntent);
            Check("policy_publish", boundaryPolicy);
            Check("policy_activate", await gm.Policy.Activate("contract-policy", boundaryPolicy.Version));
            var listed = await gm.Policy.List();
            var listedFields = ContractTypesTests.JsonFields(typeof(PolicyVersionSummary));
            Assert.All(recorder.Json!["policies"]!.AsArray(), item =>
                Assert.True(item!.AsObject().All(k => listedFields.ContainsKey(k.Key)), "policy_list: unmodeled keys"));
            Assert.Contains(listed, v => v.PolicyId == "contract-policy" && v.Active);
            Check("policy_active", await gm.Policy.Active());
            Check("policy_history", await gm.Policy.History("contract-policy"));
            var policyCheck = await gm.Policy.Verify(boundaryPolicy);
            Check("policy_verify", policyCheck);
            Assert.True(policyCheck.Valid, recorder.Json?.ToJsonString());

            var vendor = await gm.Attestation.Issue(D(("subject_id", "vendor-evaluate"), ("roles", new[] { "role:client" }),
                ("claims", D(("capabilities", new[] { "contract.read" }))), ("validity_hours", 24)));
            EvaluateRequest Evaluation(int days) => new()
            {
                AttestationId = vendor.AttestationId,
                RequestedCapability = "contract.read",
                Context = new EvaluationContext { RequestParameters = new Dictionary<string, object?> { ["days"] = days } },
            };
            var allowed = await gm.Boundary.Evaluate(Evaluation(30));
            Check("boundary_evaluate", allowed);
            Assert.True(allowed.Decision.Authorized, recorder.Json?.ToJsonString());
            var denied = await gm.Boundary.Evaluate(Evaluation(120));
            Assert.False(denied.Decision.Authorized);
            // The typed decisions, passed back as they are, keep the form the NA signed (1.9.0: denial_reason null).
            Accepted("boundary_verify_typed_allowed", await gm.Boundary.Verify(D(("decision", allowed.Decision))));
            Accepted("boundary_verify_typed_denied", await gm.Boundary.Verify(D(("decision", denied.Decision))));
            await Assert.ThrowsAsync<SecretMaterialException>(() => gm.Boundary.Evaluate(new EvaluateRequest
            {
                AttestationId = vendor.AttestationId,
                RequestedCapability = "contract.read",
                Context = new EvaluationContext { Attributes = new Dictionary<string, object?> { ["password"] = "x" } },
            }));
            Check("policy_deactivate", await gm.Policy.Deactivate("contract-policy", boundaryPolicy.Version));
        }
        finally
        {
            na.Kill(entireProcessTree: true);
        }
    }
}
