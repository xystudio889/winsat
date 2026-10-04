from lxml import etree
import argparse
import json
import re
import os
import sys
import zipfile
from traceback import format_exc
from colorama import Fore, Style, init
from pathlib import Path
from bs4 import BeautifulSoup

init(autoreset=True)  # 重置颜色设置，避免在终端中出现颜色冲突

__version__ = 8.2
cdata_regex = re.compile(r'<!\[CDATA\[\s+(.+?)\s+\]\]>', re.DOTALL)

# 特殊值：不是 xpath，而是直接产生结构化标记（键名只用于区分，spacing 不需要键名）
SPECIAL_VALUES = {
    'line': '<line>',
    'subline': '<subline>',
    'spacing': '<spacing>',
}

GREEN = Fore.GREEN
CYAN = Fore.CYAN
YELLOW = Fore.YELLOW
RESET = Style.RESET_ALL

# 分词：空白、方括号、竖线、其他词
TOKEN_RE = re.compile(r'(\s+|\[|\]|\||[^\s\[\]|]+)')

# ---------------------------------------------------------------- $locale 键名 --
# 解析器要输出的所有文案都写在符号文件的 $locale 里（键名用英文标识符），这里只保存
# 键名；任何一个键取不到都算符号文件写法错误，抛 SignalConfigError。
L_NONE = 'none'
L_MODE_NORMAL = 'mode_normal'
L_MODE_COMBINED = 'mode_combined'
L_MODE_TRANSPOSE = 'mode_transpose'
L_ERROR_NO_LOCALE = 'error_no_locale'
L_ERROR_LOCALE_MISSING = 'error_locale_missing'
L_ERROR_IN_SYMBOL = 'error_in_symbol'
L_ERROR_NESTED_TRANSPOSE = 'error_nested_transpose'
L_ERROR_ELEMENT_MISSING = 'error_element_missing'
L_ERROR_UNKNOWN_SYMBOL = 'error_unknown_symbol'
L_ERROR_LIST_LENGTH = 'error_list_length'
L_ERROR_NOT_COMBINED = 'error_not_combined'
L_ERROR_PARSE_FAILED = 'error_parse_failed'
L_ERROR_SOURCE_MISSING = 'error_source_missing'
L_ERROR_SYMBOL_MISSING = 'error_symbol_missing'
L_ERROR_VERSION = 'error_version'
L_WARNING_FILE_MISSING = 'warning_file_missing'
L_ERROR_WST_FAILED = 'error_wst_failed'

# 唯一无法从符号文件读取的兜底文案：符号文件读不出来或 $locale 缺键时，已经没有可用的
# 文案表了，只能用英文兜底（内容与 en-US.json 的对应项保持一致）。
DEFAULT_TEXTS = {
    L_ERROR_NO_LOCALE: 'signal file has no valid "$locale" object',
    L_ERROR_LOCALE_MISSING: '"$locale" is missing the key "{0}"',
    L_ERROR_SOURCE_MISSING: "source file path '{0}' does not exist",
    L_ERROR_SYMBOL_MISSING: "symbol file path '{0}' does not exist",
    L_WARNING_FILE_MISSING: "file '{0}' does not exist",
    L_ERROR_WST_FAILED: "failed to write wst package '{0}': {1}",
}

AVAILABLE = {'zh-CN': 'zh-Hans-cn', 'en-US': 'en-US'}
HTML_FILE: dict[str, BeautifulSoup] = None # 设置html列表

stdout = []

class SignalConfigError(Exception):
    """符号文件写法有误（与「取不到值」区分开）。"""

def createTemplate(key: str) -> BeautifulSoup:
    '''新增模板。'''
    try:
        return HTML_FILE[key].__copy__()
    except KeyError:
        _print_error(f'HTML 模板不存在: {key}', False)
        exit(0)

def _locale_text(locale, key):
    """按键名从符号文件的 $locale 取文案；取不到即符号文件写法错误。"""
    text = locale.get(key) if isinstance(locale, dict) else None
    if not isinstance(text, str) or not text:
        template = locale.get(L_ERROR_LOCALE_MISSING) if isinstance(locale, dict) else None
        if not isinstance(template, str) or not template:
            template = DEFAULT_TEXTS[L_ERROR_LOCALE_MISSING]
        raise SignalConfigError(template.format(key))
    return text


