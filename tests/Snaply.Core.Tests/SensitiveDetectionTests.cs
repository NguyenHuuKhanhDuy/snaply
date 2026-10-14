using System.Diagnostics;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Redaction;

namespace Snaply.Core.Tests;

public class SensitiveDetectionTests
{
    private const int CharW = 10, LineH = 24, BoxH = 16;
    private const string Jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    /// <summary>Lays text out like a monospace screenshot: one OCR word per whitespace-separated token, 10 px per character.</summary>
    private static List<OcrWord> Layout(params string[] lines)
    {
        var words = new List<OcrWord>();
        for (var l = 0; l < lines.Length; l++)
        {
            var col = 0;
            foreach (var part in lines[l].Split(' '))
            {
                if (part.Length > 0) words.Add(new OcrWord(part, col * CharW, l * LineH, part.Length * CharW, BoxH, 0.95f, l));
                col += part.Length + 1;
            }
        }
        return words;
    }

    private static IReadOnlyList<SensitiveFinding> Run(string line, IReadOnlyCollection<SensitiveType>? disabled = null) => RunLines(disabled, line);

    private static IReadOnlyList<SensitiveFinding> RunLines(IReadOnlyCollection<SensitiveType>? disabled, params string[] lines) =>
        new SensitiveDataDetector().Detect(Layout(lines), 3000, 600, disabled);

    // ---- positives ----

    [Fact]
    public void Email_is_matched_exactly()
    {
        var f = Assert.Single(Run("Contact jane.doe@example.com today"));
        Assert.Equal(SensitiveType.Email, f.Type);
        Assert.Equal("jane.doe@example.com", f.Text);
    }

    [Fact]
    public void Bearer_jwt_is_reported_as_jwt_and_leaves_the_label_alone()
    {
        var f = Assert.Single(Run("Authorization: Bearer " + Jwt));
        Assert.Equal(SensitiveType.Jwt, f.Type);
        var rect = Assert.Single(f.Regions);
        Assert.True(rect.X > "Authorization: Bearer".Length * CharW - 20, "the box must not cover the header name or the word Bearer");
    }

    [Fact]
    public void Bearer_value_that_is_not_a_jwt_is_an_authorization_value()
    {
        var f = Assert.Single(Run("Authorization: Bearer 8f3a9c1d2b7e4f60a1b2c3d4"));
        Assert.Equal(SensitiveType.AuthValue, f.Type);
        Assert.Equal("8f3a9c1d2b7e4f60a1b2c3d4", f.Text);
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("999.1.1.1", false)]
    [InlineData("1.2.3", false)]
    public void IPv4_is_validated(string text, bool expected) =>
        Assert.Equal(expected, Run("host " + text + " up").Any(x => x.Type == SensitiveType.IPv4));

    [Fact]
    public void IPv6_is_matched_but_clock_times_are_not()
    {
        Assert.Contains(Run("addr 2001:0db8:85a3:0000:0000:8a2e:0370:7334 ok"), x => x.Type == SensitiveType.IPv6);
        Assert.Contains(Run("addr fe80::1ff:fe23:4567:890a ok"), x => x.Type == SensitiveType.IPv6);
        Assert.Empty(Run("started at 12:30:45 today"));
    }

