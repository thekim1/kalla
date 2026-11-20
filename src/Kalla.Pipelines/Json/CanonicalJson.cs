using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kalla.Pipelines.Json;

/// <summary>
/// RFC 8785 JSON Canonicalization Scheme (engine design §3.3, to-verify #17): object members sorted by their names'
/// UTF-16 code units, no whitespace, strings with the minimal escapes, numbers in ECMAScript's shortest form. The input
/// must be I-JSON (no duplicate names, no lone surrogates, finite numbers); anything else throws, so two different
/// documents never share a hash by accident.
/// </summary>
public static class CanonicalJson
{
    public static byte[] Serialize(JsonElement value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static byte[] Sha256(JsonElement value) => SHA256.HashData(Serialize(value));

    private static void Write(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var members = value.EnumerateObject().ToList();
                members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                builder.Append('{');
                for (var i = 0; i < members.Count; i++)
                {
                    if (i > 0)
                    {
                        if (string.Equals(members[i - 1].Name, members[i].Name, StringComparison.Ordinal))
                        {
                            throw new JsonException($"Duplicate member name '{members[i].Name}' is not I-JSON.");
                        }

                        builder.Append(',');
                    }

                    WriteString(builder, members[i].Name);
                    builder.Append(':');
                    Write(builder, members[i].Value);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    Write(builder, item);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(builder, value.GetString()!);
                break;
            case JsonValueKind.Number:
                builder.Append(FormatNumber(value.GetDouble()));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new JsonException($"Unexpected JSON value kind {value.ValueKind}.");
        }
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
                continue;
            }

            if (char.IsSurrogate(c))
            {
                throw new JsonException("A lone surrogate is not I-JSON.");
            }

            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case < ' ':
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>ECMAScript's <c>Number::toString</c> (ECMA-262), which RFC 8785 §3.2.2.3 requires.</summary>
    internal static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new JsonException("NaN and infinities are not I-JSON.");
        }

        if (value == 0)
        {
            return "0";
        }

        // "R" is the shortest round-trip form: digits, an optional point and an optional exponent ("1.5E-07").
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = shortest.IndexOf('E', StringComparison.Ordinal);
        var mantissa = exponentAt < 0 ? shortest : shortest[..exponentAt];
        var exponent = exponentAt < 0 ? 0 : int.Parse(shortest[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var integerDigits = point < 0 ? mantissa.Length : point;
        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits.Trim('0');
        if (digits.Length == 0)
        {
            return "0";
        }

        // value = 0.d1…dk × 10^n with d1 ≠ 0, as in ECMA-262.
        var k = digits.Length;
        var n = integerDigits - leadingZeros + exponent;
        var text = new StringBuilder(value < 0 ? "-" : string.Empty);
        if (k <= n && n <= 21)
        {
            text.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            text.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            text.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            text.Append(digits[0]);
            if (k > 1)
            {
                text.Append('.').Append(digits, 1, k - 1);
            }

            var e = n - 1;
            text.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
