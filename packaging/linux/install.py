"""User-local install/uninstall. No sudo, shell configuration edits or model downloads."""

import argparse
import fcntl
import hashlib
import os
import shutil
import stat
import subprocess
import time
from pathlib import Path

PACKAGE = Path(__file__).resolve().parent


def xdg(name, fallback):
    value = Path(os.environ.get(name, ""))
    return value if value.is_absolute() else fallback


def private(path):
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    info = path.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid():
        raise OSError("install-directory-owner")
    path.chmod(0o700)
    return path


def install(root, command, desktop):
    subprocess.run(
        ["sha256sum", "--check", "--strict", "--status", "MANIFEST.sha256"], cwd=PACKAGE, check=True
    )
    expected = root / "current/typelay"
    if (command.exists() or command.is_symlink()) and (
        not command.is_symlink() or os.readlink(command) != str(expected)
    ):
        raise OSError("existing-command-not-owned")
    executable = str(command)
    if any(character in executable for character in '\n\r\x00%"\\`$'):
        raise ValueError("desktop-exec-path")
    private(root)
    private(root / "releases")
    manifest = PACKAGE / "MANIFEST.sha256"
    version = hashlib.sha256(manifest.read_bytes()).hexdigest()[:20]
    target = root / "releases" / version
    if not target.exists():
        stage = root / "releases" / (version + ".staging")
        if stage.exists():
            shutil.rmtree(stage)
        shutil.copytree(PACKAGE, stage)
        stage.rename(target)
    else:
        # Reuse only an intact release. Do not rewrite files that a running
        # client may still be using, or trust the installed copy's manifest.
        subprocess.run(
            ["sha256sum", "--check", "--strict", "--status", str(manifest)], cwd=target, check=True
        )
    current = root / "current"
    temporary = root / ".current-new"
    temporary.unlink(missing_ok=True)
    temporary.symlink_to(Path("releases") / version, target_is_directory=True)
    os.replace(temporary, current)
    command.parent.mkdir(parents=True, exist_ok=True)
    expected = current / "typelay"
    if command.exists() or command.is_symlink():
        if not command.is_symlink() or os.readlink(command) != str(expected):
            raise OSError("existing-command-not-owned")
    else:
        command.symlink_to(expected)
    desktop.parent.mkdir(parents=True, exist_ok=True)
    executable = str(command)
    if any(character in executable for character in '\n\r\x00%"\\`$'):
        raise ValueError("desktop-exec-path")
    desktop.write_text(
        "[Desktop Entry]\nType=Application\nName=TypeLay\n"
        "Comment=Local input field translation\n"
        f'Exec="{executable}" --settings\nTerminal=false\n'
        f"Icon={root}/current/desktop/icons/typelay-active.svg\n"
        "Categories=Utility;Accessibility;\nStartupNotify=false\n",
        encoding="utf-8",
    )
    print(f"已安装：{command}\n启动：{command} --settings\n此操作不会下载模型或修改 shell 配置。")


def uninstall(root, command, desktop, config, data, cache, purge):
    # Ask only this program to exit. Do not kill processes by executable name.
    if command.is_symlink() and os.readlink(command) == str(root / "current/typelay"):
        try:
            subprocess.run([str(command), "--quit"], check=False, timeout=5)
        except FileNotFoundError:
            # A damaged installation still needs to be removable. Session locks
            # below remain authoritative, even when the launcher has disappeared.
            pass
    held = []
    runtime = os.environ.get("XDG_RUNTIME_DIR")
    try:
        if runtime and Path(runtime).is_absolute():
            for path in (Path(runtime) / "typelay").glob("*/desktop.lock"):
                fd = os.open(path, os.O_RDWR | os.O_NOFOLLOW)
                held.append(fd)
                deadline = time.monotonic() + 5
                while True:
                    try:
                        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                        break
                    except BlockingIOError:
                        if time.monotonic() >= deadline:
                            raise
                        time.sleep(0.05)
        model_lock = data / "components/install.lock"
        if purge and model_lock.exists():
            fd = os.open(model_lock, os.O_RDWR | os.O_NOFOLLOW)
            held.append(fd)
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if root.exists() and (root.is_symlink() or root.stat().st_uid != os.getuid()):
            raise OSError("install-directory-owner")
        if command.is_symlink() and os.readlink(command) == str(root / "current/typelay"):
            command.unlink()
        desktop.unlink(missing_ok=True)
        (config.parent / "autostart/typelay.desktop").unlink(missing_ok=True)
        if root.exists():
            shutil.rmtree(root)
        if purge:
            for path in (config, data, cache):
                if path.exists():
                    if path.is_symlink() or path.stat().st_uid != os.getuid():
                        raise OSError("data-directory-owner")
                    shutil.rmtree(path)
        print(
            "TypeLay 已卸载。"
            + ("本地设置、模型和缓存已删除。" if purge else "本地设置和模型已保留。")
        )
    finally:
        for fd in held:
            os.close(fd)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("install", "uninstall"))
    parser.add_argument(
        "--purge", action="store_true", help="卸载时同时删除 TypeLay 设置、模型和缓存"
    )
    args = parser.parse_args()
    home = Path.home()
    root = home / ".local/lib/typelay"
    command = home / ".local/bin/typelay"
    data_home = xdg("XDG_DATA_HOME", home / ".local/share")
    desktop = data_home / "applications/typelay.desktop"
    try:
        if args.operation == "install":
            if args.purge:
                parser.error("--purge 仅用于卸载")
            install(root, command, desktop)
        else:
            uninstall(
                root,
                command,
                desktop,
                xdg("XDG_CONFIG_HOME", home / ".config") / "typelay",
                data_home / "typelay",
                xdg("XDG_CACHE_HOME", home / ".cache") / "typelay",
                args.purge,
            )
    except (OSError, ValueError, subprocess.SubprocessError):
        parser.exit(1, "未完成：请确认目录权限和校验文件，关闭所有 TypeLay 会话后重试。\n")
