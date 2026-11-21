// ============================================================================
// 本文件是 ConsoleApp1\NativeBridge.cs 的副本，由仓库根目录的 sync_shared_sources.ps1 生成。
// winsat 是能单独拷走的独立程序，所以不复用 ConsoleApp1 的工程，只复用这份代码。
// 改逻辑请改 ConsoleApp1\NativeBridge.cs，然后重跑 .\sync_shared_sources.ps1 —— 别直接改这里。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace winsat.helpers;

/// <summary>
/// export_html 的分数表。
///
/// 字段顺序必须和 C ABI 里的 <c>double[5]</c> 一致（cpu, ram, graphics, d3, disk），
/// 桥接层按位置把它还原成 Python 侧的 <c>xml_parser.Score</c>。
/// 序列化顺序见 <see cref="ToAbi"/>。
/// </summary>
public readonly record struct Score(double Cpu, double Ram, double Graphics, double D3, double Disk)
{
    /// <summary>摊平成 C ABI 需要的 5 个 double（顺序不能改）。</summary>
    internal double[] ToAbi() => [Cpu, Ram, Graphics, D3, Disk];
}

/// <summary>原生侧返回 -1（Python 抛异常、参数非法、未 boot）时抛出的异常。</summary>
public sealed class PythonBridgeException : Exception
{
    public PythonBridgeException(string message, string? method = null, Exception? inner = null)
        : base(message, inner)
    {
        Method = method;
    }

    /// <summary>出错的原生入口名（parse_xml / export_html / ...）。</summary>
    public string? Method { get; }
}

/// <summary>
/// xml_parser 的 .NET 门面。
///
/// 字符串一律走「调用方提供缓冲区」的 ABI：
///   &gt;=0 = 写入的字节数，-1 = 出错（last_error），-2 = 缓冲区不足
///   （last_required_size 给出完整长度）。
/// 这样绝不会把 Python 临时对象的内存地址交给托管代码（否则函数返回后就是野指针）。
/// </summary>
public sealed class PythonBridge : IDisposable
{
    private const string Dll = "parser.dll";

    /// <summary>
    /// parser.dll / xml_parser.pyd 所在目录。boot() 会把它插进 sys.path[0]，
    /// 所以 `import xml_parser` 和「templates 在模块旁边」都靠它。
    /// 默认是程序目录（平铺布局）；收进子目录时（如 &lt;程序目录&gt;\parser）自己设。
    /// </summary>
    public static string ParserDirectory
    {
        get => s_parserDirectory;
        set
        {
            s_parserDirectory = value;
            s_runtimeDirectory = value;   // 平铺布局：运行时就在同一层
        }
    }

    /// <summary>
    /// python314.dll / python314.zip / Lib 所在目录。默认跟随 <see cref="ParserDirectory"/>；
    /// 把运行时单独收进子目录时（如 &lt;程序目录&gt;\parser\runtime）单独指定。
    /// </summary>
    public static string RuntimeDirectory
    {
        get => s_runtimeDirectory;
        set => s_runtimeDirectory = value;
    }

    /// <summary>旧名字，等价于 <see cref="ParserDirectory"/>。</summary>
    public static string ScriptDirectory => s_parserDirectory;

    private static string s_parserDirectory = AppContext.BaseDirectory;
    private static string s_runtimeDirectory = AppContext.BaseDirectory;
    private static int s_resolverInstalled;
    private static int s_pythonPreloaded;

    /// <summary>第一次调用用的缓冲区大小，故意小一点以演示 -2 重试路径。</summary>
    private const int InitialBufferSize = 4096;

    private const int MaxAttempts = 8;

    /// <summary>Boot 出来的实例个数：解释器是进程级单例，最后一个 Dispose 才真正关闭。</summary>
    private static int s_liveInstances;

    private bool _disposed;

    /// <summary>是否打印缓冲区协商过程，默认开启（演示用）。</summary>
    public static bool Verbose { get; set; } = true;

    private PythonBridge()
    {
    }

    // --------------------------------------------------------------------
    // P/Invoke 声明：全部 Cdecl，与 Cython 默认调用约定一致
    // --------------------------------------------------------------------
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int boot([MarshalAs(UnmanagedType.LPUTF8Str)] string scriptDirectory);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int shutdown();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int parse_xml(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string symbol,
        [Out] byte[] outBuffer,
        int outCapacity);

