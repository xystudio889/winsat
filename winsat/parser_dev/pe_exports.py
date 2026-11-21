"""Dump PE exports / imports of a DLL. Usage: python pe_exports.py <path>"""
import sys
import pefile

path = sys.argv[1]
pe = pefile.PE(path, fast_load=True)
pe.parse_data_directories(
    directories=[
        pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"],
        pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
    ]
)

print(f"== {path}")
print(f"machine={hex(pe.FILE_HEADER.Machine)}")

exports = []
if hasattr(pe, "DIRECTORY_ENTRY_EXPORT"):
    for sym in pe.DIRECTORY_ENTRY_EXPORT.symbols:
        name = sym.name.decode() if sym.name else f"ordinal:{sym.ordinal}"
        exports.append(name)
print(f"exports({len(exports)}): {exports}")

imports = []
if hasattr(pe, "DIRECTORY_ENTRY_IMPORT"):
    for entry in pe.DIRECTORY_ENTRY_IMPORT:
        imports.append(entry.dll.decode())
print(f"imported_dlls: {imports}")
