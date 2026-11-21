# cython: language_level=3
"""C ABI 桥接层：把 xml_parser 的 parse_xml / export_html 暴露给 C#。

导出的 C 接口
-------------
    int  boot(char* script_dir)                 启动解释器并加载 xml_parser
    int  shutdown()                             释放解释器（进程级单例，幂等）
    int  parse_xml(char* source, char* symbol,
                       char* out_buf, int out_cap)
    int  export_html(char* source, char* symbol, char* output_path,
                         double* score,              NULL = 不导出分数表
                         char* out_buf, int out_cap)  score 依次是 cpu/ram/graphics/d3/disk
    int  runtime_info(char* out_buf, int out_cap)   运行环境自查（sys.prefix / 依赖路径）
    const char* last_error()
    int  last_required_size()
    const char* version()

返回值约定（三个 int 回调用同一套）
    >= 0  写入缓冲区的字节数（不含结尾 NUL）
    -1    出错，原因见 last_error()（UTF-8）
    -2    缓冲区不足，完整长度见 last_required_size()

三条硬规矩（都是踩过的坑）
--------------------------
1. **导出函数内部只许写纯 C API**，不许出现 `import xxx`、`obj.attr`、`x.decode()`
   这类 Cython 级 Python 语法。

   原因：这个 .pyd 是被 C# 用 LoadLibrary 加载的，CPython 从来没有执行过
   PyInit_parser / __pyx_pymod_exec_parser —— 也就是说 Cython 缓存的
   那些静态名字常量（__pyx_n_s_*，interned strings）全都没有初始化。一旦代码里
   出现 Cython 级的 import / 属性访问，就会拿这些 NULL 去查名字，直接崩成
   0xC0000005（没有 traceback，只能靠二分排查，极难定位）。

   所以下面全部用 PyImport_ImportModule / PyObject_GetAttrString /
   PyObject_CallObject 这些 C 函数，名字都是 C 字符串字面量，不依赖模块初始化。

2. 导出函数一律 `cdef public`，并且在 setup.py 的 `export_symbols` 里显式列出
   —— MSVC 不会自动导出未声明的符号（只有 MinGW 才会），少了这一步 C# 会抛
   EntryPointNotFoundException。

3. 字符串返回值一律用「调用方提供缓冲区」的 ABI，绝不把 Python 临时对象的
   内存地址递给 C#（函数返回后会被释放，变成野指针）。

注：字符串参数用 `char*` 而不是 `const char*`，只是为了写法简单；语义上仍是只读入参。
"""

from libc.stdio cimport snprintf
from libc.string cimport memcpy, strlen

cdef extern from "Python.h":
    ctypedef struct PyObject
    ctypedef struct PyThreadState

    int Py_IsInitialized()
    void Py_Initialize()
    void Py_Finalize()
    const char* Py_GetVersion()

    PyObject* PySys_GetObject(const char* name)
    int PyList_Insert(PyObject* list, Py_ssize_t where, PyObject* item)

    PyObject* PyUnicode_DecodeUTF8(const char* s, Py_ssize_t size, const char* errors)
    const char* PyUnicode_AsUTF8AndSize(PyObject* unicode, Py_ssize_t* size)

    PyObject* PyImport_ImportModule(const char* name)
    PyObject* PyObject_GetAttrString(PyObject* obj, const char* attr)
    PyObject* PyTuple_New(Py_ssize_t size)
    int PyTuple_SetItem(PyObject* tuple, Py_ssize_t pos, PyObject* item)
    PyObject* PyObject_CallObject(PyObject* callable, PyObject* args)
    PyObject* PyFloat_FromDouble(double value)
    PyObject* PyObject_Str(PyObject* obj)

    void PyErr_Fetch(PyObject** ptype, PyObject** pvalue, PyObject** ptraceback)
    void PyErr_Restore(PyObject* ptype, PyObject* pvalue, PyObject* ptraceback)
    void PyErr_Clear()
    void PyErr_Print()

    void Py_DecRef(PyObject* obj)

    PyThreadState* PyEval_SaveThread()
    void PyEval_RestoreThread(PyThreadState* state)

    ctypedef int PyGILState_STATE
    PyGILState_STATE PyGILState_Ensure()
    void PyGILState_Release(PyGILState_STATE state)


