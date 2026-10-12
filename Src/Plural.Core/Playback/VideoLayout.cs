namespace Plural.Core.Playback;

/// <summary>Size in dp.</summary>
public readonly record struct Area(double Width, double Height);

/// <summary>Rectangle in dp, origin top-left.</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Width * Height;
}

/// <summary>A video to place. <see cref="Aspect"/> is the display aspect (width / height) after rotation.</summary>
public readonly record struct VideoTile(double Aspect)
{
    /// <summary>Tile from coded size and the container rotation (any multiple of 90 degrees).</summary>
    public static VideoTile From(int width, int height, int rotationDegrees)
    {
        if (width <= 0 || height <= 0)
        {
            return new VideoTile(16.0 / 9);
        }
        var quarter = ((rotationDegrees / 90) % 4 + 4) % 4;
        return quarter % 2 == 1 ? new VideoTile((double)height / width) : new VideoTile((double)width / height);
    }
}

public enum FormFactor { PhonePortrait, PhoneLandscape, Tablet, Desktop }

/// <summary>
/// Pure video tile layout for the player page: budget, packing and spotlight. No UI types.
/// </summary>
public static class VideoLayout
{
    /// <summary>Width below which MainView switches to its compact (one column) layout.</summary>
    public const double CompactBelow = 720;
    /// <summary>Wider than this (and not a short landscape window) counts as desktop.</summary>
    public const double DesktopFrom = 1100;
    /// <summary>A landscape window shorter than this is a phone held sideways.</summary>
    public const double PhoneLandscapeBelowHeight = 600;

    public const double TrackRowHeightDesktop = 64;
    public const double TrackRowHeightPhone = 56;
    public const double MinVisibleTrackRows = 2.5;
    public const double MaxFractionLargeScreen = 0.5;
    public const double MaxFractionPhonePortrait = 0.45;
    public const double MaxFractionPhoneLandscape = 0.60;
    public const double SpotlightShare = 0.75;
    public const int MaxGridTiles = 8;
    /// <summary>Above this many tiles only a single row or column is tried (2^(n-1) splits would explode).</summary>
    private const int MaxEnumerated = 12;

    /// <summary>
    /// Mapping (dp): width &lt; 720 (MainView compact) is a phone: portrait when height &gt;= width, else landscape.
    /// Width &gt;= 720 with a landscape window shorter than 600 is a phone in landscape (844x390).
    /// Otherwise width &lt; 1100 is Tablet, and 1100+ is Desktop.
    /// </summary>
    public static FormFactor FormFactorFor(double widthDp, double heightDp)
    {
        var landscape = widthDp > heightDp;
        if (widthDp < CompactBelow)
        {
            return landscape ? FormFactor.PhoneLandscape : FormFactor.PhonePortrait;
        }
        if (landscape && heightDp < PhoneLandscapeBelowHeight)
        {
            return FormFactor.PhoneLandscape;
        }
        return widthDp < DesktopFrom ? FormFactor.Tablet : FormFactor.Desktop;
    }

    public static double TrackRowHeight(FormFactor form) =>
        form is FormFactor.PhonePortrait or FormFactor.PhoneLandscape ? TrackRowHeightPhone : TrackRowHeightDesktop;

    /// <summary>Height available to the video area.</summary>
    /// <param name="pageHeight">Whole player page height.</param>
    /// <param name="headerHeight">Header height (not available to the video area).</param>
    /// <param name="transportHeight">Transport bar height (always visible).</param>
    /// <param name="hasVideo">False collapses the area to 0.</param>
    /// <param name="focusVideo">Phone landscape "Focus video": all but header and transport.</param>
    /// <param name="splitterFraction">Desktop/tablet user splitter position (fraction of page height); capped at 50 %.</param>
    public static double VideoAreaHeight(FormFactor form, double pageHeight, double headerHeight, double transportHeight,
        bool hasVideo, bool focusVideo = false, double? splitterFraction = null)
    {
        if (!hasVideo || pageHeight <= 0)
        {
            return 0;
        }
        var remaining = Math.Max(0, pageHeight - headerHeight - transportHeight);
        if (form == FormFactor.PhoneLandscape && focusVideo)
        {
            return remaining;
        }
        var fraction = form switch
        {
            FormFactor.PhonePortrait => MaxFractionPhonePortrait,
            FormFactor.PhoneLandscape => MaxFractionPhoneLandscape,
            _ => Math.Clamp(splitterFraction ?? MaxFractionLargeScreen, 0, MaxFractionLargeScreen),
        };
        var byFraction = pageHeight * fraction;
        var keepTracks = remaining - MinVisibleTrackRows * TrackRowHeight(form);
        return Math.Max(0, Math.Min(byFraction, keepTracks));
    }

