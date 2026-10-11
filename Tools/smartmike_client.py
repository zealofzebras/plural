"""SmartMike+ test client for Windows (also works on Linux/BlueZ with --channel).

Talks the Sabinetek Swiss protocol over the private SPP (RFCOMM) channel.
Protocol reference: Docs/Protocol/*.md, framing in swiss_protocol.py.

Examples (pair the mic in Windows Bluetooth settings first):
    python Tools/smartmike_client.py list
    python Tools/smartmike_client.py probe  12:FC:08:xx:xx:xx
    python Tools/smartmike_client.py record 12:FC:08:xx:xx:xx --seconds 10 --bitrate 190
    python Tools/smartmike_client.py set    12:FC:08:xx:xx:xx mic_gain=60 monitor=40
    python Tools/smartmike_client.py scan-channels 12:FC:08:xx:xx:xx

Every run writes a capture folder under Captures/ with:
    rx.bin       all raw bytes received
    tx.bin       all raw bytes sent
    log.txt      decoded frame log
    audio.frames AUDIO_STREAM(_VER) payloads as [u32 BE length][payload] records (record only)
"""
import argparse
import ctypes
import datetime
import os
import socket
import struct
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import swiss_protocol as sp  # noqa: E402

SPP_UUID = "00001101-0000-1000-8000-00805F9B34FB"
CAPTURE_ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Captures")

# Session-control packets hard-coded in the app (p.a.X / p.a.e0); checksums already filled in.
SESSION_OPEN = bytes.fromhex("ff5a001b400000014b404000 11ea0000050000010006000100 5d1b".replace(" ", ""))
SESSION_CLOSE = bytes.fromhex("ff5a00164059ec010b4040000cea010006000000 2f54".replace(" ", ""))

BITRATES = {"high": 190, "medium": 170, "low": 140}

# name -> (command, sub-type)  — see Docs/Protocol/Commands.md
SETTINGS = {
    "mic_gain": ("MIC_PARAM", 2), "mic_gain_l": ("MIC_PARAM", 0), "mic_gain_r": ("MIC_PARAM", 1),
    "mic_preset_eq": ("MIC_PARAM", 3), "ext_mic_autosel": ("MIC_PARAM", 50),
    "spk_volume": ("SPK_PARAM", 2), "spk_volume_l": ("SPK_PARAM", 0), "spk_volume_r": ("SPK_PARAM", 1),
    "monitor": ("MONITOR", 2), "monitor_l": ("MONITOR", 0), "monitor_r": ("MONITOR", 1),
    "monitor_mute": ("MONITOR", 3),
    "agc": ("AGC_PARAM", 0), "ans": ("ANS_PARAM", 0), "ans_level": ("ANS_PARAM", 1),
    "reverb": ("EFFECT_PARAM", 1), "music_mixer": ("EFFECT_PARAM", 2), "dsp_scene": ("EFFECT_PARAM", 5),
    "sound_effect": ("EFFECT_PARAM", 6),
    "led": ("EXTEND_PARAMTERS", 0), "low_cut": ("EXTEND_PARAMTERS", 3),
    "app_workstate": ("EXTEND_PARAMTERS", 6), "voice_prompt": ("EXTEND_PARAMTERS", 10),
}

DEVICE_TYPES = {64: "M_MIKE (SmartMike+)", 66: "M_MIKE_SLV (SmartMikeSilver)", 67: "UAT_SILVER",
                68: "HBM_SILVER", 69: "M_MIKE_SLV_1 (Hohem)", 70: "M_MIKE_SLV_2 (Saramonic-BTW)",
                72: "M_MIKE_V2 (SmartMike Classic)", 73: "M_MIKE_LOUD (OnyxGO)"}
BUTTONS = {0: "POWER", 1: "RECORD", 2: "PLAY_PAUSE", 3: "VOL_UP", 4: "VOL_DOWN", 5: "FORWARD",
           6: "BACKWARD", 7: "JACK_3PIN_INSERT", 8: "JACK_CTIA_INSERT", 9: "JACK_OMTP_INSERT",
           10: "JACK_UNPLUGGED", 11: "EXT_KMIKE_PLUGGED", 12: "EXT_KMIKE_UNPLUGGED",
           13: "BUTTON_PRESSED", 14: "BUTTON_RELEASE", 15: "MFB_SINGLE", 16: "MFB_DOUBLE",
           17: "MFB_LONG", 18: "MFB_VLONG", 19: "MFB_VVLONG", 64: "LINEIN_PLUGGED", 65: "LINEIN_UNPLUGGED"}