DEF ERR_CAP = 512
DEF SCORE_FIELDS = 5

cdef char _errbuf[ERR_CAP]
cdef char _version_buf[192]
cdef int _booted = 0
cdef int _owns_interp = 0      # 解释器是不是我们自己创建的
cdef int _required = 0         # 上次调用所需的完整字节数
cdef PyThreadState* _tstate = NULL

# xml_parser 里的入口（启动时取一次，之后直接用）
cdef PyObject* _fn_parse_xml = NULL
cdef PyObject* _fn_export_html = NULL
cdef PyObject* _fn_runtime_info = NULL
cdef PyObject* _score_type = NULL


# --------------------------------------------------------------------------
# 小工具（全部纯 C API）
# --------------------------------------------------------------------------
cdef PyObject* _to_unicode(const char* text):
    """UTF-8 C 字符串 -> 新引用的 python str；失败返回 NULL。"""
    if text == NULL:
        return NULL
    return PyUnicode_DecodeUTF8(text, <Py_ssize_t>strlen(text), "strict")


cdef int _capture_error(const char* context):
    """把当前 Python 异常写进 last_error()，并打印一次 traceback 到 stderr。"""
    cdef PyObject* ptype = NULL
    cdef PyObject* pvalue = NULL
    cdef PyObject* ptb = NULL
    cdef PyObject* text_obj = NULL
    cdef const char* message = NULL
    cdef Py_ssize_t size = 0
    cdef int have_message = 0

    PyErr_Fetch(&ptype, &pvalue, &ptb)      # 取出并清除错误指示器（接管引用）

    if pvalue != NULL:
        text_obj = PyObject_Str(pvalue)
        if text_obj != NULL:
            message = PyUnicode_AsUTF8AndSize(text_obj, &size)
            if message != NULL:
                snprintf(_errbuf, ERR_CAP, "%s: %s", context, message)
                have_message = 1

    if not have_message:
        snprintf(_errbuf, ERR_CAP, "%s: unknown python error", context)

    if text_obj != NULL:
        Py_DecRef(text_obj)

    if ptype != NULL or pvalue != NULL:
        # 放回去再打印，traceback 落到 stderr，方便定位
        PyErr_Restore(ptype, pvalue, ptb)
        PyErr_Print()
    else:
        PyErr_Clear()

    return -1


cdef int _emit(PyObject* result, char* out_buf, int out_cap):
    """把 python str 结果（UTF-8）拷进调用方缓冲区。"""
    global _required

    cdef const char* data = NULL
    cdef Py_ssize_t size = 0

    data = PyUnicode_AsUTF8AndSize(result, &size)
    if data == NULL:
        return _capture_error("python side did not return str")

    if size + 1 > <Py_ssize_t>out_cap:
        _required = <int>(size + 1)
        snprintf(_errbuf, ERR_CAP,
                 "output buffer too small: need %d bytes, got %d", _required, out_cap)
        return -2

    memcpy(out_buf, data, <size_t>size)
    out_buf[size] = 0
    return <int>size


cdef PyObject* _make_score(double* values):
    """Score(cpu, ram, graphics, d3, disk) -> 新引用；失败返回 NULL。"""
    cdef PyObject* args = NULL
    cdef PyObject* value = NULL
    cdef PyObject* score = NULL
    cdef Py_ssize_t i

    args = PyTuple_New(SCORE_FIELDS)
    if args == NULL:
        return NULL

    for i in range(SCORE_FIELDS):
        value = PyFloat_FromDouble(values[i])
        if value == NULL:
            Py_DecRef(args)
            return NULL
        PyTuple_SetItem(args, i, value)     # 接管引用

    score = PyObject_CallObject(_score_type, args)
    Py_DecRef(args)
    return score


