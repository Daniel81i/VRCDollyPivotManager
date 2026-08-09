#!/usr/bin/env python3
"""unity/Assets から .unitypackage を作る。

.unitypackage は gzip 圧縮した tar で、中身は決まった構造をしている。
Unity が無くても組み立てられるので、Unity を持たない CI でも配布物を作れる。

    <GUID>/
      asset       ファイルの中身。フォルダには無い
      asset.meta  対応する .meta をそのまま
      pathname    展開先のパス（Assets/... からの相対）

GUID は .meta の中に書かれているものをそのまま使う。ここで振り直すと
既に導入している人のプロジェクトで参照が全部切れるため、絶対に生成しない。

    使い方: python tools/build_unitypackage.py <出力先.unitypackage>
"""

from __future__ import annotations

import io
import re
import sys
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "unity" / "Assets"

GUID_PATTERN = re.compile(r"^guid:\s*([0-9a-fA-F]{32})\s*$", re.MULTILINE)


def read_guid(meta: Path) -> str:
    match = GUID_PATTERN.search(meta.read_text(encoding="utf-8"))
    if match is None:
        raise SystemExit(f"guid が読み取れません: {meta}")
    return match.group(1)


def collect() -> list[tuple[str, Path, Path | None]]:
    """(guid, meta のパス, 中身のパス or None) を集める。

    フォルダも含める。フォルダの entry が無いと Unity 側で GUID が
    振り直され、次の更新時に別フォルダとして二重に入ってしまう。
    """
    if not ASSETS.is_dir():
        raise SystemExit(f"{ASSETS} がありません")

    entries: list[tuple[str, Path, Path | None]] = []
    for meta in sorted(ASSETS.rglob("*.meta")):
        target = meta.with_suffix("")  # Foo.cs.meta -> Foo.cs / Bar.meta -> Bar
        if not target.exists():
            raise SystemExit(f"{meta.name} に対応する実体がありません: {target}")
        entries.append((read_guid(meta), meta, None if target.is_dir() else target))

    # .meta の無いファイルがあると Unity が GUID を振り直して参照が切れる
    for path in sorted(ASSETS.rglob("*")):
        if path.suffix == ".meta":
            continue
        if not path.with_name(path.name + ".meta").exists():
            raise SystemExit(f".meta がありません: {path}")

    if not entries:
        raise SystemExit(f"{ASSETS} に何もありません")
    return entries


def add(tar: tarfile.TarFile, name: str, payload: bytes) -> None:
    info = tarfile.TarInfo(name)
    info.size = len(payload)
    info.mtime = 0  # 同じ入力なら同じ出力になるよう固定する
    info.mode = 0o644
    tar.addfile(info, io.BytesIO(payload))


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    out = Path(sys.argv[1])
    out.parent.mkdir(parents=True, exist_ok=True)

    entries = collect()
    with tarfile.open(out, "w:gz") as tar:
        for guid, meta, content in entries:
            # 展開先はプロジェクトルートからの相対。区切りは常に /
            pathname = meta.with_suffix("").relative_to(ROOT / "unity").as_posix()
            add(tar, f"{guid}/pathname", pathname.encode("utf-8"))
            add(tar, f"{guid}/asset.meta", meta.read_bytes())
            if content is not None:
                add(tar, f"{guid}/asset", content.read_bytes())

    folders = sum(1 for _, _, c in entries if c is None)
    print(f"{out.name} を作成しました "
          f"（ファイル {len(entries) - folders} 件 / フォルダ {folders} 件 / "
          f"{out.stat().st_size:,} バイト）")
    for guid, meta, _ in entries:
        print(f"  {guid}  {meta.with_suffix('').relative_to(ROOT / 'unity').as_posix()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
