using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// The layer table of docs/architecture.md. Every source file belongs to a module, and a module uses only the
/// modules its row allows: the source is scanned for the SDK's type names, and the compiled offline modules are
/// scanned for every type their code references (so a reference the source does not spell out still counts).
/// <para>
/// Today's exceptions are listed in <see cref="AllowedSourceEdges"/> and <see cref="AllowedCompiledEdges"/>. The
/// lists only shrink: a test fails when an edge outside them appears, and when a listed edge is gone but still
/// listed.
/// </para>
/// </summary>
public partial class ArchitectureTests
{
    /// <summary>The module of each source file, by path under src/GenesisMesh.Sdk/ (a trailing / is a folder).</summary>
    private static readonly (string Path, string Module)[] FileModules =
    [
        ("StrictJson.cs", "json"),
        ("Canonical.cs", "canonical"),
        ("StrictFields.cs", "registry"),
        ("OfflineVerifier.cs", "verify"),
        ("KeyHistory.cs", "verify"),
        ("Grants.cs", "verify"),
        ("Vouching.cs", "verify"),
        ("Metadata.cs", "verify"),
        ("Models.cs", "models"),
        ("Errors.cs", "errors"),
        ("Auth.cs", "auth"),
        ("Signer.cs", "auth"),
        ("Transport.cs", "transport"),
        ("ClientOptions.cs", "transport"),
        ("Clients/", "clients"),
        ("GenesisMeshClient.cs", "client"),
    ];

    /// <summary>The modules each module may use (docs/architecture.md, "Layers").</summary>
    private static readonly Dictionary<string, string[]> MayUse = new()
    {
        ["json"] = [],
        ["errors"] = [],
        ["models"] = [],
        ["canonical"] = ["json"],
        ["registry"] = ["canonical", "json"],
        ["auth"] = ["canonical", "json"],
        ["verify"] = ["registry", "canonical", "json", "models"],
        ["transport"] = ["auth", "errors", "models", "canonical", "json"],
        ["clients"] = ["transport", "models", "errors", "verify"],
        ["client"] = ["json", "errors", "models", "canonical", "registry", "auth", "verify", "transport", "clients"],
    };

    /// <summary>The modules that may name System.Net types.</summary>
    private static readonly HashSet<string> NetworkModules = ["transport", "client"];

    /// <summary>The offline half: its compiled code is checked too.</summary>
    private static readonly HashSet<string> OfflineModules = ["json", "canonical", "registry", "verify"];

    /// <summary>System.Net types the source may name without a using directive (implicit usings).</summary>
    private static readonly HashSet<string> NetworkTypeNames =
    [
        "HttpClient", "HttpClientHandler", "HttpMessageHandler", "DelegatingHandler", "HttpRequestMessage",
        "HttpResponseMessage", "HttpMethod", "HttpContent", "StringContent", "ByteArrayContent", "StreamContent",
        "HttpRequestException", "HttpStatusCode", "HttpCompletionOption", "SocketsHttpHandler", "WebRequest", "Dns",
        "IPAddress", "Socket",
    ];

    /// <summary>
    /// Source edges that break the table today, as "file -> type (module)". Only remove entries: when an edge is
    /// gone, delete its line; a new edge is a design change for the Maintainer, not a line to add.
    /// </summary>
    private static readonly string[] AllowedSourceEdges = [];

    /// <summary>Compiled edges of the offline modules that break the table today, as "type -> type (module)".</summary>
    private static readonly string[] AllowedCompiledEdges = [];

    [Fact]
    public void EveryModuleHasARowAndEveryRowNamesModules()
    {
        var modules = FileModules.Select(f => f.Module).ToHashSet();
        Assert.Equal(modules.OrderBy(m => m), MayUse.Keys.OrderBy(m => m));
        Assert.All(MayUse.Values.SelectMany(v => v), m => Assert.Contains(m, modules));
        Assert.All(NetworkModules.Concat(OfflineModules), m => Assert.Contains(m, modules));
    }

