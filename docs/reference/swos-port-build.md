# Running swos-port locally as living reference

**Status: working, built 2026-05-16.** Binary at
`external/swos-port/bin/x64/swos-port-x64-Release.exe`.

The build scripts in `external/swos-port/` are from 2018-2022 era and assume
specific dependency versions that are no longer available without manual
intervention. This doc captures the exact recipe that worked on this machine.

## Toolchain on this machine

- **Visual Studio 2019 BuildTools** with MSVC `14.29.30133` + MASM
  (`ml.exe` / `ml64.exe`). The C++ workload is what matters — VS 2022
  Community without the C++ workload **will not work**.
- **Python 3.13** (needed for `compileAssets.py` and `mnu2h` pipeline).
- All SDL2 + CrashRpt + minizip + zlib-ng **import libs** are already bundled
  in `external/swos-port/3rd-party/`. Runtime DLLs are NOT bundled (see below).

## One-time setup

### 1. Python deps

```pwsh
python -m pip install ddt PyTexturePacker tabulate
```

But the PyPI `PyTexturePacker` is **the wrong fork**. `assets/compileAssets.py`
uses `ignore_blank`, `crop_atlas`, `detect_duplicates` kwargs which are in
**zlatkok's private fork** of PyTexturePacker — never published to PyPI.

Install the fork:

```pwsh
cd I:\GITHUB\W_OPEN_SWOS\external
git clone --depth 1 https://github.com/zlatkok/PyTexturePacker.git
python -m pip uninstall -y PyTexturePacker
python -m pip install .\PyTexturePacker\
```

### 2. swos-port source patch

`src/stdinc.h:66` defines `sprintf_s` as `static_assert(false, ...)` to poison
that function. VS 2019 stdlib (`xlocmon`) uses `sprintf_s` internally, so the
poison breaks the stdlib parse. Guard the macro with `!defined(_MSC_VER)`:

```c
#if !defined(SWOS_TEST) && !defined(_MSC_VER)
# define sprintf_s(...) static_assert(false, "sprintf_s detected, use snprintf instead!")
#endif
```

### 3. Generate assets

```pwsh
cd I:\GITHUB\W_OPEN_SWOS\external\swos-port\assets
$env:PYTHONIOENCODING="utf-8"; $env:PYTHONUTF8="1"
python compileAssets.py
```

This populates `tmp/assets/{4k,hd,low-res}/` with sprite atlases and writes
`pitchDatabase.h` / `spriteDatabase.h` / `stadiumSprites.h` /
`variableSprites.h` to `tmp/assets/`. The build picks them up from there.

### 4. Build C++

```pwsh
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
$sln = "I:\GITHUB\W_OPEN_SWOS\external\swos-port\project\vc++\swos-port.sln"
& $msbuild $sln /t:swos-port /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v142 /m
```

Output: `external/swos-port/bin/x64/swos-port-x64-Release.exe`.

### 5. Runtime DLLs

The EXE depends on:
- `SDL2.dll` — bundled .lib is `2.0.12`, download matching DLL from
  https://github.com/libsdl-org/SDL/releases/tag/release-2.0.12
- `SDL2_image.dll` — newer ABI-compat `2.8.12` works
  (https://github.com/libsdl-org/SDL_image/releases/tag/release-2.8.12)
- `SDL2_mixer.dll` — newer ABI-compat `2.8.1` works
  (https://github.com/libsdl-org/SDL_mixer/releases/tag/release-2.8.1)
- `CrashRpt1403.dll` — no public x64 release. Build a stub (see below).
- `zlib1.dll` — bundled `.lib` (`3rd-party/zlib-ng/lib/win64/zlib.lib`) is
  an import lib for `zlib1.dll`. Grab the **zlib-ng compat** build from
  https://github.com/zlib-ng/zlib-ng/releases — asset
  `zlib-ng-win-x86-64-compat.zip` exports under classic zlib1 names.

### 6. CrashRpt1403.dll stub

CrashRpt is just a crash reporter and is non-essential for our reference-run
purposes. swos-port imports 6 ordinals from it: 8, 16, 19, 24, 27, 30,
mapping to `crInstallA`, `crAddFile2A`, `crAddPropertyA`,
`crGetLastErrorMsgA`, `crAddScreenshot2`, `crSetCrashCallbackA`. A no-op
stub satisfies the imports.

Source + def file in `external/crashrpt-stub/`. Build via VS 2019 `cl.exe`:

```pwsh
$vcvars = "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
cmd /c "`"$vcvars`" && cd /d I:\GITHUB\W_OPEN_SWOS\external\crashrpt-stub && cl /nologo /LD /O2 crashrpt_stub.c /link /DEF:crashrpt_stub.def /OUT:CrashRpt1403.dll"
```

### 7. Layout next to EXE

Copy into `external/swos-port/bin/x64/`:
- `SDL2.dll` (download **SDL 2.32.x** — needed because SDL2_image 2.8.x
  requires `SDL_roundf` which doesn't exist in older SDL2)
- `SDL2_image.dll`, `SDL2_mixer.dll`, `CrashRpt1403.dll`, `zlib1.dll`
- `assets/{4k,hd,low-res}/` (copy of `../../tmp/assets/{4k,hd,low-res}/`)
- **`data/`** — copy `Swos9697_PC/SensiWs9/SOC/DATA/*` here. swos-port does
  NOT embed team data — it reads `data/team.NNN` at runtime. Without it the
  game crashes on competition selection.

### 8. Launching — set working directory

The EXE reads `assets\hd\charset0.png` and `data\team.028` as relative
paths, so you MUST launch with the working directory set to `bin\x64\`.
Either click the EXE in Explorer (Explorer sets cwd to file's parent), or
from PowerShell:

```pwsh
Start-Process -FilePath "I:\...\swos-port-x64-Release.exe" `
              -WorkingDirectory "I:\...\bin\x64"
```

## Running

```pwsh
& "I:\GITHUB\W_OPEN_SWOS\external\swos-port\bin\x64\swos-port-x64-Release.exe"
```

The EXE is self-contained — does NOT need original `Swos9697_PC/` files at
runtime. The original SWOS PC binary is fully integrated via `ida2asm` at
build time. PNG sprite atlases replace the original `SPRITE.DAT`.

## Why we use it

Read-only **living reference for behaviour A/B**. When OpenSWOS gameplay
feels wrong, launch swos-port side-by-side, observe the original, then read
the source under `external/swos-port/src/game/` to extract the relevant
constants and algorithms. License rule from CLAUDE.md still applies:
**paraphrase algorithms, copy constants, cite source file:line in C# code
comments.**
