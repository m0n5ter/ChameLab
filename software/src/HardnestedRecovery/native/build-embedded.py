#!/usr/bin/env python3
"""Build the embedded hardnested library for an app head (no global tool changes)."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

SOURCE = Path(__file__).resolve().parent


def run(args, env):
    subprocess.run([str(arg) for arg in args], env=env, check=True)


def cmake_path():
    found = shutil.which("cmake")
    if found:
        return Path(found)
    locator = Path(os.environ.get("ProgramFiles(x86)", "")) / "Microsoft Visual Studio/Installer/vswhere.exe"
    if locator.is_file():
        result = subprocess.check_output([str(locator), "-latest", "-products", "*", "-find",
            "Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"], text=True).strip()
        if result:
            return Path(result.splitlines()[0])
    raise RuntimeError("CMake 3.24+ is required. Add it to PATH.")


def emscripten(env):
    emcc = shutil.which("emcc")
    if emcc:
        return Path(emcc).resolve().parent
    sdk = env.get("EMSDK")
    if sdk and (Path(sdk) / "upstream/emscripten").is_dir():
        return Path(sdk) / "upstream/emscripten"
    raise RuntimeError("Install and activate standalone Emscripten SDK 3.1.74+ (emsdk), then source emsdk_env or set EMSDK. The .NET workload's custom SDK cannot build this standalone module.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("platform", choices=["android", "ios", "web"])
    parser.add_argument("--rid", default="")
    parser.add_argument("--build-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--test-capture", type=Path, help="For web: verify cancellation and recovery using a synthetic known-key capture")
    args = parser.parse_args()
    build, output = args.build_dir.resolve(), args.output.resolve()
    build.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    cmake = cmake_path()
    ninja = shutil.which("ninja")
    if not ninja:
        bundled = cmake.parents[2] / "Ninja/ninja.exe"
        if bundled.is_file():
            ninja = str(bundled)
    generator = ["-G", "Ninja", f"-DCMAKE_MAKE_PROGRAM={ninja}"] if ninja else ["-G", "Unix Makefiles"]

    def configure_build(name, options, destination, target="chamelab-hardnested"):
        directory = build / name
        run([cmake, "-S", SOURCE, "-B", directory, *generator,
             "-DCMAKE_BUILD_TYPE=Release", "-DCHAMELAB_EMBEDDED=ON", *options], env)
        run([cmake, "--build", directory, "--target", target, "--parallel"], env)
        run([cmake, "--install", directory, "--prefix", destination, "--component", "Hardnested"], env)

    if args.platform == "android":
        sdk = Path(env.get("ANDROID_SDK_ROOT", env.get("ANDROID_HOME", "")))
        ndk_path = env.get("ANDROID_NDK_HOME", env.get("ANDROID_NDK_ROOT"))
        installed = sorted((sdk / "ndk").glob("*"), key=lambda p: tuple(int(s) for s in p.name.split(".")))
        ndk = Path(ndk_path) if ndk_path else installed[-1] if installed else None
        if not ndk or not (ndk / "build/cmake/android.toolchain.cmake").is_file():
            raise RuntimeError("Install Android NDK r28+ using sdkmanager, or set ANDROID_NDK_HOME.")
        abis = {"android-arm64": "arm64-v8a", "android-x64": "x86_64"}
        if args.rid and args.rid not in abis:
            raise RuntimeError(f"Unsupported hardnested Android RID: {args.rid}. Use android-arm64 or android-x64.")
        for rid, abi in abis.items():
            if args.rid and rid != args.rid:
                continue
            configure_build(abi, [f"-DCMAKE_TOOLCHAIN_FILE={ndk}/build/cmake/android.toolchain.cmake",
                f"-DANDROID_ABI={abi}", "-DANDROID_PLATFORM=android-23",
                "-DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384"], output / abi)
    elif args.platform == "ios":
        if sys.platform != "darwin":
            raise RuntimeError("Building iOS native libraries requires macOS and Xcode.")
        rids = {"ios-arm64": ("iphoneos", "arm64"), "iossimulator-arm64": ("iphonesimulator", "arm64"),
                "iossimulator-x64": ("iphonesimulator", "x86_64")}
        if args.rid not in rids:
            raise RuntimeError("Specify --rid ios-arm64, iossimulator-arm64 or iossimulator-x64.")
        sdk, arch = rids[args.rid]
        configure_build(args.rid, ["-DCMAKE_SYSTEM_NAME=iOS", f"-DCMAKE_OSX_SYSROOT={sdk}",
            f"-DCMAKE_OSX_ARCHITECTURES={arch}", "-DCMAKE_OSX_DEPLOYMENT_TARGET=13.0"], output)
    else:
        em = emscripten(env)
        configure_build("wasm", [f"-DCMAKE_TOOLCHAIN_FILE={em}/cmake/Modules/Platform/Emscripten.cmake"],
                        output, "hardnested-wasm")
        if args.test_capture:
            run([cmake, "--build", build / "wasm", "--target", "hardnested-embedded-tests", "--parallel"], env)
            node = shutil.which("node")
            if not node:
                raise RuntimeError("Node.js is required for the embedded engine tests.")
            result = subprocess.run([node, str(build / "wasm/hardnested-embedded-tests.cjs"),
                str(args.test_capture.resolve())], env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            (build / "embedded-tests.log").write_text(result.stdout, encoding="utf8")
            if result.returncode:
                print(result.stdout[-6000:])
                raise RuntimeError("Embedded hardnested tests failed; see embedded-tests.log in the build directory.")
            print("Embedded engine: cancellation, memory budget and repeated known-key recovery passed.")


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