def _fallback_text(locale, key):
    """报错兜底：符号文件读得出来就按 $locale 输出，读不出来用英文。"""
    if isinstance(locale, dict):
        text = locale.get(key)
        if isinstance(text, str) and text:
            return text
    return DEFAULT_TEXTS[key]


def _print_error(text, no_print):
    """错误行统一加协议前缀 <error>（C# 端按此前缀识别）。"""
    _print('<error>{0}'.format(text).replace('\n', '\\n'), no_print)

def _print_warning(text, no_print):
    """警告行统一加协议前缀 <warning>（C# 端按此前缀收集，换行同样转义成\\n）。"""
    _print('<warning>{0}'.format(text).replace('\n', '\\n'), no_print)

def _print(text, no_print):
    '''打印并添加输出'''
    global stdout

    if not no_print:
        print(text)
    stdout.append(text)

def _get_text(element, locale):
    """取出元素的文本值（兼容 CDATA 包裹）。"""
    matches = re.search(cdata_regex, element)
    if matches:
        element = matches.group(1)
    if not element:
        raise ValueError(_locale_text(locale, L_ERROR_ELEMENT_MISSING))
    return element


def _xpath_first(root, path, locale):
    """执行 XPath，若匹配多项则只取第一项；无匹配抛异常。"""
    result = root.xpath(path)
    if len(result) == 0:
        raise ValueError(_locale_text(locale, L_ERROR_ELEMENT_MISSING))
    return result[0]


def _special_line(value, name):
    """值是 Line / subline / spacing 时返回要输出的整行；否则返回 None。"""
    marker = SPECIAL_VALUES.get(value.lower()) if isinstance(value, str) else None
    if marker is None:
        return None
    return marker if marker == '<spacing>' else marker + name


def _resolve(value, defs):
    """把 `$defs名` 替换为 defs 中定义的值（未命中则原样返回）。"""
    return defs.get(value.lstrip('$'), value) if isinstance(value, str) else value


def _transpose_group(value, defs):
    """值是转置模式（object，或指向 object 的 `$defs` 名）时返回该 object，否则返回 None。"""
    if isinstance(value, dict):
        return value
    if isinstance(value, str):
        found = defs.get(value.lstrip('$'))
        if isinstance(found, dict):
            return found
    return None


def _mode_of(value, locale):
    """值所属的模式名（取自 $locale，用于报错信息）。"""
    return _locale_text(locale, L_MODE_NORMAL if isinstance(value, str) else L_MODE_COMBINED)


def _values_of(value, root, defs, locale, lenient=False):
    """把值解析成「值的列表」，各模式的结果与原先逐项输出时保持一致。

    - "xpath"              → 普通模式：每个匹配一项（已 strip）
    - ["xpath", "后缀"]     → 后缀模式
    - ["xpath", {翻译表}]   → 翻译模式
    - [[p1, p2, ...], sep] → 合并模式：只产生一项

    值为 Line / subline / spacing 等特殊值时不在这里处理（见 _special_line）。

    lenient=True（转置模式用）：空值（如空 CDATA 的 `<Vendor/>`）显示为 $locale 里
    "none" 的文案而不是抛异常，免得一个空字段把整组拖垮；xpath 一个都匹配不到时
    仍抛 ValueError，由调用方决定是否跳过该成员。
    """
    def text_of(element):
        if not lenient:
            return _get_text(element, locale)
        try:
            return _get_text(element, locale)
        except ValueError:
            return _locale_text(locale, L_NONE)

    if isinstance(value, str):
        elements = root.xpath(value)
        if not isinstance(elements, list) or len(elements) == 0:
            raise ValueError(_locale_text(locale, L_ERROR_ELEMENT_MISSING))
        return [text_of(e).strip() for e in elements]

    head, tail = value[0], _resolve(value[1], defs)

    if isinstance(tail, dict):
        # 翻译模式
        elements = root.xpath(head)
        if not isinstance(elements, list) or len(elements) == 0:
            raise ValueError(_locale_text(locale, L_ERROR_ELEMENT_MISSING))
        values = []
        for e in elements:
            text = text_of(e)
            values.append(tail.get(text, text))
        return values

    if isinstance(head, list):
        # 合并模式
        return [tail.join(text_of(_xpath_first(root, p, locale)) for p in head)]

    # 后缀模式
    elements = root.xpath(head)
    if not isinstance(elements, list) or len(elements) == 0:
        raise ValueError(_locale_text(locale, L_ERROR_ELEMENT_MISSING))
    return [text_of(e) + ' ' + tail for e in elements]