# --------------------------------------------------------------------------
# 元信息
# --------------------------------------------------------------------------
cdef public const char* last_error():
    return _errbuf


cdef public int last_required_size():
    return _required


cdef public const char* version():
    return _version_buf


# --------------------------------------------------------------------------
# 启动 / 关闭
# --------------------------------------------------------------------------
cdef public int boot(char* script_dir):
    # Cython 3: 在 cdef public 函数里给模块级 cdef 变量赋值，
    # 必须显式 global，否则该名字会被当成局部变量（编译期直接报
    # "local variable referenced before assignment"）。
    global _booted, _owns_interp, _tstate
    global _fn_parse_xml, _fn_export_html, _fn_runtime_info, _score_type

    cdef PyGILState_STATE gil
    cdef PyObject* sys_path = NULL
    cdef PyObject* dir_obj = NULL
    cdef PyObject* parser = NULL
    cdef PyObject* fn = NULL
    cdef int ours = 0
    cdef int rc = 0

    _errbuf[0] = 0

    if _booted:
        return 0

    if not Py_IsInitialized():
        Py_Initialize()
        ours = 1
        if not Py_IsInitialized():
            snprintf(_errbuf, ERR_CAP, "Py_Initialize() failed")
            return -1

    # 从这里开始必须持有 GIL：Py_Initialize() 之后当前线程已经持有，
    # 此时 PyGILState_Ensure() 返回 LOCKED，下面的 Release 不会把 GIL 放掉，
    # 所以「ours 时由 PyEval_SaveThread() 释放」这个顺序是安全的。
    gil = PyGILState_Ensure()

    # 1) 让 import 能找到 .py / .pyd 所在的目录
    if script_dir != NULL and script_dir[0] != 0:
        sys_path = PySys_GetObject("path")
        dir_obj = _to_unicode(script_dir)
        if sys_path == NULL or dir_obj == NULL:
            rc = _capture_error("cannot prepare sys.path")
        else:
            PyList_Insert(sys_path, 0, dir_obj)   # 只借用引用，下面自己释放
        if dir_obj != NULL:
            Py_DecRef(dir_obj)
            dir_obj = NULL

    # 2) import xml_parser，并缓存三个入口（名字都是 C 字面量，不依赖模块初始化）
    if rc == 0:
        parser = PyImport_ImportModule("xml_parser")
        if parser == NULL:
            rc = _capture_error(
                "import xml_parser failed (keep xml_parser.py next to the .pyd)")
        else:
            fn = PyObject_GetAttrString(parser, "parse_xml")
            if fn == NULL:
                rc = _capture_error("xml_parser.parse_xml not found")
            else:
                _fn_parse_xml = fn

            if rc == 0:
                fn = PyObject_GetAttrString(parser, "export_html")
                if fn == NULL:
                    rc = _capture_error("xml_parser.export_html not found")
                else:
                    _fn_export_html = fn

            if rc == 0:
                fn = PyObject_GetAttrString(parser, "Score")
                if fn == NULL:
                    rc = _capture_error("xml_parser.Score not found")
                else:
                    _score_type = fn

            if rc == 0:
                fn = PyObject_GetAttrString(parser, "runtime_info")
                if fn == NULL:
                    rc = _capture_error("xml_parser.runtime_info not found")
                else:
                    _fn_runtime_info = fn

            Py_DecRef(parser)

    if rc == 0:
        snprintf(_version_buf, sizeof(_version_buf), "parser/1.2 | CPython %s",
                 Py_GetVersion())

    PyGILState_Release(gil)

    if rc != 0:
        # 启动失败：ours 时此刻仍持有 GIL，直接把解释器收掉，保持状态干净
        if ours:
            Py_Finalize()
        return rc

    _owns_interp = ours
    _booted = 1

    if ours:
        # 释放本线程的 GIL：否则后续从 C# 其它线程调用时
        # PyGILState_Ensure() 会永远等不到锁而死锁。
        _tstate = PyEval_SaveThread()

    return 0


