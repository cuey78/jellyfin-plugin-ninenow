#!/usr/bin/env python3
"""End-to-end playback probe: ask Jellyfin for a live stream and fetch its playlist."""
import json
import subprocess
import sys

BASE = "http://127.0.0.1:8096"


def jf(path, *extra):
    out = subprocess.run(["./jfapi.sh", BASE + path, *extra],
                         capture_output=True, text=True, check=True, timeout=180).stdout
    return json.loads(out)


def main():
    name = sys.argv[1] if len(sys.argv) > 1 else "Channel 9"
    ch = jf("/LiveTv/Channels?Limit=50")
    item = next((i for i in ch["Items"] if i["Name"] == name), None)
    if not item:
        print("channel not found", [i["Name"] for i in ch["Items"]])
        return 1
    print(f"channel: {item['Name']} id={item['Id']}")

    pb = jf(
        f"/Items/{item['Id']}/PlaybackInfo",
        "-X", "POST", "-H", "Content-Type: application/json",
        "-d", json.dumps({"MaxStreamingBitrate": 8000000,
                          "EnableTranscoding": False}),
    )
    ms = pb["MediaSources"][0]
    print("media source:", ms.get("Protocol"), "path=", ms.get("Path"),
          "container=", ms.get("Container"), "tc-container=", ms.get("TranscodingContainer"))
    msid = ms["Id"]

    master_url = (f"{BASE}/Videos/{item['Id']}/master.m3u8?Static=true&MediaSourceId={msid}"
                  f"&DeviceId=testdev1&MaxStreamingBitrate=8000000")
    master = subprocess.run(["./jfapi.sh", master_url], capture_output=True, text=True, timeout=180)
    print("--- master ---")
    print(master.stdout[:400])

    variant = next((l.strip() for l in master.stdout.splitlines()
                    if l.strip().startswith("live") and ".m3u8" in l), None)
    if not variant:
        print("no variant in master")
        return 1

    base = master_url.rsplit("/", 1)[0]
    live = subprocess.run(["./jfapi.sh", f"{base}/{variant}"], capture_output=True, text=True, timeout=180)
    seg = next((l.strip() for l in live.stdout.splitlines() if l.strip().endswith(".ts")), None)
    if not seg:
        print("no segment yet:", live.stdout[:300])
        return 1

    seg_url = f"{base}/{seg}"
    segres = subprocess.run(["./jfapi.sh", seg_url, "-o", "seg.ts"], capture_output=True, text=True, timeout=180)
    print("segment:", segres.stdout.strip() or "downloaded")
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height",
         "-of", "csv", "seg.ts"], capture_output=True, text=True)
    print("ffprobe:", probe.stdout.strip() or probe.stderr[:300])
    return 0


if __name__ == "__main__":
    sys.exit(main())
