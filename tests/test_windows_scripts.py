"""Run the real VBScript sources with isolated COM doubles on Windows or WSL."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CSCRIPT = shutil.which("cscript.exe")
WSLPATH = shutil.which("wslpath")


def windows_path(path):
    if os.name == "nt":
        return str(path)
    return subprocess.check_output([WSLPATH, "-w", str(path)], text=True).strip()


@unittest.skipUnless(CSCRIPT and (os.name == "nt" or WSLPATH), "Windows Script Host required")
class WindowsScriptTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="surfing-wsh-test-")
        cls.addClassCleanup(cls.temp.cleanup)
        cls.fixture = Path(cls.temp.name) / "路径 with spaces & symbols"
        (cls.fixture / "scripts").mkdir(parents=True)
        (cls.fixture / "bin").mkdir()
        (cls.fixture / "config" / "log").mkdir(parents=True)
        for name in ("Surfing.start.vbs", "Surfing.task.start.vbs", "Surfing.task.stop.vbs", "Surfing.startup.xml"):
            shutil.copyfile(PROJECT_ROOT / "scripts" / name, cls.fixture / "scripts" / name)
        cls.harness = Path(cls.temp.name) / "harness.vbs"
        source = (PROJECT_ROOT / "tests" / "windows_script_harness.vbs").read_text(encoding="utf-8")
        cls.harness.write_bytes(source.encode("utf-16"))

    def run_scenario(self, scenario, script, expected_exit):
        result = subprocess.run(
            [CSCRIPT, "//nologo", windows_path(self.harness), scenario,
             windows_path(self.fixture / "scripts" / script), str(expected_exit)],
            capture_output=True, timeout=30,
        )
        diagnostics = (result.stdout + result.stderr).decode("gb18030", errors="replace")
        self.assertEqual(result.returncode, expected_exit, diagnostics)
        self.assertIn("PASS:" + scenario, diagnostics)

    def test_task_management_success_and_failure_paths(self):
        cases = (
            ("enable_elevate", "Surfing.task.start.vbs", 0),
            ("enable_cancel", "Surfing.task.start.vbs", 1),
            ("enable_ok", "Surfing.task.start.vbs", 0),
            ("enable_create_fail", "Surfing.task.start.vbs", 1),
            ("enable_query_fail", "Surfing.task.start.vbs", 1),
            ("disable_elevate", "Surfing.task.stop.vbs", 0),
            ("disable_missing", "Surfing.task.stop.vbs", 0),
            ("disable_access_denied", "Surfing.task.stop.vbs", 1),
            ("disable_connect_fail", "Surfing.task.stop.vbs", 1),
            ("disable_delete_fail", "Surfing.task.stop.vbs", 1),
            ("disable_still_present", "Surfing.task.stop.vbs", 1),
            ("disable_verify_fail", "Surfing.task.stop.vbs", 1),
            ("disable_ok", "Surfing.task.stop.vbs", 0),
        )
        for scenario, script, expected_exit in cases:
            with self.subTest(scenario=scenario):
                self.run_scenario(scenario, script, expected_exit)

    def test_startup_command_executes_with_special_paths(self):
        windows_dir = Path(CSCRIPT).parent.parent
        compiler = windows_dir / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
        if not compiler.exists():
            self.skipTest(".NET Framework C# compiler required for harmless core stub")
        source = Path(self.temp.name) / "CoreStub.cs"
        source.write_text(
            'using System; using System.Text; class CoreStub {'
            'static void Main(string[] args) {'
            'Console.OutputEncoding = new UTF8Encoding(false);'
            'Console.WriteLine("ARGC=" + args.Length);'
            'for (int i = 0; i < args.Length; i++) '
            'Console.WriteLine("ARG" + i + "=" + args[i]); }}',
            encoding="utf-8",
        )
        result = subprocess.run(
            [str(compiler), "-nologo", "-target:exe",
             "-out:" + windows_path(self.fixture / "bin" / "clash-amd64.exe"), windows_path(source)],
            capture_output=True, timeout=30,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.run_scenario("startup_ok", "Surfing.start.vbs", 0)


if __name__ == "__main__":
    unittest.main()
