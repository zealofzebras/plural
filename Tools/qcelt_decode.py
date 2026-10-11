"""Decode SmartMike+ QCELT audio (captured by smartmike_client.py record) to WAV.

QCELT = stock CELT 0.7.1 bitstream wrapped in a 4-byte frame header
(reconstructed from libjni-smartdecoder.so, see Docs/Protocol/Audio.md):

    byte 0   0x9E sync
    byte 1   bits 7-6 sample rate  (0=16000 1=32000 2=44100 3=48000)
             bits 5-4 channels     (0=mono 1=stereo)
             bit 5 and bit 2 must be 0
             bits 2-0 frame size   (0=128 1=256 2=512 3=1024)
    byte 2   unused by the decoder (00/03 observed)
    byte 3   CELT bytes per frame (e.g. 190)
    then     <byte 3> bytes of CELT 0.7.1 data; several frames may follow back to back

Usage:
    python Tools/qcelt_decode.py Captures/<run>/audio.frames [out.wav]
"""
import ctypes
import os
import struct
import sys
import wave

RATES = (16000, 32000, 44100, 48000)
FRAME_SIZES = (128, 256, 512, 1024)
HERE = os.path.dirname(os.path.abspath(__file__))
DLL_PATH = os.path.join(HERE, "bin", "celt071_x64.dll")


def parse_header(b):
    if len(b) < 4 or b[0] != 0x9E or (b[1] & 0x24):
        return None
    rate = RATES[(b[1] >> 6) & 3]
    ch_code = (b[1] >> 4) & 3
    if ch_code > 1:
        return None
    return {"rate": rate, "channels": ch_code + 1, "frame_size": FRAME_SIZES[b[1] & 3],
            "flags": b[2], "nbytes": b[3]}


class Celt071:
    def __init__(self, rate, frame_size, channels, dll_path=DLL_PATH):
        self.lib = ctypes.CDLL(dll_path)
        L = self.lib
        L.celt_mode_create.restype = ctypes.c_void_p
        L.celt_mode_create.argtypes = [ctypes.c_int32, ctypes.c_int, ctypes.POINTER(ctypes.c_int)]
        L.celt_decoder_create.restype = ctypes.c_void_p
        L.celt_decoder_create.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_int)]
        L.celt_decode.restype = ctypes.c_int
        L.celt_decode.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.POINTER(ctypes.c_int16)]
        L.celt_decoder_destroy.argtypes = [ctypes.c_void_p]
        L.celt_mode_destroy.argtypes = [ctypes.c_void_p]
        err = ctypes.c_int(0)
        self.mode = L.celt_mode_create(rate, frame_size, ctypes.byref(err))
        if not self.mode or err.value:
            raise RuntimeError(f"celt_mode_create failed: {err.value}")
        self.dec = L.celt_decoder_create(self.mode, channels, ctypes.byref(err))
        if not self.dec or err.value:
            raise RuntimeError(f"celt_decoder_create failed: {err.value}")
        self.n = frame_size * channels
        self.pcm = (ctypes.c_int16 * self.n)()

    def decode(self, data):
        """Decode one frame; data=None for packet-loss concealment."""
        ret = self.lib.celt_decode(self.dec, data, len(data) if data else 0, self.pcm)
        if ret != 0:
            raise RuntimeError(f"celt_decode returned {ret}")
        return bytes(self.pcm)

    def close(self):
        self.lib.celt_decoder_destroy(self.dec)
        self.lib.celt_mode_destroy(self.mode)


def read_frames_file(path):
    """audio.frames: [u32 BE len][payload]; payload = 4-byte timestamp + QCELT frame(s)."""
    with open(path, "rb") as f:
        d = f.read()
    i = 0
    while i + 4 <= len(d):
        n = struct.unpack_from(">I", d, i)[0]
        yield d[i + 4:i + 4 + n]
        i += 4 + n


def decode_file(src, dst):
    dec = None
    hdr0 = None
    frames = errors = 0
    with wave.open(dst, "wb") as w:
        for payload in read_frames_file(src):
            body = payload[4:]  # skip the 4-byte timestamp
            while len(body) >= 4:
                h = parse_header(body)
                if h is None:
                    errors += 1
                    break
                if dec is None:
                    hdr0 = h
                    dec = Celt071(h["rate"], h["frame_size"], h["channels"])
                    w.setnchannels(h["channels"])
                    w.setsampwidth(2)
                    w.setframerate(h["rate"])
                data = body[4:4 + h["nbytes"]]
                try:
                    w.writeframes(dec.decode(data))
                    frames += 1
                except RuntimeError:
                    errors += 1
                body = body[4 + h["nbytes"]:]
    if dec:
        dec.close()
    return hdr0, frames, errors


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else os.path.splitext(src)[0] + ".wav"
    hdr, frames, errors = decode_file(src, dst)
    print(f"header: {hdr}")
    print(f"decoded {frames} frames, {errors} errors -> {dst}")


if __name__ == "__main__":
    main()
