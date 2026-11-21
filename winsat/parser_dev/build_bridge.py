"""一键构建原生库并重命名为 C# 可直接 DllImport 的名字。

setuptools 生成的扩展文件名带 ABI 标签，例如
    parser.cp314-win_amd64.pyd      -> parser.dll   ← 让 C# 的 DllImport("parser.dll") 能加载
    xml_parser.cp314-win_amd64.pyd  -> xml_parser.pyd

为什么要有 xml_parser.pyd
-------------------------
`xml_parser.py` 是我们的核心逻辑。把它编成原生模块后，发布包里只有
机器码，客户机看不到源码（也搜不到函数名和中文文案）；Python 侧照样
能 `import xml_parser`，因为 import 机制本来就把 `.pyd` 当模块。

两种形态二选一，由 build_bridge 决定，csproj 按文件存在与否自动跟随：
    --core=compile（默认）  python\\xml_parser.pyd  → 发布不带源码
    --core=copy            python\\xml_parser.py   → 开发期改一行就能跑，但会外露源码

用法：
    python build_bridge.py                 # 增量编译两个模块（默认）
    python build_bridge.py --force         # 强制重新编译
    python build_bridge.py --core=copy      # 只编桥接层，核心走 .py（开发期）
    python build_bridge.py --core=compile   # 编完立刻 import 一次做自检
"""

from __future__ import annotations

import argparse
import glob
import os
import shutil
import subprocess
import sys
import sysconfig

HERE = os.path.dirname(os.path.abspath(__file__))

# 控制台/管道默认是 GBK，统一切 UTF-8，避免中文日志乱码；
# line_buffering 让我们的日志和 cl.exe 的输出按真实顺序交错。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
except Exception:  # pragma: no cover
    pass

# 模块名 -> 交付文件名
TARGETS = {
    "parser": "parser.dll",
    "xml_parser": "xml_parser.pyd",
}

CORE = "xml_parser"


def _ext_suffix() -> str:
    """setuptools 实际产物的后缀，Windows 上就是 .pyd（带 ABI 标签）。"""
    return sysconfig.get_config_var("EXT_SUFFIX") or ".pyd"


def _find_artifact(module: str) -> str | None:
    """按优先级找 setuptools 刚编译出来的扩展模块文件。"""
    suffix = _ext_suffix()  # 例如 .cp314-win_amd64.pyd / .cpython-314-...-linux-gnu.so
    candidates = [
        os.path.join(HERE, module + suffix),   # 标准 ABI 命名
        os.path.join(HERE, module + ".pyd"),   # 无 ABI 标签（老命名）
    ]
    # 兜底：扫同目录下 module.*.pyd / module.*.so
    candidates += sorted(glob.glob(os.path.join(HERE, module + ".*.pyd")))
    candidates += sorted(glob.glob(os.path.join(HERE, module + ".*.so")))

    return next((p for p in candidates if os.path.isfile(p)), None)


def _drop_core_artifacts() -> None:
    """--core=copy 时删掉编好的核心模块。

    留着会很坑：Python 的 import 机制优先 .pyd，于是你改 xml_parser.py
    跑起来没反应（实际加载的仍是旧 .pyd）。所以两种形态必须互斥。
    """
    for pattern in (CORE + '.pyd', CORE + '.*.pyd', CORE + '.*.so'):
        for path in glob.glob(os.path.join(HERE, pattern)):
            os.remove(path)
            print(f"[build] 删除遗留 {os.path.basename(path)}")


def _self_check_core() -> None:
    """编完立刻 import 一次：Cython 编纯 Python 偶有语义差异，这里先炸比现场炸好。

    只验证「模块能 import」「dataclass 能构造」「一个纯函数能跑」，不碰文件系统，
    所以不会受工作目录 / 环境变量影响。
    """
    code = (
        'import xml_parser;'
        'print("  core   :", xml_parser.__file__);'
        'print("  version:", xml_parser.__version__);'
        'assert xml_parser.Score(1, 2, 3, 4, 5).disk == 5;'
        'assert xml_parser._escape("a\\nb") == "a\\\\nb"'
    )
    result = subprocess.run([sys.executable, "-c", code], cwd=HERE)
    if result.returncode != 0:
        raise SystemExit("[build] xml_parser.pyd 自检失败：编出来的模块 import / 调用有问题")


def build(force: bool = False, core_mode: str = "compile") -> list[str]:
    cmd = [sys.executable, "setup.py", "build_ext", "--inplace"]
    if force:
        cmd.append("--force")

    env = dict(os.environ)
    if core_mode == "copy":
        env["PARSER_SKIP_CORE"] = "1"
        _drop_core_artifacts()
    else:
        env.pop("PARSER_SKIP_CORE", None)

    print("[build] " + " ".join(cmd) + f"   (core={core_mode})")
    subprocess.check_call(cmd, cwd=HERE, env=env)

    produced: list[str] = []
    for module, target_name in TARGETS.items():
        if module == CORE and core_mode == "copy":
            continue

        source = _find_artifact(module)
        if source is None:
            hint = "检查上面编译日志是否报错"
            if module == CORE:
                hint = ("是不是 PARSER_SKIP_CORE=1 残留在环境变量里？"
                        "或者 Cython 编 .py 失败（看上面日志）")
            raise SystemExit(f"[build] 没找到 {module} 的编译产物，{hint}")

        target = os.path.join(HERE, target_name)
        if os.path.abspath(source) != os.path.abspath(target):
            shutil.copyfile(source, target)
        produced.append(target)
        print(f"[build] {os.path.basename(source)}  ->  {target_name}")

    if core_mode != "copy":
        _self_check_core()

    return produced


def main() -> int:
    parser = argparse.ArgumentParser(description="编译 parser.dll / xml_parser.pyd")
    parser.add_argument("--force", action="store_true", help="强制重新编译")
    parser.add_argument("--core", choices=("compile", "copy"), default="compile",
                        help="compile=核心编成 .pyd（发布不带源码，默认）；"
                             "copy=核心用 .py（开发期改完就能跑，会外露源码）")
    args = parser.parse_args()

    for path in build(force=args.force, core_mode=args.core):
        size = os.path.getsize(path)
        print(f"  {os.path.basename(path):22s} {size:>9,} bytes")

    if args.core == "copy":
        print()
        print("[build] 注意：core=copy 是开发形态，xml_parser.py 会随程序分发。")
        print("[build]       正式发布前必须重跑：python python\\build_bridge.py --force")
    else:
        print()
        print("[build] 核心已编译为 xml_parser.pyd —— 发布包里不会出现 xml_parser.py。")
        print("[build] 改了解析逻辑要重跑本脚本（否则跑的还是旧 .pyd）。")

    print("[build] 完成。现在可以 dotnet build / dotnet run。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
