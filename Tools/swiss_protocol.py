"""Sabinetek Swiss SPP protocol — frame encoder/decoder.

Reconstructed from MetAudio 1.5.2 (see Docs/Protocol/SPP-Framing.md).
Run directly to self-test against packets hard-coded in the app.
"""
import struct

SYNC = b"\xff\x5a"
HDR_BYTE4 = 0x40
SUB_MAGIC = b"SS"

# Command IDs (d4.o)
CMD = {
    "DFU_BEGIN": 0, "DFU_DATA": 1, "DFU_END": 2,
    "AUDIO_BEGIN": 3, "AUDIO_STREAM": 4, "AUDIO_STREAM_VER": 260, "AUDIO_END": 5,
    "CUR_VERSION": 6, "MEDIA_INFO": 7, "BUTTON_PRESSED": 8, "STATUS_INFO": 9,
    "STATUS_ERROR": 10, "EXTEND_STATUS": 11, "MIC_PARAM": 12, "SPK_PARAM": 13,
    "MONITOR": 14, "AGC_PARAM": 15, "ANS_PARAM": 16, "EFFECT_PARAM": 17,
    "EXTEND_PARAMTERS": 18, "HEARING_AID": 19, "NTP_SYNC": 20, "NTP_DELAY_REQ": 21,
    "NTP_DELAY_RESP": 22, "NTP_DELAY_RESP_FINAL": 23, "PANIC_REPORT": 24,
    "SN_AUTH_CHALLENGE": 32, "SN_AUTH_RESPONSE": 33, "SN_AUTH_REPORT": 34,
    "EARSET_MODE": 48,
}
CMD_NAME = {v: k for k, v in CMD.items()}


def checksum(data: bytes) -> int:
    """Two's complement of the byte sum (e4.e.a / e4.e.b): sum(data) + cksum == 0 mod 256."""
    return (-sum(data)) & 0xFF


def build_link_header(total_len: int, seq: int, ack: int, ptype: int) -> bytes:
    """9-byte link header: FF 5A len16 40 seq ack type cksum (r.a.b / l5.a)."""
    h = SYNC + struct.pack(">HBBBB", total_len, HDR_BYTE4, seq & 0xFF, ack & 0xFF, ptype)
    return h + bytes([checksum(h)])


def build_frame(cmd: int, payload: bytes, seq: int = 0, ack: int = 0) -> bytes:
    """Command frame (r.a.a): link header (type 2) + 'SS' sub-header + payload + cksum.

    Note: the app writes only the low byte of the sub-header length and command
    (bytes 14 and 16); the parser reads them as 16-bit big-endian.
    """
    sub = b"\x00\x00" + SUB_MAGIC + struct.pack(">HH", len(payload) + 6, cmd)
    total = 9 + len(sub) + len(payload) + 1
    body = sub + payload
    return build_link_header(total, seq, ack, 2) + body + bytes([checksum(body)])


def build_heartbeat(seq: int, ack: int) -> bytes:
    """9-byte keep-alive/ack (l5.a), sent every 500 ms while streaming."""
    return build_link_header(9, seq, ack, 0)


def tlv_param(sub_type: int, value: int) -> bytes:
    """Generic parameter payload (l5.d): len16=6, type16, value16.

    The app encodes high bytes as value/255 (sic); identical to /256 for values < 255.
    """
    return struct.pack(">HBBBB", 6, (sub_type // 255) & 0xFF, sub_type & 0xFF,
                       (value // 255) & 0xFF, value & 0xFF)


def parse_frame(buf: bytes) -> dict:
    """Parse one received frame (k.a.a + l.c.a)."""
    if buf[:2] != SYNC:
        raise ValueError("bad sync")
    total = struct.unpack_from(">H", buf, 2)[0]
    out = {
        "total_len": total, "byte4": buf[4], "seq": buf[5], "ack": buf[6], "type": buf[7],
        "hdr_ok": (sum(buf[:9]) & 0xFF) == 0,
    }
    if total >= 17 and buf[11:13] == SUB_MAGIC:
        sub_len, cmd = struct.unpack_from(">HH", buf, 13)
        out.update(
            cmd=cmd, cmd_name=CMD_NAME.get(cmd, f"0x{cmd:04x}"), sub_len=sub_len,
            payload=buf[17:total - 1],
            body_ok=(sum(buf[9:total]) & 0xFF) == 0,
        )
    return out


class FrameReader:
    """Incremental stream parser: feed() raw bytes, get complete frames back.

    Resynchronises on FF 5A if garbage arrives. Returns (raw_bytes, parsed_dict).
    """

    def __init__(self):
        self.buf = bytearray()
        self.skipped = 0

    def feed(self, data: bytes):
        self.buf += data
        out = []
        while True:
            i = self.buf.find(SYNC)
            if i < 0:
                self.skipped += max(0, len(self.buf) - 1)
                del self.buf[:-1]
                break
            if i:
                self.skipped += i
                del self.buf[:i]
            if len(self.buf) < 9:
                break
            total = struct.unpack_from(">H", self.buf, 2)[0]
            if total < 9 or (sum(self.buf[:9]) & 0xFF) != 0:
                # bad header: drop the sync bytes and search again
                self.skipped += 2
                del self.buf[:2]
                continue
            if len(self.buf) < total:
                break
            raw = bytes(self.buf[:total])
            del self.buf[:total]
            out.append((raw, parse_frame(raw)))
        return out


def version_str(v: int) -> str:
    """16-bit version -> 'major.minor.patch' (e4.e.c)."""
    return f"{(v >> 12) & 0xF}.{(v >> 8) & 0xF}.{v & 0xFF}"


def _self_test():
    # Hard-coded in p.a.X() — sent 500 ms after the RFCOMM socket connects.
    x = bytes([0xFF, 0x5A, 0, 27, 64, 0, 0, 1, 0, 64, 64, 0, 17, 0xEA, 0, 0, 5, 0, 0, 1, 0, 6, 0, 1, 0, 93, 62])
    x = bytearray(x)
    x[8] = checksum(x[0:8])
    x[26] = checksum(x[9:26])
    print("session-open  :", bytes(x).hex(" "))
    # Hard-coded in p.a.e0() — sent on destroy/disconnect.
    e = bytearray([0xFF, 0x5A, 0, 22, 64, 89, 0xEC, 1, 11, 64, 64, 0, 12, 0xEA, 1, 0, 6, 0, 0, 0, 47, 84])
    e[8] = checksum(e[0:8])
    e[21] = checksum(e[9:21])
    print("session-close :", bytes(e).hex(" "))

    f = build_frame(CMD["AUDIO_BEGIN"], tlv_param(4, 190), seq=3, ack=7)  # CO_QCELT, HIGH_BITRATE
    p = parse_frame(f)
    assert p["hdr_ok"] and p["body_ok"] and p["cmd_name"] == "AUDIO_BEGIN", p
    assert p["payload"] == bytes([0, 6, 0, 4, 0, 190])
    print("AUDIO_BEGIN   :", f.hex(" "))
    print("heartbeat     :", build_heartbeat(4, 7).hex(" "))
    assert version_str(0x0420) == "0.4.32"

    r = FrameReader()
    stream = b"\x00\x13" + f + build_heartbeat(1, 2) + f[:10]
    got = r.feed(stream)
    assert [g[1].get("cmd_name") for g in got] == ["AUDIO_BEGIN", None] and r.skipped == 2
    got = r.feed(f[10:])
    assert len(got) == 1 and got[0][0] == f
    print("self-test OK")


if __name__ == "__main__":
    _self_test()