    [Theory]
    [InlineData("+84 912 345 678")]
    [InlineData("0912345678")]
    [InlineData("(415) 555-0132")]
    [InlineData("+1-415-555-0132")]
    public void Phone_numbers_are_matched(string phone) =>
        Assert.Contains(Run("Call " + phone + " now"), x => x.Type == SensitiveType.Phone);

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz1234")]
    [InlineData("xoxb-1234567890-abcdefghij")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstuv")]
    public void Known_key_formats_are_matched(string key)
    {
        var f = Assert.Single(Run("key is " + key + " end"));
        Assert.Equal(SensitiveType.ApiKey, f.Type);
        Assert.Equal(key, f.Text);
    }

    [Fact]
    public void Labelled_secrets_are_matched_by_value()
    {
        var f = Assert.Single(Run("api_key=Zx9fQ2mLp7TgVb41Kd8s"));
        Assert.Equal(SensitiveType.Secret, f.Type);
        Assert.Equal("Zx9fQ2mLp7TgVb41Kd8s", f.Text);
        Assert.Contains(Run("password: hunter2!"), x => x.Type == SensitiveType.Secret);
    }

    [Fact]
    public void Placeholders_and_short_values_are_not_secrets()
    {
        Assert.Empty(Run("api_key=xxxxxxxxxxxx"));
        Assert.Empty(Run("token: null"));
        Assert.Empty(Run("password: ****"));
        Assert.Empty(Run("secret=abc"));
    }

    [Fact]
    public void Url_query_matches_only_the_sensitive_value_and_cuts_inside_the_word()
    {
        var url = "https://x.test/a?id=5&token=abc123def456&page=2";
        var f = Assert.Single(Run("open " + url));
        Assert.Equal(SensitiveType.UrlQuery, f.Type);
        Assert.Equal("abc123def456", f.Text);

        var rect = Assert.Single(f.Regions);
        var urlLeft = "open ".Length * CharW;
        var tokenLeft = urlLeft + url.IndexOf("abc123", StringComparison.Ordinal) * CharW;
        Assert.True(rect.X > urlLeft + 10 * CharW, "the box starts well inside the URL, not at its beginning");
        Assert.True(rect.X <= tokenLeft, "the box covers the start of the token");
        Assert.True(rect.Right >= tokenLeft + "abc123def456".Length * CharW, "the box covers the whole token");
    }

    [Fact]
    public void Card_numbers_need_a_valid_checksum()
    {
        var ok = Assert.Single(Run("card 4111 1111 1111 1111 exp"));
        Assert.Equal(SensitiveType.Card, ok.Type);
        Assert.DoesNotContain(Run("card 4111 1111 1111 1112 exp"), x => x.Type == SensitiveType.Card);
    }

    [Fact]
    public void Iban_needs_a_valid_checksum()
    {
        Assert.Contains(Run("IBAN GB82 WEST 1234 5698 7654 32 ok"), x => x.Type == SensitiveType.Iban);
        Assert.DoesNotContain(Run("IBAN GB82 WEST 1234 5698 7654 33 ok"), x => x.Type == SensitiveType.Iban);
    }

    [Fact]
    public void Uuid_is_off_by_default_and_low_confidence_when_enabled()
    {
        const string id = "123e4567-e89b-12d3-a456-426614174000";
        Assert.Empty(Run("id " + id));
        var f = Assert.Single(Run("id " + id, disabled: Array.Empty<SensitiveType>()));
        Assert.Equal(SensitiveType.Uuid, f.Type);
        Assert.Equal(SensitiveConfidence.Low, f.Confidence);
    }

    // ---- negatives ----

    [Fact]
    public void Ordinary_text_has_no_matches()
    {
        var lines = new[]
        {
            "The quick brown fox jumps over the lazy dog.",
            "Version 1.2.3 released on 2026-10-14 14:54 with 25 fixes.",
            "Total: 1234567 items in 3 folders; build 20261014.",
            "See README.md and docs/architecture.md for details.",
            "Press Ctrl+Shift+S to capture, then Enter to save.",
        };
        Assert.Empty(RunLines(null, lines));
    }

    // ---- mapping to regions ----

    [Fact]
    public void A_match_over_several_words_on_one_line_is_one_rectangle()
    {
        var f = Assert.Single(Run("card 4111 1111 1111 1111 exp"));
        var rect = Assert.Single(f.Regions);
        var left = "card ".Length * CharW;
        var right = left + "4111 1111 1111 1111".Length * CharW;
        Assert.True(rect.X <= left && rect.Right >= right);
        Assert.True(rect.Width < right - left + 4 * CharW, "the box does not run far past the match");
    }

    [Fact]
    public void A_match_that_wraps_gives_one_rectangle_per_line()
    {
        var f = Assert.Single(RunLines(null, "card 4111 1111", "1111 1111 end"));
        Assert.Equal(SensitiveType.Card, f.Type);
        Assert.Equal(2, f.Regions.Count);
        Assert.True(f.Regions[0].Y < f.Regions[1].Y);
    }

    [Fact]
    public void Regions_are_padded_and_clamped_to_the_image()
    {
        var edge = new List<OcrWord> { new("jane@example.com", 0, 0, 160, 16, 0.9f, 0) };
        var f = Assert.Single(new SensitiveDataDetector().Detect(edge, 100, 12));
        var r = Assert.Single(f.Regions);
        Assert.True(r.X >= 0 && r.Y >= 0 && r.Right <= 100 && r.Bottom <= 12);

        var inner = new List<OcrWord> { new("jane@example.com", 50, 50, 160, 16, 0.9f, 0) };
        var padded = Assert.Single(Assert.Single(new SensitiveDataDetector().Detect(inner, 500, 500)).Regions);
        Assert.True(padded.X < 50 && padded.Y < 50 && padded.Right > 210 && padded.Bottom > 66, "padding is applied");
    }

    // ---- toggles, privacy, scale ----

    [Fact]
    public void Disabled_types_produce_no_findings() =>
        Assert.Empty(Run("Call +84 912 345 678", new[] { SensitiveType.Phone, SensitiveType.Uuid }));

    [Fact]
    public void Preview_never_shows_the_full_value()
    {
        var f = Assert.Single(Run("mail jane.doe@example.com"));
        Assert.NotEqual(f.Text, f.Preview);
        Assert.DoesNotContain("doe@example", f.Preview);
        Assert.Contains('•', f.Preview);
        Assert.Equal("••••", SensitiveText.Mask("abcd"));
    }

    [Fact]
    public void Five_thousand_words_are_processed_quickly()
    {
        var lines = Enumerable.Range(0, 250).Select(i => $"Line {i} lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor").ToList();
        lines[100] = "reach me at jane.doe@example.com or +84 912 345 678";
        var words = Layout(lines.ToArray());
        Assert.True(words.Count >= 3000);

        var sw = Stopwatch.StartNew();
        var found = new SensitiveDataDetector().Detect(words, 4000, 8000);
        sw.Stop();

        Assert.Contains(found, x => x.Type == SensitiveType.Email);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Empty_input_is_fine() =>
        Assert.Empty(new SensitiveDataDetector().Detect(Array.Empty<OcrWord>(), 100, 100));
}

public class RedactionSettingsTests
{
    [Fact]
    public void Defaults_disable_only_uuid_and_clone_is_independent()
    {
        var s = new AppSettings();
        Assert.Equal(new[] { "Uuid" }, s.DisabledRedactionTypes);
        var c = s.Clone();
        c.DisabledRedactionTypes.Add("Phone");
        Assert.Single(s.DisabledRedactionTypes);
    }
}