STATUS_TYPES = {0: "STREAM_STATUS", 1: "RSSI", 2: "BAT_LEVEL", 32: "A2DP_STATUS", 48: "NTP_LOCAL_TIME"}


# --------------------------------------------------------------------------- Windows helpers

def list_paired():
    """Paired Bluetooth Classic devices from the Windows registry."""
    if os.name != "nt":
        print("list is Windows-only; on Linux use: bluetoothctl devices Paired")
        return
    import winreg
    key = r"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices"
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, key) as k:
        i = 0
        while True:
            try:
                sub = winreg.EnumKey(k, i)
            except OSError:
                break
            i += 1
            try:
                with winreg.OpenKey(k, sub) as d:
                    name = winreg.QueryValueEx(d, "Name")[0]
                    name = bytes(name).rstrip(b"\0").decode("utf-8", "replace")
            except OSError:
                name = "?"
            mac = ":".join(sub[j:j + 2] for j in range(0, 12, 2)).upper()
            hint = "  <-- SmartMike MAC prefix" if mac.startswith(("12:FC:08", "28:36:38")) else ""
            print(f"{mac}  {name}{hint}")


class _GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_uint32), ("Data2", ctypes.c_uint16),
                ("Data3", ctypes.c_uint16), ("Data4", ctypes.c_uint8 * 8)]


class _SOCKADDR_BTH(ctypes.Structure):
    _pack_ = 1
    _fields_ = [("addressFamily", ctypes.c_uint16), ("btAddr", ctypes.c_uint64),
                ("serviceClassId", _GUID), ("port", ctypes.c_uint32)]


def _connect_by_uuid_windows(sock, mac, uuid):
    """Connect with port=0 + service UUID: Windows resolves the RFCOMM channel via SDP."""
    import uuid as uuidlib
    u = uuidlib.UUID(uuid)
    g = _GUID(u.time_low, u.time_mid, u.time_hi_version,
              (ctypes.c_uint8 * 8)(*u.bytes[8:]))
    addr = _SOCKADDR_BTH(32, int(mac.replace(":", ""), 16), g, 0)  # AF_BTH = 32
    ws2 = ctypes.WinDLL("ws2_32")
    ws2.connect.argtypes = [ctypes.c_size_t, ctypes.c_void_p, ctypes.c_int]
    if ws2.connect(sock.fileno(), ctypes.byref(addr), ctypes.sizeof(addr)) != 0:
        raise OSError(f"connect by UUID failed, WSAGetLastError={ws2.WSAGetLastError()}")


def open_rfcomm(mac, channel=None, timeout=10.0):
    s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM)
    if channel:
        s.settimeout(timeout)
        s.connect((mac, channel))
    elif os.name == "nt":
        s.setblocking(True)  # raw Winsock connect must not see a non-blocking socket
        _connect_by_uuid_windows(s, mac, SPP_UUID)
    else:
        raise SystemExit("On Linux pass --channel (find it with: sdptool browse <MAC>)")
    s.settimeout(None)
    return s


# --------------------------------------------------------------------------- session

