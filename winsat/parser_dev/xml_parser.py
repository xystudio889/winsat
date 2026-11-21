"""XML 符号解析 + HTML 导出：C# 通过 parser.dll 调用的核心逻辑。

与 examples/xmlParser.py 命令行版的区别
---------------------------------------
* 删掉 argparse / colorama 分词高亮 / main() / sys.exit：只保留核心处理器与
  私有辅助函数；
* 不再 print：两个公开函数都返回「一整串文本」——
    - parse_xml  一次输出（原来的一次 print）占一行，行之间用 \\n 连接；
    - export_html 一切正常时返回空字符串，否则返回 <warning> / <error> 行；
* 每行都以协议前缀开头（<line> / <subline> / <spacing> / <warning> / <error>，
  或者 "键,值" / "键 #序号,值"），C# 侧按前缀识别；
  警告、错误文本里的换行统一转义成字面量 \\n，保证「一行一条」；
* export_html 的语言表与模板目录改为模块常量（AVAILABLE / 模板搜索路径），
  不再从命令行传入；score 改为可选（None 或 Score 结构）；
* export_wst 已内化到 C# 层（WstPackager.cs），本文件不再提供。

错误约定
--------
调用方传错参数、文件读不出来这类「改一下输入就能好」的问题，一律返回
<error> 行交给 C# 显示；只有程序内部真的出了意外（例如符号文件的 $locale
本身残缺到无法生成任何文案）才会向上抛异常，由桥接层转成 -1 +
last_error()。
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from pathlib import Path
from traceback import format_exc

from bs4 import BeautifulSoup
from lxml import etree

__version__ = 8.2

CDATA_RE = re.compile(r'<!\[CDATA\[\s+(.+?)\s+\]\]>', re.DOTALL)

# 特殊值：不是 xpath，而是直接产生结构化标记（键名只用于区分，spacing 不需要键名）
SPECIAL_VALUES = {
    'line': '<line>',
    'subline': '<subline>',
    'spacing': '<spacing>',
}

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
# 最后两条（warning_file_missing / error_wst_failed）已随 export_wst 迁到 C# 层，
# 这里保留键名，方便和符号文件里的 $locale 对照。
DEFAULT_TEXTS = {
    L_ERROR_NO_LOCALE: 'signal file has no valid "$locale" object',
    L_ERROR_LOCALE_MISSING: '"$locale" is missing the key "{0}"',
    L_ERROR_SOURCE_MISSING: "source file path '{0}' does not exist",
    L_ERROR_SYMBOL_MISSING: "symbol file path '{0}' does not exist",
    L_WARNING_FILE_MISSING: "file '{0}' does not exist",
    L_ERROR_WST_FAILED: "failed to write wst package '{0}': {1}",
}

# 要导出/解析的语言：界面语言标记 -> 符号文件名（不含扩展名）
AVAILABLE = {'zh-CN': 'zh-Hans-cn', 'en-US': 'en-US'}

# export_html 需要的模板（相对模板根目录、不含 .html）
REQUIRED_TEMPLATES = (
    'index',
    'grid',
    'expander',
    'ContentCard/index',
    'ContentCard/bigline',
    'ContentCard/subline',
    'ContentCard/spacing',
    'ContentCard/Content',
)


@dataclass(frozen=True)
class Score:
    """export_html 可选的分数表。

    字段顺序与 C ABI 里的 double[5] 一致（cpu, ram, graphics, d3, disk），
    桥接层按位置还原成这个结构。
    """

    cpu: float
    ram: float
    graphics: float
    d3: float
    disk: float


class SignalConfigError(Exception):
    """符号文件写法有误（与「取不到值」区分开）。"""


# ------------------------------------------------------------ 输出行（协议） --

def _escape(text) -> str:
    """把换行转义成字面量 \\n：保证「一条输出 = 一行」。"""
    return str(text).replace('\n', '\\n')


def _error_line(text, *args) -> str:
    return '<error>' + _escape(text.format(*args) if args else text)


def _warning_line(text, *args) -> str:
    return '<warning>' + _escape(text.format(*args) if args else text)


# ------------------------------------------------------------------ 文案 ----

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


# ------------------------------------------------------------ 取值（XPath） --

def _get_text(element, locale):
    """取出元素的文本值（兼容 CDATA 包裹）。"""
    matches = CDATA_RE.search(element)
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


def _render_transpose(group, root, defs, locale, out):
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
            out.append('<spacing>')  # 组与组之间插入一个空隙（#1 → #2 时）
        for label, values, marker in members:
            if marker is not None:
                out.append(marker)
            elif index < len(values):
                out.append('{0} #{1},{2}'.format(label, index, values[index]))


# --------------------------------------------------------------- 公开接口 --

def parse_xml(source: str, symbol: str) -> str:
    """解析 source（XML）与 symbol（符号文件 JSON），返回一整串输出文本。

    一行一条：`<line>标题` / `<subline>小标题` / `<spacing>` /
    `键,值` / `键 #序号,值`，以及解析失败时的 `<error>...` 行。
    """
    try:
        tree = etree.parse(source)
    except (OSError, etree.Error) as exc:
        return _error_line('{0}: {1}', _fallback_text(None, L_ERROR_SOURCE_MISSING).format(source), exc)
    root = tree.getroot()

    try:
        with open(symbol, 'r', encoding='utf-8-sig') as f:
            signals = json.load(f)
    except (OSError, ValueError) as exc:
        return _error_line('{0}: {1}', _fallback_text(None, L_ERROR_SYMBOL_MISSING).format(symbol), exc)

    if not isinstance(signals, dict):
        return _error_line(DEFAULT_TEXTS[L_ERROR_NO_LOCALE])

    out: list[str] = []

    # $locale：解析器要输出的文案全写在符号文件里；取不到就没法输出，直接算写法错误
    locale = signals.pop('$locale', None)
    if not isinstance(locale, dict):
        raise SignalConfigError(DEFAULT_TEXTS[L_ERROR_NO_LOCALE])
    none_text = _locale_text(locale, L_NONE)

    requires = signals.pop('$requires', 0)
    if __version__ < requires:
        out.append(_error_line(
            _locale_text(locale, L_ERROR_VERSION).format(requires, __version__)))
        return '\n'.join(out)

    defs = signals.pop('$defs', {})

    for key, value in signals.items():
        name = key.lstrip('$')

        # ---------- 转置模式：值是 object，或用 "$defs名" 引用一个 object ----------
        group = _transpose_group(value, defs)
        if group is not None:
            try:
                _render_transpose(group, root, defs, locale, out)
            except SignalConfigError as exc:
                out.append(_error_line(
                    _locale_text(locale, L_ERROR_IN_SYMBOL).format(exc, name)))
            except ValueError:
                out.append('{0},{1}'.format(name, none_text))
            except Exception as exc:
                out.append(_error_line(_locale_text(locale, L_ERROR_PARSE_FAILED).format(
                    _locale_text(locale, L_MODE_TRANSPOSE), name, exc, format_exc())))
            continue

        if not isinstance(value, (str, list)):
            out.append(_error_line(_locale_text(locale, L_ERROR_UNKNOWN_SYMBOL).format(name)))
            continue

        if isinstance(value, list) and len(value) != 2:
            out.append(_error_line(_locale_text(locale, L_ERROR_LIST_LENGTH).format(
                _locale_text(locale, L_MODE_COMBINED), name)))
            continue

        if isinstance(value, list) and not (isinstance(value[0], (list, str))
                                            or isinstance(_resolve(value[1], defs), dict)):
            out.append(_error_line(_locale_text(locale, L_ERROR_NOT_COMBINED).format(
                _locale_text(locale, L_MODE_COMBINED), name)))
            continue

        try:
            line = _special_line(value, name)
            if line is not None:
                out.append(line)
                continue
            values = _values_of(value, root, defs, locale)
        except SignalConfigError as exc:
            out.append(_error_line(_locale_text(locale, L_ERROR_IN_SYMBOL).format(exc, name)))
            continue
        except ValueError:
            out.append('{0},{1}'.format(name, none_text))
            continue
        except Exception as exc:
            out.append(_error_line(_locale_text(locale, L_ERROR_PARSE_FAILED).format(
                _mode_of(value, locale), name, exc, format_exc())))
            continue

        if len(values) == 1:
            out.append('{0},{1}'.format(name, values[0]))
        else:
            for index, item in enumerate(values):
                out.append('{0} #{1},{2}'.format(name, index, item))

    return '\n'.join(out)


def export_html(source: str, symbol: str, output_path: str,
                score: Score | None = None) -> str:
    """把 source（XML）+ symbol（符号目录）渲染成一个 HTML 文件。

    - symbol 是**目录**，里面按 AVAILABLE 的约定放 `<语言>.json`；
    - score 为 None 时不导出分数表，否则按 Score 填表（并列最低分的行加 lowest）；
    - 一切正常时返回空字符串；
    - 有问题时返回 <warning> / <error> 行（多条用 \\n 连接）：出现 <error> 时
      不写文件，只有 <warning> 时仍然写文件。
    """
    problems: list[str] = []
    errors: list[str] = []

    if not os.path.isfile(source):
        return _error_line(_fallback_text(None, L_ERROR_SOURCE_MISSING).format(source))
    if not os.path.isdir(symbol):
        return _error_line(_fallback_text(None, L_ERROR_SYMBOL_MISSING).format(symbol))

    try:
        templates = _templates()
    except SignalConfigError as exc:
        return _error_line(str(exc))

    missing = [key for key in REQUIRED_TEMPLATES if key not in templates]
    if missing:
        return _error_line("HTML template(s) not found under '{0}': {1}",
                           _TEMPLATE_ROOT, ', '.join(missing))

    base_html = templates['index'].__copy__()
    content = base_html.find('div', class_='content')
    if content is None:
        return _error_line("HTML template 'index' has no div.content")

    expander = None
    expander_inlined = False

    if score is not None:
        # 分数表：模板里若已内联一份（方便直接用浏览器预览），就复用它；
        # 否则用 grid.html 追加一份。两个模板的内容保持一致。
        grid = base_html.find('div', class_='grid')
        grid_inlined = grid is not None
        if not grid_inlined:
            grid = templates['grid'].__copy__()

        cells = {
            'CPUScore': score.cpu,
            'RAMScore': score.ram,
            'DWMScore': score.graphics,
            '3DScore': score.d3,
            'DiskScore': score.disk,
        }
        nodes = {}
        for node_id, node_value in cells.items():
            node = grid.find('div', id=node_id)
            if node is None:
                return _error_line("HTML template 'grid' has no div#{0}", node_id)
            nodes[node_id] = node

        total_node = grid.find('div', id='TotalScore')
        if total_node is None:
            return _error_line("HTML template 'grid' has no div#TotalScore")

        total = min(cells.values())

        # 并列最低分的行都加 "lowest" 类：左边两角圆角、右边无圆角，
        # 与第 4 列（基本分数）的无圆角背景连成一条。
        # （模板里预置的 lowest 只是预览用的示例，先清掉再按真实分数重新标）
        for node_id, node_value in cells.items():
            node = nodes[node_id]
            classes = [c for c in (node.get('class') or []) if c != 'lowest']
            if node_value == total:
                classes.append('lowest')
            node['class'] = classes
            node.string = str(node_value)
        total_node.string = str(total)

        # 这里才能 append：bs4 的 append 会把节点从原来的树里搬走（原树会变空），
        # 先 append 的话上面那些 find(id=...) 就都找不到了。
        if not grid_inlined:
            content.append(grid)

        # 高级信息（expander）：同样优先复用模板里已内联的那一份
        expander = base_html.find('div', class_='expander')
        expander_inlined = expander is not None
        if not expander_inlined:
            expander = templates['expander'].__copy__()

        # 同样要先把 add_content 取出来，再（在下面循环里）把 expander 追加进 content
        add_content = expander.find('div', class_='expander-body')
        if add_content is None:
            return _error_line("HTML template 'expander' has no div.expander-body")
    else:
        # 不导出分数表：把模板里内联的分数表 / 高级信息去掉
        for klass in ('grid', 'expander'):
            node = base_html.find('div', class_=klass)
            if node is not None:
                node.decompose()

        # 设置添加的组件
        add_content = content

    # 逐个语言渲染一张信息卡（语言表是模块常量，不再从命令行传入）
    for locale_tag, file_stem in AVAILABLE.items():
        file_path = os.path.join(symbol, file_stem + '.json')
        if not os.path.isfile(file_path):
            errors.append(_error_line(_fallback_text(None, L_ERROR_SYMBOL_MISSING).format(file_path)))
            continue

        lines = parse_xml(source, file_path).split('\n')

        card = templates['ContentCard/index'].__copy__()
        card_main = card.div  # 主卡片
        if card_main is None:  # 如果为空
            card_main = card.new_tag('div')
            card_main['class'] = 'content-card'
        card_main['data-locale'] = locale_tag

        for line in lines:
            if line.startswith('<error>'):
                errors.append(_error_line('At {0}: {1}', locale_tag, line[len('<error>'):]))
            elif line.startswith('<warning>'):
                problems.append(_warning_line('At {0}: {1}', locale_tag, line[len('<warning>'):]))
            elif line.startswith('<line>'):
                line_obj = templates['ContentCard/bigline'].__copy__()
                line_div = line_obj.find('div', class_='line-text')
                if line_div is not None:
                    line_div.string = line[len('<line>'):]
                card_main.append(line_obj)
            elif line.startswith('<subline>'):
                line_obj = templates['ContentCard/subline'].__copy__()
                line_div = line_obj.div
                if line_div is not None:
                    line_div.string = line[len('<subline>'):]
                    card_main.append(line_div)
            elif line.startswith('<spacing>'):
                card_main.append(templates['ContentCard/spacing'].__copy__())
            else:
                splited = line.split(',', 1)
                if len(splited) != 2:
                    errors.append(_error_line('At {0}: unknown line from symbol file: {1}',
                                              locale_tag, line))
                    continue

                # 文本
                text = templates['ContentCard/Content'].__copy__()
                if text.div is not None:
                    text.div.string = splited[0]

                # 值
                value = templates['ContentCard/Content'].__copy__()
                if value.div is not None:
                    value.div.string = splited[1]

                card_main.append(text)
                card_main.append(value)

        add_content.append(card)

    # 高级信息放在所有信息卡之后（模板已内联时它本来就在树里）
    if score is not None and not expander_inlined and expander is not None:
        content.append(expander)

    if not errors:
        try:
            with open(output_path, 'w', encoding='utf-8') as f:
                f.write(str(base_html))
        except OSError as exc:
            errors.append(_error_line("cannot write output file '{0}': {1}", output_path, exc))

    return '\n'.join(errors + problems)


# ------------------------------------------------------------ HTML 模板 ----

_TEMPLATES: dict[str, BeautifulSoup] | None = None
_TEMPLATE_ROOT: Path | None = None


def _template_root() -> Path | None:
    """找模板目录：先找发布形态（.py/.pyd 旁边的 templates），再找源码树形态。"""
    here = Path(__file__).resolve().parent
    candidates = (
        here / 'templates',                          # 发布形态：<输出目录>\templates
        here.parent / 'examples' / 'templates',      # 源码树：ConsoleApp1\examples\templates
        Path.cwd() / 'templates',
        Path.cwd() / 'examples' / 'templates',
    )
    for path in candidates:
        if path.is_dir():
            return path
    return None


def _load_templates(root: Path) -> dict[str, BeautifulSoup]:
    """递归读取 root 下所有 .html，返回 {相对路径(不含扩展名): 文档树}。"""
    return {
        path.relative_to(root).with_suffix('').as_posix(): BeautifulSoup(
            path.read_text(encoding='utf-8'), 'html.parser')
        for path in root.rglob('*.html')
        if path.is_file()
    }


def _templates() -> dict[str, BeautifulSoup]:
    global _TEMPLATES, _TEMPLATE_ROOT
    if _TEMPLATES is None:
        root = _template_root()
        if root is None:
            raise SignalConfigError(
                "HTML template directory not found (looked for 'templates' next to "
                "the parser module and '../examples/templates')")
        _TEMPLATE_ROOT = root
        _TEMPLATES = _load_templates(root)
    return _TEMPLATES


# ------------------------------------------------------------ 运行环境自查 --

def runtime_info() -> str:
    """「到底用的是哪个 Python、依赖从哪来」——出问题时的第一手材料。

    返回多行 `key=value`（同样是一行一条）。C# 侧 `--diagnose` 与 Demo 第 1 段
    都会打印它：客户机器上跑不起来时，先看这里的 `sys.prefix` / `lxml` 路径，
    就能判断是我们带过去的运行时，还是被机器上别的 Python 抢走了。

    这是诊断函数，**不允许抛异常**（一个会崩的自检没有任何用处），
    所以每一项都单独兜底。
    """
    import platform
    import sys

    def safe(func) -> str:
        try:
            return str(func())
        except Exception as exc:  # pragma: no cover - 只在环境残缺时走
            return '<{0}: {1}>'.format(type(exc).__name__, exc)

    def module_path(name: str) -> str:
        try:
            module = __import__(name)
        except Exception as exc:  # pragma: no cover
            return '<import failed: {0}>'.format(exc)
        return '{0} ({1})'.format(getattr(module, '__file__', '?'),
                                  getattr(module, '__version__', '?'))

    def site_packages() -> str:
        import site
        return ';'.join(site.getsitepackages())

    return '\n'.join([
        'python_version=' + safe(lambda: platform.python_version()),
        'python_compiler=' + safe(lambda: platform.python_compiler()),
        'arch=' + safe(lambda: platform.architecture()[0]),
        # core 指向实际被 import 的解析核心：发布形态是 xml_parser.pyd（源码不外露），
        # 开发形态是 xml_parser.py。客户机上第一眼就该看这一行。
        'core=' + safe(lambda: __file__),
        'core_compiled=' + safe(lambda: not __file__.endswith('.py')),
        'sys_prefix=' + safe(lambda: sys.prefix),
        'sys_base_prefix=' + safe(lambda: sys.base_prefix),
        'sys_executable=' + safe(lambda: sys.executable or '<empty>'),
        'sys_path=' + safe(lambda: ';'.join(sys.path)),
        'frozen=' + safe(lambda: getattr(sys, 'frozen', False)),
        'isolated=' + safe(lambda: sys.flags.isolated),
        'site_packages=' + safe(site_packages),
        'lxml=' + module_path('lxml'),
        'lxml.etree=' + module_path('lxml.etree'),
        'bs4=' + module_path('bs4'),
        'templates=' + safe(lambda: _template_root() or '<not found>'),
    ])