def _render_transpose(name, group, root, defs, locale, print_output):
    """转置模式：v = {"成员名": "xpath" | 其它模式, ...}（或用 "$defs名" 引用一个 object）

    把组内所有成员的*所有*结果做转置输出：编号是「组号」而不是「每个成员各自的序号」
    （原先为每个项独自编号），因此同一组的各项紧挨在一起：

        name1 #1,match1
        name2 #1,match2
        name1 #2,match3
        name2 #2,match4

    - 成员的值可以内联普通 / 后缀 / 翻译 / 合并模式，但不能嵌套转置模式；
    - 成员的值是 Line / subline / spacing 时，按**声明位置**在每个组里各输出一行标记
      （不会全部提到组前面，这样小标题能和它下面的数据排在一起）；
    - 组数取「组内最大匹配数」，所有结果都会被输出；
    - 某成员取不到值（xpath 无匹配）时跳过该成员，不影响同组其它成员；
      元素存在但内容为空时显示为 $locale 里 "none" 的文案（与其它成员保持组号对齐）；
    - 没有任何数据成员取到值、且也没有标记成员时，整组输出该文案；
    - 组与组之间会插入一个 `<spacing>`（即从 #1 切换到 #2 时）。
    """
    members = []          # (显示名, 值的列表, 标记行)；标记成员的值为 None
    for member, path in group.items():
        label = member.lstrip('$')  # 与顶层键一致：成员名同样去掉开头的 $
        if _transpose_group(path, defs) is not None:
            raise SignalConfigError(_locale_text(locale, L_ERROR_NESTED_TRANSPOSE).format(
                _locale_text(locale, L_MODE_TRANSPOSE), label))
        marker = _special_line(path, label)
        if marker is not None:
            members.append((label, None, marker))
            continue
        try:
            values = _values_of(path, root, defs, locale, lenient=True)
        except ValueError:
            values = []       # 该成员一个都取不到：跳过它，不要让整组失败
        members.append((label, values, None))

    counts = [len(values) for _, values, marker in members if marker is None]
    total = max(counts) if counts else 0
    has_marker = any(marker is not None for _, _, marker in members)
    if total == 0 and not has_marker:
        raise ValueError(_locale_text(locale, L_ERROR_ELEMENT_MISSING))

    for index in range(max(total, 1)):
        if index:
            _print('<spacing>', print_output)  # 组与组之间插入一个空隙（#1 → #2 时）
        for label, values, marker in members:
            if marker is not None:
                _print(marker, print_output)
            elif index < len(values):
                _print('{0} #{1},{2}'.format(label, index, values[index]), print_output)


def parse_xml(file, signal, no_print: bool=False):
    global stdout

    stdout = [] # 输出内容

    # 加载 XML
    tree = etree.parse(file)
    root = tree.getroot()

    with open(signal, 'r', encoding='utf-8') as f:
        signals = json.load(f)

    # $locale：解析器要输出的文案全写在符号文件里；取不到就没法输出，直接算写法错误
    locale = signals.pop('$locale', None)
    if not isinstance(locale, dict):
        raise SignalConfigError(DEFAULT_TEXTS[L_ERROR_NO_LOCALE])
    none_text = _locale_text(locale, L_NONE)

    requires = signals.pop('$requires', 0)
    if __version__ < requires:
        _print_error(_locale_text(locale, L_ERROR_VERSION).format(requires, __version__), no_print)
        return

    defs = signals.pop('$defs', {})

    for k, v in signals.items():
        k = k.lstrip('$')

        # ---------- 转置模式：值是 object，或用 "$defs名" 引用一个 object ----------
        group = _transpose_group(v, defs)
        if group is not None:
            try:
                _render_transpose(k, group, root, defs, locale, no_print)
            except SignalConfigError as e:
                _print_error(_locale_text(locale, L_ERROR_IN_SYMBOL).format(e, k), no_print)
            except ValueError:
                _print('{0},{1}'.format(k, none_text), no_print)
            except Exception as e:
                _print_error(_locale_text(locale, L_ERROR_PARSE_FAILED).format(
                    _locale_text(locale, L_MODE_TRANSPOSE), k, e,
                    format_exc().replace('\n', '\\n')), no_print)
            continue

        if not isinstance(v, (str, list)):
            _print_error(_locale_text(locale, L_ERROR_UNKNOWN_SYMBOL).format(k), no_print)
            continue

        if isinstance(v, list) and len(v) != 2:
            _print_error(_locale_text(locale, L_ERROR_LIST_LENGTH).format(
                _locale_text(locale, L_MODE_COMBINED), k), no_print)
            continue

        if isinstance(v, list) and not (isinstance(v[0], (list, str))
                                        or isinstance(_resolve(v[1], defs), dict)):
            _print_error(_locale_text(locale, L_ERROR_NOT_COMBINED).format(
                _locale_text(locale, L_MODE_COMBINED), k), no_print)
            continue

        try:
            line = _special_line(v, k)
            if line is not None:
                _print(line, no_print)
                continue
            values = _values_of(v, root, defs, locale)
        except SignalConfigError as e:
            _print_error(_locale_text(locale, L_ERROR_IN_SYMBOL).format(e, k), no_print)
            continue
        except ValueError:
            _print('{0},{1}'.format(k, none_text), no_print)
            continue
        except Exception as e:
            _print_error(_locale_text(locale, L_ERROR_PARSE_FAILED).format(
                _mode_of(v, locale), k, e, format_exc().replace('\n', '\\n')), no_print)
            continue

        if len(values) == 1:
            _print('{0},{1}'.format(k, values[0]), no_print)
        else:
            for i, value in enumerate(values):
                _print('{0} #{1},{2}'.format(k, i, value), no_print)

