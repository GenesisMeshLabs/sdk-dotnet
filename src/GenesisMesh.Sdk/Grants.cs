using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenesisMesh;

/// <summary>Whether a pre-issued grant is the NA's: <see cref="Reason"/> is <c>accepted</c> when it is.</summary>
/// <param name="Valid">True when the grant is accepted.</param>
/// <param name="Reason">
/// <c>accepted</c>, or why not: <c>malformed</c> (not a grant as the reference reads it), <c>missing_signature</c>,
/// <c>invalid_signature</c>, <c>unknown_field</c>, <c>non_canonical_form</c>, <c>na_key_not_valid_at_time</c> or
/// <c>outlives_key</c>.
/// </param>
/// <param name="GrantId">The grant's ID, when it has one.</param>
public sealed record GrantVerificationResult(bool Valid, string Reason, string? GrantId);

/// <summary>
/// A number as a grant's envelope holds it, as every implementation reads one: an integral number is an integer
/// (<c>5.0</c> is <c>5</c>), integers fit 64 bits (<c>-2^63</c> to <c>2^64 - 1</c>), and comparisons are exact, an
/// integer against any other number too.
/// </summary>
public readonly struct GrantNumber : IEquatable<GrantNumber>, IComparable<GrantNumber>
{
    private static readonly BigInteger MinInteger = -BigInteger.Pow(2, 63);
    private static readonly BigInteger MaxInteger = BigInteger.Pow(2, 64) - 1;
    private readonly BigInteger _integer;
    private readonly double _fraction;
    private readonly bool _isFraction;

    private GrantNumber(BigInteger integer)
    {
        _integer = integer;
    }

    private GrantNumber(double fraction)
    {
        _fraction = fraction;
        _isFraction = true;
    }

    /// <summary>The number as an integer, when it is integral.</summary>
    public BigInteger? IntegralValue => _isFraction ? null : _integer;

    /// <summary>True when the number is integral; <c>default(GrantNumber)</c> is the integer 0.</summary>
    public bool IsIntegral => !_isFraction;

    /// <summary>The number as a double (an integer beyond 2^53 rounded to the nearest, as JavaScript and Python round it).</summary>
    // From its digits: a BigInteger cast truncates (2^64 - 1 becomes 2^64 - 2048).
    public double Value => _isFraction ? _fraction : double.Parse(_integer.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a JSON number as the reference reads it: an integer literal exactly, any other number as a double, and an
    /// integral result as an integer. False for anything but a finite number, and for an integer beyond 64 bits.
    /// </summary>
    /// <param name="value">The JSON value.</param>
    /// <param name="number">The number read.</param>
    public static bool TryRead(JsonElement value, out GrantNumber number)
    {
        number = default;
        if (value.ValueKind != JsonValueKind.Number) return false;
        var text = value.GetRawText();
        BigInteger integer;
        if (text.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
        {
            integer = BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
        else
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return false;
            if (!double.IsInteger(d))
            {
                number = new GrantNumber(d);
                return true;
            }
            integer = new BigInteger(d);
        }
        if (integer < MinInteger || integer > MaxInteger) return false;
        number = new GrantNumber(integer);
        return true;
    }

    /// <summary>An integer, exactly (an envelope's bound, for <see cref="NumericRange"/>).</summary>
    /// <param name="value">The integer.</param>
    public static GrantNumber Of(long value) => new(new BigInteger(value));

    /// <summary>An integer, exactly: up to 2^64 - 1.</summary>
    /// <param name="value">The integer.</param>
    /// <exception cref="ArgumentOutOfRangeException">It does not fit 64 bits (-2^63 to 2^64 - 1).</exception>
    public static GrantNumber Of(BigInteger value) => value < MinInteger || value > MaxInteger
        ? throw new ArgumentOutOfRangeException(nameof(value), value, "integers must fit in 64 bits")
        : new GrantNumber(value);

    /// <summary>A number as the reference holds it: an integral one as an integer (<c>5.0</c> is <c>5</c>).</summary>
    /// <param name="value">The number.</param>
    /// <exception cref="ArgumentOutOfRangeException">It is not finite, or is integral beyond 64 bits.</exception>
    public static GrantNumber Of(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), value, "numbers must be finite");
        return double.IsInteger(value) ? Of(new BigInteger(value)) : new GrantNumber(value);
    }

    /// <summary>The number in JSON: an integer's digits, any other number as the reference prints a float.</summary>
    public override string ToString() => _isFraction ? Canonical.FloatRepr(_fraction) : _integer.ToString(CultureInfo.InvariantCulture);

    internal JsonElement Element()
    {
        using var doc = JsonDocument.Parse(ToString());
        return doc.RootElement.Clone();
    }

    /// <summary>Compares exactly: an integer against another number by value, never rounded.</summary>
    /// <param name="other">The number to compare with.</param>
    public int CompareTo(GrantNumber other)
    {
        if (!_isFraction && !other._isFraction) return _integer.CompareTo(other._integer);
        if (_isFraction && other._isFraction) return _fraction.CompareTo(other._fraction);
        // An integer against a number with a fraction: by its floor, which the fraction exceeds.
        var (integer, fraction, sign) = _isFraction ? (other._integer, _fraction, -1) : (_integer, other._fraction, 1);
        return sign * (integer <= new BigInteger(Math.Floor(fraction)) ? -1 : 1);
    }

    /// <summary>True when the numbers are equal, exactly.</summary>
    /// <param name="other">The number to compare with.</param>
    public bool Equals(GrantNumber other) => CompareTo(other) == 0;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GrantNumber n && Equals(n);

    /// <inheritdoc/>
    public override int GetHashCode() => _isFraction ? _fraction.GetHashCode() : _integer.GetHashCode();

    /// <summary>True when the numbers are equal, exactly.</summary>
    public static bool operator ==(GrantNumber left, GrantNumber right) => left.Equals(right);

    /// <summary>True when the numbers differ.</summary>
    public static bool operator !=(GrantNumber left, GrantNumber right) => !left.Equals(right);

    /// <summary>True when <paramref name="left"/> is less, exactly.</summary>
    public static bool operator <(GrantNumber left, GrantNumber right) => left.CompareTo(right) < 0;

    /// <summary>True when <paramref name="left"/> is less or equal, exactly.</summary>
    public static bool operator <=(GrantNumber left, GrantNumber right) => left.CompareTo(right) <= 0;

    /// <summary>True when <paramref name="left"/> is greater, exactly.</summary>
    public static bool operator >(GrantNumber left, GrantNumber right) => left.CompareTo(right) > 0;

    /// <summary>True when <paramref name="left"/> is greater or equal, exactly.</summary>
    public static bool operator >=(GrantNumber left, GrantNumber right) => left.CompareTo(right) >= 0;
}

