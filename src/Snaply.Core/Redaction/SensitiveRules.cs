using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snaply.Core.Redaction;

/// <summary>A match inside the recognised text (character offsets), before it is mapped to image regions.</summary>
internal readonly record struct RawMatch(int Start, int Length, SensitiveType Type, SensitiveConfidence Confidence)
{
    public int End => Start + Length;
}

/// <summary>
/// Pure text rules. Each takes the recognised text and returns matches; none touches the image. Rules are listed in priority
/// order: when two matches overlap, the earlier rule wins (for example a JWT is reported as a JWT, not as an authorization value).
/// </summary>
internal static class SensitiveRules
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    public static readonly IReadOnlyList<(SensitiveType Type, Func<string, IEnumerable<RawMatch>> Run)> Ordered = new (SensitiveType, Func<string, IEnumerable<RawMatch>>)[]
    {
        (SensitiveType.Jwt, Jwt),
        (SensitiveType.ApiKey, KnownKeys),
        (SensitiveType.AuthValue, AuthValues),
        (SensitiveType.UrlQuery, UrlQuery),
        (SensitiveType.Secret, LabelledSecrets),
        (SensitiveType.Card, Cards),
        (SensitiveType.Iban, Ibans),
        (SensitiveType.Email, Emails),
        (SensitiveType.IPv4, IPv4),
        (SensitiveType.IPv6, IPv6),
        (SensitiveType.Phone, Phones),
        (SensitiveType.Uuid, Uuids),
    };

    private static IEnumerable<Match> Matches(Regex rx, string text)
    {
        try { return rx.Matches(text).Cast<Match>().ToList(); }
        catch (RegexMatchTimeoutException) { return Array.Empty<Match>(); }
    }

    // ---- email ----
    private static readonly Regex EmailRx = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", Opts, Timeout);

    private static IEnumerable<RawMatch> Emails(string text) =>
        Matches(EmailRx, text).Select(m => new RawMatch(m.Index, m.Length, SensitiveType.Email, SensitiveConfidence.High));

    // ---- IPv4 / IPv6 ----
    private static readonly Regex Ip4Rx = new(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d]|\.\d)", Opts, Timeout);

    private static IEnumerable<RawMatch> IPv4(string text) =>
        Matches(Ip4Rx, text).Select(m => new RawMatch(m.Index, m.Length, SensitiveType.IPv4, SensitiveConfidence.Medium));

    private static readonly Regex Ip6Rx = new(@"(?<![\w:])[0-9A-Fa-f:]{3,39}(?![\w:])", Opts, Timeout);

    private static IEnumerable<RawMatch> IPv6(string text)
    {
        foreach (var m in Matches(Ip6Rx, text))
        {
            var v = m.Value;
            var colons = v.Count(c => c == ':');
            if (colons < 2 || !(v.Contains("::") || colons == 7)) continue; // times like 12:30:45 are not addresses
            if (IPAddress.TryParse(v, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
                yield return new RawMatch(m.Index, m.Length, SensitiveType.IPv6, SensitiveConfidence.Medium);
        }
    }

    // ---- JWT ----
    private static readonly Regex JwtRx = new(@"\beyJ[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]*", Opts, Timeout);

    private static IEnumerable<RawMatch> Jwt(string text)
    {
        foreach (var m in Matches(JwtRx, text))
            if (HeaderLooksLikeJwt(m.Value.Split('.')[0]))
                yield return new RawMatch(m.Index, m.Length, SensitiveType.Jwt, SensitiveConfidence.High);
    }

    private static bool HeaderLooksLikeJwt(string header)
    {
        try
        {
            var b64 = header.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.ValueKind == JsonValueKind.Object && (doc.RootElement.TryGetProperty("alg", out _) || doc.RootElement.TryGetProperty("typ", out _));
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return false; }
    }

    // ---- authorization values ----
    private static readonly Regex BearerRx = new(@"\b(?:bearer|basic)\s+(?<v>[A-Za-z0-9._~+/\-=]{8,})", Opts | RegexOptions.IgnoreCase, Timeout);
    private static readonly Regex AuthHeaderRx = new(@"\bauthorization\s*[:=]\s*(?!bearer\b|basic\b)[""']?(?<v>[^\s""',;]{8,})", Opts | RegexOptions.IgnoreCase, Timeout);

    private static IEnumerable<RawMatch> AuthValues(string text)
    {
        foreach (var rx in new[] { BearerRx, AuthHeaderRx })
            foreach (var m in Matches(rx, text))
            {
                var g = m.Groups["v"];
                if (!IsPlaceholder(g.Value)) yield return new RawMatch(g.Index, g.Length, SensitiveType.AuthValue, SensitiveConfidence.High);
            }
    }

    // ---- well-known key formats ----
    private static readonly Regex KeyRx = new(
        @"(?<![A-Za-z0-9_\-])(?:AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{22,}|glpat-[A-Za-z0-9_\-]{20,}|sk-[A-Za-z0-9_\-]{20,}|[sr]k_(?:live|test)_[A-Za-z0-9]{16,}|pk_live_[A-Za-z0-9]{16,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AIza[0-9A-Za-z_\-]{35}|npm_[A-Za-z0-9]{36}|SG\.[A-Za-z0-9_\-]{16,}\.[A-Za-z0-9_\-]{16,})(?![A-Za-z0-9_\-])",
        Opts, Timeout);

    private static IEnumerable<RawMatch> KnownKeys(string text) =>
        Matches(KeyRx, text).Select(m => new RawMatch(m.Index, m.Length, SensitiveType.ApiKey, SensitiveConfidence.High));

    // ---- labelled secrets: "api_key = ...", "password: ...", "\"token\": \"...\"" ----
    private static readonly Regex SecretRx = new(
        @"\b(?<label>api[_\- ]?key|apikey|access[_\- ]?key|client[_\- ]?secret|secret(?:[_\- ]?key)?|(?:access|auth|refresh|id)[_\- ]?token|token|password|passwd|pwd)[""']?\s*[:=]\s*[""']?(?<v>[^\s""',;&]{4,})",
        Opts | RegexOptions.IgnoreCase, Timeout);

    private static IEnumerable<RawMatch> LabelledSecrets(string text)
    {
        foreach (var m in Matches(SecretRx, text))
        {
            var v = m.Groups["v"];
            if (IsPlaceholder(v.Value)) continue;
            var label = m.Groups["label"].Value.ToLowerInvariant();
            var passwordLike = label.StartsWith("pass") || label == "pwd";
            var ok = passwordLike
                ? v.Length >= 4
                : v.Length >= 12 && (Entropy(v.Value) >= 3.0 || (v.Value.Any(char.IsLetter) && v.Value.Any(char.IsDigit)));
            if (ok) yield return new RawMatch(v.Index, v.Length, SensitiveType.Secret, SensitiveConfidence.Medium);
        }
    }

    private static bool IsPlaceholder(string v)
    {
        var t = v.Trim('"', '\'', '<', '>', '[', ']', '{', '}', '(', ')');
        if (t.Length == 0) return true;
        if (t.All(c => c is '*' or '•' or '●' or 'x' or 'X' or '.' or '-' or '_' or '#')) return true;
        return t.ToLowerInvariant() is "null" or "none" or "true" or "false" or "undefined" or "string" or "yourkey" or "your_api_key" or "redacted" or "required" or "optional";
    }

    internal static double Entropy(string s)
    {
        if (s.Length == 0) return 0;
        var counts = s.GroupBy(c => c).Select(g => g.Count());
        return -counts.Sum(n => { var p = (double)n / s.Length; return p * Math.Log2(p); });
    }

    // ---- URL query parameters ----
    private static readonly Regex UrlRx = new(@"https?://[^\s""'<>]+", Opts | RegexOptions.IgnoreCase, Timeout);
    private static readonly HashSet<string> SensitiveParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "id_token", "refresh_token", "auth_token", "api_key", "apikey", "key", "secret", "client_secret",
        "password", "passwd", "pwd", "sig", "signature", "auth", "session", "sessionid", "session_id", "code", "x-amz-signature", "x-amz-credential",
    };

    private static IEnumerable<RawMatch> UrlQuery(string text)
    {
        foreach (var url in Matches(UrlRx, text))
        {
            var q = url.Value.IndexOf('?');
            if (q < 0) continue;
            var end = url.Value.IndexOf('#', q);
            if (end < 0) end = url.Value.Length;

            var pos = q + 1;
            while (pos < end)
            {
                var amp = url.Value.IndexOfAny(new[] { '&', ';' }, pos);
                var segEnd = amp < 0 || amp > end ? end : amp;
                var seg = url.Value[pos..segEnd];
                var eq = seg.IndexOf('=');
                if (eq > 0 && eq < seg.Length - 1 && SensitiveParams.Contains(seg[..eq]) && seg.Length - eq - 1 >= 4)
                    yield return new RawMatch(url.Index + pos + eq + 1, seg.Length - eq - 1, SensitiveType.UrlQuery, SensitiveConfidence.Medium);
                pos = segEnd + 1;
            }
        }
    }

    // ---- payment cards ----
    private static readonly Regex CardRx = new(@"(?<!\d)(?:\d[ \-\n]?){12,18}\d(?!\d)", Opts, Timeout);

    private static IEnumerable<RawMatch> Cards(string text)
    {
        foreach (var m in Matches(CardRx, text))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is < 13 or > 19 || digits[0] is '0' or '1' or '7' or '8' or '9') continue;
            if (Luhn(digits)) yield return new RawMatch(m.Index, m.Length, SensitiveType.Card, SensitiveConfidence.High);
        }
    }

    internal static bool Luhn(string digits)
    {
        var sum = 0; var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (alt) { d *= 2; if (d > 9) d -= 9; }
            sum += d; alt = !alt;
        }
        return sum % 10 == 0;
    }

    // ---- IBAN ----
    private static readonly Regex IbanRx = new(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,3})?\b", Opts, Timeout);

    private static IEnumerable<RawMatch> Ibans(string text)
    {
        foreach (var m in Matches(IbanRx, text))
        {
            var v = m.Value.Replace(" ", "");
            if (v.Length is >= 15 and <= 34 && IbanMod97(v)) yield return new RawMatch(m.Index, m.Length, SensitiveType.Iban, SensitiveConfidence.Medium);
        }
    }

    internal static bool IbanMod97(string iban)
    {
        var rearranged = iban[4..] + iban[..4];
        var rem = 0;
        foreach (var c in rearranged)
        {
            var piece = char.IsDigit(c) ? (c - '0').ToString() : (c - 'A' + 10).ToString();
            foreach (var d in piece) rem = (rem * 10 + (d - '0')) % 97;
        }
        return rem == 1;
    }

    // ---- phone numbers ----
    private static readonly Regex PhoneRx = new(@"(?<![\w@.\-/])\+?\d[\d ().\-]{7,18}\d(?![\w@])", Opts, Timeout);
    private static readonly Regex DateLikeRx = new(@"\d{4}[-./]\d{1,2}[-./]\d{1,2}|\d{1,2}[-./]\d{1,2}[-./]\d{2,4}", Opts, Timeout);

    private static IEnumerable<RawMatch> Phones(string text)
    {
        foreach (var m in Matches(PhoneRx, text))
        {
            var v = m.Value.TrimEnd();
            var digits = new string(v.Where(char.IsDigit).ToArray());
            if (digits.Length is < 9 or > 15) continue;
            if (DateLikeRx.IsMatch(v) || Ip4Rx.IsMatch(v)) continue;
            if (digits.Length >= 13 && Luhn(digits)) continue; // card-like, handled by the card rule

            var hasPlus = v.StartsWith('+');
            var hasSeparator = v.Any(c => c is ' ' or '(' or ')' or '-' or '.');
            var localStyle = digits[0] == '0' && digits.Length is >= 9 and <= 11;
            if (!(hasPlus || localStyle || hasSeparator)) continue; // a bare run of digits is more likely an id or a count
            if (!hasPlus && !localStyle && digits.Length < 10) continue;

            yield return new RawMatch(m.Index, v.Length, SensitiveType.Phone, hasPlus || localStyle ? SensitiveConfidence.Medium : SensitiveConfidence.Low);
        }
    }

    // ---- UUID ----
    private static readonly Regex UuidRx = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", Opts, Timeout);

    private static IEnumerable<RawMatch> Uuids(string text) =>
        Matches(UuidRx, text).Select(m => new RawMatch(m.Index, m.Length, SensitiveType.Uuid, SensitiveConfidence.Low));
}
