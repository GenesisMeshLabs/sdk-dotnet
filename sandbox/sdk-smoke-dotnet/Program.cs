// Genesis Mesh .NET SDK smoke test.
//
// Requires 001-na running on port 9443. Start it from WSL:
//
//   $GM na start --config $SANDBOX/001-na/genesis-mesh.toml
//
// Run from this directory:
//
//   dotnet run
//
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;

// ── Config ────────────────────────────────────────────────────────────────────

const string NaUrl      = "http://127.0.0.1:9443";
const string Network    = "001-NA";
const string KeyId      = "operator-local";

static string SandboxPath(string rel)
{
    var env = Environment.GetEnvironmentVariable("SANDBOX");
    if (env is not null) return Path.Combine(env, rel);
    var candidates = new[] {
        @"C:\Source\GenesisMeshLabs\sandbox",
        "/mnt/c/Source/GenesisMeshLabs/sandbox",
    };
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        candidates = [candidates[1], candidates[0]];
    foreach (var c in candidates)
        if (Directory.Exists(c)) return Path.Combine(c, rel);
    return rel;
}

var keyFile     = SandboxPath(Path.Combine("001-na", "keys", "operator.key"));
var genesisFile = SandboxPath(Path.Combine("001-na", "genesis.signed.json"));

// ── Helpers ───────────────────────────────────────────────────────────────────

int passed = 0, failed = 0;

void Ok(string label, string detail = "")
{
    Console.WriteLine(detail.Length > 0 ? $"  ✓  {label}  ({detail})" : $"  ✓  {label}");
    passed++;
}

void Fail(string label, Exception ex)
{
    var msg = ex is GenesisMeshException gme
        ? $"{gme.Detail} [{gme.Code} HTTP {gme.Status}]"
        : ex.Message;
    Console.Error.WriteLine($"  ✗  {label}: {msg}");
    failed++;
}

void Section(string title)
{
    var pad = Math.Max(0, 50 - title.Length);
    Console.WriteLine($"\n── {title} {new string('─', pad)}");
}

string Shorten(string s) => s.Length > 8 ? s[..8] : s;

string LoadSeed(string path)
{
    foreach (var line in File.ReadAllLines(path))
    {
        var t = line.Trim();
        if (t.Length > 0 && !t.StartsWith('#')) return t;
    }
    Console.Error.WriteLine($"No seed found in {path}"); Environment.Exit(1); return "";
}

string LoadNaPublicKey(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    if (root.TryGetProperty("network_authority", out var na) && na.TryGetProperty("public_key", out var pk))
        return pk.GetString()!;
    return root.GetProperty("public_key").GetString()!;
}

// Raw admin helper — returns deserialized JSON as Dictionary
var _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

async Task<Dictionary<string, JsonElement>> AdminPostRaw(
    string path, object body, byte[] seed)
{
    var headers = Auth.BuildAdminHeaders(body, KeyId, seed);
    var json    = JsonSerializer.Serialize(body, Auth.SerializerOptions);
    using var req = new HttpRequestMessage(HttpMethod.Post, NaUrl + path)
    {
        Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
    };
    req.Headers.Add("X-Admin-Key-Id",    headers.KeyId);
    req.Headers.Add("X-Admin-Signature", headers.Signature);
    req.Headers.Add("X-Admin-Timestamp", headers.Timestamp);
    req.Headers.Add("X-Admin-Nonce",     headers.Nonce);
    using var resp = await _http.SendAsync(req);
    var raw = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        throw new Exception($"HTTP {(int)resp.StatusCode}: {raw}");
    return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw, Auth.SerializerOptions)
        ?? [];
}

async Task AdminPost(string path, object body, byte[] seed)
{
    var headers = Auth.BuildAdminHeaders(body, KeyId, seed);
    var json    = JsonSerializer.Serialize(body, Auth.SerializerOptions);
    using var req = new HttpRequestMessage(HttpMethod.Post, NaUrl + path)
    {
        Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
    };
    req.Headers.Add("X-Admin-Key-Id",    headers.KeyId);
    req.Headers.Add("X-Admin-Signature", headers.Signature);
    req.Headers.Add("X-Admin-Timestamp", headers.Timestamp);
    req.Headers.Add("X-Admin-Nonce",     headers.Nonce);
    using var resp = await _http.SendAsync(req);
    var raw = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
    {
        var env = JsonSerializer.Deserialize<JsonElement>(raw);
        var msg = env.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
            ? m.GetString()! : raw;
        var code = e.TryGetProperty("code", out var c) ? c.GetString()! : "";
        throw new Exception($"{msg} [{code} HTTP {(int)resp.StatusCode}]");
    }
}

