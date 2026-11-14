"""Pinned cooperation sources and exact production build inputs."""

from __future__ import annotations

from pathlib import Path

from eng.linux.cooperative.third_party import (
    BUILD_INPUTS as THIRD_PARTY_INPUTS,
)
from eng.linux.cooperative.third_party import (
    CORE_VERSION as CORE_VERSION,
)
from eng.linux.cooperative.third_party import (
    DATA as DATA,
)
from eng.linux.cooperative.third_party import (
    FCITX_PINS as FCITX_PINS,
)
from eng.linux.cooperative.third_party import (
    PATCH_PROJECTS as PATCH_PROJECTS,
)
from eng.linux.cooperative.third_party import (
    PRIVATE_VERSIONS as PRIVATE_VERSIONS,
)
from eng.linux.cooperative.third_party import (
    SOURCE_VERSIONS as SOURCE_VERSIONS,
)
from eng.linux.cooperative.third_party import (
    hashes as hashes,
)
from eng.linux.cooperative.third_party import (
    sha256 as sha256,
)

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
PATCHES = HERE / "patches"
QT_VERSION = "6.11.2"
QT_SOURCE_VERSION = "5.1.15"
QT_PIN = "272b6b9eba97f0ed97e8a5caaf0bb22c6123f462"
QT_SHA256 = "b2c5f7c47009656ce79eac0c8f047a73b224210d0bc232150349577525fbd0d3"
DATA_ARCHIVES = {name: digest for directory, name, digest in DATA if directory == "libime/data"}
CHINESE_DATA_ARCHIVES = {
    name: digest for directory, name, digest in DATA if directory != "libime/data"
}
FCITX_BUILD_INPUTS = (
    "eng/linux/cooperative/inputs.py",
    "eng/linux/cooperative/fcitx.py",
    *THIRD_PARTY_INPUTS,
    "src/linux/native/fcitx5/typelay-fcitx-v1.h",
    "src/linux/native/common/gtk-textbuffer-write.inc",
    "src/linux/native/common/presentation-identity.h",
    "src/linux/native/common/write-settlement.h",
    "src/linux/native/common/ordered-publication.h",
    "src/linux/native/common/overlay-design.h",
    *(
        str(path.relative_to(ROOT))
        for path in sorted((ROOT / "src/linux/native/gtk4").glob("*"))
        if path.suffix in {".c", ".h"} or path.name == "CMakeLists.txt"
    ),
)
QT_BUILD_INPUTS = (
    "eng/linux/cooperative/inputs.py",
    "eng/linux/cooperative/third_party.py",
    "eng/linux/cooperative/qt.py",
    "eng/linux/cooperative/patches/fcitx5-qt.patch",
    "eng/linux/cooperative/qt-runtime-probe.cpp",
    "src/linux/native/fcitx5/typelay-fcitx-qt-v1.h",
    "src/linux/native/common/overlay-design.h",
    *(
        str(path.relative_to(ROOT))
        for path in sorted((ROOT / "src/linux/native/qt6").glob("*"))
        if path.is_file()
    ),
)


FCITX_TEST_INPUTS = (
    "src/linux/native/CMakeLists.txt",
    *(
        str(path.relative_to(ROOT))
        for path in sorted((ROOT / "src/linux/native/gtk3").glob("*"))
        if path.is_file()
    ),
    "tests/linux/fcitx_cooperative/run.py",
    "tests/linux/fcitx_cooperative/fixture.c",
    "tests/linux/native_e2e.py",
    "tests/linux/fixtures/gtk4_fixture.c",
    "tests/linux/fixtures/gtk_fixture.c",
    "tests/linux/fixtures/gtk_glossary.inc",
    "tests/linux/fixtures/gtk_font_observation.inc",
    "tests/linux/fixtures/gtk_input_notifications.inc",
    "tests/linux/fixtures/gtk_direct_epoch.inc",
    "tests/linux/fixtures/gtk_reentry.inc",
    "tests/linux/fixtures/gtk_motion.inc",
    "tests/linux/support/fixture_state.h",
)


QT_TEST_INPUTS = (
    "tests/linux/qt_cooperative/run.py",
    "tests/linux/qt_cooperative/fixture.cpp",
    "tests/linux/qt_cooperative/font_order.cpp",
    "src/linux/native/common/overlay-design.h",
    "src/linux/native/qt6/overlay-regional-display.h",
    "tests/linux/qt_cooperative/qpa_ack.cpp",
    "tests/linux/fixtures/qt_native_fixture.cpp",
    "tests/linux/support/xtest.py",
)
