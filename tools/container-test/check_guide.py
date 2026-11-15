#!/usr/bin/env python3
"""Check what Jellyfin's Live TV subsystem now exposes for the 9Now plugin."""
import json
import subprocess
import sys

BASE = "http://127.0.0.1:8096"


def jf(path):
    out = subprocess.run(
        ["./jfapi.sh", BASE + path], capture_output=True, text=True, check=True
    ).stdout
    return json.loads(out)


def main():
    ch = jf("/LiveTv/Channels?EnableImages=true&Limit=50")
    print(f"channels: {ch['TotalRecordCount']}")
    with_logo = 0
    for it in sorted(ch["Items"], key=lambda x: x.get("Number") or "zz"):
        has_primary = "Primary" in it.get("ImageTags", {})
        with_logo += has_primary
        prog = it.get("CurrentProgram", {})
        print(
            f"  {it.get('Number','?'):>5}  {it['Name']:<30} primary={has_primary!s:<5}"
            f"  now={prog.get('Name','')!r}"
        )
    print(f"channels with Primary image: {with_logo}")

    progs = jf("/LiveTv/Programs?EnableImages=true&Limit=10000")
    print(f"\nprograms: {progs['TotalRecordCount']}")
    with_img = sum(1 for p in progs["Items"] if "Primary" in p.get("ImageTags", {}))
    thumbs = sum(1 for p in progs["Items"] if "Thumb" in p.get("ImageTags", {}))
    named = [p for p in progs["Items"] if p.get("ChannelName")]
    print(f"  with Primary image: {with_img}, with Thumb: {thumbs}, with ChannelName: {len(named)}")
    for p in progs["Items"][:12]:
        print(
            f"  {p.get('StartDate','')[11:16]}-{p.get('EndDate','')[11:16]}"
            f"  {p.get('ChannelName',''):<16} {p.get('Name','')!r}"
            f" img={'Primary' in p.get('ImageTags',{})}"
            f" thumb={'Thumb' in p.get('ImageTags',{})}"
        )

    # sample a future block to check FAST mega-list clipping
    fast = [p for p in progs["Items"] if "Seinfeld" in (p.get("Name") or "")]
    print(f"\nSeinfeld programs: {len(fast)}")
    for p in fast[:6]:
        print(
            f"  {p.get('StartDate')} -> {p.get('EndDate')}  {p.get('Name')!r}"
            f" ep={p.get('EpisodeTitle')!r}"
        )


if __name__ == "__main__":
    sys.exit(main())