    [Fact]
    public void EverySourceFileHasAModule()
    {
        var missing = Sources().Where(s => s.Module is null).Select(s => s.Path).ToList();
        Assert.True(missing.Count == 0,
            "Add these files to FileModules in ArchitectureTests.cs and to docs/architecture.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void SourceFollowsTheLayerTable() => AssertRatchet(SourceEdges(includeAllowed: false), AllowedSourceEdges, "source");

    [Fact]
    public void OfflineCodeUsesNoNetworkOrClientType() =>
        AssertRatchet(CompiledEdges(includeAllowed: false), AllowedCompiledEdges, "compiled offline");

    /// <summary>Both scans see the edges the table allows, so a scan that finds nothing cannot pass by accident.</summary>
    [Fact]
    public void TheScansSeeAllowedEdges()
    {
        var source = SourceEdges(includeAllowed: true);
        Assert.Contains("Clients/BoundaryClient.cs -> Transport (transport)", source);
        Assert.Contains("OfflineVerifier.cs -> StrictFields (registry)", source);
        Assert.Contains("Transport.cs -> System.Net (HttpClient)", source);
        var compiled = CompiledEdges(includeAllowed: true);
        Assert.Contains("OfflineVerifier -> StrictFields (registry)", compiled);
        Assert.Contains("Canonical -> StrictJsonReader (json)", compiled);
    }

    private static void AssertRatchet(IReadOnlySet<string> found, string[] allowed, string what)
    {
        var added = found.Except(allowed).Order(StringComparer.Ordinal).ToList();
        var gone = allowed.Except(found).Order(StringComparer.Ordinal).ToList();
        var message = new StringBuilder();
        if (added.Count > 0)
            message.Append("New ").Append(what).Append(" edges break the layer table (docs/architecture.md):\n  ")
                .AppendJoin("\n  ", added).Append('\n');
        if (gone.Count > 0)
            message.Append("These allowed ").Append(what)
                .Append(" edges are gone; delete them from the allowlist in ArchitectureTests.cs:\n  ")
                .AppendJoin("\n  ", gone).Append('\n');
        Assert.True(message.Length == 0, message.ToString());
    }

    // ── source scan ─────────────────────────────────────────────────────────

    private sealed record Source(string Path, string? Module, string Code);

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GenesisMesh.sln")))
                return Path.Combine(dir.FullName, "src", "GenesisMesh.Sdk");
        }
        throw new DirectoryNotFoundException("GenesisMesh.sln not found above " + AppContext.BaseDirectory);
    }

    private static string? ModuleOf(string path) => FileModules
        .Where(f => f.Path.EndsWith('/') ? path.StartsWith(f.Path, StringComparison.Ordinal) : path == f.Path)
        .Select(f => f.Module).FirstOrDefault();

    private static List<Source> Sources()
    {
        var root = SourceRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(p => !p.StartsWith("bin/", StringComparison.Ordinal) && !p.StartsWith("obj/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(p => new Source(p, ModuleOf(p), CodeOnly(File.ReadAllText(Path.Combine(root, p)))))
            .ToList();
    }

    /// <summary>A type declaration: modifiers at the start of a line, the kind, the name.</summary>
    [GeneratedRegex(@"^[ \t]*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|file|unsafe|new|ref)\s+)*(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b")]
    private static partial Regex Identifier();

    /// <summary>The SDK's type names and the module each is declared in.</summary>
    private static Dictionary<string, string> DeclaredTypes(List<Source> sources)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources.Where(s => s.Module is not null))
        {
            foreach (Match m in Declaration().Matches(source.Code))
            {
                var name = m.Groups[1].Value;
                if (types.TryGetValue(name, out var other) && other != source.Module)
                    throw new InvalidOperationException($"{name} is declared in two modules, {other} and {source.Module}");
                types[name] = source.Module!;
            }
        }
        return types;
    }