class Session:
    def __init__(self, mac, channel, capture_dir, verbose_audio=False):
        self.mac = mac
        self.channel = channel
        self.dir = capture_dir
        os.makedirs(capture_dir, exist_ok=True)
        self.rx_f = open(os.path.join(capture_dir, "rx.bin"), "wb")
        self.tx_f = open(os.path.join(capture_dir, "tx.bin"), "wb")
        self.log_f = open(os.path.join(capture_dir, "log.txt"), "w", encoding="utf-8")
        self.audio_f = None
        self.verbose_audio = verbose_audio
        self.sock = None
        self.reader = sp.FrameReader()
        self.seq = 0
        self.ack = 0
        self.t0 = time.monotonic()
        self.lock = threading.Lock()
        self.running = False
        self.version = None
        self.version_evt = threading.Event()
        self.sync_round = 0
        self.synced = threading.Event()
        self.audio_frames = 0
        self.audio_bytes = 0
        self.first_audio = None

    # ---- utils
    def ms(self):
        return int((time.monotonic() - self.t0) * 1000) & 0xFFFFFFFF

    def log(self, msg):
        line = f"[{time.monotonic() - self.t0:8.3f}] {msg}"
        print(line)
        self.log_f.write(line + "\n")
        self.log_f.flush()

    # ---- io
    def connect(self):
        self.log(f"connecting to {self.mac} " + (f"channel {self.channel}" if self.channel else "via SPP UUID"))
        self.sock = open_rfcomm(self.mac, self.channel)
        self.t0 = time.monotonic()
        self.running = True
        threading.Thread(target=self._rx_loop, daemon=True).start()
        self.log("RFCOMM connected")

    def close(self):
        if self.sock:
            try:
                self.send_raw(SESSION_CLOSE, "SESSION_CLOSE")
                time.sleep(0.2)
            except OSError:
                pass
            self.running = False
            try:
                self.sock.close()
            except OSError:
                pass
        for f in (self.rx_f, self.tx_f, self.log_f, self.audio_f):
            if f:
                f.close()

    def send_raw(self, data, label):
        with self.lock:
            self.sock.sendall(data)
            self.tx_f.write(data)
            self.tx_f.flush()
        self.log(f"TX {label}: {data.hex(' ')}")

    def _next_seq(self):
        s = self.seq
        self.seq = (self.seq + 1) & 0xFF
        return s

    def send_cmd(self, cmd_name, payload, label=None):
        f = sp.build_frame(sp.CMD[cmd_name], payload, seq=self._next_seq(), ack=self.ack)
        self.send_raw(f, label or cmd_name)

    def send_param(self, cmd_name, sub, value, label=None):
        self.send_cmd(cmd_name, sp.tlv_param(sub, value), label or f"{cmd_name} sub={sub} val={value}")

    def send_heartbeat(self):
        f = sp.build_heartbeat(self._next_seq(), self.ack)
        with self.lock:
            self.sock.sendall(f)
            self.tx_f.write(f)

    def _rx_loop(self):
        while self.running:
            try:
                data = self.sock.recv(4096)
            except OSError as e:
                if self.running:
                    self.log(f"recv error: {e}")
                break
            if not data:
                self.log("connection closed by device")
                break
            self.rx_f.write(data)
            self.rx_f.flush()
            skipped = self.reader.skipped
            for raw, fr in self.reader.feed(data):
                self.ack = fr["seq"]
                self._handle(raw, fr)
            if self.reader.skipped != skipped:
                self.log(f"resync: skipped {self.reader.skipped - skipped} bytes")
        self.running = False

    # ---- protocol handling
    def _handle(self, raw, fr):
        name = fr.get("cmd_name")
        if name is None:
            self.log(f"RX type={fr['type']} len={fr['total_len']} (no SS sub-header): {raw.hex(' ')}")
            return
        if not fr.get("body_ok", True):
            self.log(f"RX {name} BAD CHECKSUM: {raw.hex(' ')}")
        p = fr["payload"]
        if name in ("AUDIO_STREAM_VER", "AUDIO_STREAM"):
            self._on_audio(name, p)
            return
        self.log(f"RX {name} seq={fr['seq']} ack={fr['ack']} payload={p.hex(' ')}")
        if name == "CUR_VERSION":
            self._on_version(raw)
        elif name == "NTP_DELAY_REQ":
            self._on_ntp_delay_req()
        elif name == "STATUS_INFO":
            self._on_status(p)
        elif name == "BUTTON_PRESSED" and len(raw) >= 21:
            code = struct.unpack_from(">H", raw, 19)[0]
            self.log(f"   button/jack event: {BUTTONS.get(code, code)}")
        elif name in ("SN_AUTH_CHALLENGE", "SN_AUTH_REPORT") and len(raw) >= 45:
            self.log(f"   serial={raw[21:37].hex()} machine_code={raw[37:45].hex()}")

    def _on_version(self, raw):
        if len(raw) != 60:
            self.log(f"   CUR_VERSION unexpected length {len(raw)} (app expects 60)")
        v = lambda off: struct.unpack_from(">H", raw, off)[0]  # noqa: E731
        try:
            info = {
                "device_type": v(21), "protocol": sp.version_str(v(27)), "firmware": sp.version_str(v(33)),
                "hardware": sp.version_str(v(39)), "codec": sp.version_str(v(45)),
                "manufacture": sp.version_str(v(51)), "licensed_for": sp.version_str(v(57)),
            }
        except struct.error:
            return
        info["device_name"] = DEVICE_TYPES.get(info["device_type"], "?")
        self.version = info
        self.log("   VERSION: " + ", ".join(f"{k}={val}" for k, val in info.items()))
        self.version_evt.set()

    def _on_status(self, p):
        i = 0
        while i + 4 <= len(p):
            ln, typ = struct.unpack_from(">HH", p, i)
            if ln < 4:
                break
            val = p[i + 4:i + ln]
            desc = STATUS_TYPES.get(typ, typ)
            if typ == 2 and val:
                mv = int.from_bytes(val, "big")
                pct = 0 if mv <= 3000 else min(100, (mv - 3000) * 100 // 920)
                self.log(f"   status {desc}: {mv} mV (~{pct}%)")
            else:
                self.log(f"   status {desc}: {val.hex()}")
            i += ln

    def _on_audio(self, name, p):
        if self.first_audio is None:
            self.first_audio = time.monotonic()
            self.log(f"RX first {name}: len={len(p)} head={p[:16].hex(' ')}")
        self.audio_frames += 1
        self.audio_bytes += len(p)
        if self.audio_f:
            self.audio_f.write(struct.pack(">I", len(p)) + p)
        if self.verbose_audio:
            self.log(f"RX {name} len={len(p)} ts={int.from_bytes(p[:4], 'big')} head={p[4:12].hex(' ')}")

    # ---- clock sync (p.a.Z): 4x NTP_SYNC, 3x NTP_DELAY_RESP, 1x NTP_DELAY_RESP_FINAL
    def start_sync(self):
        self.sync_round = 1
        self.send_cmd("NTP_SYNC", struct.pack(">HI", 6, self.ms()), "NTP_SYNC")

    def _on_ntp_delay_req(self):
        t = self.ms()
        if self.sync_round == 0:
            return
        if self.sync_round < 4:
            self.send_cmd("NTP_DELAY_RESP", struct.pack(">HI", 6, t), "NTP_DELAY_RESP")
            self.sync_round += 1
            time.sleep(0.001)
            self.send_cmd("NTP_SYNC", struct.pack(">HI", 6, self.ms()), "NTP_SYNC")
        else:
            self.send_cmd("NTP_DELAY_RESP_FINAL", struct.pack(">HI", 6, t), "NTP_DELAY_RESP_FINAL")
            self.sync_round = 0
            self.synced.set()

    # ---- high-level steps
    def handshake(self, version_timeout=3.0, sync=True, session_open=True):
        time.sleep(0.5)  # app waits 500 ms after connect
        if session_open:
            self.send_raw(SESSION_OPEN, "SESSION_OPEN")
        if not self.version_evt.wait(version_timeout):
            self.log("no CUR_VERSION received (the app would disconnect after 2.5 s)")
            return False
        if sync:
            self.start_sync()
            if not self.synced.wait(1.5):
                self.log("clock sync did not complete within 1.5 s (continuing anyway)")
            else:
                self.log("clock sync complete")
        return True


# --------------------------------------------------------------------------- commands

def _capture_dir(tag, mac):
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    return os.path.join(CAPTURE_ROOT, f"{stamp}_{tag}_{mac.replace(':', '')}")


def cmd_probe(a):
    s = Session(a.mac, a.channel, _capture_dir("probe", a.mac))
    try:
        s.connect()
        s.handshake(sync=not a.no_sync, session_open=not a.no_open)
        s.log(f"listening for {a.seconds} s ...")
        time.sleep(a.seconds)
    finally:
        s.close()
        print(f"capture: {s.dir}")


def cmd_record(a):
    s = Session(a.mac, a.channel, _capture_dir("record", a.mac), verbose_audio=a.verbose)
    s.audio_f = open(os.path.join(s.dir, "audio.frames"), "wb")
    bitrate = BITRATES.get(a.bitrate, None) or int(a.bitrate)
    codec_sub = 1 if bitrate == 0 else 4  # CODEC_PLUGIN_CO_SBC / CODEC_PLUGIN_CO_QCELT
    try:
        s.connect()
        s.handshake(sync=not a.no_sync, session_open=not a.no_open)
        apply_settings(s, a.set or [])
        s.send_param("AUDIO_BEGIN", codec_sub, bitrate, f"AUDIO_BEGIN codec_sub={codec_sub} bitrate={bitrate}")
        end = time.monotonic() + a.seconds
        last = 0.0
        while time.monotonic() < end and s.running:
            time.sleep(0.5)
            s.send_heartbeat()
            if time.monotonic() - last >= 2:
                last = time.monotonic()
                s.log(f"audio: {s.audio_frames} frames, {s.audio_bytes} bytes")
        s.send_param("AUDIO_END", codec_sub, bitrate, "AUDIO_END")
        time.sleep(0.3)
    finally:
        s.close()
        if s.first_audio:
            dur = time.monotonic() - s.first_audio
            print(f"received {s.audio_frames} audio frames, {s.audio_bytes} bytes "
                  f"(~{s.audio_bytes * 8 / max(dur, 0.001) / 1000:.0f} kbit/s)")
        print(f"capture: {s.dir}")


def apply_settings(s, items):
    for item in items:
        name, _, val = item.partition("=")
        if name not in SETTINGS:
            s.log(f"unknown setting {name}; known: {', '.join(sorted(SETTINGS))}")
            continue
        cmd, sub = SETTINGS[name]
        s.send_param(cmd, sub, int(val), f"{name}={val}")
        time.sleep(0.1)


def cmd_set(a):
    s = Session(a.mac, a.channel, _capture_dir("set", a.mac))
    try:
        s.connect()
        s.handshake(sync=not a.no_sync, session_open=not a.no_open)
        apply_settings(s, a.settings)
        time.sleep(a.seconds)
    finally:
        s.close()
        print(f"capture: {s.dir}")


def cmd_scan(a):
    found = []
    for ch in range(1, 31):
        try:
            sock = open_rfcomm(a.mac, ch, timeout=5)
        except OSError as e:
            print(f"channel {ch:2d}: -  ({e.__class__.__name__})")
            continue
        sock.settimeout(1.5)
        try:
            sock.sendall(SESSION_OPEN)
            data = sock.recv(256)
        except OSError:
            data = b""
        sock.close()
        tag = "SWISS (FF 5A reply)" if data.startswith(sp.SYNC) else (data[:16].hex(" ") or "open, no reply")
        print(f"channel {ch:2d}: OPEN  {tag}")
        found.append(ch)
        time.sleep(0.5)
    print("open channels:", found)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list", help="list paired Bluetooth devices (Windows)")

    def common(p, seconds):
        p.add_argument("mac", help="device address, e.g. 12:FC:08:12:34:56")
        p.add_argument("--channel", type=int, help="RFCOMM channel (default: resolve SPP UUID via SDP)")
        p.add_argument("--seconds", type=float, default=seconds)
        p.add_argument("--no-sync", action="store_true", help="skip the clock sync step")
        p.add_argument("--no-open", action="store_true", help="skip the session-open packet")

    common(sub.add_parser("probe", help="connect, handshake, log everything"), 10)
    r = sub.add_parser("record", help="probe + AUDIO_BEGIN, save audio frames")
    common(r, 10)
    r.add_argument("--bitrate", default="high", help="high/medium/low or a number; 0 = request SBC")
    r.add_argument("--verbose", action="store_true", help="log every audio frame")
    r.add_argument("--set", action="append", metavar="NAME=VALUE", help="apply a setting before AUDIO_BEGIN (repeatable)")
    st = sub.add_parser("set", help="change settings, e.g. mic_gain=60 monitor=40")
    common(st, 2)
    st.add_argument("settings", nargs="+")
    sc = sub.add_parser("scan-channels", help="try RFCOMM channels 1-30")
    sc.add_argument("mac")

    a = ap.parse_args()
    if getattr(a, "mac", None):
        a.mac = a.mac.upper().replace("-", ":")
    {"list": lambda _: list_paired(), "probe": cmd_probe, "record": cmd_record,
     "set": cmd_set, "scan-channels": cmd_scan}[a.cmd](a)


if __name__ == "__main__":
    main()
