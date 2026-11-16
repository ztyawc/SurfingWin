"""Exercise the actual C# tray, native process control and loopback API on Windows/WSL."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CSCRIPT = shutil.which("cscript.exe")
POWERSHELL = shutil.which("powershell.exe")
WSLPATH = shutil.which("wslpath")


def windows_path(path):
    if os.name == "nt":
        return str(path)
    return subprocess.check_output([WSLPATH, "-w", str(path)], text=True).strip()


@unittest.skipUnless(CSCRIPT and POWERSHELL and (os.name == "nt" or WSLPATH), "Windows desktop runtime required")
class TrayTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if os.name == "nt":
            temporary_root = None
        else:
            cmd = Path(CSCRIPT).parent / "cmd.exe"
            native_temp = subprocess.check_output(
                [str(cmd), "/d", "/c", "echo %TEMP%"], cwd="/mnt/c", timeout=15,
            ).decode("gb18030").strip()
            temporary_root = subprocess.check_output([WSLPATH, "-u", native_temp], text=True).strip()
        cls.temp = tempfile.TemporaryDirectory(prefix="surfing-tray-test-", dir=temporary_root)
        cls.addClassCleanup(cls.temp.cleanup)
        cls.folder = Path(cls.temp.name)
        windows_dir = Path(CSCRIPT).parent.parent
        cls.compiler = windows_dir / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
        if not cls.compiler.exists():
            cls.compiler = windows_dir / "Microsoft.NET" / "Framework" / "v4.0.30319" / "csc.exe"
        if not cls.compiler.exists():
            raise unittest.SkipTest(".NET Framework compiler required")
        cls.stub = cls.folder / "CoreStub.exe"
        cls.compile(cls.stub, "exe", [PROJECT_ROOT / "tests" / "tray_core_stub.cs"])

    @classmethod
    def compile(cls, output, target, sources, extra=()):
        result = subprocess.run(
            [str(cls.compiler), "/nologo", "/utf8output", "/codepage:65001", "/target:" + target,
             "/reference:System.Windows.Forms.dll", "/reference:System.Drawing.dll",
             "/reference:System.Net.Http.dll", "/reference:System.Web.Extensions.dll",
             "/out:" + windows_path(output), *extra, *(windows_path(path) for path in sources)],
            capture_output=True, timeout=60,
        )
        if result.returncode:
            raise AssertionError((result.stdout + result.stderr).decode("utf-8", errors="replace"))

    def test_native_core_api_menu_and_instance_lifecycle(self):
        rebuilt = self.folder / "RebuiltTray.exe"
        result = subprocess.run(
            [POWERSHELL, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
             windows_path(PROJECT_ROOT / "scripts" / "build-tray.ps1"), "-OutputPath", windows_path(rebuilt)],
            capture_output=True, timeout=60,
        )
        self.assertEqual(result.returncode, 0, (result.stdout + result.stderr).decode("utf-8", errors="replace"))
        harness = self.folder / "TrayHarness.exe"
        sources = [PROJECT_ROOT / "src" / "tray" / name for name in ("TrayCore.cs", "TrayApplication.cs")]
        self.compile(harness, "winexe", [*sources, PROJECT_ROOT / "tests" / "tray_harness.cs"],
                     ["/main:SurfingWin.Tray.Tests.Harness"])
        hasher = hashlib.sha256()
        for name in ("TrayCore.cs", "TrayApplication.cs", "SurfingTray.manifest", "SurfingTray.ico"):
            path = PROJECT_ROOT / "src" / "tray" / name
            data = path.read_bytes() if path.suffix == ".ico" else path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").encode()
            hasher.update((name + "\n").encode())
            hasher.update(data)
        results = self.folder / "results.json"
        preview = self.folder / "menu.png"
        packaged = self.folder / "PackagedTray.exe"
        shutil.copyfile(PROJECT_ROOT / "bin" / "SurfingTray.exe", packaged)
        completed = subprocess.run(
            [str(harness), windows_path(self.folder), windows_path(self.stub),
             windows_path(packaged), hasher.hexdigest(),
             windows_path(results), windows_path(preview)], capture_output=True, timeout=150,
        )
        self.assertTrue(results.is_file(), "Native harness produced no diagnostics")
        diagnostics = json.loads(results.read_text(encoding="utf-8-sig"))
        self.assertEqual(completed.returncode, 0, json.dumps(diagnostics, ensure_ascii=False, indent=2))
        self.assertTrue(diagnostics["success"], diagnostics)
        self.assertGreaterEqual(len(diagnostics["passed"]), 13)
        print("\n  Tray scenarios: " + "; ".join(diagnostics["passed"]))
        for reason in diagnostics.get("skipped", []):
            print("  Skipped native scenario: " + reason)
        self.assertTrue(preview.is_file())
        if os.environ.get("SURFING_TRAY_PREVIEW"):
            shutil.copyfile(preview, os.environ["SURFING_TRAY_PREVIEW"])

    def test_vbs_launcher_passes_actions_without_shell_interpretation(self):
        fixture = self.folder / "launcher 路径 with spaces & symbols"
        (fixture / "scripts").mkdir(parents=True)
        (fixture / "bin").mkdir()
        shutil.copyfile(self.stub, fixture / "bin" / "SurfingTray.exe")
        launcher = fixture / "scripts" / "Surfing.tray.vbs"
        shutil.copyfile(PROJECT_ROOT / "scripts" / "Surfing.tray.vbs", launcher)
        record = fixture / "bin" / "launcher.json"
        for argument, expected in ((None, []), ("/start", ["--start"]), ("/stop", ["--stop"]), ("/restart", ["--restart"])):
            with self.subTest(argument=argument):
                if record.exists():
                    record.unlink()
                result = subprocess.run(
                    [CSCRIPT, "//nologo", windows_path(launcher), *([argument] if argument else [])],
                    capture_output=True, timeout=15,
                )
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                deadline = time.monotonic() + 10
                while not record.exists() and time.monotonic() < deadline:
                    time.sleep(0.05)
                self.assertTrue(record.exists(), "Launcher did not execute the harmless stub")
                data = json.loads(record.read_text(encoding="utf-8-sig"))
                self.assertEqual(data["arguments"], expected)
                actual_cwd = data["cwd"] if os.name == "nt" else subprocess.check_output(
                    [WSLPATH, "-u", data["cwd"]], text=True,
                ).strip()
                self.assertTrue(os.path.samefile(actual_cwd, fixture),
                                "Launcher used a different working directory: " + data["cwd"])

    def test_real_mihomo_loopback_only_without_tun(self):
        harness = self.folder / "TrayHarness.exe"
        if not harness.is_file():
            sources = [PROJECT_ROOT / "src" / "tray" / name for name in ("TrayCore.cs", "TrayApplication.cs")]
            self.compile(harness, "winexe", [*sources, PROJECT_ROOT / "tests" / "tray_harness.cs"],
                         ["/main:SurfingWin.Tray.Tests.Harness"])
        fixture = self.folder / "real-core 路径 with spaces & symbols"
        (fixture / "bin").mkdir(parents=True)
        shutil.copyfile(PROJECT_ROOT / "bin" / "clash-amd64.exe", fixture / "bin" / "clash-amd64.exe")
        result_path = self.folder / "real-core-results.json"
        result = subprocess.run(
            [str(harness), "--real-smoke", windows_path(fixture), windows_path(result_path)],
            capture_output=True, timeout=60,
        )
        self.assertTrue(result_path.is_file(), "Real-core harness produced no diagnostics")
        diagnostics = json.loads(result_path.read_text(encoding="utf-8-sig"))
        self.assertEqual(result.returncode, 0, json.dumps(diagnostics, ensure_ascii=False, indent=2))
        self.assertTrue(diagnostics["success"], diagnostics)
        self.assertFalse(diagnostics["tun"])
        self.assertTrue(diagnostics["gracefulStop"])
        print("\n  Real Mihomo smoke: " + json.dumps(diagnostics, ensure_ascii=False))


if __name__ == "__main__":
    unittest.main()