def _read_locale(signal):
    """报错前先尝试读一次符号文件的 $locale；读不到返回 None（改用英文兜底）。"""
    try:
        with open(signal, 'r', encoding='utf-8') as f:
            signals = json.load(f)
    except Exception:
        return None
    locale = signals.get('$locale')
    return locale if isinstance(locale, dict) else None

def _tokenize(text):
    return TOKEN_RE.findall(text)

def _find_matching_bracket(tokens, start_idx):
    """从 start_idx 处的 '[' 开始，找到匹配的 ']' 的索引。"""
    depth = 0
    for i in range(start_idx, len(tokens)):
        if tokens[i] == '[':
            depth += 1
        elif tokens[i] == ']':
            depth -= 1
            if depth == 0:
                return i
    return len(tokens) - 1

def _contains_option(tokens):
    """检查 token 列表中是否包含以 '-' 开头的词（选项）。"""
    for tok in tokens:
        if tok.isspace() or tok in ('[', ']', '|'):
            continue
        if tok.startswith('-'):
            return True
    return False

def _option_color(tok):
    """根据选项前缀选择颜色：'--' 青色，'-' 绿色。"""
    if tok.startswith('--'):
        return CYAN
    return GREEN

def _process_tokens(tokens, first_word, expect_value, yellow_mode=False, in_bracket=False):
    """
    处理 token 列表，返回 (渲染后的字符串, 新的 first_word, 新的 expect_value)。
    yellow_mode: 当前是否处于黄色输出模式（由外层方括号触发）。
    in_bracket: 当前是否在方括号内部。
    """
    out = []
    i = 0
    n = len(tokens)

    while i < n:
        tok = tokens[i]

        # 1. 空白
        if tok.isspace():
            if yellow_mode:
                out.append(YELLOW + tok + RESET)
            else:
                out.append(tok)
            i += 1
            continue

        # 2. 方括号开始
        if tok == '[':
            j = _find_matching_bracket(tokens, i)
            inner_tokens = tokens[i + 1:j]

            if not _contains_option(inner_tokens):
                # 内部没有选项：整个方括号对整体黄色
                whole = ''.join(tokens[i:j + 1])
                out.append(YELLOW + whole + RESET)
                i = j + 1
                continue
            else:
                # 内部包含选项：方括号本身不整体黄色
                if yellow_mode:
                    out.append(YELLOW + '[' + RESET)
                else:
                    out.append('[')

                saved_first = first_word
                saved_expect = expect_value

                inner_rendered, first_word, expect_value = _process_tokens(
                    inner_tokens, first_word, expect_value,
                    yellow_mode, in_bracket=True
                )
                out.append(inner_rendered)

                # 方括号内部的状态不影响外部
                first_word = saved_first
                expect_value = saved_expect

                if yellow_mode:
                    out.append(YELLOW + ']' + RESET)
                else:
                    out.append(']')

                i = j + 1
                continue

        # 3. 其他结构符号
        if tok in ('|', '<', '>'):
            if yellow_mode:
                out.append(YELLOW + tok + RESET)
            else:
                out.append(tok)
            i += 1
            continue

        # 4. 普通词
        if first_word:
            # 文件名：不加任何修饰
            out.append(tok)
            first_word = False
            expect_value = False
        elif tok.startswith('-'):
            # 选项：-- 青色，- 绿色；黄色模式下整段覆盖为黄色
            if yellow_mode:
                out.append(YELLOW + tok + RESET)
            else:
                out.append(_option_color(tok) + tok + RESET)
            expect_value = True
        elif expect_value:
            # 选项的取值：黄色
            out.append(YELLOW + tok + RESET)
            expect_value = False
        else:
            # 位置参数
            if in_bracket and not yellow_mode:
                yellow_mode = True
            if yellow_mode:
                out.append(YELLOW + tok + RESET)
            else:
                out.append(GREEN + tok + RESET)

        i += 1

    return ''.join(out), first_word, expect_value

