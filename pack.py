# LocaleCapture 폴더를 Unity 가 만드는 것과 같은 구조의 LocaleCapture.unitypackage 로 묶는다(에디터 없이).
#   python pack.py
# 구조: guid/asset, guid/asset.meta, guid/pathname 을 GNU tar + gzip 으로. 폴더도 엔트리로 넣는다(없으면 Import 창이 죽는다).
import io, os, re, tarfile, time

ROOT, PREFIX, OUT = "LocaleCapture", "Assets/CookApps/LocaleCapture", "LocaleCapture.unitypackage"


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

now = int(time.time())


def info(name, size=0, isdir=False):
    ti = tarfile.TarInfo(name)
    ti.type = tarfile.DIRTYPE if isdir else tarfile.REGTYPE
    ti.mode, ti.uid, ti.gid, ti.uname, ti.gname, ti.mtime, ti.size = 0o777, 0, 0, "", "", now, size
    return ti


with tarfile.open(OUT, "w:gz", format=tarfile.GNU_FORMAT) as tar:
    for guid, asset, meta, path in entries:
        tar.addfile(info(guid + "/", isdir=True))
        if asset:
            with open(asset, "rb") as f:
                tar.addfile(info(f"{guid}/asset", os.path.getsize(asset)), f)
        with open(meta, "rb") as f:
            tar.addfile(info(f"{guid}/asset.meta", os.path.getsize(meta)), f)
        data = path.encode("utf-8")
        tar.addfile(info(f"{guid}/pathname", len(data)), io.BytesIO(data))
print(f"{OUT}: {len(entries)} entries, {os.path.getsize(OUT) // 1048576} MB")
