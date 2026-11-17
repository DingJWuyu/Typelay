"""Required samples, independent of check.yml's upload patterns.

Keys are artifact-name prefixes. Each sample belongs to an existing producer step;
this is deliberately a contract for check.yml, not a workflow execution engine.
"""

from dataclasses import dataclass

SOURCE = "artifacts/ci/source.json"
LINUX = "artifacts/linux/"
FCITX = LINUX + "fcitx-cooperative-5.1.22/"
NATIVE = ("native", "native-gtk", "native-gtk4", "native-qt")
GROUPS = ("contracts", "browser", "settings", *NATIVE)


def files(directory, names):
    return tuple(directory + name for name in names.split())


@dataclass(frozen=True)
class Evidence:
    producer: str
    paths: tuple[str, ...]
    groups: tuple = ()


@dataclass(frozen=True)
class Upload:
    evidence: tuple[Evidence, ...]
    groups: tuple = ()
    packages: bool = False
    outcome: str | None = None
    missing: str = "warn"


source = Evidence("Record tested commit", (SOURCE,))
windows_package = Evidence("windows-package", ("artifacts/TypeLay-Windows-preview.zip.sha256",))
linux_build = Evidence("build", (LINUX + "package/build-info.json",))
trx = Evidence(
    "windows-contracts",
    files(
        "artifacts/test-results/",
        "core.trx current-contracts.json contracts-App/result.json contracts-App/run/app.trx",
    ),
)
physical = Evidence("physical-tab-package", ("artifacts/physical-tab-package/result.json",), (2,))
continuous = Evidence(
    "windows-continuous",
    files("artifacts/continuous-translation/", "result.json trace-events.jsonl"),
)
notepad = Evidence(
    "windows-ordinary-notepad",
    files("artifacts/ordinary-notepad/", "result.json trace-events.jsonl"),
    ("ordinary",),
)
hy_prepare = Evidence("hy-prepare", files("artifacts/hy-results/", "result.json worker.stderr"))
hy_production = Evidence("hy-production", ("artifacts/hy-production/result.json",), ("hy",))
hy_packages = Evidence(
    "hy-packages",
    files("artifacts/hy-packages/", "result.json settings.png trace-events.jsonl"),
    ("hy",),
)
index_lifecycle = Evidence("index-lifecycle", ("artifacts/index-production/result.json",))
index_browser = Evidence(
    "windows-browser",
    files("artifacts/browser-cooperation/", "result.json trace-events.jsonl browser.png")
    + ("artifacts/browser-profile/before-browser-cooperation.json",),
)
index_package = Evidence("index-package", ("artifacts/TypeLay-Windows-preview.zip.sha256",))
qt_language = Evidence(
    "qt-language-options",
    files(
        LINUX + "qt-language-options/",
        "versions.json language-options-state.json language-options-events.log language-search.png",
    )
    + (LINUX + "qt-language-options.log",),
    ("native", "native-qt"),
)
gtk = Evidence(
    "gtk-desktop",
    files(LINUX + "gtk-desktop/", "result.json events.jsonl desktop.log screenshot.png"),
    ("native-gtk",),
)
gtk4 = Evidence(
    "gtk4-desktop",
    files(LINUX + "gtk4-desktop/", "result.json events.jsonl desktop.log screenshot.png"),
    ("native-gtk4",),
)
gtk4_probe = Evidence(
    "fcitx",
    files(
        FCITX,
        "test-inputs.json gtk4-glossary-result.json gtk4-glossary-events.jsonl gtk4-glossary.log gtk4-glossary.png gtk4-glossary-session/state.json gtk4-glossary-session/cleanup.log",
    ),
    ("native-gtk4",),
)
qt = Evidence(
    "qt-desktop",
    files(LINUX + "qt-desktop/", "result.json events.jsonl desktop.log"),
    ("native-qt",),
)
gimp = Evidence(
    "gimp",
    files(
        LINUX + "ordinary-gimp/",
        "result.json events.jsonl desktop.log screenshot.png niri.kdl scenario.failed",
    ),
    ("native",),
)
linux_packages = (
    Evidence("complete-package", ("artifacts/TypeLay-Linux-preview-x86_64.tar.gz",), ("native",)),
    Evidence("fcitx-package", ("artifacts/TypeLay-Fcitx-cooperative.tar.gz",), NATIVE),
)
fcitx_build = Evidence("build", files(FCITX, "build-inputs.json versions.json"), NATIVE)

