using Plural.Core.Protocol;

namespace Plural.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void SessionPackets_HaveValidChecksums()
    {
        foreach (var p in new[] { Frame.SessionOpen.ToArray(), Frame.SessionClose.ToArray() })
        {
            Assert.True(Frame.SumsToZero(p.AsSpan(0, 9)));
            Assert.True(Frame.SumsToZero(p.AsSpan(9)));
            Assert.Equal(p.Length, p[3]);
        }
    }

    [Fact]
    public void BuildCommand_MatchesReferenceImplementation()
    {
        // From Tools/swiss_protocol.py self-test (verified on hardware).
        var f = Frame.BuildCommand(Command.AudioBegin, Frame.ParamTlv((ushort)CodecPlugin.CoQCelt, (ushort)AudioQuality.High), seq: 3, ack: 7);
        Assert.Equal(Convert.FromHexString("FF5A00184003070243000053530 00C00030006000400BE83".Replace(" ", "")), f);
        Assert.Equal(Convert.FromHexString("FF5A00094004070053"), Frame.BuildHeartbeat(4, 7));
    }

    [Fact]
    public void FrameReader_ResyncsAndHandlesSplitInput()
    {
        var cmd = Frame.BuildCommand(Command.AudioBegin, Frame.ParamTlv(4, 190), 3, 7);
        var hb = Frame.BuildHeartbeat(1, 2);
        var stream = new byte[] { 0x00, 0x13 }.Concat(cmd).Concat(hb).Concat(cmd.Take(10)).ToArray();
        var r = new FrameReader();
        var outp = new List<ReceivedFrame>();
        r.Feed(stream, outp);
        Assert.Equal(2, outp.Count);
        Assert.True(outp[0].IsCommand);
        Assert.Equal(Command.AudioBegin, outp[0].Command);
        Assert.True(outp[0].BodyChecksumOk);
        Assert.False(outp[1].IsCommand);
        Assert.Equal(2, r.Skipped);
        outp.Clear();
        r.Feed(cmd.AsSpan(10), outp);
        Assert.Single(outp);
        Assert.Equal(cmd, outp[0].Raw);
    }

    [Fact]
    public void FormatVersion() => Assert.Equal("0.4.32", DeviceVersion.FormatVersion(0x0420));

    [Fact]
    public void SettingMap_RoundTrips()
    {
        foreach (var s in Enum.GetValues<MikeSetting>())
        {
            var (cmd, sub) = MikeSettingMap.Resolve(s);
            Assert.Equal(s, MikeSettingMap.FromReport(cmd, sub));
        }
    }
}