    /// <summary>
    /// score 传 null 时，编组层直接把 NULL 指针递过去 = Python 侧 score 参数用默认值（None），
    /// 即「不导出分数表」。
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int export_html(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string symbol,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath,
        [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 5)] double[]? score,
        [Out] byte[] outBuffer,
        int outCapacity);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int last_required_size();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr last_error();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr version();

    /// <summary>运行环境自查（sys.prefix / sys.path / lxml 实际来源），排查部署问题用。</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int runtime_info(
        [Out] byte[] outBuffer,
        int outCapacity);

    // --------------------------------------------------------------------
    // 原生库定位：把 DllImport("parser.dll") 重定向到 ParserDirectory
    // --------------------------------------------------------------------
    private static void EnsureResolver()
    {
        if (System.Threading.Interlocked.Exchange(ref s_resolverInstalled, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(PythonBridge).Assembly, Resolve);
    }

    /// <summary>
    /// 解析器：先按绝对路径把 python3XX.dll 载入，再载入 parser.dll。
    ///
    /// 为什么必须预加载：parser.dll 的导入表里有 python314.dll，Windows 默认只在
    /// 「程序目录 / PATH」里找它。把运行时收进子目录（parser\runtime）后就不在默认
    /// 搜索范围里了；先按绝对路径 Load 一次，parser.dll 的同名导入会直接复用已载入
    /// 的模块，与搜索路径无关。
    /// </summary>
    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, Dll, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;   // 不归我们管，交回默认解析
        }

        PreloadPythonRuntime();

        string path = Path.Combine(s_parserDirectory, Dll);
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }

    private static void PreloadPythonRuntime()
    {
        if (System.Threading.Volatile.Read(ref s_pythonPreloaded) != 0)
        {
            return;
        }

        int loaded = 0;
        foreach (string dll in RuntimeCheck.FindPythonDlls(s_runtimeDirectory))
        {
            try
            {
                NativeLibrary.Load(dll);   // 已载入时返回同一句柄，重复调用无害
                loaded++;
            }
            catch (Exception)
            {
                // 载入失败就让它失败：随后 parser.dll 的加载会给出真正的错误信息
            }
        }

        // 一个都没载入（运行时还没放好）时不置位，下次还能重试
        if (loaded > 0)
        {
            System.Threading.Interlocked.Exchange(ref s_pythonPreloaded, 1);
        }
    }

    public static string Version
    {
        get
        {
            EnsureResolver();
            IntPtr ptr = version();
            return ptr == IntPtr.Zero ? "(unknown)" : Marshal.PtrToStringUTF8(ptr) ?? "(unknown)";
        }
    }

    // --------------------------------------------------------------------
    // 启动 / 关闭
    // --------------------------------------------------------------------
    public static PythonBridge Boot()
    {
        // 先安装解析器：否则 DllImport("parser.dll") 会按默认搜索路径找，
        // 运行时/桥收在子目录里时就会直接 DllNotFoundException。
        EnsureResolver();

        // 再查文件：缺文件时抛的是 DllNotFoundException，
        // 那条消息只说「找不到模块」，客户机器上根本没法照着修。
        // 这里提前把「缺哪个文件、该放哪」讲清楚。
        IReadOnlyList<string> missing = RuntimeCheck.MissingFiles(ParserDirectory, RuntimeDirectory);
        if (missing.Count > 0)
        {
            throw new PythonBridgeException(
                "Python 运行时文件不完整：" + Environment.NewLine
                + RuntimeCheck.Describe(ParserDirectory, RuntimeDirectory));
        }

        int rc;
        try
        {
            rc = boot(ParserDirectory);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(BuildHint($"加载 {Dll} 失败。", ex), ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidOperationException(BuildHint($"{Dll} 位数与当前进程不匹配（需要同为 x64）。", ex), ex);
        }

        if (rc != 0)
        {
            throw new PythonBridgeException(
                "boot 失败：" + LastError() + Environment.NewLine + BuildHint(string.Empty, null));
        }

        System.Threading.Interlocked.Increment(ref s_liveInstances);
        return new PythonBridge();
    }

    /// <summary>
    /// 运行环境自查：Python 版本、<c>sys.prefix</c>、<c>sys.path</c>、
    /// lxml / bs4 到底从哪个目录加载的。
    /// 客户机器上「跑不起来」时先看这里——能立刻区分「用的是我们带过去的运行时」
    /// 还是「被机器上别的 Python 抢走了」。
    /// </summary>
    public string RuntimeInfo() => Invoke("runtime_info", (buffer, capacity) => runtime_info(buffer, capacity));

    // --------------------------------------------------------------------
    // 业务入口
    // --------------------------------------------------------------------

    /// <summary>
    /// 解析 source（XML）+ symbol（符号文件 JSON），返回整串输出文本：一行一条。
    /// 出错时返回 &lt;error&gt; 行；只有真出了意外才会抛 <see cref="PythonBridgeException"/>。
    /// </summary>
    public string ParseXml(string source, string symbol) =>
        Invoke("parse_xml", (buffer, capacity) => parse_xml(source, symbol, buffer, capacity));

    /// <summary>
    /// 把 source + symbol（符号目录）渲染成 outputPath 指向的 HTML 文件。
    /// 返回空字符串表示成功；否则是 &lt;warning&gt; / &lt;error&gt; 行。
    /// </summary>
    public string ExportHtml(string source, string symbol, string outputPath, Score? score = null) =>
        Invoke("export_html", (buffer, capacity) =>
            export_html(source, symbol, outputPath, score?.ToAbi(), buffer, capacity));

    /// <summary>底层调用：负责缓冲区协商（-2 → 按 last_required_size() 重试）。</summary>
    private static string Invoke(string method, Func<byte[], int, int> call)
    {
        EnsureResolver();

        int capacity = InitialBufferSize;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            byte[] buffer = new byte[capacity];
            int written = call(buffer, capacity);

            if (written >= 0)
            {
                return Encoding.UTF8.GetString(buffer, 0, written);
            }

            if (written == -2)
            {
                int required = last_required_size();
                if (Verbose)
                {
                    Console.WriteLine(
                        $"    [bridge] 缓冲区不足（{capacity} 字节），按需要的 {required} 字节重试");
                }

                capacity = Math.Max(required, capacity * 2);
                continue;
            }

            throw new PythonBridgeException($"{method} 调用失败：{LastError()}", method);
        }

        throw new PythonBridgeException(
            $"{method} 的缓冲区协商超过 {MaxAttempts} 次", method);
    }

    /// <summary>故意只给一小块缓冲区，用来演示 -2 错误码。</summary>
    public static (int ReturnCode, int Required, string Error) ProbeBuffer(
        string source, string symbol, int capacity)
    {
        EnsureResolver();

        byte[] buffer = new byte[capacity];
        int rc = parse_xml(source, symbol, buffer, capacity);
        return rc == -2
            ? (rc, last_required_size(), LastError())
            : (rc, 0, rc < 0 ? LastError() : "ok");
    }

    public static string LastError()
    {
        IntPtr ptr = last_error();
        return ptr == IntPtr.Zero ? "(no error)" : Marshal.PtrToStringUTF8(ptr) ?? "(no error)";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 解释器是进程级单例：只有最后一个实例释放时才真正 Py_Finalize
        if (System.Threading.Interlocked.Decrement(ref s_liveInstances) <= 0)
        {
            System.Threading.Interlocked.Exchange(ref s_liveInstances, 0);
            _ = shutdown();
        }
    }

    private static string BuildHint(string prefix, Exception? inner)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(prefix))
        {
            sb.AppendLine(prefix);
        }

        sb.AppendLine(RuntimeCheck.Describe(ScriptDirectory, RuntimeDirectory));
        sb.AppendLine();
        sb.AppendLine("  其它排查点：");
        sb.AppendLine("    · 进程位数必须是 x64（Python 3.14 x64 编出的 pyd 只能给 x64 进程用）；");
        sb.AppendLine("    · 目标解释器要有 lxml / beautifulsoup4：python -m pip install lxml beautifulsoup4；");
        sb.AppendLine("    · 别把 exe 单独拷走 —— parser.dll / xml_parser.pyd / templates / Python 运行时");
        sb.AppendLine("      必须和 exe 在同一个目录里（或者按 ParserDirectory / RuntimeDirectory 的约定放好）。");
        if (inner is not null)
        {
            sb.AppendLine($"  原始异常：{inner.GetType().Name}: {inner.Message}");
        }

        return sb.ToString();
    }
}
