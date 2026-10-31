using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GenesisMesh.Tests;

/// <summary>
/// A disposable Network Authority for the live tests: scripts/contract_na.py, run by the Python that
/// <c>GM_E2E_PYTHON</c> names, with the genesis-mesh core installed. Disposing it stops it.
/// </summary>
internal sealed class LiveNa : IDisposable
{
    private readonly Process _process;

    private LiveNa(Process process, JsonObject inputs)
    {
        _process = process;
        Inputs = inputs;
    }

    /// <summary>The line the script printed: the connection details, the operator keys and the inputs the tests need.</summary>
    public JsonObject Inputs { get; }

    /// <summary>Where the NA listens.</summary>
    public string BaseUrl => (string)Inputs["baseUrl"]!;

    /// <summary>The NA's public key.</summary>
    public string NaPublicKey => (string)Inputs["naPublicKey"]!;

    /// <summary>
    /// Starts the NA with the script's <paramref name="arguments"/>; null where the live tests are not configured
    /// (<c>GM_E2E_PYTHON</c> unset). 1.8.0: where the live tests are required (CI sets <c>GM_E2E_REQUIRED=1</c>), an
    /// unconfigured run fails instead of passing green without an NA.
    /// </summary>
    public static async Task<LiveNa?> Start(params string[] arguments)
    {
        var python = Environment.GetEnvironmentVariable("GM_E2E_PYTHON");
        if (string.IsNullOrEmpty(python))
        {
            if (Environment.GetEnvironmentVariable("GM_E2E_REQUIRED") == "1")
                throw new InvalidOperationException("GM_E2E_REQUIRED=1 but GM_E2E_PYTHON is not set: no NA to check against");
            return null; // not configured here; CI runs the live tests in their own job
        }

        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true };
        start.ArgumentList.Add(Script());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        try
        {
            var line = await process.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("the NA did not start");
            return new LiveNa(process, JsonNode.Parse(line)!.AsObject());
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }

    private static string Script()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var script = Path.Combine(dir.FullName, "scripts", "contract_na.py");
            if (File.Exists(script)) return script;
        }
        throw new FileNotFoundException("scripts/contract_na.py not found above " + AppContext.BaseDirectory);
    }
}
