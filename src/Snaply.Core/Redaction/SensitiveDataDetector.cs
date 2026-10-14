using System.Text;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Core.Redaction;

/// <summary>
/// Finds sensitive-looking values in OCR output and maps them to image regions. It never reads or writes files, never logs the text
/// and keeps nothing: the caller owns the returned findings (which do contain the matched text) for as long as it needs them.
/// </summary>
public sealed class SensitiveDataDetector
{
    /// <param name="words">Recognised words in reading order, as returned by OCR.</param>
    /// <param name="disabled">Types the user turned off; defaults to <see cref="SensitiveText.DisabledByDefault"/>.</param>
    public IReadOnlyList<SensitiveFinding> Detect(IReadOnlyList<OcrWord> words, int imageWidth, int imageHeight, IReadOnlyCollection<SensitiveType>? disabled = null)
    {
        if (words.Count == 0 || imageWidth <= 0 || imageHeight <= 0) return Array.Empty<SensitiveFinding>();
        var off = disabled ?? SensitiveText.DisabledByDefault;

        var map = TextMap.Build(words);
        var kept = new List<RawMatch>();
        foreach (var (type, run) in SensitiveRules.Ordered)
        {
            if (off.Contains(type)) continue;
            foreach (var m in run(map.Text))
            {
                if (m.Length <= 0 || kept.Any(k => m.Start < k.End && k.Start < m.End)) continue; // an earlier (higher priority) rule owns this text
                kept.Add(m);
            }
        }

        var bounds = new PixelRect(0, 0, imageWidth, imageHeight);
        var findings = new List<SensitiveFinding>();
        foreach (var m in kept.OrderBy(k => k.Start))
        {
            var regions = map.RegionsFor(m.Start, m.End, words, bounds);
            if (regions.Count == 0) continue;
            findings.Add(new SensitiveFinding(m.Type, m.Confidence, map.Text.Substring(m.Start, m.Length), regions));
        }
        return findings;
    }

    /// <summary>The recognised words laid out as text (a space between words, a newline between lines) with their character spans.</summary>
    private sealed class TextMap
    {
        private readonly List<(int Start, int End)> _spans = new();
        public string Text { get; private set; } = "";

        public static TextMap Build(IReadOnlyList<OcrWord> words)
        {
            var map = new TextMap();
            var sb = new StringBuilder();
            for (var i = 0; i < words.Count; i++)
            {
                if (i > 0) sb.Append(words[i].Line != words[i - 1].Line ? '\n' : ' ');
                var start = sb.Length;
                sb.Append(words[i].Text);
                map._spans.Add((start, sb.Length));
            }
            map.Text = sb.ToString();
            return map;
        }

        /// <summary>
        /// One padded rectangle per text line the match touches. A word that is only partly inside the match (a URL whose last
        /// parameter is the secret) is cut proportionally to its characters, widened by one character on the cut side so that
        /// proportional-font error errs on the side of covering too much.
        /// </summary>
        public IReadOnlyList<PixelRect> RegionsFor(int start, int end, IReadOnlyList<OcrWord> words, PixelRect bounds)
        {
            var perLine = new SortedDictionary<int, PixelRect?>();
            for (var i = 0; i < words.Count; i++)
            {
                var (ws, we) = _spans[i];
                if (we <= start || ws >= end) continue;

                var w = words[i];
                var len = Math.Max(1, we - ws);
                var a = Math.Max(start, ws) - ws;
                var b = Math.Min(end, we) - ws;
                var cw = (double)w.Width / len;
                var x0 = w.X + (a > 0 ? a * cw - cw : 0);
                var x1 = w.X + (b < len ? b * cw + cw : w.Width);
                var rect = PixelRect.FromCorners((int)Math.Floor(x0), w.Y, (int)Math.Ceiling(x1), w.Y + w.Height);

                perLine[w.Line] = perLine.TryGetValue(w.Line, out var existing) && existing is { } e ? PixelRect.Union(new[] { e, rect }) : rect;
            }

            var result = new List<PixelRect>();
            foreach (var rect in perLine.Values)
            {
                if (rect is not { } r) continue;
                var pad = Math.Max(2, (int)Math.Round(r.Height * 0.15));
                var padded = new PixelRect(r.X - pad, r.Y - pad, r.Width + 2 * pad, r.Height + 2 * pad).Intersect(bounds);
                if (!padded.IsEmpty) result.Add(padded);
            }
            return result;
        }
    }
}
