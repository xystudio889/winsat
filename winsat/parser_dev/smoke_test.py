"""原生库自检：不经过 C#，先用 ctypes 从 Python 侧把 ABI 跑一遍。

用法：
    python smoke_test.py

检查项：
    1. boot 能起来、version 有输出
    2. parse_xml 返回整串文本（行数 / 首行 / <error> 计数）
    3. parse_xml 遇到坏符号文件时返回 <error> 行；致命时返回 -1 + 消息
    4. export_html 的 score 指针：None = 不带分数表，double[5] = 带分数表
    5. 缓冲区不足时返回 -2，且 last_required_size() 给出完整长度
    6. shutdown 可重复调用

注意：本脚本在**真正的 Python 进程**里跑，所以 boot 里的
Py_Initialize()/Py_Finalize() 都不会执行（解释器已经存在）——这里验证的是
ABI 与协议；嵌入式解释器那条路径由 C# Demo 覆盖。
"""

from __future__ import annotations

import ctypes
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
EXAMPLES = os.path.join(os.path.dirname(HERE), "examples")
TESTDATA = os.path.join(EXAMPLES, "testdata")

# 控制台默认是 GBK，先切成 UTF-8 才能打印中文
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:  # pragma: no cover
    pass

FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" — {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


def load() -> ctypes.CDLL:
    lib = ctypes.CDLL(os.path.join(HERE, "parser.dll"))

    lib.boot.argtypes = [ctypes.c_char_p]
    lib.boot.restype = ctypes.c_int

    lib.shutdown.restype = ctypes.c_int

    lib.parse_xml.argtypes = [ctypes.c_char_p, ctypes.c_char_p,
                                  ctypes.c_char_p, ctypes.c_int]
    lib.parse_xml.restype = ctypes.c_int

    lib.export_html.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p,
                                    ctypes.POINTER(ctypes.c_double),
                                    ctypes.c_char_p, ctypes.c_int]
    lib.export_html.restype = ctypes.c_int

    lib.last_error.restype = ctypes.c_char_p
    lib.last_required_size.restype = ctypes.c_int
    lib.version.restype = ctypes.c_char_p
    return lib


def call(lib, fn, *args, size: int = 512) -> tuple[int, str]:
    """调用并处理 -2 扩容；成功返回 (0, 文本)，失败返回 (负的返回码, 错误信息)。

    扩容重试次数和 C# 侧 NativeBridge.Invoke 一致（8 次）：错误文案里可能带
    变长的东西（路径、行号），一次精准扩容不保证够。
    """
    max_attempts = 8
    buf = ctypes.create_string_buffer(size)
    rc = fn(*args, buf, size)
    attempts = 1
    while rc == -2 and attempts < max_attempts:
        need = lib.last_required_size()
        buf = ctypes.create_string_buffer(need)
        rc = fn(*args, buf, need)
        attempts += 1
    if rc < 0:
        return rc, lib.last_error().decode("utf-8", "replace")
    return 0, buf.value.decode("utf-8")