cdef public int shutdown():
    global _booted, _owns_interp, _tstate
    global _fn_parse_xml, _fn_export_html, _fn_runtime_info, _score_type

    cdef PyGILState_STATE gil

    _errbuf[0] = 0
    if not Py_IsInitialized():
        _booted = 0
        return 0

    if (_fn_parse_xml != NULL or _fn_export_html != NULL
            or _fn_runtime_info != NULL or _score_type != NULL):
        gil = PyGILState_Ensure()
        if _fn_parse_xml != NULL:
            Py_DecRef(_fn_parse_xml)
            _fn_parse_xml = NULL
        if _fn_export_html != NULL:
            Py_DecRef(_fn_export_html)
            _fn_export_html = NULL
        if _fn_runtime_info != NULL:
            Py_DecRef(_fn_runtime_info)
            _fn_runtime_info = NULL
        if _score_type != NULL:
            Py_DecRef(_score_type)
            _score_type = NULL
        PyGILState_Release(gil)

    _booted = 0

    if _owns_interp and _tstate != NULL:
        # 先把 GIL 收回来，再销毁解释器
        PyEval_RestoreThread(_tstate)
        _tstate = NULL
        _owns_interp = 0
        Py_Finalize()
        snprintf(_version_buf, sizeof(_version_buf), "parser/1.2 | shut down")

    return 0


# --------------------------------------------------------------------------
# 业务入口：parse_xml / export_html
# --------------------------------------------------------------------------
cdef public int parse_xml(char* source, char* symbol, char* out_buf, int out_cap):
    global _required

    cdef PyGILState_STATE gil
    cdef PyObject* args = NULL
    cdef PyObject* result = NULL
    cdef PyObject* arg_source = NULL
    cdef PyObject* arg_symbol = NULL
    cdef int rc = -1

    _errbuf[0] = 0
    _required = 0

    if not _booted:
        snprintf(_errbuf, ERR_CAP, "bridge not booted, call boot() first")
        return -1
    if _fn_parse_xml == NULL:
        snprintf(_errbuf, ERR_CAP, "xml_parser.parse_xml is not loaded, call boot() again")
        return -1
    if source == NULL or symbol == NULL or out_buf == NULL or out_cap <= 0:
        snprintf(_errbuf, ERR_CAP, "invalid argument: null pointer or out_cap <= 0")
        return -1

    gil = PyGILState_Ensure()

    arg_source = _to_unicode(source)
    arg_symbol = _to_unicode(symbol)
    if arg_source == NULL or arg_symbol == NULL:
        _capture_error("arguments are not valid UTF-8")
    else:
        args = PyTuple_New(2)
        if args == NULL:
            _capture_error("PyTuple_New failed")
        else:
            PyTuple_SetItem(args, 0, arg_source)   # 接管引用
            arg_source = NULL
            PyTuple_SetItem(args, 1, arg_symbol)
            arg_symbol = NULL

            result = PyObject_CallObject(_fn_parse_xml, args)
            if result == NULL:
                _capture_error("xml_parser.parse_xml raised")
            else:
                rc = _emit(result, out_buf, out_cap)

    if arg_source != NULL:
        Py_DecRef(arg_source)
    if arg_symbol != NULL:
        Py_DecRef(arg_symbol)
    if args != NULL:
        Py_DecRef(args)
    if result != NULL:
        Py_DecRef(result)

    PyGILState_Release(gil)
    return rc


