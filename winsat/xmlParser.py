from lxml import etree
import argparse
import json
import re

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
        print(output + ',' + element)
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
        if isinstance(v, str):
            # ---------- 普通模式 ----------
            try:
                element = root.xpath(v)
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
                print(f'<error>{e}')

        elif isinstance(v, list):
            # ---------- 合并模式 / 后缀模式 ----------
            try:
                if len(v) != 2 or not isinstance(v[1], str):
                    print('<error>合并/后缀模式下，列表格式应为 [xpath, unit] 或 [[path1, path2, ...], sep]')
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
                    print('<error>合并/后缀模式下，列表首项应为字符串或字符串列表')
            except ValueError:
                print(k + ',无')
            except Exception as e:
                print(f'<error>{e}')
        else:
            print('<error>出现了未知的符号')


if __name__ == '__main__':
    parse_xml('C:\\Windows\\Performance\\WinSAT\\DataStore\\2026-09-05 21.36.19.510 Cpu.Assessment (Initial).WinSAT.xml', 'Signals\\CPU.json')