def highlight(text):
    tokens = _tokenize(text)
    rendered, _, _ = _process_tokens(
        tokens, first_word=True, expect_value=False,
        yellow_mode=False, in_bracket=False
    )
    return rendered

def export_html(source: str, symbol: str, available: dict[str, str], output_path: str, score: bool):
    if not HTML_FILE:
        _print_error('不存在HTML模板', True)
        sys.exit(1)

    base_html = createTemplate('index')
    content = base_html.find('div', class_='content') # 建立content

    if score:
        # 分数表：模板里若已内联一份（方便直接用浏览器预览），就复用它；
        # 否则用 grid.html 追加一份。两个模板的内容保持一致。
        grid = base_html.find('div', class_='grid')
        if grid is None:
            grid = createTemplate('grid')
            content.append(grid) # 表格

        cpu_score = grid.find('div', id='CPUScore') # 表格
        ram_score = grid.find('div', id='RAMScore')
        dwm_score = grid.find('div', id='DWMScore')
        d3_score = grid.find('div', id='3DScore')
        disk_score = grid.find('div', id='DiskScore')
        total_score = grid.find('div', id='TotalScore')

        arg_total_score = min(
            args.cpu_score, args.ram_score, args.graphics_score, args.d3_score, args.disk_score
        )

        # 并列最低分的行都加 "lowest" 类：左边两角圆角、右边无圆角，
        # 与第 4 列（基本分数）的无圆角背景连成一条。
        # （模板里预置的 lowest 只是预览用的示例，先清掉再按真实分数重新标）
        for cell, value in (
            (cpu_score, args.cpu_score),
            (ram_score, args.ram_score),
            (dwm_score, args.graphics_score),
            (d3_score, args.d3_score),
            (disk_score, args.disk_score),
        ):
            classes = [c for c in (cell.get('class') or []) if c != 'lowest']
            if value == arg_total_score:
                classes.append('lowest')
            cell['class'] = classes

        cpu_score.string = str(args.cpu_score)
        ram_score.string = str(args.ram_score)
        dwm_score.string = str(args.graphics_score)
        d3_score.string = str(args.d3_score)
        disk_score.string = str(args.disk_score)
        total_score.string = str(arg_total_score)

        # 高级信息（expander）：同样优先复用模板里已内联的那一份
        expander = base_html.find('div', class_='expander')
        if expander is None:
            expander = createTemplate('expander')
            content.append(expander)

        add_content = expander.find('div', class_="expander-body") # 设置添加组件
    else:
        # 不导出分数表：把模板里内联的分数表 / 高级信息去掉
        for klass in ('grid', 'expander'):
            node = base_html.find('div', class_=klass)
            if node is not None:
                node.decompose()

        # 设置添加的组件
        add_content = content

    # 读取可用列表
    for k, i in available.items():
        file_path = os.path.join(symbol, i + '.json')

        if not os.path.exists(file_path):
            print(f'<error>{file_path} 文件不存在。')
            return 1

        parse_xml(source, file_path, no_print=True)

        card = createTemplate('ContentCard/index')
        card_main = card.div # 主卡片
        if card_main is None: # 如果为空
            card_main = card.new_tag('div')
            card_main['class'] = 'content-card'
        card_main['data-locale'] = k

        for line in stdout:
            if line.startswith('<error>'):
                print(line.replace('<error>', f'<error>At {k}: '))
                sys.exit(1)
            elif line.startswith('<line>'):
                line_obj = createTemplate('ContentCard/bigline')

                line_div = line_obj.find('div', class_='line-text')
                line_div.string = line.lstrip('<line>')
                card_main.append(line_obj)
            elif line.startswith('<subline>'):
                line_obj = createTemplate('ContentCard/subline')
                line_div = line_obj.div
                line_div.string = line.lstrip('<subline>')
                
                card_main.append(line_div)
            elif line.startswith('<spacing>'):
                card_main.append(createTemplate('ContentCard/spacing'))
            elif len(line.split(',', 1)) == 2:
                splited = line.split(',', 1)

                # 文本
                text = createTemplate('ContentCard/Content')
                text.div.string = splited[0]

                # 值
                value = createTemplate('ContentCard/Content')
                value.div.string = splited[1]

                card_main.append(text)
                card_main.append(value)
            else:
                print(f'<error>未知的文件值：{line}')
                return
        add_content.append(card)
        if score:
            content.append(expander)

        with open(output_path, "w", encoding="utf-8") as f:
            f.write(str(base_html))

