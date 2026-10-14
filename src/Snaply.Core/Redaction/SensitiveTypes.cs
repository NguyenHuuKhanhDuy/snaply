using Snaply.Core.Models;

namespace Snaply.Core.Redaction;

public enum SensitiveType
{
    Email, Phone, IPv4, IPv6, Jwt, AuthValue, ApiKey, Secret, UrlQuery, Card, Iban, Uuid,
}

public enum SensitiveConfidence { Low, Medium, High }

/// <summary>A suspected sensitive value: what kind it is, how sure we are, the text (kept in memory only) and the image regions to cover.</summary>
public sealed record SensitiveFinding(SensitiveType Type, SensitiveConfidence Confidence, string Text, IReadOnlyList<PixelRect> Regions)
{
    /// <summary>Partly masked text that is safe to show in a list.</summary>
    public string Preview => SensitiveText.Mask(Text);
}

public static class SensitiveText
{
    /// <summary>Types that are looked for unless the user turns them off. UUIDs are too common to suggest by default.</summary>
    public static readonly IReadOnlyList<SensitiveType> DisabledByDefault = new[] { SensitiveType.Uuid };

    public static string Label(SensitiveType type) => type switch
    {
        SensitiveType.Email => "Email address",
        SensitiveType.Phone => "Phone number",
        SensitiveType.IPv4 => "IP address (IPv4)",
        SensitiveType.IPv6 => "IP address (IPv6)",
        SensitiveType.Jwt => "JWT",
        SensitiveType.AuthValue => "Authorization value",
        SensitiveType.ApiKey => "API key or access token",
        SensitiveType.Secret => "Secret or password",
        SensitiveType.UrlQuery => "Sensitive URL parameter",
        SensitiveType.Card => "Card number",
        SensitiveType.Iban => "Bank account (IBAN)",
        SensitiveType.Uuid => "UUID",
        _ => type.ToString(),
    };

    /// <summary>Shows the start and end of a value and hides the middle, so a list can identify items without exposing them.</summary>
    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = text.Trim();
        const char dot = '•';
        if (t.Length <= 4) return new string(dot, t.Length);
        if (t.Length <= 8) return t[..1] + new string(dot, t.Length - 1);
        return t[..2] + new string(dot, Math.Min(t.Length - 4, 8)) + t[^2..];
    }
}