cdef public int export_html(char* source, char* symbol, char* output_path,
                                double* score, char* out_buf, int out_cap):
    global _required

    cdef PyGILState_STATE gil
    cdef PyObject* args = NULL
    cdef PyObject* result = NULL
    cdef PyObject* arg_source = NULL
    cdef PyObject* arg_symbol = NULL
    cdef PyObject* arg_output = NULL
    cdef PyObject* arg_score = NULL
    cdef int argc = 3
    cdef int rc = -1

    _errbuf[0] = 0
    _required = 0

    if not _booted:
        snprintf(_errbuf, ERR_CAP, "bridge not booted, call boot() first")
        return -1
    if _fn_export_html == NULL:
        snprintf(_errbuf, ERR_CAP, "xml_parser.export_html is not loaded, call boot() again")
        return -1
    if (source == NULL or symbol == NULL or output_path == NULL
            or out_buf == NULL or out_cap <= 0):
        snprintf(_errbuf, ERR_CAP, "invalid argument: null pointer or out_cap <= 0")
        return -1

    gil = PyGILState_Ensure()

    arg_source = _to_unicode(source)
    arg_symbol = _to_unicode(symbol)
    arg_output = _to_unicode(output_path)

    if arg_source == NULL or arg_symbol == NULL or arg_output == NULL:
        _capture_error("arguments are not valid UTF-8")
    else:
        # score 指针非空 = C# 传了 Score；NULL = 不导出分数表（走 score 的默认值 None）
        if score != NULL:
            arg_score = _make_score(score)
            if arg_score == NULL:
                _capture_error("cannot build xml_parser.Score")

        if score == NULL or arg_score != NULL:
            if arg_score != NULL:
                argc = 4

            args = PyTuple_New(argc)
            if args == NULL:
                _capture_error("PyTuple_New failed")
            else:
                PyTuple_SetItem(args, 0, arg_source)
                arg_source = NULL
                PyTuple_SetItem(args, 1, arg_symbol)
                arg_symbol = NULL
                PyTuple_SetItem(args, 2, arg_output)
                arg_output = NULL
                if arg_score != NULL:
                    PyTuple_SetItem(args, 3, arg_score)
                    arg_score = NULL

                result = PyObject_CallObject(_fn_export_html, args)
                if result == NULL:
                    _capture_error("xml_parser.export_html raised")
                else:
                    rc = _emit(result, out_buf, out_cap)

    if arg_source != NULL:
        Py_DecRef(arg_source)
    if arg_symbol != NULL:
        Py_DecRef(arg_symbol)
    if arg_output != NULL:
        Py_DecRef(arg_output)
    if arg_score != NULL:
        Py_DecRef(arg_score)
    if args != NULL:
        Py_DecRef(args)
    if result != NULL:
        Py_DecRef(result)

    PyGILState_Release(gil)
    return rc


# --------------------------------------------------------------------------
# 运行环境自查（客户机上「跑不起来」时的第一手材料）
# --------------------------------------------------------------------------
cdef public int runtime_info(char* out_buf, int out_cap):
    global _required

    cdef PyGILState_STATE gil
    cdef PyObject* result = NULL
    cdef int rc = -1

    _errbuf[0] = 0
    _required = 0

    if not _booted:
        snprintf(_errbuf, ERR_CAP, "bridge not booted, call boot() first")
        return -1
    if _fn_runtime_info == NULL:
        snprintf(_errbuf, ERR_CAP, "xml_parser.runtime_info is not loaded, call boot() again")
        return -1
    if out_buf == NULL or out_cap <= 0:
        snprintf(_errbuf, ERR_CAP, "invalid argument: null pointer or out_cap <= 0")
        return -1

    gil = PyGILState_Ensure()

    result = PyObject_CallObject(_fn_runtime_info, NULL)   # NULL = 无参调用
    if result == NULL:
        _capture_error("xml_parser.runtime_info raised")
    else:
        rc = _emit(result, out_buf, out_cap)
        Py_DecRef(result)

    PyGILState_Release(gil)
    return rc