def main() -> int:
    lib = load()
    path = lambda *parts: os.path.join(*parts).encode("utf-8")  # noqa: E731
    tmp = tempfile.mkdtemp(prefix="smoke-")

    print("1) boot / version")
    rc = lib.boot(HERE.encode("utf-8"))
    check("boot", rc == 0, lib.last_error().decode("utf-8", "replace") if rc else "")
    version = lib.version().decode("utf-8", "replace")
    check("version", "CPython" in version, version)
    if rc != 0:
        return 1

    print("\n2) parse_xml（examples\\file.xml + symbols\\en-US.json）")
    rc, text = call(lib, lib.parse_xml,
                    path(EXAMPLES, "file.xml"), path(EXAMPLES, "symbols", "en-US.json"))
    lines = text.split("\n") if rc >= 0 else []
    check("返回码 >= 0", rc == 0, f"rc={rc}")
    check("890 行", len(lines) == 890, f"实际 {len(lines)} 行")
    check("首行是 <line>General", bool(lines) and lines[0] == "<line>General",
          lines[0] if lines else "(空)")
    check("没有 <error> 行", not any(line.startswith("<error>") for line in lines))

    print("\n3) parse_xml（自造样本 testdata\\sample.xml）")
    rc, sample = call(lib, lib.parse_xml,
                      path(TESTDATA, "sample.xml"), path(TESTDATA, "symbols", "en-US.json"))
    check("返回码 = 0", rc == 0, f"rc={rc}")
    check("含转置分组 #1", "Size #1,16777216 KB" in sample)
    check("空 CDATA → none 文案", "Empty Note (none),None" in sample)
    check("元素不存在 → 键,None", "Missing Element,None" in sample)

    print("\n4) parse_xml（坏符号文件）")
    rc, edge = call(lib, lib.parse_xml,
                    path(TESTDATA, "sample.xml"), path(TESTDATA, "symbols", "edge-cases.json"))
    errors = [line for line in edge.split("\n") if line.startswith("<error>")]
    check("仍是 0（错误以 <error> 行返回）", rc == 0, f"rc={rc}")
    check("至少 4 条 <error>", len(errors) >= 4, f"{len(errors)} 条")

    rc, fatal = call(lib, lib.parse_xml,
                     path(TESTDATA, "sample.xml"), path(TESTDATA, "symbols", "fatal-no-none.json"))
    check("致命错误返回 -1", rc == -1, f"rc={rc}")
    check("错误信息可读", 'missing the key "none"' in fatal, fatal)

    print("\n5) export_html（score = NULL / double[5]）")
    no_score_path = os.path.join(tmp, "no_score.html")
    rc, text = call(lib, lib.export_html,
                    path(TESTDATA, "sample.xml"), path(TESTDATA, "symbols"),
                    no_score_path.encode("utf-8"), None)
    check("不带分数表返回空串", rc == 0 and text == "", f"rc={rc} text={text!r}")
    html = open(no_score_path, encoding="utf-8").read()
    check("产物里没有 grid / expander",
          'class="grid"' not in html and 'class="expander"' not in html)

    score = (ctypes.c_double * 5)(1.0, 2.0, 3.0, 4.0, 5.0)
    score_path = os.path.join(tmp, "score.html")
    rc, text = call(lib, lib.export_html,
                    path(EXAMPLES, "file.xml"), path(EXAMPLES, "symbols"),
                    score_path.encode("utf-8"), score)
    check("带分数表返回空串", rc == 0 and text == "", f"rc={rc} text={text!r}")
    html = open(score_path, encoding="utf-8").read()
    check("TotalScore = 1.0（取最低分）", '<div class="card" id="TotalScore">1.0</div>' in html)
    check("只有最低分那行标了 lowest", html.count("sub-score lowest") == 1,
          f"实际 {html.count('sub-score lowest')}")

    rc, text = call(lib, lib.export_html,
                    path(TESTDATA, "sample.xml"), path(EXAMPLES, "no_such_dir"),
                    os.path.join(tmp, "x.html").encode("utf-8"), None)
    check("符号目录不存在 → <error> 行", rc == 0 and text.startswith("<error>"), text)

    print("\n6) 缓冲区协商")
    rc = lib.parse_xml(path(TESTDATA, "sample.xml"),
                           path(TESTDATA, "symbols", "en-US.json"),
                           ctypes.create_string_buffer(64), 64)
    check("-2 = 缓冲区不足", rc == -2, f"rc={rc}")
    check("last_required_size 给出完整长度",
          lib.last_required_size() > 64, str(lib.last_required_size()))

    print("\n7) shutdown（幂等）")
    check("第一次 shutdown", lib.shutdown() == 0)
    check("第二次 shutdown", lib.shutdown() == 0)

    print()
    if FAILURES:
        print(f"失败 {len(FAILURES)} 项：{', '.join(FAILURES)}")
        return 1
    print("全部通过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