/// <summary>A typed numeric range: <c>integer</c> (integral numbers) or <c>number</c> (any finite number) within the bounds.</summary>
/// <param name="NumberType"><c>integer</c> or <c>number</c>.</param>
/// <param name="Minimum">The lowest bound.</param>
/// <param name="Maximum">The highest bound.</param>
/// <param name="ExclusiveMinimum">True when the lowest bound is outside the range.</param>
/// <param name="ExclusiveMaximum">True when the highest bound is outside the range.</param>
public sealed record NumericRange(string NumberType, GrantNumber Minimum, GrantNumber Maximum, bool ExclusiveMinimum, bool ExclusiveMaximum);

/// <summary>
/// One parameter a grant's uses may carry, normalized (<see cref="GrantRules.Parameter"/>): exactly one of
/// <see cref="OneOf"/> (a finite set of scalars), <see cref="SubsetOf"/> (a list of strings drawn from a set) and
/// <see cref="NumberRange"/>.
/// </summary>
/// <param name="Required">True when every use carries it.</param>
/// <param name="OneOf">The values a use may give, as scalars (an integral number as an integer); null when not this kind.</param>
/// <param name="SubsetOf">The strings a use's list may hold; null when not this kind.</param>
/// <param name="NumberRange">The range a use's number lies in; null when not this kind.</param>
public sealed record EnvelopeParameter(bool Required, IReadOnlyList<JsonElement>? OneOf, IReadOnlyList<string>? SubsetOf, NumericRange? NumberRange)
{
    /// <summary>The parameter in JSON, as the reference writes the normalized parameter.</summary>
    public string ToJson()
    {
        static JsonNode? Number(GrantNumber n) => JsonNode.Parse(n.ToString());
        var range = NumberRange is { } r
            ? new JsonObject
            {
                ["number_type"] = r.NumberType,
                ["minimum"] = Number(r.Minimum),
                ["maximum"] = Number(r.Maximum),
                ["exclusive_minimum"] = r.ExclusiveMinimum,
                ["exclusive_maximum"] = r.ExclusiveMaximum,
            }
            : null;
        return new JsonObject
        {
            ["required"] = Required,
            ["one_of"] = OneOf is null ? null : new JsonArray([.. OneOf.Select(v => JsonNode.Parse(v.GetRawText()))]),
            ["subset_of"] = SubsetOf is null ? null : new JsonArray([.. SubsetOf.Select(s => (JsonNode?)JsonValue.Create(s))]),
            ["number_range"] = range,
        }.ToJsonString();
    }
}

/// <summary>The last admitted use of a grant before another: the order and time rules compare a use with it (never with a quarantined use).</summary>
/// <param name="ExecutedAt">When it ran.</param>
/// <param name="TimeTokenIssuedAt">When its time token was issued.</param>
public sealed record PreviousUse(string ExecutedAt, string TimeTokenIssuedAt);

/// <summary>The head of a grant's use chain: its last use, admitted or quarantined after it continued the chain.</summary>
/// <param name="UseSequence">The head's position; 0 before the first use.</param>
/// <param name="Digest">The head's digest; null before the first use.</param>
public sealed record UseChainHead(int UseSequence, string? Digest);

/// <summary>The use a time token names, when held: admitted or quarantined.</summary>
/// <param name="Digest">Its digest.</param>
/// <param name="ExecutedAt">When it ran.</param>
public sealed record TokenHead(string Digest, string ExecutedAt);

/// <summary>What <see cref="GrantRules.UseProblem"/> checks a use against: whatever the caller holds of its grant and chain.</summary>
public sealed record UseCheck
{
    /// <summary>The grant; null skips the checks that need it.</summary>
    public CapabilityGrant? Grant { get; init; }

    /// <summary>The last admitted use before it; null for none or when it is not held.</summary>
    public PreviousUse? Previous { get; init; }

    /// <summary>How many uses of the grant were admitted before it; null when the chain is not held from its start (the budget is then not checked).</summary>
    public int? Admitted { get; init; }

    /// <summary>The use the time token names, when held.</summary>
    public TokenHead? TokenHead { get; init; }

    /// <summary>The NA keys valid at the token's <c>issued_at</c>.</summary>
    public IReadOnlyList<string> TokenKeys { get; init; } = [];

    /// <summary>The time token as received; default the use's.</summary>
    public JsonElement? RawToken { get; init; }

    /// <summary>When the grant's revocation recorded before the use took effect; null for none.</summary>
    public string? RevokedAt { get; init; }

    /// <summary>The use's time in the store; null before it is stored.</summary>
    public string? RecordedAt { get; init; }
}

/// <summary>Why a use is refused: a value of <see cref="GrantUseRejectionCodes.All"/>, and what is wrong.</summary>
/// <param name="Code">The NA's code.</param>
/// <param name="Detail">What is wrong.</param>
public sealed record UseProblem(string Code, string Detail);

/// <summary>
/// The rules every implementation applies to pre-issued grants (Genesis Mesh 1.6.0; 1.9.0 in this SDK), as pure
/// functions with the reference's codes (the <c>grants</c> conformance suite): a grant's parameter envelope and the
/// values a use gives, a use's place in its grant's use chain and the rules it must keep (<see cref="UseProblem"/>), and
/// the digests uses chain by. Numbers follow one rule everywhere (<see cref="GrantNumber"/>); scalars compare
/// type-strictly (<c>true</c> is not <c>1</c>, <c>"1"</c> is not <c>1</c>, <c>1</c> is <c>1.0</c>). Nothing here calls
/// the NA.
/// </summary>
public static partial class GrantRules
{
    private const long MicrosPerSecond = 1_000_000;
    private const long SkewMicros = GrantLimits.ClockSkewSeconds * MicrosPerSecond;

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterName();

    private static readonly string[] ParameterFields = ["required", "one_of", "subset_of", "number_range"];
    private static readonly string[] RangeFields = ["number_type", "minimum", "maximum", "exclusive_minimum", "exclusive_maximum"];
    private static readonly string[] RecordModels = ["CapabilityGrant", "GrantExecutionEvidence", "GrantRevocationRecord", "TimeToken", "TimeTokenRequest"];

    // ── Numbers and scalars ─────────────────────────────────────────────────

