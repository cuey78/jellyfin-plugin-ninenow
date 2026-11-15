#!/usr/bin/env python3
"""Record a slice of BBC News (always airing) through Jellyfin, then cancel + inspect."""
import json
import subprocess
import sys
import time

BASE = "http://127.0.0.1:8096"


def jf(path, *extra):
    out = subprocess.run(["./jfapi.sh", BASE + path, *extra],
                         capture_output=True, text=True, check=True, timeout=180).stdout
    return json.loads(out) if out.strip().startswith(("{", "[")) else out


def main():
    progs = jf("/LiveTv/Programs?SortBy=StartDate&SortOrder=Ascending&IsAiring=true&Limit=60")
    target = next((p for p in progs["Items"] if "BBC News" in (p.get("Name") or "")), None)
    if not target:
        print("no airing BBC News program:", [p.get("Name") for p in progs["Items"]][:10])
        return 1
    print(f"target: {target['Name']!r} id={target['Id']}")

    defaults = jf(f"/LiveTv/Timers/Defaults?programId={target['Id']}")
    st = jf("/LiveTv/SeriesTimers", "-X", "POST",
            "-H", "Content-Type: application/json", "-d", json.dumps(defaults))
    timers = jf("/LiveTv/SeriesTimers")
    timer_id = next((t["Id"] for t in timers["Items"]
                     if t.get("ProgramId") == target["Id"] or "BBC" in (t.get("Name") or "")), None)
    print("series timer:", timer_id)

    time.sleep(45)
    files = []
    for _ in range(12):
        find = subprocess.run(
            ["podman", "exec", "jellyfin-ninetv", "sh", "-c",
             "find /config/data/livetv /media -type f \\( -name '*.mp4' -o -name '*.ts' -o -name '*.mkv' \\) 2>/dev/null"],
            capture_output=True, text=True)
        files = [f for f in find.stdout.splitlines() if f.strip()]
        if files:
            break
        time.sleep(10)
    print("files:", files)
    if not files:
        return 1

    for f in files:
        print(subprocess.run(["podman", "exec", "jellyfin-ninetv", "ls", "-la", f],
                             capture_output=True, text=True).stdout.strip())

    # stop the recording
    if timer_id:
        subprocess.run(["./jfapi.sh", f"{BASE}/LiveTv/SeriesTimers/{timer_id}",
                        "-X", "DELETE", "-o", "/dev/null", "-w", "cancel: %{http_code}\n"],
                       check=True, timeout=60)
    time.sleep(8)

    # ffprobe inside the container
    probe = subprocess.run(
        ["podman", "exec", "jellyfin-ninetv", "ffprobe", "-v", "error",
         "-show_entries", "format=duration:stream=codec_name,codec_type",
         "-of", "default=nw=1", files[0]], capture_output=True, text=True)
    print("probe:", probe.stdout.strip() or probe.stderr[:400])
    size = subprocess.run(["podman", "exec", "jellyfin-ninetv", "stat", "-c", "%s", files[0]],
                          capture_output=True, text=True).stdout.strip()
    print("bytes now:", size)
    return 0


if __name__ == "__main__":
    sys.exit(main())
