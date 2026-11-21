"""列出打包要复制的 site-packages 顶层条目（含传递依赖）。

用法：
    python deps_closure.py lxml beautifulsoup4

输出（一行一个，相对 site-packages）：
    bs4
    beautifulsoup4-4.15.0.dist-info
    lxml
    lxml-6.1.2.dist-info
    soupsieve
    soupsieve-2.9.2.dist-info
    typing_extensions.py
    typing_extensions-4.16.0.dist-info

为什么要自动算：手写依赖清单一定会漏。bs4 4.15 起依赖 typing_extensions，
第一次打包就是漏了它，客户机上 import xml_parser 直接 ModuleNotFoundError。
这里用 importlib.metadata 里的 Requires-Dist 递归展开，并跳过 extras
（`cchardet; extra == "cchardet"` 这种不是必需依赖）。

只依赖标准库；有 packaging 时用它正确解析版本约束与 marker，
没有就退化成最简单的按名字切分。
"""

from __future__ import annotations

import importlib.metadata as md
import sys

try:  # packaging 是 pip / setuptools 的依赖，通常都在
    from packaging.requirements import Requirement

    _HAS_PACKAGING = True
except Exception:  # pragma: no cover
    _HAS_PACKAGING = False

_SKIP_ENTRIES = {'__pycache__'}
# 顶层条目里只取这些（.dist-info 也算，方便 importlib.metadata 查版本）
_SEPARATORS = '<>=!~[; (,\t'


def _normalize(name: str) -> str:
    return name.lower().replace('_', '-').replace('.', '-')


def _requirement_names(dist: md.Distribution) -> list[str]:
    """该发行版声明的运行时依赖（已跳过 extras 与不适用的 marker）。"""
    names: list[str] = []

    for raw in dist.requires or []:
        if _HAS_PACKAGING:
            try:
                requirement = Requirement(raw)
            except Exception:
                continue

            if requirement.marker is not None:
                try:
                    # 没请求 extras 时 `extra == "xxx"` 会得到 False / 未定义，
                    # 两种情况都按「不需要」处理
                    if not requirement.marker.evaluate():
                        continue
                except Exception:
                    continue

            names.append(requirement.name)
            continue

        # 退化路径：丢掉 `;` 之后的条件，再按分隔符切出包名
        head = raw.split(';', 1)[0].strip()
        if not head:
            continue
        name = head
        for separator in _SEPARATORS:
            name = name.split(separator, 1)[0]
        name = name.strip()
        if name:
            names.append(name)

    return names


def closure(roots: list[str]) -> list[md.Distribution]:
    """把 roots 及其传递依赖展开成一串 Distribution（按发现顺序）。"""
    seen: set[str] = set()
    found: list[md.Distribution] = []
    queue: list[str] = list(roots)

    while queue:
        name = queue.pop(0)
        key = _normalize(name)
        if key in seen:
            continue
        seen.add(key)

        try:
            dist = md.distribution(name)
        except md.PackageNotFoundError:
            print(f'# 未安装，跳过：{name}', file=sys.stderr)
            continue

        found.append(dist)
        queue.extend(_requirement_names(dist))

    return found


def top_level_entries(dist: md.Distribution) -> list[str]:
    """该发行版在 site-packages 下占用的顶层条目（目录或 .py / .pyd 文件）。"""
    entries: set[str] = set()

    for file in dist.files or []:
        parts = file.parts
        if not parts:
            continue
        entry = parts[0] if len(parts) > 1 else parts[0]
        if entry in _SKIP_ENTRIES or entry.endswith('.pth'):
            continue
        entries.add(entry)

    return sorted(entries)


def main(argv: list[str]) -> int:
    roots = argv[1:] or ['lxml', 'beautifulsoup4']

    if not _HAS_PACKAGING:
        print('# 没有 packaging，按名字粗略解析依赖', file=sys.stderr)

    for dist in closure(roots):
        for entry in top_level_entries(dist):
            print(entry)

    return 0


if __name__ == '__main__':
    raise SystemExit(main(sys.argv))