    /// <summary>A scalar an envelope may hold: null, a string, a boolean or a number.</summary>
    private static bool IsScalar(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number;

    /// <summary>
    /// Type-strict scalar equality, exact for numbers: <c>true</c> is not <c>1</c>, <c>"1"</c> is not <c>1</c>, <c>1</c>
    /// is <c>1.0</c>. False when either is not a scalar.
    /// </summary>
    /// <param name="a">A JSON value.</param>
    /// <param name="b">Another.</param>
    public static bool ScalarEquals(JsonElement a, JsonElement b)
    {
        if (!IsScalar(a) || !IsScalar(b)) return false;
        return (a.ValueKind, b.ValueKind) switch
        {
            (JsonValueKind.Null, JsonValueKind.Null) or (JsonValueKind.True, JsonValueKind.True) or (JsonValueKind.False, JsonValueKind.False) => true,
            (JsonValueKind.String, JsonValueKind.String) => a.GetString() == b.GetString(),
            (JsonValueKind.Number, JsonValueKind.Number) => GrantNumber.TryRead(a, out var x) && GrantNumber.TryRead(b, out var y)
                ? x == y
                : a.GetRawText() == b.GetRawText(),
            _ => false,
        };
    }

    /// <summary>A scalar normalized: an integral number as an integer; null when a number cannot be read.</summary>
    private static JsonElement? NormalizedScalar(JsonElement value) =>
        value.ValueKind != JsonValueKind.Number ? value.Clone() : GrantNumber.TryRead(value, out var n) ? n.Element() : null;

    /// <summary>
    /// Whether a value is within a range: a number (never a boolean), integral for an integer range, whose ends are then
    /// the integers inside its bounds.
    /// </summary>
    /// <param name="value">The JSON value.</param>
    /// <param name="range">The range.</param>
    public static bool InRange(JsonElement value, NumericRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!GrantNumber.TryRead(value, out var n)) return false;
        if (range.NumberType == "integer")
        {
            // The ends may step one past 64 bits: compared as integers.
            if (n.IntegralValue is not { } i || range.Minimum.IntegralValue is not { } min || range.Maximum.IntegralValue is not { } max) return false;
            return i >= min + (range.ExclusiveMinimum ? 1 : 0) && i <= max - (range.ExclusiveMaximum ? 1 : 0);
        }
        var above = n.CompareTo(range.Minimum);
        var below = n.CompareTo(range.Maximum);
        return (above > 0 || (above == 0 && !range.ExclusiveMinimum)) && (below < 0 || (below == 0 && !range.ExclusiveMaximum));
    }

    // ── The envelope ────────────────────────────────────────────────────────

    /// <summary>
    /// An envelope parameter as the reference reads it, normalized (defaults filled in, integral numbers as integers);
    /// null with <paramref name="problem"/> (<c>invalid envelope parameter: ...</c>) when the NA would refuse it
    /// (<c>invalid_grant_envelope</c>).
    /// </summary>
    /// <param name="raw">The parameter, as received.</param>
    /// <param name="problem">Why it is refused; null when it is read.</param>
    public static EnvelopeParameter? Parameter(JsonElement raw, out string? problem)
    {
        var (parameter, why) = ReadParameter(raw);
        problem = why is null ? null : "invalid envelope parameter: " + why;
        return parameter;
    }

    private static (EnvelopeParameter? Parameter, string? Problem) ReadParameter(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return (null, "an envelope parameter is an object");
        var unknown = Members(raw).Keys.Where(k => !ParameterFields.Contains(k)).Order(Canonical.CodePointComparer.Instance).ToList();
        if (unknown.Count > 0) return (null, "unknown field " + string.Join(", ", unknown));
        JsonElement? Given(string name) => raw.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
        var required = true;
        if (raw.TryGetProperty("required", out var req))
        {
            if (req.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, "required is a boolean");
            required = req.ValueKind == JsonValueKind.True;
        }
        List<JsonElement>? oneOf = null;
        if (Given("one_of") is { } rawOneOf)
        {
            if (rawOneOf.ValueKind != JsonValueKind.Array || rawOneOf.GetArrayLength() is < 1 or > GrantLimits.MaxEnvelopeValues)
                return (null, $"one_of is a list of 1 to {GrantLimits.MaxEnvelopeValues} scalars");
            if (!rawOneOf.EnumerateArray().All(IsScalar)) return (null, "one_of holds scalars only");
            oneOf = [];
            foreach (var value in rawOneOf.EnumerateArray())
            {
                if (NormalizedScalar(value) is not { } normalized) return (null, NumberProblem(value));
                if (oneOf.Any(v => ScalarEquals(v, normalized))) return (null, "one_of repeats a value");
                oneOf.Add(normalized);
            }
        }
        List<string>? subsetOf = null;
        if (Given("subset_of") is { } rawSubset)
        {
            if (rawSubset.ValueKind != JsonValueKind.Array || rawSubset.GetArrayLength() is < 1 or > GrantLimits.MaxEnvelopeValues
                || rawSubset.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                return (null, $"subset_of is a list of 1 to {GrantLimits.MaxEnvelopeValues} strings");
            subsetOf = [.. rawSubset.EnumerateArray().Select(v => v.GetString()!)];
            if (subsetOf.Distinct(StringComparer.Ordinal).Count() != subsetOf.Count) return (null, "subset_of repeats a value");
        }
        NumericRange? range = null;
        if (Given("number_range") is { } rawRange)
        {
            var (read, why) = ReadRange(rawRange);
            if (read is null) return (null, why);
            range = read;
        }
        if (new object?[] { oneOf, subsetOf, range }.Count(v => v is not null) != 1)
            return (null, "give exactly one of one_of, subset_of and number_range");
        return (new EnvelopeParameter(required, oneOf, subsetOf, range), null);
    }

    /// <summary>Why a number is not one an envelope holds.</summary>
    private static string NumberProblem(JsonElement value) => value.ValueKind != JsonValueKind.Number
        ? "a number is expected"
        : double.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? "integers must fit in 64 bits"
            : "numbers must be finite";

    private static (NumericRange? Range, string? Problem) ReadRange(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || raw.EnumerateObject().Any(p => !RangeFields.Contains(p.Name)))
            return (null, "number_range takes number_type, minimum, maximum, exclusive_minimum and exclusive_maximum");
        var type = raw.TryGetProperty("number_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (type is not ("integer" or "number")) return (null, "number_type is integer or number");
        GrantNumber? Bound(string name, out string? why)
        {
            why = null;
            if (raw.TryGetProperty(name, out var v) && GrantNumber.TryRead(v, out var n)) return n;
            why = raw.TryGetProperty(name, out var given) ? NumberProblem(given) : "a number is expected";
            return null;
        }
        if (Bound("minimum", out var minimumProblem) is not { } minimum) return (null, minimumProblem);
        if (Bound("maximum", out var maximumProblem) is not { } maximum) return (null, maximumProblem);
        bool? Flag(string name) => !raw.TryGetProperty(name, out var v) ? false
            : v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : null;
        if (Flag("exclusive_minimum") is not { } exclusiveMinimum) return (null, "exclusive_minimum is a boolean");
        if (Flag("exclusive_maximum") is not { } exclusiveMaximum) return (null, "exclusive_maximum is a boolean");
        var range = new NumericRange(type, minimum, maximum, exclusiveMinimum, exclusiveMaximum);
        if (type == "integer")
        {
            if (!minimum.IsIntegral || !maximum.IsIntegral) return (null, "an integer range has integer bounds");
            var low = minimum.IntegralValue!.Value + (exclusiveMinimum ? 1 : 0);
            var high = maximum.IntegralValue!.Value - (exclusiveMaximum ? 1 : 0);
            if (low > high) return (null, "the range holds no integer");
        }
        else
        {
            var order = minimum.CompareTo(maximum);
            if (order > 0 || (order == 0 && (exclusiveMinimum || exclusiveMaximum))) return (null, "the range holds no number");
        }
        return (range, null);
    }

    /// <summary>Why a parameter envelope cannot be a grant's (its names, its size, each parameter), or null.</summary>
    /// <param name="envelope">The envelope, as received.</param>
    public static string? EnvelopeShapeProblem(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object) return "parameter_envelope is an object of parameter names";
        var names = Members(envelope);
        if (names.Count > GrantLimits.MaxEnvelopeParameters) return $"at most {GrantLimits.MaxEnvelopeParameters} envelope parameters";
        foreach (var (name, value) in names)
        {
            if (!ParameterName().IsMatch(name)) return "envelope parameter names match [A-Za-z0-9_-]{1,64}";
            if (Parameter(value, out var problem) is null) return $"{name}: {problem}";
        }
        return null;
    }