def _wst_arcname(path, root=None):
    """包内路径：单个文件用它的文件名；文件夹里的文件用「文件夹名/相对路径」。"""
    if root is None:
        return os.path.basename(path)
    return '/'.join([os.path.basename(os.path.normpath(root)),
                     os.path.relpath(path, root).replace('\\', '/')])


def _wst_write(archive, source, arcname, added, locale):
    """向包里写入一项；添加途中源文件被删除时只发警告，不中断其余项。"""
    if arcname in added:
        return  # 已存在的同名项（含包里原有的）不再添加，避免重复条目
    try:
        archive.write(source, arcname)
    except FileNotFoundError:
        # 添加途中被删除（或路径不可达）
        _print_warning(_fallback_text(locale, L_WARNING_FILE_MISSING).format(source), False)
    else:
        added.add(arcname)


def export_wst(output_path, paths, locale):
    """把所有 paths 里的文件/文件夹追加进 output_path 指向的 wst（zip）包。

    - 文件：包内路径就是文件名；文件夹：递归添加，包内路径为「文件夹名/相对路径」
    - 源文件/文件夹不存在，或添加途中被删除：输出 <warning>，继续处理其余项
    - 其它错误（如权限不足）：输出 <error> 并以退出码 1 结束
    """
    try:
        # 'a'：用户传入的 wst 文件可能已存在（保存对话框会先创建出空文件）
        with zipfile.ZipFile(output_path, 'a', zipfile.ZIP_DEFLATED) as archive:
            # 包里已有的同名项不再重复添加（同名条目会让读取端不知道用哪一个）
            added = set(archive.namelist())

            for path in paths:
                if not os.path.exists(path):
                    _print_warning(_fallback_text(locale, L_WARNING_FILE_MISSING).format(path), False)
                    continue

                if os.path.isdir(path):
                    for current, _, names in os.walk(path):
                        for name in names:
                            full = os.path.join(current, name)
                            _wst_write(archive, full, _wst_arcname(full, path), added, locale)
                else:
                    _wst_write(archive, path, _wst_arcname(path), added, locale)
    except (OSError, zipfile.BadZipFile) as e:
        _print_error(_fallback_text(locale, L_ERROR_WST_FAILED).format(output_path, e), False)
        sys.exit(1)


def load_html_files(root: str = "a", encoding: str = "utf-8") -> dict[str, str]:
    """
    递归读取 root 目录下所有 .html 文件，返回 {相对路径(不含扩展名): 文件内容} 的字典。

    例如 root="a" 时：
        a/a.html      -> {"a": "..."}
        a/b/c.html    -> {"b/c": "..."}
    """
    root_path = Path(root)
    result: dict[str, str] = {}

    if not root_path.is_dir():
        raise NotADirectoryError(f"{root} 不是一个目录")

    for path in root_path.rglob("*.html"):
        if not path.is_file():
            continue
        # 相对于 root 的路径，去掉 .html 后缀，统一用 / 分隔
        key = path.relative_to(root_path).with_suffix("").as_posix()
        result[key] = BeautifulSoup(path.read_text(encoding=encoding), 'html.parser')

    return result

