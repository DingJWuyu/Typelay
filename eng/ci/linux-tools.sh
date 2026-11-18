#!/usr/bin/env bash
# The fixed archive contains the packages collected by these pacman transactions.
set -eu -o pipefail
case "$1" in
    bootstrap)
        snapshot=$(cat eng/ci/arch-snapshot)
        printf 'Server = https://archive.archlinux.org/repos/%s/$repo/os/$arch\n' "$snapshot" > /etc/pacman.d/mirrorlist
        sed -i -E '/^#?ParallelDownloads[[:space:]]*=/d' /etc/pacman.conf
        sed -i '/^\[options\]/a ParallelDownloads = 8' /etc/pacman.conf
        mkdir -p "$GITHUB_WORKSPACE/artifacts/linux-tools-cache"
        sed -i '/^CacheDir[[:space:]]*=/d' /etc/pacman.conf
        sed -i "/^\[options\]/a CacheDir = $GITHUB_WORKSPACE/artifacts/linux-tools-cache" /etc/pacman.conf
        pacman -Syyuu --needed --noconfirm python github-cli
        ;;
    sdk)
        pacman -Su --needed --noconfirm git cmake ninja pkgconf gtk3 gtk4 json-glib python \
            curl icu openssl zlib libxkbcommon-x11 dbus fcitx5 fcitx5-gtk fcitx5-chinese-addons boost libime \
            patch fmt glib2-devel nlohmann-json expat xkeyboard-config iso-codes extra-cmake-modules gettext wayland-protocols plasma-wayland-protocols \
            github-cli opencc procps-ng zstd qt6-base libxkbcommon libxcb
        if [[ "$DESKTOP" == true || ( "$PRODUCER" == true && "$HIT" != true ) ]]; then
            echo 'install_desktop=true' >> "$GITHUB_OUTPUT"
        fi
        ;;
    desktop)
        pacman -S --needed --noconfirm gimp at-spi2-core python-gobject libayatana-appindicator \
            niri mesa uv grim noto-fonts-cjk xdg-desktop-portal-gtk xorg-server-xvfb xdotool \
            chromium python-playwright firefox geckodriver code nodejs npm
        ;;
    *) exit 2 ;;
esac