    /// <summary>Edges of the source; with <paramref name="includeAllowed"/>, those the table allows too.</summary>
    private static HashSet<string> SourceEdges(bool includeAllowed)
    {
        var sources = Sources();
        var types = DeclaredTypes(sources);
        var edges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources.Where(s => s.Module is not null))
        {
            var module = source.Module!;
            foreach (var name in Identifier().Matches(source.Code).Select(m => m.Value).Distinct())
            {
                if (types.TryGetValue(name, out var target) && target != module
                    && (includeAllowed || !MayUse[module].Contains(target)))
                    edges.Add($"{source.Path} -> {name} ({target})");
                if (NetworkTypeNames.Contains(name) && (includeAllowed || !NetworkModules.Contains(module)))
                    edges.Add($"{source.Path} -> System.Net ({name})");
            }
            if (source.Code.Contains("System.Net", StringComparison.Ordinal) && (includeAllowed || !NetworkModules.Contains(module)))
                edges.Add($"{source.Path} -> System.Net (namespace)");
        }
        return edges;
    }

    /// <summary>C# source without comments, string and character literals (so their words are not references).</summary>
    internal static string CodeOnly(string text)
    {
        var code = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else if (c == '\'')
            {
                i++;
                while (i < text.Length && text[i] != '\'') i += text[i] == '\\' ? 2 : 1;
                i++;
                code.Append("' '");
            }
            else if (c is '"' or '@' or '$' && StringLiteralAt(text, i) is { } start)
            {
                i = SkipString(text, start);
                code.Append("\"\"");
            }
            else
            {
                code.Append(c);
                i++;
            }
        }
        return code.ToString();
    }

    private sealed record Literal(int Quote, bool Verbatim, int RawQuotes);

    /// <summary>The kind of string literal starting at <paramref name="i"/> (after any $ and @ prefixes), if any.</summary>
    private static Literal? StringLiteralAt(string text, int i)
    {
        var j = i;
        var verbatim = false;
        while (j < text.Length && text[j] is '$' or '@')
        {
            verbatim |= text[j] == '@';
            j++;
        }
        if (j >= text.Length || text[j] != '"') return null;
        var quotes = 0;
        while (j + quotes < text.Length && text[j + quotes] == '"') quotes++;
        return new Literal(j, verbatim, quotes >= 3 ? quotes : 0);
    }

    private static int SkipString(string text, Literal start)
    {
        if (start.RawQuotes > 0)
        {
            var close = new string('"', start.RawQuotes);
            var end = text.IndexOf(close, start.Quote + start.RawQuotes, StringComparison.Ordinal);
            return end < 0 ? text.Length : end + start.RawQuotes;
        }
        var k = start.Quote + 1;
        while (k < text.Length)
        {
            if (start.Verbatim)
            {
                if (text[k] == '"')
                {
                    if (k + 1 < text.Length && text[k + 1] == '"') { k += 2; continue; }
                    return k + 1;
                }
                k++;
            }
            else
            {
                if (text[k] == '\\') { k += 2; continue; }
                if (text[k] == '"') return k + 1;
                if (text[k] == '\n') return k; // an unterminated literal ends at the line
                k++;
            }
        }
        return text.Length;
    }

    // ── compiled scan of the offline modules ────────────────────────────────

    private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

    static ArchitectureTests()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)field.GetValue(null)!;
            var value = (ushort)op.Value;
            if (value < 0x100) OneByteOpCodes[value] = op;
            else if ((value & 0xff00) == 0xfe00) TwoByteOpCodes[value & 0xff] = op;
        }
    }

    private static Type Outermost(Type type)
    {
        while (type.IsNested) type = type.DeclaringType!;
        return type;
    }

    private static string TypeName(Type type)
    {
        var name = Outermost(type).Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>Edges of the compiled offline modules; with <paramref name="includeAllowed"/>, those the table allows too.</summary>
    private static HashSet<string> CompiledEdges(bool includeAllowed)
    {
        var types = DeclaredTypes(Sources());
        var sdk = typeof(Canonical).Assembly;
        var edges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in sdk.GetTypes())
        {
            var outer = Outermost(type);
            if (outer.Namespace != "GenesisMesh" || !types.TryGetValue(TypeName(outer), out var module)
                || !OfflineModules.Contains(module))
                continue;
            foreach (var used in ReferencedTypes(type))
            {
                var target = Outermost(used);
                if (target.Namespace is { } ns && (ns == "System.Net" || ns.StartsWith("System.Net.", StringComparison.Ordinal)))
                {
                    edges.Add($"{TypeName(outer)} -> {target.FullName} (System.Net)");
                }
                else if (target.Assembly == sdk && types.TryGetValue(TypeName(target), out var targetModule)
                    && targetModule != module && (includeAllowed || !MayUse[module].Contains(targetModule)))
                {
                    edges.Add($"{TypeName(outer)} -> {TypeName(target)} ({targetModule})");
                }
            }
        }
        return edges;
    }

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Every type a type's declaration and code use: base types, members, locals and IL operands.</summary>
    private static HashSet<Type> ReferencedTypes(Type type)
    {
        var found = new HashSet<Type>();
        void Add(Type? t)
        {
            if (t is null || t.IsGenericParameter || !found.Add(t)) return;
            if (t.HasElementType) Add(t.GetElementType());
            if (t.IsGenericType) foreach (var arg in t.GetGenericArguments()) Add(arg);
        }
        void AddMember(MemberInfo? member)
        {
            switch (member)
            {
                case Type t: Add(t); break;
                case FieldInfo f: Add(f.DeclaringType); Add(f.FieldType); break;
                case MethodBase m:
                    Add(m.DeclaringType);
                    if (m is MethodInfo mi) Add(mi.ReturnType);
                    if (m.IsGenericMethod) foreach (var arg in m.GetGenericArguments()) Add(arg);
                    foreach (var p in m.GetParameters()) Add(p.ParameterType);
                    break;
            }
        }

        Add(type.BaseType);
        foreach (var i in type.GetInterfaces()) Add(i);
        foreach (var f in type.GetFields(Declared)) Add(f.FieldType);
        foreach (var p in type.GetProperties(Declared)) Add(p.PropertyType);
        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            AddMember(method);
            if (method.GetMethodBody() is not { } body) continue;
            foreach (var local in body.LocalVariables) Add(local.LocalType);
            var typeArgs = type.IsGenericType ? type.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            foreach (var token in MemberTokens(body.GetILAsByteArray() ?? []))
                AddMember(type.Module.ResolveMember(token, typeArgs, methodArgs));
        }
        return found;
    }

    /// <summary>The metadata tokens of the IL's field, method and type operands.</summary>
    private static IEnumerable<int> MemberTokens(byte[] il)
    {
        var i = 0;
        while (i < il.Length)
        {
            var op = il[i] == 0xfe ? TwoByteOpCodes[il[i + 1]] : OneByteOpCodes[il[i]];
            i += op.Size;
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + (4 * BitConverter.ToInt32(il, i));
                    break;
                case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType:
                    yield return BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                default:
                    i += 4;
                    break;
            }
        }
    }
}
