using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SteamTrailerTool.Core.Models;

namespace SteamTrailerTool.Core.Services;

/// <summary>
/// Steam 트레일러용 MPEG-DASH(MPD) 파서.
/// Steam은 type="static" + SegmentTemplate($Number$, duration 고정) 형태만 쓰지만,
/// 향후 형식 변경에 대비해 SegmentTimeline($Time$/$Number$)과 단일 파일 BaseURL도 처리한다.
/// 첫 번째 Period만 사용한다 (트레일러는 단일 Period).
/// </summary>
public static partial class DashManifestParser
{
    public static DashManifest Parse(string xml, Uri manifestUri)
    {
        var doc = XDocument.Parse(xml);
        var mpd = doc.Root ?? throw new FormatException("MPD 루트 요소가 없습니다.");
        if (mpd.Name.LocalName != "MPD")
        {
            throw new FormatException($"MPD 문서가 아닙니다 (루트: {mpd.Name.LocalName}).");
        }

        var period = Child(mpd, "Period") ?? throw new FormatException("Period 요소가 없습니다.");

        var duration =
            ParseDuration(Attr(period, "duration")) ??
            ParseDuration(Attr(mpd, "mediaPresentationDuration")) ??
            throw new FormatException("mediaPresentationDuration을 알 수 없습니다.");

        var mpdBase = ResolveBase(manifestUri, mpd);
        var periodBase = ResolveBase(mpdBase, period);

        var reps = new List<DashRepresentation>();
        foreach (var set in Children(period, "AdaptationSet"))
        {
            var setBase = ResolveBase(periodBase, set);
            foreach (var rep in Children(set, "Representation"))
            {
                var parsed = ParseRepresentation(set, rep, setBase, duration);
                if (parsed is not null)
                {
                    reps.Add(parsed);
                }
            }
        }

        return new DashManifest(duration, reps);
    }

    private static DashRepresentation? ParseRepresentation(XElement set, XElement rep, Uri setBase, TimeSpan duration)
    {
        var mimeType = Attr(rep, "mimeType") ?? Attr(set, "mimeType") ?? "";
        var contentType = Attr(set, "contentType") ?? Attr(rep, "contentType") ?? "";

        DashMediaKind kind;
        if (contentType == "video" || mimeType.StartsWith("video/", StringComparison.Ordinal))
        {
            kind = DashMediaKind.Video;
        }
        else if (contentType == "audio" || mimeType.StartsWith("audio/", StringComparison.Ordinal))
        {
            kind = DashMediaKind.Audio;
        }
        else
        {
            return null; // 썸네일 타일(image/avif) 등은 무시
        }

        var id = Attr(rep, "id") ?? "";
        var codecs = Attr(rep, "codecs") ?? Attr(set, "codecs") ?? "";
        var bandwidth = ParseLong(Attr(rep, "bandwidth")) ?? 0;
        var width = (int)(ParseLong(Attr(rep, "width")) ?? ParseLong(Attr(set, "width")) ?? 0);
        var height = (int)(ParseLong(Attr(rep, "height")) ?? ParseLong(Attr(set, "height")) ?? 0);
        var repBase = ResolveBase(setBase, rep);

        // SegmentTemplate은 Representation이 AdaptationSet 것을 상속/덮어쓴다.
        var template = MergeTemplate(Child(set, "SegmentTemplate"), Child(rep, "SegmentTemplate"));

        Uri? init = null;
        List<Uri> segments;

        if (template is not null)
        {
            var ctx = new TemplateContext(id, bandwidth);
            var initPattern = template.Initialization;
            if (initPattern is not null)
            {
                init = new Uri(repBase, Expand(initPattern, ctx, number: 0, time: 0));
            }

            if (template.Media is null)
            {
                throw new FormatException($"Representation {id}: SegmentTemplate@media가 없습니다.");
            }

            segments = template.Timeline is { Count: > 0 }
                ? ExpandTimeline(template, ctx, repBase, duration)
                : ExpandNumbered(template, ctx, repBase, duration);
        }
        else if (Child(rep, "BaseURL") is not null)
        {
            // SegmentBase / 단일 파일 형태: BaseURL 자체가 완전한 파일
            segments = [repBase];
        }
        else
        {
            throw new FormatException($"Representation {id}: 세그먼트 정보를 찾을 수 없습니다.");
        }

        return new DashRepresentation(id, kind, mimeType, codecs, bandwidth, width, height, init, segments);
    }

