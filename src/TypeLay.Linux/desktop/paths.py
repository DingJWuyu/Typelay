"""Linux-owned paths and same-user socket boundaries. No configuration in the package."""

import hashlib
import os
import socket
import stat
import struct
from pathlib import Path


def private_directory(path: Path) -> Path:
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    info = path.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid():
        raise OSError("private-directory-unavailable")
    path.chmod(0o700)
    return path


class Paths:
    def __init__(self, home: str | Path | None = None):
        self.home = Path(home or os.environ.get("TYPELAY_HOME", Path(__file__).parent.parent)).resolve()
        user = Path.home()
        self.config = self._xdg("XDG_CONFIG_HOME", user / ".config") / "typelay"
        self.data = self._xdg("XDG_DATA_HOME", user / ".local/share") / "typelay"
        self.cache = self._xdg("XDG_CACHE_HOME", user / ".cache") / "typelay"
        runtime = Path(os.environ.get("XDG_RUNTIME_DIR", ""))
        if not runtime.is_absolute() or not runtime.is_dir():
            raise OSError("xdg-runtime-directory-unavailable")
        identity = os.environ.get("WAYLAND_DISPLAY", "") + "\n" + os.environ.get("NIRI_SOCKET", "")
        suffix = hashlib.sha256(identity.encode()).hexdigest()[:16]
        self.session = runtime / "typelay" / suffix
        for path in (self.config, self.data, self.cache, self.session.parent, self.session):
            private_directory(path)
        self.control = self.session / "control.sock"
        self.native = self.session / "native.sock"
        if len(os.fsencode(self.control)) >= 108:
            raise OSError("unix-socket-path-too-long")
        self.settings = self.config / "settings.json"
        self.module = self.home / "native/libtypelay-gtk3.so"
        self.launcher = self.home / "typelay"
        self.python = self.data / "runtime/venv/bin/python"
        self.models = self.data / "models"

    @staticmethod
    def _xdg(name: str, default: Path) -> Path:
        value = Path(os.environ.get(name, ""))
        return value if value.is_absolute() else default


def peer_credentials(connection: socket.socket) -> tuple[int, int, int]:
    pid, uid, gid = struct.unpack("3i", connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
    if uid != os.getuid() or pid <= 0:
        raise PermissionError("socket-peer-not-owner")
    return pid, uid, gid


def connect_private(path: Path, timeout: float = 0.5) -> socket.socket:
    info = path.lstat()
    if not stat.S_ISSOCK(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
        raise PermissionError("private-socket-unavailable")
    connection = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        connection.settimeout(timeout)
        connection.connect(str(path))
        peer_credentials(connection)
        return connection
    except BaseException:
        connection.close()
        raise


def process_identity(pid: int) -> str:
    # Do not read argv, titles or /proc/environ: they can contain input or credentials.
    return Path(os.readlink(f"/proc/{pid}/exe")).name.lower()
