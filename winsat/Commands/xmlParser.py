from lxml import etree
import argparse
import json
import re
import os
import sys
from traceback import format_exc

cdata_regex = re.compile(r'<!\[CDATA\[\s+(.+?)\s+\]\]>', re.DOTALL)


def _get_text(element):
    """取出元素的文本值（兼容 CDATA 包裹）。"""
    matches = re.search(cdata_regex, element)
    if matches:
        element = matches.group(1)
    if not element:
        raise ValueError('元素不存在')
    return element


def parse_element(element, output, show_in_termal=True):
    element = _get_text(element)
    if show_in_termal:
        print(output + ',' + element.strip())
    return output, element


def _xpath_first(root, path):
    """执行 XPath，若匹配多项则只取第一项；无匹配抛异常。"""
    result = root.xpath(path)
    if len(result) == 0:
        raise ValueError('元素不存在')
    return result[0]


def parse_xml(file, signal):
    # 加载 XML
    tree = etree.parse(file)
    root = tree.getroot()

    with open(signal, 'r', encoding='utf-8') as f:
        signals = json.load(f)

    for k, v in signals.items():
        k = k.lstrip('$')
        if isinstance(v, str):
            # ---------- 普通模式 ----------
            try:
                element = root.xpath(v)
                if v.lower() == 'line': # 线段模式：显示线
                    print(f'<line>{k}')
                    continue
                if v.lower() == 'spacing': # 空格模式：添加空内容
                    print(f'<spacing>')
                    continue
                if len(element) == 1:
                    parse_element(element[0], k)
                elif len(element) == 0:
                    raise ValueError('元素不存在')
                else:
                    for i, e in enumerate(element):
                        parse_element(e, f'{k} #{i}')
            except ValueError:
                print(k + ',无')
            except Exception as e:
                print(f'<error>(来自普通模式) 解析 "{k}" 遇到未处理的错误: {e}\\n{format_exc().replace("\n", "\\n")}')

        elif isinstance(v, list):
            # ---------- 合并模式 / 后缀模式 / 翻译模式 ----------
            try:
                if len(v) != 2:
                    print(f'<error>合并 / 后缀 / 翻译模式 下，列表应为2项 (符号名: "{k}")')
                elif isinstance(v[1], dict):
                    # 翻译模式：v = [xpath, {source: translated}]
                    xpath, translations = v
                    value = _get_text(_xpath_first(root, xpath))

                    print(k + ',' + translations.get(value, value))
                elif isinstance(v[0], list):
                    # 合并模式：v = [[path1, path2, ...], sep]
                    paths, sep = v
                    # 每个 path 只取第一项，再取文本，最后按 sep 拼接
                    values = [_get_text(_xpath_first(root, p)) for p in paths]
                    print(k + ',' + sep.join(values))
                elif isinstance(v[0], str):
                    # 后缀模式：v = [xpath, unit]
                    xpath, unit = v
                    value = _get_text(_xpath_first(root, xpath))
                    print(k + ',' + value + unit)
                else:
                    print(f'<error>合并/后缀模式下，列表首项应为字符串或字符串列表 (符号名: "{k}")')
            except ValueError:
                print(k + ',无')
            except Exception as e:
                print(f'<error>(来自合并模式 / 后缀模式) 解析 "{k}" 遇到未处理的错误: {e}\\n{format_exc().replace("\n", "\\n")}')
        else:
            print(f'<error>出现了未知的符号 (符号名: "{k}")')

def main():
    parser = argparse.ArgumentParser(
        description="处理符号文件"
    )
    parser.add_argument(
        "source",
        help="源文件路径"
    )
    parser.add_argument(
        "symbol",
        help="符号文件路径"
    )
    args = parser.parse_args()

    # 收集所有路径并逐一检查
    all_exist = True
    if not os.path.exists(args.source):
        print(f"<error>源文件路径 '{args.source}' 不存在")
        all_exist = False

    if not os.path.exists(args.symbol):
        print(f"<error>符号文件路径 '{args.symbol}' 不存在")
        all_exist = False

    if not all_exist:
        sys.exit(1)  # 存在不存在的路径，退出码为1

    parse_xml(args.source, args.symbol)
    sys.exit(0)

if __name__ == '__main__':
    try:
        main()
    except Exception:
        print(f'<error>{format_exc().replace("\n", "\\n")}')