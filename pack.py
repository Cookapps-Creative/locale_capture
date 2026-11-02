# LocaleCapture 폴더를 Unity 가 만드는 것과 같은 구조의 LocaleCapture.unitypackage 로 묶는다(에디터 없이).
#   python pack.py
# 구조: guid/asset, guid/asset.meta, guid/pathname 을 GNU tar + gzip 으로, guid 순. 폴더도 엔트리로 넣는다.
# tar 끝은 0 블록 두 개(1024바이트)로만 — tarfile 의 10240 레코드 패딩이 붙으면 Unity Import 창이 NullReferenceException 으로 죽는다.
import gzip, io, os, re, tarfile, time

import sys
ROOT, PREFIX = "LocaleCapture", "Assets/CookApps/LocaleCapture"
OUT = sys.argv[1] if len(sys.argv) > 1 else "LocaleCapture.unitypackage"


def guid_of(meta):
    return re.search(r"guid:\s*([0-9a-f]{32})", open(meta, encoding="utf-8").read()).group(1)


entries = [(guid_of(ROOT + ".meta"), None, ROOT + ".meta", PREFIX)]
for d, dirs, files in os.walk(ROOT):
    for f in files:
        if f.endswith(".meta"):
            continue
        full = os.path.join(d, f)
        entries.append((guid_of(full + ".meta"), full, full + ".meta", PREFIX + "/" + os.path.relpath(full, ROOT).replace("\\", "/")))
    for s in dirs:
        full = os.path.join(d, s)
        entries.append((guid_of(full + ".meta"), None, full + ".meta", PREFIX + "/" + os.path.relpath(full, ROOT).replace("\\", "/")))

entries.sort(key=lambda e: e[0])   # Unity 처럼 guid 순
now = int(time.time())


def info(name, size=0, isdir=False):
    ti = tarfile.TarInfo(name)
    ti.type = tarfile.DIRTYPE if isdir else tarfile.REGTYPE
    ti.mode, ti.uid, ti.gid, ti.uname, ti.gname, ti.mtime, ti.size = 0o777, 0, 0, "", "", now, size
    return ti


# Unity 의 tar 는 레코드 패딩 없이 0 블록 두 개(1024바이트)로 끝난다 — tarfile 의 10240 패딩은 떼어 낸다.
buf = io.BytesIO()
with tarfile.open(fileobj=buf, mode="w", format=tarfile.GNU_FORMAT) as tar:
    for guid, asset, meta, path in entries:
        tar.addfile(info(guid + "/", isdir=True))
        if asset:
            with open(asset, "rb") as f:
                tar.addfile(info(f"{guid}/asset", os.path.getsize(asset)), f)
        with open(meta, "rb") as f:
            tar.addfile(info(f"{guid}/asset.meta", os.path.getsize(meta)), f)
        data = path.encode("utf-8")
        tar.addfile(info(f"{guid}/pathname", len(data)), io.BytesIO(data))
    body_end = tar.offset   # 마지막 엔트리 끝(0 블록 전)
raw = buf.getvalue()[:body_end] + bytes(1024)
gz = bytearray(gzip.compress(raw, compresslevel=6, mtime=now))
gz[8], gz[9] = 4, 0   # XFL·OS 를 Unity 와 같게
with open(OUT, "wb") as f:
    f.write(gz)
print(f"{OUT}: {len(entries)} entries, {os.path.getsize(OUT) // 1048576} MB")
