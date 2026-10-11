"""Cross-compile the no-CRT Windows launcher with Clang/LLD; no Windows SDK required."""
import argparse, pathlib, shutil, subprocess, tempfile

def main():
    p = argparse.ArgumentParser(); p.add_argument('--output'); args = p.parse_args()
    root = pathlib.Path(__file__).resolve().parents[2]
    output = pathlib.Path(args.output).resolve() if args.output else root / 'TypeLay.exe'
    clang, link = shutil.which('clang'), shutil.which('lld-link')
    if not clang or not link: raise SystemExit('Clang and lld-link are required.')
    with tempfile.TemporaryDirectory(prefix='typelay-launcher-') as td:
        d = pathlib.Path(td)
        for lib, exports in {
            'kernel32': ['GetModuleFileNameW','GetSystemDirectoryW','CreateProcessW','CloseHandle','GetFileAttributesW','ExitProcess'],
            'user32': ['MessageBoxW'],
        }.items():
            definition = d / f'{lib}.def'
            definition.write_text('LIBRARY '+lib+'.dll\nEXPORTS\n'+'\n'.join(exports)+'\n')
            subprocess.run([link,'/lib','/machine:x64',f'/def:{definition}',f'/out:{d / (lib+".lib")}'],check=True)
        obj = d / 'launcher.obj'
        subprocess.run([clang,'--target=x86_64-pc-windows-msvc','-Oz','-ffreestanding','-fno-stack-protector','-fno-builtin',
                        '-Werror','-Wall','-Wextra','-c',str(root/'tools/bootstrap/launcher.c'),'-o',str(obj)],check=True)
        subprocess.run([link,'/machine:x64','/subsystem:windows,6.02','/entry:mainCRTStartup','/nodefaultlib','/dynamicbase',
                        '/highentropyva','/nxcompat','/Brepro',f'/out:{output}',str(obj),str(d/'kernel32.lib'),str(d/'user32.lib')],check=True)
        print(output)
if __name__ == '__main__': main()
