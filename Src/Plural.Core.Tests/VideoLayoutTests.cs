using Plural.Core.Playback;

namespace Plural.Core.Tests;

public class VideoLayoutTests
{
    private const double Land = 16.0 / 9;
    private const double Port = 9.0 / 16;

    private static readonly Area DesktopArea = new(1280, 360);
    private static readonly Area PhonePortraitArea = new(390, 380);
    private static readonly Area PhoneLandscapeArea = new(844, 230);

    private static VideoTile[] Tiles(params double[] aspects) => aspects.Select(a => new VideoTile(a)).ToArray();

    private static void AssertSane(Area area, IReadOnlyList<VideoTile> tiles, IReadOnlyList<Rect> rects)
    {
        Assert.Equal(tiles.Count, rects.Count);
        const double eps = 1e-6;
        for (var i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            Assert.True(r.X >= -eps && r.Y >= -eps && r.Right <= area.Width + eps && r.Bottom <= area.Height + eps,
                $"tile {i} {r} outside {area}");
            Assert.True(r.Width > 0 && r.Height > 0, $"tile {i} empty");
            Assert.True(Math.Abs(r.Width / r.Height / tiles[i].Aspect - 1) < 0.005, $"tile {i} aspect");
            for (var j = i + 1; j < rects.Count; j++)
            {
                var o = rects[j];
                var overlap = r.X < o.Right - eps && o.X < r.Right - eps && r.Y < o.Bottom - eps && o.Y < r.Bottom - eps;
                Assert.False(overlap, $"tiles {i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void OneLandscapeDesktopFillsBudgetCentered()
    {
        var r = VideoLayout.Compute(DesktopArea, Tiles(Land), FormFactor.Desktop, null);
        Assert.Equal(360, r[0].Height, 6);
        Assert.Equal(640, r[0].Width, 6);
        Assert.Equal((1280 - 640) / 2.0, r[0].X, 6);
        Assert.Equal(0, r[0].Y, 6);
    }

    [Fact]
    public void OnePortraitDesktopIsTallAndLetterboxed()
    {
        var r = VideoLayout.Compute(DesktopArea, Tiles(Port), FormFactor.Desktop, null);
        Assert.Equal(360, r[0].Height, 6);
        Assert.Equal(360 * Port, r[0].Width, 6);
        Assert.Equal((1280 - r[0].Width) / 2, r[0].X, 6);
    }

    [Fact]
    public void TwoLandscapePhonePortraitStackInOneColumn()
    {
        var r = VideoLayout.Compute(PhonePortraitArea, Tiles(Land, Land), FormFactor.PhonePortrait, null);
        Assert.Equal(r[0].X, r[1].X, 6);
        Assert.True(r[1].Y >= r[0].Bottom);
        AssertSane(PhonePortraitArea, Tiles(Land, Land), r);
    }

    [Fact]
    public void TwoLandscapeDesktopSideBySide()
    {
        var r = VideoLayout.Compute(DesktopArea, Tiles(Land, Land), FormFactor.Desktop, null);
        Assert.Equal(r[0].Y, r[1].Y, 6);
        Assert.True(r[1].X >= r[0].Right);
    }

    [Theory]
    [InlineData(FormFactor.Desktop)]
    [InlineData(FormFactor.PhoneLandscape)]
    public void ThreePortraitOneRow(FormFactor form)
    {
        var area = form == FormFactor.Desktop ? DesktopArea : PhoneLandscapeArea;
        var r = VideoLayout.Compute(area, Tiles(Port, Port, Port), form, null);
        Assert.Equal(r[0].Y, r[1].Y, 6);
        Assert.Equal(r[1].Y, r[2].Y, 6);
        Assert.True(r[1].X >= r[0].Right && r[2].X >= r[1].Right);
        AssertSane(area, Tiles(Port, Port, Port), r);
    }

    [Fact]
    public void LandscapePlusTwoPortraitPhonePortraitLandscapeOnTop()
    {
        var t = Tiles(Land, Port, Port);
        var r = VideoLayout.Compute(PhonePortraitArea, t, FormFactor.PhonePortrait, null);
        Assert.True(r[1].Y >= r[0].Bottom);
        Assert.True(r[2].Y >= r[0].Bottom);
        Assert.Equal(r[1].Y, r[2].Y, 6);
        Assert.True(r[2].X >= r[1].Right);
        AssertSane(PhonePortraitArea, t, r);
    }

    [Fact]
    public void LandscapePlusTwoPortraitDesktopOneRow()
    {
        var t = Tiles(Land, Port, Port);
        var r = VideoLayout.Compute(DesktopArea, t, FormFactor.Desktop, null);
        Assert.Equal(r[0].Y, r[1].Y, 6);
        Assert.Equal(r[1].Y, r[2].Y, 6);
        Assert.True(r[1].X >= r[0].Right && r[2].X >= r[1].Right);
    }

    [Fact]
    public void ResultIsCenteredAndKeepsInputOrder()
    {
        var t = Tiles(Port, Land, Port);
        var r = VideoLayout.Compute(DesktopArea, t, FormFactor.Desktop, null);
        var left = r.Min(x => x.X);
        var right = r.Max(x => x.Right);
        Assert.Equal(left, 1280 - right, 6);
        Assert.True(r[0].X < r[1].X && r[1].X < r[2].X);
    }

    [Fact]
    public void EmptyAndZeroAreaAreSafe()
    {
        Assert.Empty(VideoLayout.Compute(DesktopArea, [], FormFactor.Desktop, null));
        var r = VideoLayout.Compute(new Area(0, 0), Tiles(Land, Port), FormFactor.Desktop, null);
        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Equal(0, x.Area));
    }

    [Fact]
    public void GapsAreEightDesktopAndFourPhone()
    {
        var d = VideoLayout.Compute(DesktopArea, Tiles(Land, Land), FormFactor.Desktop, null);
        Assert.Equal(8, d[1].X - d[0].Right, 6);
        var p = VideoLayout.Compute(PhonePortraitArea, Tiles(Land, Land), FormFactor.PhonePortrait, null);
        Assert.Equal(4, p[1].Y - p[0].Bottom, 6);
    }

    [Fact]
    public void FromAppliesRotation()
    {
        Assert.Equal(9.0 / 16, VideoTile.From(1920, 1080, 90).Aspect, 9);
        Assert.Equal(9.0 / 16, VideoTile.From(1920, 1080, 270).Aspect, 9);
        Assert.Equal(16.0 / 9, VideoTile.From(1920, 1080, 180).Aspect, 9);
        Assert.Equal(16.0 / 9, VideoTile.From(1920, 1080, 0).Aspect, 9);
        Assert.Equal(9.0 / 16, VideoTile.From(1920, 1080, -90).Aspect, 9);
        Assert.Equal(16.0 / 9, VideoTile.From(0, 0, 0).Aspect, 9);
    }

    // ---- Random invariants ----

    [Fact]
    public void RandomLayoutsKeepInvariants()
    {
        var rng = new Random(1234);
        var forms = Enum.GetValues<FormFactor>();
        for (var iter = 0; iter < 400; iter++)
        {
            var n = rng.Next(1, 9);
            var tiles = Enumerable.Range(0, n).Select(_ => new VideoTile(Math.Exp(rng.NextDouble() * Math.Log(21.0 / 9 / (9.0 / 16)) + Math.Log(9.0 / 16)))).ToArray();
            var area = new Area(150 + rng.NextDouble() * 1400, 100 + rng.NextDouble() * 900);
            var form = forms[rng.Next(forms.Length)];
            int? spot = rng.Next(3) == 0 ? rng.Next(n) : null;
            AssertSane(area, tiles, VideoLayout.Compute(area, tiles, form, spot));
        }
    }

    [Fact]
    public void MoreThanEightTilesUseSpotlightZeroAutomatically()
    {
        var rng = new Random(5);
        foreach (var n in new[] { 9, 12, 20 })
        {
            var tiles = Enumerable.Range(0, n).Select(_ => new VideoTile(rng.NextDouble() < 0.5 ? Land : Port)).ToArray();
            var area = DesktopArea;
            var auto = VideoLayout.Compute(area, tiles, FormFactor.Desktop, null);
            var explicit0 = VideoLayout.Compute(area, tiles, FormFactor.Desktop, 0);
            Assert.Equal(explicit0, auto);
            AssertSane(area, tiles, auto);
            Assert.True(auto[0].Area > auto.Skip(1).Max(r => r.Area));
        }
    }

    [Fact]
    public void MoreThanEightTilesHonorGivenSpotlight()
    {
        var tiles = Enumerable.Repeat(new VideoTile(Land), 10).ToArray();
        var r = VideoLayout.Compute(DesktopArea, tiles, FormFactor.Desktop, 4);
        Assert.True(r[4].Area > r.Where((_, i) => i != 4).Max(x => x.Area));
    }

    [Fact]
    public void SpotlightLandscapeAreaUsesRightStrip()
    {
        var t = Tiles(Land, Land, Land);
        var r = VideoLayout.Compute(new Area(1280, 600), t, FormFactor.Desktop, 1);
        var usable = 1280 - 8.0;
        var spotW = usable * 0.75;
        Assert.True(r[1].Right <= spotW + 1e-6);
        Assert.True(r[1].Width > 0.99 * Math.Min(spotW, 600 * Land) - 1e-6 || Math.Abs(r[1].Width - Math.Min(spotW, 600 * Land)) < 1e-6);
        Assert.True(r[0].X >= spotW + 8 - 1e-6);
        Assert.True(r[2].X >= spotW + 8 - 1e-6);
        AssertSane(new Area(1280, 600), t, r);
    }

    [Fact]
    public void SpotlightPortraitAreaUsesBottomStrip()
    {
        var t = Tiles(Land, Port, Land);
        var area = new Area(390, 700);
        var r = VideoLayout.Compute(area, t, FormFactor.PhonePortrait, 0);
        var spotH = (700 - 4.0) * 0.75;
        Assert.True(r[0].Bottom <= spotH + 1e-6);
        Assert.True(r[1].Y >= spotH + 4 - 1e-6);
        Assert.True(r[2].Y >= spotH + 4 - 1e-6);
        // spotlight centered in its part, full width (landscape tile fits width)
        Assert.Equal(390, r[0].Width, 6);
        Assert.Equal((spotH - r[0].Height) / 2, r[0].Y, 6);
        AssertSane(area, t, r);
    }

    [Fact]
    public void SquareAreaCountsAsPortraitForSpotlight()
    {
        var t = Tiles(Land, Land);
        var r = VideoLayout.Compute(new Area(500, 500), t, FormFactor.Tablet, 0);
        Assert.True(r[1].Y > r[0].Y);
        Assert.True(r[1].Y >= (500 - 8.0) * 0.75 + 8 - 1e-6);
    }

    [Fact]
    public void SpotlightSingleTileFillsArea()
    {
        var r = VideoLayout.Compute(DesktopArea, Tiles(Land), FormFactor.Desktop, 0);
        Assert.Equal(360, r[0].Height, 6);
    }

    [Fact]
    public void OutOfRangeSpotlightIsIgnored()
    {
        var t = Tiles(Land, Land);
        Assert.Equal(VideoLayout.Compute(DesktopArea, t, FormFactor.Desktop, null),
            VideoLayout.Compute(DesktopArea, t, FormFactor.Desktop, 7));
    }

    [Fact]
    public void RandomSpotlightLayoutsKeepInvariants()
    {
        var rng = new Random(77);
        var forms = Enum.GetValues<FormFactor>();
        for (var iter = 0; iter < 300; iter++)
        {
            var n = rng.Next(2, 15);
            var tiles = Enumerable.Range(0, n).Select(_ => new VideoTile(0.5625 + rng.NextDouble() * (2.33 - 0.5625))).ToArray();
            var area = new Area(200 + rng.NextDouble() * 1400, 150 + rng.NextDouble() * 800);
            var form = forms[rng.Next(forms.Length)];
            AssertSane(area, tiles, VideoLayout.Compute(area, tiles, form, rng.Next(n)));
        }
    }

    // ---- Budget ----

    [Fact]
    public void DesktopBudgetIsHalfPage()
    {
        Assert.Equal(360, VideoLayout.VideoAreaHeight(FormFactor.Desktop, 720, 56, 56, true), 6);
        Assert.Equal(360, VideoLayout.VideoAreaHeight(FormFactor.Tablet, 720, 56, 56, true), 6);
    }

    [Fact]
    public void SplitterFractionIsHonoredButCappedAtHalf()
    {
        Assert.Equal(216, VideoLayout.VideoAreaHeight(FormFactor.Desktop, 720, 56, 56, true, false, 0.3), 6);
        Assert.Equal(360, VideoLayout.VideoAreaHeight(FormFactor.Desktop, 720, 56, 56, true, false, 0.9), 6);
    }

    [Fact]
    public void PhonePortraitBudgetIs45Percent()
    {
        Assert.Equal(0.45 * 844, VideoLayout.VideoAreaHeight(FormFactor.PhonePortrait, 844, 56, 56, true), 6);
    }

    [Fact]
    public void PhoneLandscapeBudgetAndFocus()
    {
        var normal = VideoLayout.VideoAreaHeight(FormFactor.PhoneLandscape, 600, 0, 40, true);
        Assert.Equal(360, normal, 6); // 60 % of 600, 560 - 2.5 * 56 = 420 left for it
        Assert.Equal(560, VideoLayout.VideoAreaHeight(FormFactor.PhoneLandscape, 600, 0, 40, true, true), 6);
        // focus is ignored outside phone landscape
        Assert.Equal(360, VideoLayout.VideoAreaHeight(FormFactor.Desktop, 720, 56, 56, true, true), 6);
    }

    [Fact]
    public void NoVideoCollapses()
    {
        Assert.Equal(0, VideoLayout.VideoAreaHeight(FormFactor.Desktop, 720, 56, 56, false));
        Assert.Equal(0, VideoLayout.VideoAreaHeight(FormFactor.PhoneLandscape, 400, 0, 40, false, true));
    }

    [Theory]
    [InlineData(FormFactor.Desktop, 64)]
    [InlineData(FormFactor.Tablet, 64)]
    [InlineData(FormFactor.PhonePortrait, 56)]
    [InlineData(FormFactor.PhoneLandscape, 56)]
    public void AtLeastTwoAndAHalfTrackRowsStayVisible(FormFactor form, double row)
    {
        foreach (var page in new[] { 300.0, 400, 500, 700, 1000 })
        {
            var video = VideoLayout.VideoAreaHeight(form, page, 56, 56, true);
            Assert.True(video >= 0);
            var left = page - 56 - 56 - video;
            if (video > 0)
            {
                Assert.True(left >= 2.5 * row - 1e-6, $"{form} page {page}: {left}");
            }
        }
    }

    // ---- Form factor ----

    [Theory]
    [InlineData(390, 844, FormFactor.PhonePortrait)]
    [InlineData(719, 719, FormFactor.PhonePortrait)]
    [InlineData(600, 400, FormFactor.PhoneLandscape)]
    [InlineData(844, 390, FormFactor.PhoneLandscape)]
    [InlineData(932, 430, FormFactor.PhoneLandscape)]
    [InlineData(800, 1280, FormFactor.Tablet)]
    [InlineData(1024, 768, FormFactor.Tablet)]
    [InlineData(1280, 800, FormFactor.Desktop)]
    [InlineData(1920, 1080, FormFactor.Desktop)]
    public void FormFactorMapping(double w, double h, FormFactor expected) =>
        Assert.Equal(expected, VideoLayout.FormFactorFor(w, h));

    // ---- SpotlightFollower ----

    private static IReadOnlyDictionary<string, float> L(float a, float b) =>
        new Dictionary<string, float> { ["A"] = a, ["B"] = b };

    private static string? Run(SpotlightFollower f, ref TimeSpan t, double seconds, float a, float b, double step = 0.1)
    {
        string? cur = f.Current;
        var end = t + TimeSpan.FromSeconds(seconds);
        while (t < end)
        {
            t += TimeSpan.FromSeconds(step);
            cur = f.Update(t, L(a, b));
        }
        return cur;
    }

    [Fact]
    public void FollowerStartsEmptyAndPicksFirstLoudDevice()
    {
        var f = new SpotlightFollower();
        Assert.Null(f.Current);
        Assert.Null(f.Update(TimeSpan.Zero, L(0, 0)));
        Assert.Equal("B", f.Update(TimeSpan.FromSeconds(0.1), L(0, 0.5f)));
    }

    [Fact]
    public void FollowerHoldsChoiceForThreeSeconds()
    {
        var f = new SpotlightFollower();
        var t = TimeSpan.Zero;
        f.Update(t, L(0.5f, 0)); // A chosen at t=0
        Assert.Equal("A", f.Current);
        // B is much louder from t=0.1 on; still A before 3 s have passed.
        Run(f, ref t, 2.8, 0, 0.8f);
        Assert.Equal("A", f.Current);
        Run(f, ref t, 0.5, 0, 0.8f);
        Assert.Equal("B", f.Current);
    }

    [Fact]
    public void FollowerIgnoresShortBurstsAndSmooths()
    {
        var f = new SpotlightFollower();
        var t = TimeSpan.Zero;
        f.Update(t, L(0.3f, 0));
        Run(f, ref t, 5, 0.3f, 0);
        // 200 ms burst on B far louder than A
        Run(f, ref t, 0.2, 0.3f, 1f);
        Run(f, ref t, 5, 0.3f, 0);
        Assert.Equal("A", f.Current);
        // smoothed level lags behind an instantaneous jump
        var g = new SpotlightFollower();
        g.Update(TimeSpan.Zero, L(0, 0));
        g.Update(TimeSpan.FromSeconds(0.1), L(1, 0));
        Assert.InRange(g.SmoothedLevel("A"), 0.01, 0.2);
    }

    [Fact]
    public void FollowerNeedsClearLeadNotJustTie()
    {
        var f = new SpotlightFollower();
        var t = TimeSpan.Zero;
        f.Update(t, L(0.5f, 0));
        Run(f, ref t, 10, 0.5f, 0.52f);
        Assert.Equal("A", f.Current);
    }

    [Fact]
    public void FollowerKeepsCurrentWhenAllSilent()
    {
        var f = new SpotlightFollower();
        var t = TimeSpan.Zero;
        f.Update(t, L(0.5f, 0));
        Run(f, ref t, 20, 0, 0);
        Assert.Equal("A", f.Current);
    }

    [Fact]
    public void FollowerIsDeterministic()
    {
        string?[] Trace()
        {
            var f = new SpotlightFollower();
            var t = TimeSpan.Zero;
            var list = new List<string?>();
            for (var i = 0; i < 300; i++)
            {
                t += TimeSpan.FromMilliseconds(100);
                var phase = (i / 60) % 2;
                list.Add(f.Update(t, L(phase == 0 ? 0.6f : 0.05f, phase == 0 ? 0.05f : 0.6f)));
            }
            return list.ToArray();
        }
        Assert.Equal(Trace(), Trace());
        Assert.Contains("A", Trace());
        Assert.Contains("B", Trace());
    }

    [Fact]
    public void FollowerResetClears()
    {
        var f = new SpotlightFollower();
        f.Update(TimeSpan.Zero, L(1, 0));
        f.Reset();
        Assert.Null(f.Current);
        Assert.Equal(0, f.SmoothedLevel("A"));
    }
}