def main():
    global HTML_FILE, args

    usage = highlight("xmlParser.py source symbol [[-m | --html export_path template_path cpu_score ram_score graphics_score 3d_score disk_score  [-s | --score]] | [-w | --wst export_path file1 [file2 [file3 ...]]]] [-h | --help]")

    parser = argparse.ArgumentParser(
        description="Process a symbol file",
        usage=usage,
    )
    parser.add_argument(
        "source",
        help="path to the source file"
    )
    parser.add_argument(
        "symbol",
        help="path to the symbol file"
    )

    group = parser.add_mutually_exclusive_group()
    group.add_argument(
        '-m', '--html', 
        help="Export file to html",
        action='store_true'
    )
    group.add_argument(
        '-w', '--wst', 
        help="Export file to wst.",
        action='store_true'
    )

    args, unknown = parser.parse_known_args()

    # 第二阶段：根据模式选择对应的子解析器来解析剩余参数
    if args.html:
        # test 模式：需要 c，可选 -d/--test2
        test_parser = argparse.ArgumentParser(
            prog='main.py',
            usage=usage
        )
        test_parser.add_argument(
            'output_path',
            help='The output file path.',
        )
        test_parser.add_argument(
            'template_path',
            help='The HTML template file path.',
        )

        # 分数
        test_parser.add_argument('cpu_score', type=float)
        test_parser.add_argument('ram_score', type=float)
        test_parser.add_argument('graphics_score', type=float)
        test_parser.add_argument('d3_score', type=float)
        test_parser.add_argument('disk_score', type=float)

        test_parser.add_argument(
            '-s', '--score', 
            help='Add score table.',
            action='store_true'
        )

        test_args = test_parser.parse_args(unknown)

        args.output_path = test_args.output_path
        args.template_path = test_args.template_path
        args.score = test_args.score
        args.cpu_score = test_args.cpu_score
        args.ram_score = test_args.ram_score
        args.graphics_score = test_args.graphics_score
        args.d3_score = test_args.d3_score
        args.disk_score = test_args.disk_score
        args.score = test_args.score
    elif args.wst:
        # update 模式：需要 output_path，以及至少一个 xml_path（可多个）
        update_parser = argparse.ArgumentParser(
            prog='main.py',
            usage=usage
        )
        update_parser.add_argument(
            'output_path',
            help='The output file path.',
        )
        update_parser.add_argument(
            'xml_path', 
            nargs='+',
            help='The xml path to import.'
        )
        update_args = update_parser.parse_args(unknown)

        args.output_path = update_args.output_path
        args.xml_path = update_args.xml_path
    else:
        # 既没有 --test 也没有 --update，则不允许有多余参数
        if unknown:
            parser.error(f"Unrecognized arguments: {' '.join(unknown)}")

    # 收集所有路径并逐一检查；路径不对时尽量用符号文件里的文案报错
    # （--wst 用不到 source/symbol，只借 symbol 读出 $locale 作为警告/错误的文案来源）

    locale = None
    if args.wst or not os.path.exists(args.source) or not os.path.exists(args.symbol):
        locale = _read_locale(args.symbol)

    all_exist = True
    if not args.wst:
        if not os.path.exists(args.source):
            _print_error(_fallback_text(locale, L_ERROR_SOURCE_MISSING).format(args.source), False)
            all_exist = False

        if not os.path.exists(args.symbol):
            _print_error(_fallback_text(locale, L_ERROR_SYMBOL_MISSING).format(args.symbol), False)
            all_exist = False


    if args.html:
        if (not os.path.isfile(args.source)) and (not os.path.isdir(args.symbol)):
            all_exist = False

        if not all_exist:
            sys.exit(1)  # 存在不存在的路径，退出码为1

        HTML_FILE = load_html_files(args.template_path) # html文件

        export_html(args.source, args.symbol, AVAILABLE, args.output_path, args.score)
    elif args.wst:
        export_wst(args.output_path, args.xml_path, locale)
    else:
        try:
            if (not os.path.isfile(args.source)) and (not os.path.isfile(args.symbol)):
                all_exist = False

            if not all_exist:
                sys.exit(1)  # 存在不存在的路径，退出码为1

            parse_xml(args.source, args.symbol)
        except SignalConfigError as e:
            _print_error(e)
            sys.exit(1)
        sys.exit(0)

if __name__ == '__main__':
    try:
        main()
    except Exception:
        _print_error(format_exc(), False)