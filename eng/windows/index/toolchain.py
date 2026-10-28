"""Resolve the installed x64 Visual Studio toolchain without acquiring software."""

import os
import shutil
from pathlib import Path

from typelay_worker.index_assets import plain_path


def sdk_environment(command):
    program_files = os.environ.get("ProgramFiles(x86)")
    if not program_files:
        raise RuntimeError("Visual Studio Installer path unavailable")
    vswhere = plain_path(
        Path(program_files) / "Microsoft Visual Studio/Installer/vswhere.exe",
        allow_hardlink=True,
    )
    raw = command(
        [
            str(vswhere),
            "-latest",
            "-products",
            "*",
            "-requires",
            "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            "-property",
            "installationPath",
            "-utf8",
        ]
    )
    installation = Path(raw.decode("utf-8", "strict").strip())
    if not installation.is_absolute():
        raise RuntimeError("installed Visual Studio path unavailable")
    script = plain_path(installation / "Common7/Tools/VsDevCmd.bat", allow_hardlink=True)
    comspec = plain_path(Path(os.environ["SystemRoot"]) / "System32/cmd.exe", allow_hardlink=True)
    # A raw command line keeps cmd.exe's nested quote rules intact with spaces.
    command_line = (
        f'"{comspec}" /d /u /s /c "call "{script}" -no_logo -arch=x64 -host_arch=x64 >nul && set"'
    )
    raw = command(command_line, shell_command=True)
    # /u makes cmd's built-in set output UTF-16LE, including Unicode paths.
    env = {}
    for line in raw.decode("utf-16le", "strict").splitlines():
        name, separator, value = line.partition("=")
        if separator and name:
            env[name.upper()] = value
    if env.get("VSCMD_ARG_TGT_ARCH") != "x64":
        raise RuntimeError("Visual Studio did not select x64")
    for name in ("CL", "_CL_", "LINK", "_LINK_", "LIBPATH"):
        # CL/LINK can inject unreviewed flags. LIBPATH is irrelevant to the
        # native link and must not introduce managed build search locations.
        env.pop(name, None)
    for tool in ("cl.exe", "lib.exe", "dumpbin.exe"):
        resolved = shutil.which(tool, path=env.get("PATH"))
        if not resolved or not Path(resolved).is_relative_to(installation):
            raise RuntimeError("installed Visual Studio x64 tools unavailable")
        plain_path(resolved, allow_hardlink=True)
    return env
