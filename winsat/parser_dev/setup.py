"""编译 Cython 扩展（两个目标）。

    parser.pyx     ->  parser.cp314-win_amd64.pyd  ->  复制成 parser.dll
                       C ABI 桥接层，C# 通过 DllImport 调用
    xml_parser.py  ->  xml_parser.cp314-win_amd64.pyd  ->  复制成 xml_parser.pyd
                       解析核心（纯 Python 代码编译成原生模块，发布时不带 .py）

关键点（之前踩过的地方）
-----------------------
MSVC 只会导出链接器明确告知的符号：
  * `PyInit_<模块名>`  —— setuptools 自动加
  * `export_symbols` 里列出的 —— 必须自己写

所以 `cdef public int add(...)` 光写 public 不够，还必须把 "add"
写进 export_symbols，否则 DLL 导出表里只有 PyInit_*，
C# 就会抛 EntryPointNotFoundException。（MinGW 默认导出全部符号，
所以在网上抄的教程里能跑通，换成 MSVC 就不行。）

另外加 /utf-8 告诉 MSVC 源文件是 UTF-8，避免中文错误信息被按
本地代码页(GBK)解析而变成乱码。

为什么不给 xml_parser 也关掉 boundscheck / wraparound
-----------------------------------------------------
它是一份纯 Python 代码，负索引、切片、`list[-1]` 全靠 Cython 生成的
等价逻辑。关掉这些保护会改变语义（例如 `splited[-1]` 直接越界），
而这里的目的是「不让客户看到源码」，不是提速，所以一条 directive 都不动。
parser.pyx 是我们自己写的 C 风格代码，关掉才有意义。
"""

import os

from setuptools import Extension, setup

from Cython.Build import cythonize

# 需要被 C# 通过 DllImport 找到的符号（业务入口只有 parse_xml / export_html）
BRIDGE_EXPORTS = [
    "boot",
    "shutdown",
    "parse_xml",
    "export_html",
    "runtime_info",
    "last_error",
    "last_required_size",
    "version",
]

COMMON_ARGS = dict(
    extra_compile_args=["/utf-8", "/O2"],
    define_macros=[("PY_SSIZE_T_CLEAN", None)],
)

# 只想编桥接层时（build_bridge.py --core=copy，开发期要让 .py 随程序走），
# 设 PARSER_SKIP_CORE=1 跳过核心的编译，省一半时间。
SKIP_CORE = os.environ.get("PARSER_SKIP_CORE") == "1"

ext_modules = cythonize(
    [
        Extension(
            "parser",
            sources=["parser.pyx"],
            export_symbols=list(BRIDGE_EXPORTS),
            **COMMON_ARGS,
        ),
    ],
    compiler_directives={
        "language_level": "3",
        "boundscheck": False,
        "wraparound": False,
    },
)

if not SKIP_CORE:
    ext_modules += cythonize(
        [Extension("xml_parser", sources=["xml_parser.py"], **COMMON_ARGS)],
        compiler_directives={"language_level": "3"},
    )

setup(
    name="parser",
    version="1.2.0",
    ext_modules=ext_modules,
)
