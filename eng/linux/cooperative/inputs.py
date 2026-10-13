"""Pinned cooperation sources and exact production build inputs."""

from __future__ import annotations

import hashlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
PATCHES = HERE / "patches"
CORE_VERSION = "5.1.22"
SOURCE_VERSIONS = {
    "fcitx5": CORE_VERSION,
    "fcitx5-chinese-addons": "5.1.14",
    "fcitx5-gtk": "5.1.7",
    "libime": "1.1.16",
}
PRIVATE_VERSIONS = {
    **{f"Fcitx5{name}": CORE_VERSION for name in ("Core", "Config", "Utils")},
    **{f"LibIME{name}": SOURCE_VERSIONS["libime"] for name in ("Core", "Pinyin", "Table")},
}
QT_VERSION = "6.11.2"
QT_SOURCE_VERSION = "5.1.15"
QT_PIN = "272b6b9eba97f0ed97e8a5caaf0bb22c6123f462"
QT_SHA256 = "b2c5f7c47009656ce79eac0c8f047a73b224210d0bc232150349577525fbd0d3"
FCITX_PINS = {
    "fcitx5": (
        "c7ecdb931d8b378ccdcd87382a3ef7ff0bd10def",
        "3743c26ee354823c3f808afd6e17af32ed5e6460edc562854e95d5b0f4d8cd4d",
    ),
    "fcitx5-chinese-addons": (
        "fa65a9b46f805fc491b88c53f6b0159fd6f756af",
        "97196e7f922c70cc18eb30e38a146abb34cf70f6ea4b37492b78bcfc2c5598f2",
    ),
    "fcitx5-gtk": (
        "3b18d2ab7401d4233daf38bbc5896f1703685a43",
        "d0afcf4188ce630a5824a10f4d2ba501dcbe3440687cebedba323c95dcc4ba9c",
    ),
    "libime": (
        "901a73898df3bf5990d6535bf8e7e02b190aee2e",
        "d423e691816a3d7229e44bb2e3fa5423513ec64251bc3aafa5389983155378b8",
    ),
    "kenlm": (
        "4cb443e60b7bf2c0ddf3c745378f76cb59e254e5",
        "11df4f929b175f0b3bd26be7a5a83b3c17cdefa83baecab0b6e69f77430184ea",
    ),
}
PATCH_PROJECTS = ("fcitx5", "fcitx5-chinese-addons", "fcitx5-gtk")
DATA = (
    (
        "libime/data",
        "lm_sc.arpa-20260629.tar.zst",
        "06808333b9173e5374cf2cb5afc12d08f5625bf9abb536489cac376fc05f2e7f",
    ),
    (
        "libime/data",
        "dict-20260703.tar.zst",
        "c686cab6df8964c48d596f57d205bac31fc72870b06a83017e44503df8c09697",
    ),
    (
        "libime/data",
        "table-20240108.tar.zst",
        "3e9d87b04a393f131723472c8eaa860dd23c378a3d4f6a9005513b2a95b3614b",
    ),
    (
        "fcitx5-chinese-addons/modules/pinyinhelper",
        "py_stroke-20250329.tar.gz",
        "c1a7ca7225d3614ab83353fc827503006a980447762018f53760425d7b5303a6",
    ),
    (
        "fcitx5-chinese-addons/modules/pinyinhelper",
        "py_table-20121124.tar.gz",
        "42146ac97de6c13d55f9e99ed873915f4c66739e9c11532a34556badf9792c04",
    ),
)

DATA_ARCHIVES = {name: digest for directory, name, digest in DATA if directory == "libime/data"}
CHINESE_DATA_ARCHIVES = {
    name: digest for directory, name, digest in DATA if directory != "libime/data"
}
FCITX_BUILD_INPUTS = (
    "eng/linux/cooperative/inputs.py",
    "eng/linux/cooperative/fcitx.py",
    *(f"eng/linux/cooperative/patches/{project}.patch" for project in PATCH_PROJECTS),
    "src/linux/native/fcitx5/typelay-fcitx-v1.h",
    "src/linux/native/common/gtk-textbuffer-write.inc",
    *(
        str(path.relative_to(ROOT))
        for path in sorted((ROOT / "src/linux/native/gtk4").glob("*"))
        if path.suffix in {".c", ".h"} or path.name == "CMakeLists.txt"
    ),
)
QT_BUILD_INPUTS = (
    "eng/linux/cooperative/inputs.py",
    "eng/linux/cooperative/qt.py",
    "eng/linux/cooperative/patches/fcitx5-qt.patch",
    "eng/linux/cooperative/qt-runtime-probe.cpp",
    "src/linux/native/fcitx5/typelay-fcitx-qt-v1.h",
    *(
        str(path.relative_to(ROOT))
        for path in sorted((ROOT / "src/linux/native/qt6").glob("*"))
        if path.is_file()
    ),
)


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def hashes(files):
    return {relative: sha256(ROOT / relative) for relative in files}
