"""Exercise the actual release script in disposable Git repositories."""

from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest
import zipfile


PROJECT_ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(os.name != "nt" and shutil.which("git") and shutil.which("sh"), "POSIX Git and sh required")
class ReleasePackagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="surfing-release-test-")
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name) / "项目 with spaces & symbols"
        self.repo.mkdir()
        self.build_script = self.repo / "build.sh"
        shutil.copyfile(PROJECT_ROOT / "build.sh", self.build_script)
        self.release_files = {
            "README.md": "release readme\n",
            "LICENSE": "release license\n",
            "bin/clash-amd64.exe": "binary fixture\n",
            "bin/SurfingTray.exe": "tray binary fixture\n",
            "src/tray/TrayCore.cs": "// tray core source\n",
            "src/tray/TrayApplication.cs": "// tray UI source\n",
            "src/tray/SurfingTray.manifest": "<assembly/>\n",
            "src/tray/SurfingTray.ico": "tray icon fixture\n",
            "config/config.yaml": "url: subscription-placeholder\n",
            "config/rules/WebRTC.list": "DOMAIN,example.invalid\n",
            "config/webroot/Zash/index.html": "<html>release fixture</html>\n",
            "scripts/Surfing.start.vbs": "' release script\n",
            "scripts/Surfing.start.bat": "@echo off\n",
            "scripts/Surfing.start.ps1": "# release script\n",
            "scripts/Surfing.startup.xml": "<Task/>\n",
            "scripts/Surfing.tray.vbs": "' tray launcher\n",
            "scripts/build-tray.ps1": "# tray build script\n",
        }
        for name in ("启动服务.bat", "停止服务.bat", "开机自启.bat", "关闭自启.bat", "托盘管理.bat"):
            self.release_files[name] = "@echo off\n"
        for name, content in self.release_files.items():
            self.write(name, content)
        # Even committed files outside the release allowlist must stay private.
        for name in (".env", "config/proxies/private.yaml", "config/log/clash.log",
                     "scripts/Surfing.startup.tmp.xml", "old-release.zip"):
            self.write(name, "PRIVATE_COMMITTED_FIXTURE\n")
        self.git("init", "-q")
        self.commit()

    def write(self, name, content):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def git(self, *args):
        return subprocess.run(
            ["git", "-c", "core.hooksPath=/dev/null", "-c", "core.excludesFile=/dev/null", "-c", "commit.gpgsign=false",
             "-c", "user.name=Release test", "-c", "user.email=release-test@example.invalid",
             *args],
            cwd=self.repo, check=True, capture_output=True, text=True,
        ).stdout.strip()

    def commit(self):
        self.git("add", ".")
        self.git("commit", "-qm", "Release test fixture")

    def archive_path(self):
        short_hash = self.git("rev-parse", "--short=7", "HEAD")
        return self.repo / f"Surfing_windows-amd64_{short_hash}.zip"

    def build(self, check=True):
        # The script must resolve its project directory independently of cwd.
        return subprocess.run(
            ["sh", str(self.build_script)], cwd=self.temp.name,
            check=check, capture_output=True, text=True,
        )

    def test_only_committed_release_inputs_are_packaged(self):
        self.write("config/config.yaml", "url: PRIVATE_LOCAL_SUBSCRIPTION\n")
        self.write("scripts/Surfing.start.vbs", "' UNCOMMITTED_SCRIPT\n")
        self.write(".env.local", "PRIVATE_LOCAL_TOKEN\n")
        self.write("config/log/new.log", "PRIVATE_BROWSING_HISTORY\n")
        self.write("Surfing_windows-amd64_oldhash.zip", "OLD_ARCHIVE\n")

        self.build()
        with zipfile.ZipFile(self.archive_path()) as archive:
            files = {name for name in archive.namelist() if not name.endswith("/")}
            self.assertEqual(files, set(self.release_files))
            for name, expected in self.release_files.items():
                self.assertEqual(archive.read(name).decode("utf-8"), expected, name)

    def test_repeated_build_ignores_local_changes_and_existing_archives(self):
        self.build()
        first_archive = self.archive_path().read_bytes()
        self.write("config/config.yaml", "url: DIFFERENT_PRIVATE_SUBSCRIPTION\n")
        self.write("config/log/another.log", "LOCAL_HISTORY\n")
        self.build()
        self.assertEqual(self.archive_path().read_bytes(), first_archive)

    def test_failed_export_preserves_existing_archive(self):
        self.git("rm", "-q", "bin/clash-amd64.exe")
        self.commit()
        archive = self.archive_path()
        archive.write_bytes(b"existing release must survive")

        result = self.build(check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(archive.read_bytes(), b"existing release must survive")


if __name__ == "__main__":
    unittest.main()