    public static double Gap(FormFactor form) =>
        form is FormFactor.PhonePortrait or FormFactor.PhoneLandscape ? 4 : 8;

    /// <summary>Place tiles (in input order) inside <paramref name="area"/>; rects are returned in input order.</summary>
    public static IReadOnlyList<Rect> Compute(Area area, IReadOnlyList<VideoTile> tiles, FormFactor form, int? spotlight)
    {
        var n = tiles.Count;
        if (n == 0)
        {
            return [];
        }
        var aspects = new double[n];
        for (var i = 0; i < n; i++)
        {
            var a = tiles[i].Aspect;
            aspects[i] = a > 0 && double.IsFinite(a) ? a : 1;
        }
        var gap = Gap(form);
        var w = Math.Max(0, area.Width);
        var h = Math.Max(0, area.Height);

        if (spotlight is int s && (s < 0 || s >= n))
        {
            spotlight = null;
        }
        if (n > MaxGridTiles && spotlight is null)
        {
            spotlight = 0;
        }
        if (spotlight is null || n == 1)
        {
            return Pack(w, h, 0, 0, aspects, gap);
        }

        var si = spotlight.Value;
        var result = new Rect[n];
        double sW, sH, fX, fY, fW, fH;
        if (h >= w)
        {
            // Portrait or square: spotlight on top, filmstrip below.
            var usable = Math.Max(0, h - gap);
            sH = usable * SpotlightShare;
            sW = w;
            fH = usable - sH;
            fW = w;
            fX = 0;
            fY = sH + gap;
        }
        else
        {
            var usable = Math.Max(0, w - gap);
            sW = usable * SpotlightShare;
            sH = h;
            fW = usable - sW;
            fH = h;
            fX = sW + gap;
            fY = 0;
        }
        result[si] = Fit(0, 0, sW, sH, aspects[si]);

        var restIdx = new List<int>(n - 1);
        var restAspects = new List<double>(n - 1);
        for (var i = 0; i < n; i++)
        {
            if (i != si)
            {
                restIdx.Add(i);
                restAspects.Add(aspects[i]);
            }
        }
        var strip = Pack(fW, fH, fX, fY, restAspects.ToArray(), gap);
        for (var k = 0; k < restIdx.Count; k++)
        {
            result[restIdx[k]] = strip[k];
        }
        return result;
    }

    private static Rect Fit(double x, double y, double w, double h, double aspect)
    {
        if (w <= 0 || h <= 0)
        {
            return new Rect(x, y, 0, 0);
        }
        var tw = Math.Min(w, h * aspect);
        var th = tw / aspect;
        return new Rect(x + (w - tw) / 2, y + (h - th) / 2, tw, th);
    }

    private readonly record struct Candidate(double MinArea, double TotalArea, bool IsRows, int[] Starts);

    /// <summary>Best contiguous row/column packing of the ordered aspects inside a w x h box at (ox, oy).</summary>
    private static Rect[] Pack(double w, double h, double ox, double oy, double[] aspects, double gap)
    {
        var n = aspects.Length;
        if (n == 0)
        {
            return [];
        }
        if (w <= 0 || h <= 0)
        {
            return Enumerable.Repeat(new Rect(ox, oy, 0, 0), n).ToArray();
        }

        Candidate? best = null;
        foreach (var isRows in new[] { true, false })
        {
            foreach (var starts in EnumerateSplits(n))
            {
                var c = Evaluate(w, h, aspects, gap, isRows, starts);
                if (best is null || Better(c, best.Value))
                {
                    best = c;
                }
            }
        }
        return Place(w, h, ox, oy, aspects, gap, best!.Value);
    }