# Comprehensive diagnostics retain visual evidence not included in the small event uploads.
linux_diagnostics = (
    source,
    Evidence("build", files(LINUX, "build.json build.log")),
    Evidence("browser-native", (LINUX + "browser-native/result.json",), ("browser",)),
    Evidence("firefox-native", (LINUX + "firefox-native/result.json",), ("browser",)),
    Evidence("vscode-transactions", (LINUX + "vscode/result.json",), ("browser",)),
    Evidence("vscode-model", (LINUX + "vscode-model/result.json",), ("browser",)),
    Evidence("editor-transactions", (LINUX + "editor-probe/result.json",), ("browser",)),
    Evidence("fcitx-client", (LINUX + "fcitx-probe/result.json",), ("native",)),
    Evidence("hy-packages", (LINUX + "hy-package/result.json",), ("settings",)),
    Evidence("settings", (LINUX + "setup-settings/result.json",), ("settings",)),
    Evidence("tutorial", (LINUX + "setup-tutorial/result.json",), ("settings",)),
    Evidence("browser-desktop", (LINUX + "desktop/result.json",), ("browser",)),
    gtk,
    Evidence(
        "gtk-desktop", files(LINUX + "gtk-desktop/", "niri.kdl scenario.failed"), ("native-gtk",)
    ),
    gtk4,
    Evidence(
        "gtk4-desktop",
        files(
            LINUX + "gtk4-desktop/",
            "niri.kdl scenario.failed gtk4-connected-before-map/result.json",
        ),
        ("native-gtk4",),
    ),
    qt,
    Evidence(
        "qt-desktop",
        files(
            LINUX + "qt-desktop/",
            "screenshot.png niri.kdl scenario.failed qt-motion-expand/frame-000.png qt-motion-expand/frame-001.png qt-motion-chinese-draft/frame-000.png qt-motion-chinese-draft/frame-001.png",
        ),
        ("native-qt",),
    ),
    Evidence(
        "qt",
        files(LINUX + "qt-cooperative/", "versions.json probe.log language-search.png"),
        ("native", "native-qt"),
    ),
    qt_language,
    fcitx_build,
    Evidence("fcitx", files(FCITX, "test-inputs.json core.log"), NATIVE),
    Evidence(
        "fcitx",
        files(FCITX, "gtk-glossary-session/state.json gtk-glossary-session/cleanup.log"),
        ("native-gtk",),
    ),
    Evidence(
        "fcitx",
        files(
            FCITX,
            "gtk4-glossary-result.json gtk4-glossary.log gtk4-glossary-session/state.json gtk4-glossary-session/cleanup.log",
        ),
        ("native-gtk4",),
    ),
    Evidence(
        "models", (LINUX + "desktop-data/typelay/components/argos/self-check.json",), GROUPS[1:]
    ),
    gimp,
)