    private static List<Uri> ExpandNumbered(SegmentTemplateInfo t, TemplateContext ctx, Uri baseUri, TimeSpan duration)
    {
        if (t.Duration is not { } segDuration || segDuration <= 0)
        {
            throw new FormatException("SegmentTemplate@duration이 없습니다.");
        }

        // 총 길이 / 세그먼트 길이 를 올림. 부동소수 오차로 정확히 나눠떨어지는 경우 1개가 더 붙지 않도록 epsilon을 뺀다.
        var exact = duration.TotalSeconds * t.Timescale / segDuration;
        var count = (long)Math.Ceiling(exact - 1e-6);
        if (count <= 0)
        {
            count = 1;
        }

        var list = new List<Uri>((int)count);
        for (long i = 0; i < count; i++)
        {
            var number = t.StartNumber + i;
            var time = i * segDuration;
            list.Add(new Uri(baseUri, Expand(t.Media!, ctx, number, time)));
        }

        return list;
    }

    private static List<Uri> ExpandTimeline(SegmentTemplateInfo t, TemplateContext ctx, Uri baseUri, TimeSpan duration)
    {
        var list = new List<Uri>();
        var number = t.StartNumber;
        long time = 0;
        var end = (long)Math.Round(duration.TotalSeconds * t.Timescale);
        var entries = t.Timeline!;

        for (var idx = 0; idx < entries.Count; idx++)
        {
            var s = entries[idx];
            if (s.T is { } explicitT)
            {
                time = explicitT;
            }

            long repeat = s.R;
            if (repeat < 0)
            {
                // r=-1: 다음 S의 t 또는 Period 끝까지 반복
                var until = idx + 1 < entries.Count && entries[idx + 1].T is { } nextT ? nextT : end;
                repeat = Math.Max(0, (long)Math.Ceiling((until - time) / (double)s.D) - 1);
            }

            for (long r = 0; r <= repeat; r++)
            {
                list.Add(new Uri(baseUri, Expand(t.Media!, ctx, number, time)));
                number++;
                time += s.D;
            }
        }

        return list;
    }

    [GeneratedRegex(@"\$(RepresentationID|Number|Bandwidth|Time)(?:%0(\d+)d)?\$|\$\$")]
    private static partial Regex TemplateToken();

    internal static string Expand(string pattern, TemplateContext ctx, long number, long time) =>
        TemplateToken().Replace(pattern, m =>
        {
            if (m.Value == "$$")
            {
                return "$";
            }

            var width = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return m.Groups[1].Value switch
            {
                "RepresentationID" => ctx.RepresentationId,
                "Number" => number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'),
                "Bandwidth" => ctx.Bandwidth.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'),
                "Time" => time.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'),
                _ => m.Value,
            };
        });

    internal readonly record struct TemplateContext(string RepresentationId, long Bandwidth);

    private sealed record TimelineEntry(long? T, long D, long R);

    private sealed record SegmentTemplateInfo(
        long Timescale,
        long? Duration,
        long StartNumber,
        string? Initialization,
        string? Media,
        IReadOnlyList<TimelineEntry>? Timeline);

    private static SegmentTemplateInfo? MergeTemplate(XElement? parent, XElement? child)
    {
        if (parent is null && child is null)
        {
            return null;
        }

        string? Pick(string name) => (child is null ? null : Attr(child, name)) ?? (parent is null ? null : Attr(parent, name));

        var timelineEl = (child is null ? null : Child(child, "SegmentTimeline")) ??
                         (parent is null ? null : Child(parent, "SegmentTimeline"));

        List<TimelineEntry>? timeline = null;
        if (timelineEl is not null)
        {
            timeline = Children(timelineEl, "S")
                .Select(s => new TimelineEntry(
                    ParseLong(Attr(s, "t")),
                    ParseLong(Attr(s, "d")) ?? throw new FormatException("SegmentTimeline S@d가 없습니다."),
                    ParseLong(Attr(s, "r")) ?? 0))
                .ToList();
        }

        return new SegmentTemplateInfo(
            ParseLong(Pick("timescale")) ?? 1,
            ParseLong(Pick("duration")),
            ParseLong(Pick("startNumber")) ?? 1,
            Pick("initialization"),
            Pick("media"),
            timeline);
    }

    private static Uri ResolveBase(Uri parent, XElement el)
    {
        var baseUrl = Child(el, "BaseURL")?.Value.Trim();
        return string.IsNullOrEmpty(baseUrl) ? parent : new Uri(parent, baseUrl);
    }

    internal static TimeSpan? ParseDuration(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(iso.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static long? ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? Attr(XElement el, string name) => el.Attribute(name)?.Value;

    private static XElement? Child(XElement el, string localName) =>
        el.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement el, string localName) =>
        el.Elements().Where(e => e.Name.LocalName == localName);
}