// ── Main ──────────────────────────────────────────────────────────────────────

Console.WriteLine("Genesis Mesh .NET SDK Smoke Test");
Console.WriteLine($"NA: {NaUrl}  network: {Network}");

var seed      = Auth.LoadSeed(LoadSeed(keyFile));
var naPubKey  = LoadNaPublicKey(genesisFile);
var now       = DateTime.UtcNow;
var oneYear   = now.AddDays(365);
string Ts(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";

// 0. Health
Section("Health");
try
{
    using var hr = await _http.GetAsync(NaUrl + "/healthz");
    var hb = await hr.Content.ReadFromJsonAsync<JsonElement>();
    if (hb.GetProperty("status").GetString() != "ok")
    { Console.Error.WriteLine("NA health check failed"); Environment.Exit(1); }
    Ok("GET /healthz", hb.GetProperty("status").GetString()!);
}
catch
{
    Console.Error.WriteLine($"\nNA is not running at {NaUrl}");
    Console.Error.WriteLine("Start it: $GM na start --config $SANDBOX/001-na/genesis-mesh.toml");
    Environment.Exit(1);
}

var client = new GenesisMeshClient(new ClientOptions
{
    BaseUrl    = NaUrl,
    SigningKey  = LoadSeed(keyFile),
    KeyId       = KeyId,
    Timeout     = TimeSpan.FromSeconds(15),
});

// 1. Attestation
Section("Attestation");
string? attestationId = null;
try
{
    var att = await client.Attestation.Issue(new Dictionary<string, object?>
    {
        ["subject_id"]     = "sdk-smoke-dotnet-001",
        ["roles"]          = new[] { "role:client", "role:anchor" },
        ["validity_hours"] = 1,
        ["claims"]         = new Dictionary<string, object?> { ["smoke_test"] = true },
    });
    attestationId = att.AttestationId;
    Ok("issue attestation", $"id={Shorten(att.AttestationId)} roles={string.Join(",", att.Roles)}");
}
catch (Exception ex) { Fail("issue attestation", ex); }

if (attestationId is not null)
{
    try
    {
        await client.Attestation.Revoke(attestationId,
            new Dictionary<string, object?> { ["reason"] = "smoke test cleanup" });
        Ok("revoke attestation", Shorten(attestationId));
    }
    catch (Exception ex) { Fail("revoke attestation", ex); }
}

// 2. Agreement: treaty → offer → accept → verify
Section("Agreement");
Dictionary<string, JsonElement>? rawAgrMap = null;
try
{
    await AdminPost("/admin/recognition-treaties", new
    {
        subject_sovereign_id = "SMOKE-BETA",
        subject_public_keys  = new[] { naPubKey },
        scope                = new { allowed_roles = new[] { "role:client" } },
        validity_hours       = 1,
    }, seed);
    Ok("issue recognition treaty for SMOKE-BETA (raw admin call)");
}
catch (Exception ex) { Fail("issue recognition treaty for SMOKE-BETA (raw admin call)", ex); }

try
{
    var rawOffer = await AdminPostRaw("/admin/agreements/offer", new
    {
        responder_sovereign_id = "SMOKE-BETA",
        capabilities           = new[] { "read:data", "write:log" },
        roles                  = new[] { "role:client" },
        valid_from             = Ts(now),
        valid_until            = Ts(oneYear),
        expires_at             = Ts(now.AddDays(7)),
    }, seed);

    var oid = rawOffer.TryGetValue("offer_id", out var oEl) ? oEl.GetString()! : "";
    Ok("create offer", $"id={Shorten(oid)}");

    var rawAgr = await AdminPostRaw("/admin/agreements/accept",
        new Dictionary<string, object?> { ["offer"] = rawOffer }, seed);
    var aid    = rawAgr.TryGetValue("agreement_id", out var aEl) ? aEl.GetString()! : "";
    var status = rawAgr.TryGetValue("status",       out var sEl) ? sEl.GetString()! : "";
    Ok("accept offer → agreement", $"id={Shorten(aid)} status={status}");
    rawAgrMap = rawAgr;
}
catch (Exception ex) { Fail("create offer / accept", ex); }

if (rawAgrMap is not null)
{
    try
    {
        var vr = await client.Agreement.Verify(
            new Dictionary<string, object?> { ["agreement"] = rawAgrMap });
        Ok("verify agreement (public)", $"valid={vr.Valid}");
    }
    catch (Exception ex) { Fail("verify agreement (public)", ex); }
}

// 3. Boundary
Section("Boundary");
if (rawAgrMap is not null)
{
    try
    {
        var rawDec = await AdminPostRaw("/admin/boundary/decide", new Dictionary<string, object?>
        {
            ["agreement"]            = rawAgrMap,
            ["requested_capability"] = "read:data",
            ["context"]              = new Dictionary<string, object?> { ["smoke_session"] = "dotnet-001" },
        }, seed);
        var allowed = rawDec.TryGetValue("allowed", out var aEl2) && aEl2.GetBoolean();
        Ok("boundary decide", $"allowed={allowed}");

        var bv = await client.Boundary.Verify(
            new Dictionary<string, object?> { ["decision"] = rawDec });
        Ok("boundary verify (public)", $"valid={bv.Valid}");
    }
    catch (Exception ex) { Fail("boundary decide / verify", ex); }
}
else { Console.WriteLine("  (skipped — no agreement)"); }

// 4. Evidence
Section("Evidence");
try
{
    var rawEv = await AdminPostRaw("/admin/trust-evidence", new
    {
        decision = new
        {
            source_sovereign_id = "SMOKE-ALPHA",
            target_sovereign_id = Network,
            verdict             = "allow",
            reason              = "smoke test validation",
            signals             = Array.Empty<object>(),
        }
    }, seed);
    var eid     = rawEv.TryGetValue("evidence_id", out var eEl) ? eEl.GetString()! : "";
    var verdict = rawEv.TryGetValue("verdict",     out var vEl) ? vEl.GetString()! : "";
    Ok("build trust evidence", $"id={Shorten(eid)} verdict={verdict}");

    var ev = await client.Evidence.Verify(
        new Dictionary<string, object?> { ["evidence"] = rawEv });
    Ok("verify evidence (public)", $"valid={ev.Valid}");
}
catch (Exception ex) { Fail("build trust evidence / verify", ex); }

// 5. Attestation policy
Section("Attestation policy");
try
{
    await client.Attestation.SavePolicy(new Dictionary<string, object?>
    {
        ["recognition_policy"] = new Dictionary<string, object?>
        {
            ["local_sovereign_id"]  = Network,
            ["recognized_issuers"] = Array.Empty<object>(),
        }
    });
    Ok("save recognition policy");
}
catch (Exception ex) { Fail("save recognition policy", ex); }

// 6. Data usage
Section("Data usage");
try
{
    var policy = await client.DataUsage.CreatePolicy(new Dictionary<string, object?>
    {
        ["licensee_sovereign_id"] = "SMOKE-BETA",
        ["allowed_source_ids"]    = new[] { "db-smoke" },
        ["allowed_access_types"]  = new[] { "read" },
        ["valid_from"]            = now.ToString("o"),
        ["valid_until"]           = oneYear.ToString("o"),
    });
    Ok("create data license policy", $"id={Shorten(policy.PolicyId)}");

    var activePol = await client.DataUsage.GetPolicy();
    Ok("GET active policy (public)", $"id={Shorten(activePol.PolicyId)}");

    var rawIntent = await AdminPostRaw("/admin/data-usage/intent", new
    {
        sources = new object[]
        {
            new {
                source_id          = "db-smoke",
                source_type        = "proprietary",
                owner_sovereign_id = Network,
                classification_tags = new[] { "internal" },
            }
        },
        access_types = new[] { "read" },
    }, seed);
    var iid = rawIntent.TryGetValue("intent_id", out var iEl) ? iEl.GetString()! : "";
    Ok("create data access intent", $"id={Shorten(iid)}");

    var rawPolicy2 = await AdminPostRaw("/admin/data-usage/policy", new
    {
        licensee_sovereign_id = "SMOKE-BETA",
        allowed_source_ids    = new[] { "db-smoke" },
        allowed_access_types  = new[] { "read" },
        valid_from            = now.ToString("o"),
        valid_until           = oneYear.ToString("o"),
    }, seed);

    var dv = await client.DataUsage.Verify(new Dictionary<string, object?>
    {
        ["intent"] = rawIntent,
        ["policy"] = rawPolicy2,
    });
    Ok("verify intent vs policy (public)", $"valid={dv.Valid}");
}
catch (Exception ex) { Fail("data usage", ex); }

// ── Summary ───────────────────────────────────────────────────────────────────

Console.WriteLine("\n" + new string('─', 52));
var total = passed + failed;
if (failed == 0)
{
    Console.WriteLine($"All {total} checks passed.");
}
else
{
    Console.Error.WriteLine($"{passed}/{total} passed, {failed} failed.");
    Environment.Exit(1);
}