    /// <summary>Why one parameter value is outside its envelope parameter, or null.</summary>
    /// <param name="name">The parameter's name.</param>
    /// <param name="value">The value a use gives.</param>
    /// <param name="spec">The envelope parameter.</param>
    public static string? ParameterValueProblem(string name, JsonElement value, EnvelopeParameter spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.OneOf is { } oneOf)
        {
            if (!IsScalar(value)) return $"request_parameters.{name} is not a scalar";
            return oneOf.Any(v => ScalarEquals(v, value)) ? null : $"request_parameters.{name} is not one of its values";
        }
        if (spec.SubsetOf is { } subsetOf)
        {
            if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                return $"request_parameters.{name} is not a list of strings";
            return value.EnumerateArray().All(v => subsetOf.Contains(v.GetString()!, StringComparer.Ordinal))
                ? null
                : $"request_parameters.{name} has an item outside its set";
        }
        return InRange(value, spec.NumberRange!) ? null : $"request_parameters.{name} is outside its range";
    }

    /// <summary>
    /// Why a use's <c>request_parameters</c> are outside a grant's closed envelope, or null
    /// (<c>grant_envelope_mismatch</c> otherwise): a parameter the envelope does not name is refused, a required one must
    /// be present, and a present one must be inside its parameter. The envelope's parameters are read as
    /// <see cref="Parameter"/> reads them.
    /// </summary>
    /// <param name="envelope">The grant's envelope, as received.</param>
    /// <param name="parameters">The use's request parameters.</param>
    public static string? EnvelopeProblem(JsonElement envelope, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return "request_parameters is not an object";
        var named = Members(envelope);
        var outside = Members(parameters).Keys.Where(k => !named.ContainsKey(k)).Order(Canonical.CodePointComparer.Instance).ToList();
        if (outside.Count > 0) return $"request_parameters.{outside[0]} is not in the envelope";
        foreach (var name in named.Keys.Order(Canonical.CodePointComparer.Instance))
        {
            if (Parameter(named[name], out _) is not { } spec) return $"parameter_envelope.{name} is not an envelope parameter";
            if (!parameters.TryGetProperty(name, out var value))
            {
                if (spec.Required) return $"request_parameters.{name} is required";
                continue;
            }
            if (ParameterValueProblem(name, value, spec) is { } problem) return problem;
        }
        return null;
    }

    /// <summary>An object's members by name, a repeated name's last value winning (as JSON.parse and Python's json read them); none for another value.</summary>
    private static Dictionary<string, JsonElement> Members(JsonElement value)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in value.EnumerateObject()) members[p.Name] = p.Value;
        }
        return members;
    }

    // ── Digests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// SHA-256 of a grant record's signed form as received, unknown fields set aside: what a use's
    /// <c>grant_digest</c>, the next use's <c>prev_use_digest</c> and a time token's head name, as the reference
    /// computes it.
    /// </summary>
    /// <param name="model">
    /// <c>CapabilityGrant</c>, <c>GrantExecutionEvidence</c>, <c>GrantRevocationRecord</c>, <c>TimeToken</c> or
    /// <c>TimeTokenRequest</c>.
    /// </param>
    /// <param name="recordJson">The record, as received.</param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    /// <exception cref="ArgumentException"><paramref name="model"/> is not one of the five.</exception>
    public static string RecordDigest(string model, string recordJson)
    {
        if (!RecordModels.Contains(model)) throw new ArgumentException($"not a grant record model: {model}", nameof(model));
        using var doc = StrictJsonReader.Parse(recordJson);
        return OfflineVerifier.GrantRecordDigest(model, doc.RootElement);
    }

    /// <summary><c>CapabilityGrant.digest()</c>: what a use's and a revocation's <c>grant_digest</c> name.</summary>
    /// <param name="grant">The grant.</param>
    public static string GrantDigest(CapabilityGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return OfflineVerifier.GrantRecordDigest("CapabilityGrant", JsonSerializer.SerializeToElement(grant));
    }

    /// <summary><c>GrantExecutionEvidence.digest()</c>: what the next use's <c>prev_use_digest</c> and a time token's head name.</summary>
    /// <param name="use">The use.</param>
    public static string UseDigest(GrantExecutionEvidence use)
    {
        ArgumentNullException.ThrowIfNull(use);
        return OfflineVerifier.GrantRecordDigest("GrantExecutionEvidence", JsonSerializer.SerializeToElement(use));
    }

    // ── Uses ────────────────────────────────────────────────────────────────

    /// <summary>Whether a use continues a grant's use chain whose head is <paramref name="head"/>.</summary>
    /// <param name="use">The use.</param>
    /// <param name="head">The chain's head.</param>
    public static bool ContinuesChain(GrantExecutionEvidence use, UseChainHead head)
    {
        ArgumentNullException.ThrowIfNull(use);
        ArgumentNullException.ThrowIfNull(head);
        return use.UseSequence == head.UseSequence + 1 && use.PrevUseDigest == head.Digest;
    }

    /// <summary>What the next use is checked against once <paramref name="use"/> is admitted.</summary>
    /// <param name="use">The admitted use.</param>
    public static PreviousUse NextPreviousUse(GrantExecutionEvidence use)
    {
        ArgumentNullException.ThrowIfNull(use);
        return new PreviousUse(use.ExecutedAt, Token(use.TimeToken)?.IssuedAt ?? "");
    }

    /// <summary>Why a use may carry secret material, or null: the metadata guard over its parameters and outcome detail.</summary>
    /// <param name="use">The use.</param>
    public static string? UseSecretProblem(GrantExecutionEvidence use)
    {
        ArgumentNullException.ThrowIfNull(use);
        static JsonNode? Object(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(e.GetRawText());
        return EvidenceMetadata.MetadataProblem(JsonSerializer.SerializeToElement(new JsonObject
        {
            ["execution_parameters"] = Object(use.ExecutionParameters),
            ["request_parameters"] = Object(use.RequestParameters),
            ["outcome_detail"] = use.OutcomeDetail ?? "",
        }));
    }

    private static TimeToken? Token(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return OfflineVerifier.ReadPayload<TimeToken>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A record's time in microseconds; null for one it cannot read (a comparison with it is false).</summary>
    private static long? Micros(string? value) => value is null ? null : OfflineVerifier.TimestampMicros(SixDigits(value));

    /// <summary>A time the caller gives, in microseconds.</summary>
    /// <exception cref="ArgumentException">It is not a timestamp.</exception>
    private static long CallerMicros(string? value, string field) =>
        (value is null ? null : OfflineVerifier.TimestampMicros(SixDigits(value)))
        ?? throw new ArgumentException($"check.{field} is not a timestamp: {value ?? "null"}");

    /// <summary>
    /// A time with the digits past the sixth of its fraction dropped, as TypeScript and Python read one (.NET's round-trip
    /// format, <c>"o"</c>, writes seven).
    /// </summary>
    internal static string SixDigits(string value) =>
        LongFraction().Match(value) is { Success: true } m ? value.Remove(m.Index + 7, m.Length - 7) : value;

    [GeneratedRegex(@"\.[0-9]{7,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongFraction();

    /// <summary>A time as the reference prints it.</summary>
    private static string Iso(long? micros) => micros is { } m && m >= 0 && m <= DateTime.MaxValue.Ticks / 10
        ? Canonical.Timestamp(new DateTimeOffset(m * 10, TimeSpan.Zero))
        : "?";

    /// <summary>
    /// The first rule a use breaks, or null, in the order every implementation checks (the reference's
    /// <c>use_problem</c>): the capability, the envelope, the budget (admitted uses), the time token's signature, grant
    /// and age, the token's head, a token older than the last admitted use's, the token's use limit (at most
    /// <c>max_uses_per_time_token</c> uses after the use it names), more than 300 seconds before the token, before the
    /// last admitted use, outside the validity, in the future, uploaded late, revoked. Run after the use's chain
    /// position, signature, bearer key and grant digest are checked.
    /// </summary>
    /// <param name="use">The use.</param>
    /// <param name="check">What the caller holds of its grant and chain.</param>
    /// <exception cref="ArgumentException">A time in <paramref name="check"/> is not a timestamp.</exception>
    public static UseProblem? UseProblem(GrantExecutionEvidence use, UseCheck check)
    {
        ArgumentNullException.ThrowIfNull(use);
        ArgumentNullException.ThrowIfNull(check);
        var grant = check.Grant;
        var previous = check.Previous;
        // The caller's times first: one it gives must be readable (an unread time would skip its rule).
        var previousTokenIssued = previous is null ? null : (long?)CallerMicros(previous.TimeTokenIssuedAt, "Previous.TimeTokenIssuedAt");
        var previousExecuted = previous is null ? null : (long?)CallerMicros(previous.ExecutedAt, "Previous.ExecutedAt");
        var headExecuted = check.TokenHead is { } h ? (long?)CallerMicros(h.ExecutedAt, "TokenHead.ExecutedAt") : null;
        var recorded = check.RecordedAt is { } r ? (long?)CallerMicros(r, "RecordedAt") : null;
        var revoked = check.RevokedAt is { } v ? (long?)CallerMicros(v, "RevokedAt") : null;
        var token = Token(use.TimeToken);
        var executed = Micros(use.ExecutedAt);
        var tokenIssued = Micros(token?.IssuedAt);
        if (grant is not null)
        {
            if (!grant.Capabilities.Contains(use.Capability, StringComparer.Ordinal))
                return new("grant_capability_mismatch", $"{use.Capability} is not one of the grant's capabilities");
            if (EnvelopeProblem(grant.ParameterEnvelope, use.RequestParameters) is { } problem) return new("grant_envelope_mismatch", problem);
            if (check.Admitted >= grant.MaxUses)
                return new("grant_budget_exhausted", $"{check.Admitted} uses admitted of a budget of {grant.MaxUses}");
        }
        var raw = check.RawToken ?? use.TimeToken;
        if (token is null || token.GrantId != use.GrantId || !OfflineVerifier.TokenSignatureValid(raw, check.TokenKeys)
            || (grant is not null && tokenIssued < Micros(grant.IssuedAt)))
            return new("grant_time_token_invalid", "the time token is not the NA's for this grant");
        if (token.UseSequence >= use.UseSequence)
            return new("grant_time_token_mismatch", $"the time token was issued after use {token.UseSequence}, not before use {use.UseSequence}");
        if (check.TokenHead is { } head && (head.Digest != token.UseHeadDigest || headExecuted > tokenIssued + SkewMicros))
            return new("grant_time_token_mismatch", $"use {token.UseSequence} is not the one the time token names");
        if (previous is not null && tokenIssued < previousTokenIssued)
            return new("grant_time_token_regressed", "the time token is older than the previous use's");
        if (grant is not null && use.UseSequence - token.UseSequence > grant.MaxUsesPerTimeToken)
            return new("grant_time_token_exhausted",
                $"use {use.UseSequence} is more than {grant.MaxUsesPerTimeToken} uses after the time token's {token.UseSequence}");
        if (executed < tokenIssued - SkewMicros)
            return new("grant_use_before_time_token",
                $"executed at {Iso(executed)}, more than {GrantLimits.ClockSkewSeconds} s before the time token's {Iso(tokenIssued)}");
        if (previous is not null && executed < previousExecuted)
            return new("grant_use_out_of_order", $"executed at {Iso(executed)}, before the previous use");
        if (grant is not null && (executed < Micros(grant.ValidFrom) || executed > Micros(grant.ValidUntil)))
            return new("grant_use_outside_validity",
                $"executed at {Iso(executed)}, outside [{Iso(Micros(grant.ValidFrom))}, {Iso(Micros(grant.ValidUntil))}]");
        if (recorded is not null)
        {
            if (executed > recorded + SkewMicros)
                return new("grant_use_in_future", $"executed at {Iso(executed)}, after it was recorded at {Iso(recorded)}");
            if (grant is not null && Micros(grant.ValidUntil) - Micros(grant.ValidFrom) is { } validity && recorded - executed > validity)
                return new("grant_use_uploaded_late",
                    $"recorded at {Iso(recorded)}, longer after it was executed than the grant's validity ({validity / MicrosPerSecond} s)");
        }
        if (revoked is not null && executed >= revoked)
            return new("grant_revoked", $"the grant was revoked at {Iso(revoked)}");
        return null;
    }
}

public static partial class OfflineVerifier
{
    // Pre-issued grants (v1.6.0; 1.9.0 in this SDK): the NA's records (grants, time tokens, revocations) are signed by
    // the NA, a holder's (uses, time token requests) by the grant's bearer key. Each signs every field but the
    // signature, an absent optional field left out (OutOfBandCanonical).

    /// <summary>
    /// Whether a grant is the NA's, under the NA's verified key history: well formed as the reference reads it (a
    /// validity of at most 24 hours starting within 24 hours of its issue, each envelope parameter readable), signed by
    /// the key valid at its <c>issued_at</c> over the grant as received, in the reference's form, with no field this SDK
    /// does not know.
    /// </summary>
    /// <param name="grantJson">The grant, as received.</param>
    /// <param name="keyHistory">The NA's verified key history.</param>
    /// <exception cref="StrictJsonException"><paramref name="grantJson"/> is not strict JSON.</exception>
    /// <exception cref="ArgumentException"><paramref name="grantJson"/> is not a JSON object.</exception>
    public static GrantVerificationResult VerifyCapabilityGrant(string grantJson, NAKeyHistory keyHistory) =>
        VerifyCapabilityGrant(grantJson, keyHistory, null);

    /// <summary>
    /// As <see cref="VerifyCapabilityGrant(string, NAKeyHistory)"/>, judged at <paramref name="now"/> (1.8.0): only
    /// while the key that signed it still vouches (<c>outlives_key</c> after its horizon).
    /// </summary>
    /// <param name="grantJson">The grant, as received.</param>
    /// <param name="keyHistory">The NA's verified key history.</param>
    /// <param name="now">The time the grant is judged at.</param>
    /// <exception cref="StrictJsonException"><paramref name="grantJson"/> is not strict JSON.</exception>
    /// <exception cref="ArgumentException"><paramref name="grantJson"/> is not a JSON object.</exception>
    public static GrantVerificationResult VerifyCapabilityGrant(string grantJson, NAKeyHistory keyHistory, DateTimeOffset now) =>
        VerifyCapabilityGrant(grantJson, keyHistory, (DateTimeOffset?)now);

    /// <summary>As <see cref="VerifyCapabilityGrant(string, NAKeyHistory)"/>, under NA keys valid at every time.</summary>
    /// <param name="grantJson">The grant, as received.</param>
    /// <param name="naPublicKeys">The NA's public keys, base64.</param>
    /// <exception cref="StrictJsonException"><paramref name="grantJson"/> is not strict JSON.</exception>
    /// <exception cref="ArgumentException"><paramref name="grantJson"/> is not a JSON object.</exception>
    public static GrantVerificationResult VerifyCapabilityGrant(string grantJson, IReadOnlyList<string> naPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(naPublicKeys);
        return VerifyCapabilityGrant(grantJson, NAKeyHistory.ValidAlways(naPublicKeys), null);
    }

    private static GrantVerificationResult VerifyCapabilityGrant(string grantJson, NAKeyHistory keyHistory, DateTimeOffset? now)
    {
        ArgumentNullException.ThrowIfNull(keyHistory);
        const string model = "CapabilityGrant";
        using var doc = StrictJsonReader.Parse(grantJson);
        var g = doc.RootElement;
        if (g.ValueKind != JsonValueKind.Object) throw new ArgumentException("a grant must be a JSON object", nameof(grantJson));
        GrantVerificationResult Result(string reason) => new(reason == "accepted", reason, Str(g, "grant_id"));

        if (SignatureAbsent(g)) return Result("missing_signature");
        if (!Shapes.Grant(g)) return Result("malformed");
        if (!SignedAsReceived(model, g, keyHistory.AllKeys())) return Result("invalid_signature");
        if (StrictFields.UnknownFields(model, g).Count > 0) return Result("unknown_field");
        if (StrictFields.NonCanonicalFields(model, g).Count > 0) return Result("non_canonical_form");
        var issuedAt = ParseTimestamp(Str(g, NAKeyHistory.RecordTimeFields[model]) ?? "");
        if (!SignedAsReceived(model, g, keyHistory.KeysAt(issuedAt))) return Result("na_key_not_valid_at_time");
        if (now is { } judged && !SignedAsReceived(model, g, keyHistory.KeysVouchingAt(issuedAt, judged)))
            return Result("outlives_key");
        return Result("accepted");
    }

    /// <summary>
    /// True when a time token is well formed (<c>use_head_digest</c> exactly when <c>use_sequence</c> is 1 or more) and
    /// signed by one of the NA's keys over the token as received, in the reference's form, with no field this SDK does
    /// not know.
    /// </summary>
    /// <param name="tokenJson">The time token, as received.</param>
    /// <param name="naPublicKeys">The NA's public keys, valid at every time.</param>
    /// <exception cref="StrictJsonException"><paramref name="tokenJson"/> is not strict JSON.</exception>
    public static bool VerifyTimeToken(string tokenJson, IReadOnlyList<string> naPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(naPublicKeys);
        return NASignedGrantRecord("TimeToken", tokenJson, Shapes.TimeToken, "issued_at", _ => naPublicKeys);
    }

    /// <summary>As <see cref="VerifyTimeToken(string, IReadOnlyList{string})"/>, signed by the key the NA's history held at its <c>issued_at</c>.</summary>
    /// <param name="tokenJson">The time token, as received.</param>
    /// <param name="keyHistory">The NA's verified key history.</param>
    /// <exception cref="StrictJsonException"><paramref name="tokenJson"/> is not strict JSON.</exception>
    public static bool VerifyTimeToken(string tokenJson, NAKeyHistory keyHistory)
    {
        ArgumentNullException.ThrowIfNull(keyHistory);
        return NASignedGrantRecord("TimeToken", tokenJson, Shapes.TimeToken, "issued_at", at => keyHistory.KeysAt(at));
    }

    /// <summary>As <see cref="VerifyTimeToken(string, IReadOnlyList{string})"/>, for a grant revocation.</summary>
    /// <param name="revocationJson">The revocation, as received.</param>
    /// <param name="naPublicKeys">The NA's public keys, valid at every time.</param>
    /// <exception cref="StrictJsonException"><paramref name="revocationJson"/> is not strict JSON.</exception>
    public static bool VerifyGrantRevocation(string revocationJson, IReadOnlyList<string> naPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(naPublicKeys);
        return NASignedGrantRecord("GrantRevocationRecord", revocationJson, Shapes.GrantRevocation, "revoked_at", _ => naPublicKeys);
    }

    /// <summary>As <see cref="VerifyGrantRevocation(string, IReadOnlyList{string})"/>, signed by the key the NA's history held at its <c>revoked_at</c>.</summary>
    /// <param name="revocationJson">The revocation, as received.</param>
    /// <param name="keyHistory">The NA's verified key history.</param>
    /// <exception cref="StrictJsonException"><paramref name="revocationJson"/> is not strict JSON.</exception>
    public static bool VerifyGrantRevocation(string revocationJson, NAKeyHistory keyHistory)
    {
        ArgumentNullException.ThrowIfNull(keyHistory);
        return NASignedGrantRecord("GrantRevocationRecord", revocationJson, Shapes.GrantRevocation, "revoked_at", at => keyHistory.KeysAt(at));
    }

    /// <summary>
    /// True when a grant use is well formed (<c>prev_use_digest</c> exactly from the second use, <c>resource_id</c> and
    /// <c>resource_action</c> together, its time token a time token) and signed by the bearer key over the use as
    /// received, in the reference's form, with no field this SDK does not know.
    /// </summary>
    /// <param name="useJson">The use, as received.</param>
    /// <param name="bearerPublicKey">The grant's bearer public key, base64.</param>
    /// <exception cref="StrictJsonException"><paramref name="useJson"/> is not strict JSON.</exception>
    public static bool VerifyGrantUse(string useJson, string bearerPublicKey)
    {
        ArgumentNullException.ThrowIfNull(bearerPublicKey);
        using var doc = StrictJsonReader.Parse(useJson);
        return Shapes.GrantUse(doc.RootElement) && GrantRecordSigned("GrantExecutionEvidence", doc.RootElement, [bearerPublicKey]);
    }

    /// <summary>True when a time token request is well formed and signed by the bearer key (beta).</summary>
    /// <param name="requestJson">The request, as received.</param>
    /// <param name="bearerPublicKey">The grant's bearer public key, base64.</param>
    /// <exception cref="StrictJsonException"><paramref name="requestJson"/> is not strict JSON.</exception>
    public static bool VerifyTimeTokenRequest(string requestJson, string bearerPublicKey)
    {
        ArgumentNullException.ThrowIfNull(bearerPublicKey);
        using var doc = StrictJsonReader.Parse(requestJson);
        return Shapes.TimeTokenRequest(doc.RootElement) && GrantRecordSigned("TimeTokenRequest", doc.RootElement, [bearerPublicKey]);
    }

    /// <summary>Whether a time token, as received inside a use, carries a signature by one of <paramref name="keys"/> (the reference's check).</summary>
    /// <param name="rawToken">The time token, as received.</param>
    /// <param name="keys">The NA's public keys to try.</param>
    public static bool TokenSignatureValid(JsonElement rawToken, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return rawToken.ValueKind == JsonValueKind.Object && SignedAsReceived("TimeToken", rawToken, keys);
    }

    /// <summary>A grant use as the reference's model reads it.</summary>
    internal static bool ValidGrantUse(JsonElement record) => Shapes.GrantUse(record);

    /// <summary>A time token request as the reference's model reads it.</summary>
    internal static bool ValidTimeTokenRequest(JsonElement record) => Shapes.TimeTokenRequest(record);

    /// <summary>SHA-256 of a grant record's signed form as received, unknown fields set aside.</summary>
    internal static string GrantRecordDigest(string model, JsonElement record) =>
        Sha256Hex(ReceivedCanonical(model, StrictFields.WithoutUnknownFields(model, record)));

    /// <summary>Signed over its signed form by one of <paramref name="keys"/>, in the reference's form, with no unknown field.</summary>
    private static bool GrantRecordSigned(string model, JsonElement record, IReadOnlyList<string> keys) =>
        OutOfBandSignedBy(record, keys) && StrictFields.UnknownFields(model, record).Count == 0
        && StrictFields.NonCanonicalFields(model, record).Count == 0;

    private static bool NASignedGrantRecord(string model, string json, Func<JsonElement, bool> valid, string timeField,
        Func<DateTimeOffset, IReadOnlyList<string>> keysAt)
    {
        using var doc = StrictJsonReader.Parse(json);
        var record = doc.RootElement;
        return valid(record) && TryReadTimestamp(Str(record, timeField)!, out var at) && GrantRecordSigned(model, record, keysAt(Instant(at)));
    }

    private static partial class Shapes
    {
        private static readonly Check Whole = v => v is { } e && PayloadInteger(e) is not null;

        private static Check Between(int min, int max) => v => v is { } e && PayloadInteger(e) is { } n && n >= min && n <= max;

        private static Check ListOf(Check check, int min, int max) =>
            v => v?.ValueKind == JsonValueKind.Array && v.Value.GetArrayLength() is var n && n >= min && n <= max
                 && v.Value.EnumerateArray().All(item => check(item));

        private static Check GrantGateResult => Shape([("gate_name", Text), ("passed", Flag), ("detail", Text)]);
        private static Check GrantAppliedPolicy => Shape([("policy_id", Text), ("version", Whole), ("policy_digest", Text), ("signed_by", Text)]);
        private static Check GateOutcome => Shape(
        [
            ("capability", Text), ("policy_id", Text), ("policy_version", Whole), ("gate_id", Text), ("gate_type", Text),
            ("mode", OneOf("enforce", "observe")), ("outcome", OneOf("pass", "may_fail", "undecidable")), ("detail", Bounded(1024, 0)),
        ]);
        private static Check GrantPolicyBinding => Shape(
        [
            ("policies", Filled(ListOf(GrantAppliedPolicy))), ("policy_set_digest", Text), ("gate_outcomes", Filled(ListOf(GateOutcome))),
            ("registry_gate_types", Filled(ListOf(Text))),
        ]);
        private static Check Capabilities => v => ListOf(c => Bounded(256)(c) && !c!.Value.GetString()!.Contains('*'), 1, GrantLimits.MaxCapabilities)(v)
            && v!.Value.EnumerateArray().Select(c => c.GetString()).Distinct(StringComparer.Ordinal).Count() == v.Value.GetArrayLength();
        private static Check GrantFields => Shape(
        [
            ("grant_id", Filled(Bounded(128))), ("bearer_key_id", Bounded(256)), ("bearer_public_key", Bounded(256)),
            ("bearer_sovereign_id", Bounded(256)), ("capabilities", Capabilities), ("basis_kind", OneOf("agreement", "attestation")),
            ("basis_id", Bounded(128)), ("basis_digest", Sha256), ("requester_sovereign_id", Bounded(256)),
            ("provider_sovereign_id", Bounded(256)), ("valid_from", Utc), ("valid_until", Utc),
            ("max_uses", Between(1, GrantLimits.MaxUses)), ("max_uses_per_time_token", Between(1, GrantLimits.MaxUsesPerTimeToken)),
            ("parameter_envelope", Filled(v => GrantRules.EnvelopeShapeProblem(v!.Value) is null)), ("basis_checks", Filled(ListOf(GrantGateResult))),
            ("policy_binding", GrantPolicyBinding), ("issued_at", Utc), ("issuer_sovereign_id", Text), ("issued_by", Text),
            ("signature", Optional(Signature)),
        ]);
        private static Check TokenFields => Shape(
        [
            ("time_token_id", Filled(Bounded(128))), ("grant_id", Bounded(128)), ("use_sequence", Between(0, GrantLimits.MaxUses)),
            ("use_head_digest", Absent(Sha256)), ("issued_at", Utc), ("issuer_sovereign_id", Text), ("issued_by", Text),
            ("signature", Optional(Signature)),
        ]);
        private static Check RequestFields => Shape(
        [
            ("grant_id", Bounded(128)), ("use_sequence", Between(0, GrantLimits.MaxUses)), ("use_head_digest", Absent(Sha256)),
            ("requested_at", Utc), ("signature", Optional(Signature)),
        ]);
        private static Check UseFields => Shape(
        [
            ("use_id", Filled(v => v?.ValueKind == JsonValueKind.String && UseId().IsMatch(v.Value.GetString()!))), ("grant_id", Bounded(128)),
            ("grant_digest", Sha256), ("use_sequence", Between(1, GrantLimits.MaxUses)), ("prev_use_digest", Absent(Sha256)),
            ("executor_sovereign_id", Bounded(256)), ("capability", Bounded(256)), ("request_parameters", Filled(AnyObject)),
            ("resource_id", Absent(Bounded(256))), ("resource_action", Absent(ActionValue)), ("executed_at", Utc),
            ("time_token", v => v is { } t && TimeToken(t)), ("outcome", OneOf("success", "failure", "partial")),
            ("outcome_detail", Absent(Bounded(1024, 0))), ("execution_parameters", Filled(AnyObject)), ("signature", Optional(Signature)),
        ]);
        private static Check RevocationFields => Shape(
        [
            ("revocation_id", Filled(Bounded(128))), ("grant_id", Bounded(128)), ("grant_digest", Sha256),
            ("reason", OneOf("operator", "attestation_revoked", "policy_deactivated", "policy_activated", "bearer_key_retired", "agreement_untrusted")),
            ("detail", Absent(Bounded(1024))), ("revoked_by", Absent(Bounded(256))), ("revoked_at", Utc), ("issuer_sovereign_id", Text),
            ("issued_by", Text), ("signature", Optional(Signature)),
        ]);

        [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\z", RegexOptions.CultureInvariant)]
        private static partial Regex UseId();

        private static bool IsAbsent(JsonElement record, string name) =>
            !record.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;

        /// <summary><c>use_head_digest</c> exactly when <c>use_sequence</c> is 1 or more.</summary>
        private static bool HeadFields(JsonElement record) =>
            (PayloadInteger(record, "use_sequence") == 0) == IsAbsent(record, "use_head_digest");

        /// <summary>
        /// A grant as the reference reads it: a validity of at most 24 hours, each envelope parameter readable, and (1.8.0)
        /// a start within 24 hours of its issue (<c>issued_at</c> minus the clock skew to <c>issued_at</c> plus 24 hours and
        /// the skew).
        /// </summary>
        public static bool Grant(JsonElement value)
        {
            if (!GrantFields(value)) return false;
            TryReadTimestamp(Str(value, "valid_from")!, out var from);
            TryReadTimestamp(Str(value, "valid_until")!, out var until);
            TryReadTimestamp(Str(value, "issued_at")!, out var issued);
            const long day = GrantLimits.MaxValiditySeconds * 1_000_000L;
            const long skew = GrantLimits.ClockSkewSeconds * 1_000_000L;
            return from < until && until - from <= day && issued - skew <= from && from <= issued + day + skew;
        }

        /// <summary>A time token as the reference reads it.</summary>
        public static bool TimeToken(JsonElement value) => TokenFields(value) && HeadFields(value);

        /// <summary>A time token request as the reference reads it.</summary>
        public static bool TimeTokenRequest(JsonElement value) => RequestFields(value) && HeadFields(value);

        /// <summary>
        /// A grant use as the reference reads it: <c>prev_use_digest</c> exactly when <c>use_sequence</c> is 2 or more,
        /// <c>resource_id</c> and <c>resource_action</c> together.
        /// </summary>
        public static bool GrantUse(JsonElement value) =>
            UseFields(value) && IsAbsent(value, "resource_id") == IsAbsent(value, "resource_action")
            && (PayloadInteger(value, "use_sequence") == 1) == IsAbsent(value, "prev_use_digest");

        /// <summary>A grant revocation as the reference reads it.</summary>
        public static bool GrantRevocation(JsonElement value) => RevocationFields(value);
    }
}