LINUX_UPLOADS = {
    "typelay-linux-qt-language-options-${{ matrix.group }}": Upload(
        (qt_language,), ("native", "native-qt"), outcome="qt-language-options"
    ),
    "typelay-linux-gtk3-desktop-events": Upload((source, linux_build, gtk), ("native-gtk",)),
    "typelay-linux-gtk4-events": Upload(
        (source, linux_build, fcitx_build, gtk4_probe, gtk4), ("native-gtk4",)
    ),
    "typelay-linux-qt-desktop-events": Upload((qt,), ("native-qt",), missing="ignore"),
    "typelay-linux-gimp": Upload((source, linux_build, gimp), ("native",)),
    "linux-${{ matrix.group }}": Upload(linux_diagnostics),
    "linux-package": Upload(linux_packages, ("native",), packages=True),
}
UPLOADS = {
    "shared": {
        "shared": Upload(
            (source, Evidence("shared-behavior", ("artifacts/shared-test-results/core.trx",)))
        )
    },
    "windows-contracts": {
        "windows-trx-${{ matrix.shard }}": Upload(
            (trx,), outcome="windows-contracts", missing="error"
        ),
        "windows-contracts-${{ matrix.shard }}": Upload(
            (
                source,
                trx,
                physical,
                Evidence(
                    "windows-contracts",
                    ("artifacts/test-results/contracts-App/run/screenshot.png",),
                ),
            )
        ),
        "windows-physical-tab": Upload(
            (
                Evidence(
                    "physical-tab-package",
                    (
                        "artifacts/TypeLay-Windows-physical-Tab-validation.zip",
                        "artifacts/TypeLay-Windows-physical-Tab-validation.zip.sha256",
                    ),
                    (2,),
                ),
            ),
            (2,),
            packages=True,
        ),
    },
    "windows-tools": {
        "windows-tools": Upload(
            (
                source,
                Evidence(
                    "windows-native",
                    (
                        "artifacts/physical-tab/result.json",
                        "artifacts/physical-tab-injected/result.json",
                    ),
                ),
            )
        )
    },
    "windows-desktop": {
        "windows-desktop-${{ matrix.group }}": Upload(
            (source, continuous, notepad, hy_prepare, hy_production, hy_packages)
        ),
        "windows-package-identity-${{ matrix.group }}": Upload((source, windows_package)),
        "windows-package": Upload(
            (
                Evidence(
                    "windows-package",
                    (
                        "artifacts/TypeLay-Windows-preview.zip",
                        "artifacts/TypeLay-Windows-preview.zip.sha256",
                    ),
                ),
            ),
            ("hy",),
            packages=True,
        ),
    },
    "index-windows": {
        "typelay-index-windows": Upload((source, index_lifecycle, index_browser, index_package)),
        "windows-index-package": Upload(
            (
                Evidence(
                    "index-package",
                    (
                        "artifacts/TypeLay-Windows-preview.zip",
                        "artifacts/TypeLay-Windows-preview.zip.sha256",
                    ),
                ),
            ),
            packages=True,
        ),
    },
    "index-linux": {"typelay-index-linux": Upload((source, index_lifecycle))},
    "linux": LINUX_UPLOADS,
    "linux-native": LINUX_UPLOADS,
}
MATRICES = {
    "shared": (None,),
    "windows-contracts": (1, 2, 3, 4),
    "windows-tools": (None,),
    "windows-desktop": ("ordinary", "hy"),
    "index-windows": (None,),
    "index-linux": (None,),
    "linux": GROUPS[:3],
    "linux-native": NATIVE,
}

# Realistic payload locations deliberately share extensions with allowed evidence.
PAYLOADS = (
    "artifacts/TypeLay-Windows-preview.zip",
    "artifacts/TypeLay-Windows-physical-Tab-validation.zip",
    "artifacts/TypeLay-Linux-preview-x86_64.tar.gz",
    "artifacts/TypeLay-Fcitx-cooperative.tar.gz",
    "artifacts/package/TypeLay/TypeLay.exe",
    "artifacts/package/TypeLay/build-info.json",
    "artifacts/hy-runtime/llama.dll",
    "artifacts/index-runtime/llama.dll",
    "artifacts/index-model-download/model.gguf",
    "artifacts/index-package/app/TypeLay.Linux",
    LINUX + "package/typelay",
    LINUX + "package/worker/catalog.json",
    LINUX + "package/app/TypeLay.Linux.dll",
    LINUX + "package/build-info.json",  # Allowed only by explicit small identity uploads.
    LINUX + "desktop-data/typelay/components/argos/model.bin",
    LINUX + "desktop-data/typelay/components/argos/tokenizer.json",
    LINUX + "hy-cpu-runtime/libllama.so",
    LINUX + "qt-cooperative/fcitx5-qt.tar.gz",
    LINUX + "qt-cooperative/plugins/platforms/libqxcb.so",
    LINUX + "qt-language-options/fcitx5-qt.tar.gz",
    LINUX + "qt-language-options/native/qt6/libtypelay.so",
    LINUX + "qt-language-options/fcitx5-qt-5.1.11/CMakePresets.json",
    FCITX + "fcitx5.tar.gz",
    FCITX + "build/CMakeFiles/settings.json",
    FCITX + "gtk4-glossary-session/install/lib/libtypelay.so",
    LINUX + "gtk-desktop/installed/components/argos/model.bin",
    LINUX + "gtk4-desktop/installed/components/argos/model.bin",
    LINUX + "qt-desktop/installed/components/argos/model.bin",
    LINUX + "ordinary-gimp/installed/components/argos/model.bin",
)