    private static bool Better(Candidate a, Candidate b)
    {
        if (!Close(a.MinArea, b.MinArea))
        {
            return a.MinArea > b.MinArea;
        }
        if (!Close(a.TotalArea, b.TotalArea))
        {
            return a.TotalArea > b.TotalArea;
        }
        return a.Starts.Length < b.Starts.Length; // full tie keeps the earlier one (rows first)
    }

    private static bool Close(double a, double b) =>
        Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    /// <summary>All contiguous splits; each array lists the start index of every group (begins with 0).</summary>
    private static IEnumerable<int[]> EnumerateSplits(int n)
    {
        if (n > MaxEnumerated)
        {
            yield return [0];
            yield return Enumerable.Range(0, n).ToArray();
            yield break;
        }
        var count = 1 << (n - 1);
        for (var mask = 0; mask < count; mask++)
        {
            var starts = new List<int> { 0 };
            for (var i = 1; i < n; i++)
            {
                if ((mask & (1 << (i - 1))) != 0)
                {
                    starts.Add(i);
                }
            }
            yield return starts.ToArray();
        }
    }

    /// <summary>
    /// Rows: each group is a row (tiles share height, h = (W - gaps) / sum aspect). Columns: each group is a column
    /// (tiles share width, w = (H - gaps) / sum(1/aspect)). The whole block is scaled down if it overflows.
    /// </summary>
    private static (double[] Thick, double Scale) Measure(double w, double h, double[] aspects, double gap, bool isRows, int[] starts)
    {
        var g = starts.Length;
        var thick = new double[g];
        double total = 0;
        for (var r = 0; r < g; r++)
        {
            var from = starts[r];
            var to = r + 1 < g ? starts[r + 1] : aspects.Length;
            double sum = 0;
            for (var i = from; i < to; i++)
            {
                sum += isRows ? aspects[i] : 1 / aspects[i];
            }
            var span = Math.Max(0, (isRows ? w : h) - gap * (to - from - 1));
            thick[r] = span / sum;
            total += thick[r];
        }
        var avail = Math.Max(0, (isRows ? h : w) - gap * (g - 1));
        var scale = total > avail && total > 0 ? avail / total : 1;
        return (thick, scale);
    }

    private static Candidate Evaluate(double w, double h, double[] aspects, double gap, bool isRows, int[] starts)
    {
        var (thick, scale) = Measure(w, h, aspects, gap, isRows, starts);
        var min = double.MaxValue;
        double total = 0;
        for (var r = 0; r < starts.Length; r++)
        {
            var from = starts[r];
            var to = r + 1 < starts.Length ? starts[r + 1] : aspects.Length;
            var t = thick[r] * scale;
            for (var i = from; i < to; i++)
            {
                var area = isRows ? t * (aspects[i] * t) : t * (t / aspects[i]);
                min = Math.Min(min, area);
                total += area;
            }
        }
        return new Candidate(min, total, isRows, starts);
    }

    private static Rect[] Place(double w, double h, double ox, double oy, double[] aspects, double gap, Candidate c)
    {
        var starts = c.Starts;
        var (thick, scale) = Measure(w, h, aspects, gap, c.IsRows, starts);
        var g = starts.Length;
        var result = new Rect[aspects.Length];
        var blockMain = gap * (g - 1);
        for (var r = 0; r < g; r++)
        {
            blockMain += thick[r] * scale;
        }
        var pos = ((c.IsRows ? h : w) - blockMain) / 2;
        for (var r = 0; r < g; r++)
        {
            var from = starts[r];
            var to = r + 1 < g ? starts[r + 1] : aspects.Length;
            var t = thick[r] * scale;
            var cross = gap * (to - from - 1);
            for (var i = from; i < to; i++)
            {
                cross += c.IsRows ? aspects[i] * t : t / aspects[i];
            }
            var crossPos = ((c.IsRows ? w : h) - cross) / 2;
            for (var i = from; i < to; i++)
            {
                var len = c.IsRows ? aspects[i] * t : t / aspects[i];
                result[i] = c.IsRows
                    ? new Rect(ox + crossPos, oy + pos, len, t)
                    : new Rect(ox + pos, oy + crossPos, t, len);
                crossPos += len + gap;
            }
            pos += t + gap;
        }
        return result;
    }
}
