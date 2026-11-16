using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SurfingWin.Tray.Tests
{
    internal static class Harness
    {
        private static readonly List<string> Passed = new List<string>();
        private static readonly List<string> Skipped = new List<string>();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly List<string> Roots = new List<string>();
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }
        private static void Test(string name, Action action) { action(); Passed.Add(name); }
        private static void Reject(Action action, string part)
        {
            try { action(); } catch (TrayException e) { Check(e.Message.Contains(part), "Unexpected failure: " + e.Message); return; }
            throw new Exception("Expected rejection: " + part);
        }
        private static void Pump(Task task)
        {
            var watch = Stopwatch.StartNew();
            while (!task.IsCompleted && watch.ElapsedMilliseconds < 30000) { Application.DoEvents(); Thread.Sleep(10); }
            Check(task.IsCompleted, "UI task timed out"); task.GetAwaiter().GetResult();
        }
        private static int Port()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
        }
        private static bool CanConnectIPv6()
        {
            if (!Socket.OSSupportsIPv6) { Skipped.Add("IPv6 connection: unsupported by OS"); return false; }
            var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            try
            {
                listener.Start();
                using (var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp))
                    socket.Connect(listener.LocalEndpoint);
                return true;
            }
            catch (SocketException e)
            {
                Skipped.Add("IPv6 connection blocked by environment: Winsock " + e.NativeErrorCode); return false;
            }
            finally { listener.Stop(); }
        }
        private static void WaitFor(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
            Check(condition(), "Menu operation timed out");
        }
        private static string Fixture(string parent, string name, string stub, int port, string bind = "127.0.0.1")
        {
            string root = Path.Combine(parent, name);
            Roots.Add(root);
            Directory.CreateDirectory(Path.Combine(root, "bin")); Directory.CreateDirectory(Path.Combine(root, "config"));
            File.Copy(stub, Project.Core(root));
            WriteSettings(root, port, bind);
            return root;
        }
        private static void WriteSettings(string root, int port, string bind)
        {
            string address = bind.Contains(":") ? "[" + bind + "]:" + port : bind + ":" + port;
            File.WriteAllText(Project.Config(root), "external-controller: '" + address + "'\nsecret: 'fixture # token'\n");
            File.WriteAllText(Path.Combine(root, "config", "stub.json"), Json.Serialize(new Dictionary<string, object>
            { { "port", port }, { "bind", bind }, { "secret", "fixture # token" }, { "tun", false } }));
        }
        private static void Option(string root, string key, object value)
        {
            string path = Path.Combine(root, "config", "stub.json");
            var data = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)); data[key] = value;
            File.WriteAllText(path, Json.Serialize(data));
        }
        private static TrayState State(string root) { return CoreApi.ReadAsync(root).GetAwaiter().GetResult(); }
        private static void Click(System.Windows.Controls.Button button)
        {
            Check(button.IsEnabled, "Disabled WPF control was clicked");
            button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent, button));
            Application.DoEvents();
        }
        private static void DelayUi(int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--real-smoke")
            {
                string root = args[1];
                try
                {
                    int port = Port();
                    Directory.CreateDirectory(Path.Combine(root, "config"));
                    File.WriteAllText(Project.Config(root), "external-controller: '127.0.0.1:" + port +
                        "'\nsecret: '" + Guid.NewGuid().ToString("N") + "'\n" +
                        "mixed-port: 0\nallow-lan: false\nipv6: false\nmode: rule\n" +
                        "tun:\n  enable: false\ndns:\n  enable: false\n" +
                        "external-ui: ./webroot/Zash/\n" +
                        "geo-auto-update: false\nfind-process-mode: off\nrules:\n  - MATCH,DIRECT\n");
                    CoreManager.Execute(root, "start");
                    var running = State(root);
                    Check(running.Status == CoreStatus.Ready && !running.Tun && running.Mode == "rule", "real core readiness");
                    var panel = TrayBackend.PanelAddress(root);
                    Check(panel.AbsolutePath == "/ui/" && panel.Query == "" && panel.UserInfo == "" && panel.Fragment == "", "dashboard URL contains credentials or an incorrect path");
                    int assets = 0;
                    using (var handler = new HttpClientHandler { UseProxy = false })
                    using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) })
                    {
                        string html = client.GetStringAsync(panel).GetAwaiter().GetResult();
                        Check(html == File.ReadAllText(Path.Combine(root, "config", "webroot", "Zash", "index.html")), "dashboard homepage differs from bundle");
                        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(html, "(?:src|href)=\"(\\./assets/[^\"]+)\""))
                        {
                            string relative = match.Groups[1].Value.Substring(2);
                            byte[] downloaded = client.GetByteArrayAsync(new Uri(panel, relative)).GetAwaiter().GetResult();
                            Check(downloaded.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "config", "webroot", "Zash", relative.Replace('/', Path.DirectorySeparatorChar)))), "dashboard asset differs: " + relative);
                            assets++;
                        }
                        Check(assets >= 2, "dashboard entry assets were not checked");
                    }
                    CoreApi.ChangeModeAsync(root, "global").GetAwaiter().GetResult();
                    CoreApi.ChangeModeAsync(root, "direct").GetAwaiter().GetResult();
                    CoreManager.Execute(root, "stop");
                    Check(!State(root).ProcessExists, "real core did not stop");
                    string errorLog = Path.Combine(Project.Logs(root), "tray-error.log");
                    Check(!File.Exists(errorLog), "real core required forced termination");
                    File.WriteAllText(args[2], Json.Serialize(new { success = true, version = running.Version,
                        tun = running.Tun, modes = new[] { "rule", "global", "direct" }, gracefulStop = true, dashboardStatus = 200, dashboardAssets = assets }));
                    return 0;
                }
                catch (Exception e)
                {
                    File.WriteAllText(args[2], Json.Serialize(new { success = false, error = e.ToString() })); return 1;
                }
                finally { try { CoreManager.Execute(root, "stop"); } catch { } }
            }
            if (args.Length == 2 && args[0] == "--worker-start")
            {
                try { CoreManager.Execute(args[1], "start"); return 0; }
                catch { return 1; }
            }
            string resultPath = args[4];
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                string parent = args[0], stub = args[1], production = args[2], expectedHash = args[3];
                Test("packaged executable matches source stamp", delegate
                {
                    var assembly = Assembly.LoadFile(production);
                    var stamp = assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
                        .Cast<AssemblyMetadataAttribute>().Single(metadata => metadata.Key == "SourceSHA256");
                    Check(stamp.Value == expectedHash, "Rebuild bin/SurfingTray.exe after editing its sources");
                });
                Test("YAML scalar parsing preserves quoted and plain secrets", delegate
                {
                    Check(ControllerSettings.Scalar(" 'abc # 12''3' # comment") == "abc # 12'3", "single quote");
                    Check(ControllerSettings.Scalar(" \"a\\\\b\\\"c\" # comment") == "a\\b\"c", "double quote");
                    Check(ControllerSettings.Scalar(" token#value # comment") == "token#value", "plain hash");
                    Check(ControllerSettings.Scalar(" ''") == "", "empty secret");
                    Reject(() => ControllerSettings.Scalar("|"), "单行");
                    Reject(() => ControllerSettings.Scalar("\"unterminated"), "引号");
                });
                string a = Fixture(parent, "项目 A with spaces & symbols", stub, Port());
                string b = Fixture(parent, "项目 B with spaces & symbols", stub, Port());
                Test("controller discovery supports wildcards and rejects remote or duplicate settings", delegate
                {
                    string path = Path.Combine(parent, "settings.yaml");
                    File.WriteAllText(path, "external-controller: 0.0.0.0:9090 # note\nsecret: 'a # b'\n");
                    var settings = ControllerSettings.Load(path);
                    Check(settings.Address.Host == "127.0.0.1" && settings.Secret == "a # b", "wildcard settings");
                    File.WriteAllText(path, "external-controller: '[::]:9876'\n");
                    Check(ControllerSettings.Load(path).Address.Host.Trim('[', ']') == "::1", "IPv6 wildcard");
                    File.WriteAllText(path, "external-controller: 203.0.113.1:9090\n");
                    Reject(() => ControllerSettings.Load(path), "不属于本机");
                    File.WriteAllText(path, "external-controller: localhost:9090\nsecret: x\nsecret: y\n");
                    Reject(() => ControllerSettings.Load(path), "重复");
                    File.WriteAllText(path, "external-controller: localhost\n");
                    Reject(() => ControllerSettings.Load(path), "地址:端口");
                });
                Test("stopped state and failed validation never start a core", delegate
                {
                    Check(State(a).Status == CoreStatus.Stopped, "initial state");
                    Option(a, "validation-fail", true);
                    Reject(() => CoreManager.Execute(a, "start"), "配置检查失败");
                    Check(Native.OwnedProcesses(Project.Core(a)).Count == 0, "invalid config started a process");
                    Option(a, "validation-fail", false);
                });
                Test("native startup handles Chinese spaces ampersands and remains idempotent", delegate
                {
                    CoreManager.Execute(a, "start");
                    Check(State(a).Status == CoreStatus.Ready, "not ready");
                    var argv = Json.Deserialize<string[]>(File.ReadAllText(Path.Combine(a, "config", "started.json")));
                    Check(argv.SequenceEqual(new[] { "-d", Path.Combine(a, "config") }), "command-line quoting");
                    int pid = Native.OwnedProcesses(Project.Core(a)).Single();
                    CoreManager.Execute(a, "start");
                    Check(Native.OwnedProcesses(Project.Core(a)).Single() == pid, "duplicate core");
                });
                Test("mode commands are authenticated verified and do not rewrite YAML", delegate
                {
                    string before = File.ReadAllText(Project.Config(a));
                    CoreApi.ChangeModeAsync(a, "global").GetAwaiter().GetResult();
                    Check(State(a).Mode == "global", "global mode");
                    CoreApi.ChangeModeAsync(a, "direct").GetAwaiter().GetResult();
                    Check(State(a).Mode == "direct", "direct mode");
                    Check(File.ReadAllText(Project.Config(a)) == before, "mode changed config file");
                    Reject(() => CoreApi.ChangeModeAsync(a, "invalid").GetAwaiter().GetResult(), "无效");
                    Option(a, "ignore-mode", true);
                    Reject(() => CoreApi.ChangeModeAsync(a, "rule").GetAwaiter().GetResult(), "未能确认");
                    Option(a, "ignore-mode", false);
                });
                Test("authentication malformed response and timeout remain warning states", delegate
                {
                    Option(a, "secret", "different"); Check(State(a).Status == CoreStatus.Authentication, "401 state");
                    Option(a, "secret", "fixture # token");
                    Option(a, "fault", "invalid-json"); Check(State(a).Status == CoreStatus.Unavailable, "JSON state");
                    Option(a, "fault", "timeout"); Check(State(a).Detail.Contains("超时"), "timeout state");
                    Option(a, "fault", ""); Thread.Sleep(1200);
                });
                Test("stop is graceful and leaves another project core alive", delegate
                {
                    CoreManager.Execute(b, "start");
                    CoreManager.Execute(a, "stop");
                    Check(!State(a).ProcessExists && State(b).Status == CoreStatus.Ready, "instance isolation");
                    Check(File.Exists(Path.Combine(a, "config", "graceful.marker")), "no graceful shutdown signal");
                });
                Test("foreign controller port is never sent credentials or mode commands", delegate
                {
                    int port = ControllerSettings.Load(Project.Config(b)).Address.Port;
                    string before = File.ReadAllText(Path.Combine(b, "config", "requests.log"));
                    WriteSettings(a, port, "127.0.0.1");
                    Reject(() => CoreApi.ChangeModeAsync(a, "global").GetAwaiter().GetResult(), "其他程序");
                    Reject(() => CoreManager.Execute(a, "start"), "占用");
                    CoreManager.Execute(a, "stop");
                    Check(File.ReadAllText(Path.Combine(b, "config", "requests.log")) == before, "foreign API received a request");
                    Check(Native.OwnedProcesses(Project.Core(b)).Count == 1, "foreign core stopped");
                    WriteSettings(a, Port(), "127.0.0.1");
                });
                Test("restart replaces only the project process", delegate
                {
                    CoreManager.Execute(a, "start"); int previous = Native.OwnedProcesses(Project.Core(a)).Single();
                    CoreManager.Execute(a, "restart");
                    Check(Native.OwnedProcesses(Project.Core(a)).Single() != previous, "restart kept old PID");
                    Check(State(a).Status == CoreStatus.Ready && State(b).Status == CoreStatus.Ready, "restart isolation");
                    CoreManager.Execute(a, "stop"); CoreManager.Execute(b, "stop");
                });
                Test("core survives the short-lived manager process exiting", delegate
                {
                    using (var worker = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                        "--worker-start " + Project.Quote(a)) { UseShellExecute = false, CreateNoWindow = true }))
                        Check(worker.WaitForExit(15000) && worker.ExitCode == 0, "manager worker failed");
                    Check(State(a).Status == CoreStatus.Ready, "core exited with its manager");
                    CoreManager.Execute(a, "stop");
                });
                Test("duplicate tray launch signals existing instance without starting a core", delegate
                {
                    File.Copy(production, Path.Combine(a, "bin", "SurfingTray.exe"));
                    using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SurfingWin.Start." + Project.Identity(a)))
                    using (var mutex = new Mutex(false, @"Local\SurfingWin.Tray." + Project.Identity(a)))
                    {
                        mutex.WaitOne();
                        try
                        {
                            using (var duplicate = Process.Start(new ProcessStartInfo(Path.Combine(a, "bin", "SurfingTray.exe"), "--start") { UseShellExecute = false, CreateNoWindow = true }))
                            { Check(duplicate.WaitForExit(10000) && duplicate.ExitCode == 0, "duplicate did not exit"); }
                            Check(signal.WaitOne(1000), "existing tray was not signaled");
                            Check(!State(a).ProcessExists, "duplicate launch started a real action");
                        }
                        finally { mutex.ReleaseMutex(); }
                    }
                });
                if (Socket.OSSupportsIPv6)
                    Test("IPv6 controller listener ownership is resolved correctly", delegate
                    {
                        string v6 = Fixture(parent, "IPv6 ownership", stub, Port(), "::1");
                        Directory.CreateDirectory(Project.Logs(v6));
                        Native.StartHidden(Project.Core(v6), "-d " + Project.Quote(Path.Combine(v6, "config")), v6,
                            Path.Combine(Project.Logs(v6), "fixture.log"));
                        var watch = Stopwatch.StartNew();
                        while (!File.Exists(Path.Combine(v6, "config", "started.json")) && watch.ElapsedMilliseconds < 5000)
                            Thread.Sleep(20);
                        Check(Native.Listeners(ControllerSettings.Load(Project.Config(v6)).Address)
                            .Intersect(Native.OwnedProcesses(Project.Core(v6))).Any(), "IPv6 PID ownership");
                        CoreManager.Execute(v6, "stop");
                    });
                if (CanConnectIPv6())
                    Test("IPv6 loopback controller ownership and API work", delegate
                    {
                        string v6 = Fixture(parent, "IPv6 fixture", stub, Port(), "::1");
                        try { CoreManager.Execute(v6, "start"); }
                        catch (Exception e)
                        {
                            string log = Path.Combine(v6, "config", "requests.log");
                            throw new Exception("IPv6 status: " + State(v6).Detail + "; owners: " +
                                String.Join(",", Native.Listeners(ControllerSettings.Load(Project.Config(v6)).Address)) +
                                "; requests: " + (File.Exists(log) ? File.ReadAllText(log) : "none"), e);
                        }
                        Check(State(v6).Status == CoreStatus.Ready, "IPv6 not ready");
                        CoreManager.Execute(v6, "stop");
                    });
                Test("real WinForms menu routes commands reflects status and renders", delegate
                {
                    var backend = new FakeBackend();
                    using (var context = new TrayContext(backend, null, false, false, false))
                    {
                        Pump(context.RefreshAsync());
                        Check(context.Item("start").Enabled && !context.Item("stop").Enabled, "stopped menu");
                        context.Item("start").PerformClick();
                        Check(backend.Actions.SequenceEqual(new[] { "start" }) && context.Item("stop").Enabled, "start routing");
                        context.Item("mode-global").PerformClick();
                        Check(backend.State.Mode == "global" && context.Item("mode-global").Checked, "mode menu");
                        foreach (string id in new[] { "panel", "config", "logs" }) context.Item(id).PerformClick();
                        Check(backend.Opened.SequenceEqual(new[] { "panel", "config", "logs" }), "open routing");
                        context.Menu.PerformLayout(); context.Menu.Size = context.Menu.GetPreferredSize(Size.Empty);
                        using (var image = new Bitmap(context.Menu.Width, context.Menu.Height))
                        { context.Menu.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(args[5]); }
                        context.Item("exit-tray").PerformClick();
                        Check(context.IsClosing && backend.Actions.Count == 1 && backend.State.ProcessExists, "exit tray stopped core");
                    }
                });
                Test("failed stop cannot close tray and authentication disables mode changes", delegate
                {
                    var backend = new FakeBackend(); backend.State = FakeBackend.Ready(); backend.FailStop = true;
                    using (var context = new TrayContext(backend, null, false, false, false))
                    {
                        Pump(context.RefreshAsync()); context.Item("exit-stop").PerformClick();
                        Check(!context.IsClosing && context.Item("stop").Enabled, "closed after failed stop");
                        backend.State.Status = CoreStatus.Authentication; backend.State.Detail = "认证失败";
                        Pump(context.RefreshAsync());
                        Check(!context.Item("modes").Enabled && context.Item("stop").Enabled, "auth menu state");
                        backend.FailStop = false; context.Item("exit-stop").PerformClick();
                        Check(context.IsClosing && !backend.State.ProcessExists, "stop then exit");
                    }
                });
                Test("menu waits for status refresh and prevents overlapping operations", delegate
                {
                    var backend = new FakeBackend();
                    var readGate = new TaskCompletionSource<TrayState>();
                    var startGate = new TaskCompletionSource<int>();
                    backend.ReadGate = readGate; backend.StartGate = startGate;
                    using (var context = new TrayContext(backend, null, false, false, false))
                    {
                        Task refresh = context.RefreshAsync();
                        context.Item("start").PerformClick();
                        Check(backend.Actions.Count == 0, "action raced the status read");
                        readGate.SetResult(backend.State); Pump(refresh);
                        WaitFor(() => backend.Actions.Count == 1);
                        context.Item("start").PerformClick(); context.Item("stop").PerformClick();
                        Check(backend.Actions.Count == 1 && !context.Item("exit-tray").Enabled, "overlapping action");
                        startGate.SetResult(0); WaitFor(() => !context.IsBusy);
                        Check(context.Item("stop").Enabled && backend.Actions.Single() == "start", "menu did not recover");
                        context.Item("exit-tray").PerformClick();
                    }
                });
                Test("Fluent popup placement stays within negative-coordinate monitors and scaled working areas", delegate
                {
                    foreach (var area in new[] { new Rectangle(0, 0, 1920, 1040), new Rectangle(-1920, -400, 1920, 1080),
                        new Rectangle(1920, 0, 2560, 1400), new Rectangle(-900, -1400, 900, 1400), new Rectangle(0, 0, 320, 480) })
                        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                            foreach (var point in new[] { area.Location, new Point(area.Right - 20, area.Bottom + 20),
                                new Point(area.Left + 5, area.Top + 5), new Point(area.Right - 5, area.Top + 5) })
                            {
                                var bounds = FluentControlCenter.PlacePopup(area, point, (int)(400 * scale), (int)(490 * scale));
                                Check(area.Contains(bounds), "Popup escaped screen: " + bounds + " from " + area);
                            }
                });
                Test("real WPF control center routes actions reflects state and preserves native menu", delegate
                {
                    var backend = new FakeBackend();
                    using (var context = new TrayContext(backend, null, false, false, false))
                    {
                        Pump(context.RefreshAsync());
                        var area = Screen.PrimaryScreen.WorkingArea;
                        context.ShowControlCenter(new Point(area.Right - 25, area.Bottom - 15), false);
                        var center = context.Center;
                        Check(center != null && center.IsVisible, "WPF window did not open: " + backend.LastError);
                        Check(center.Heading == "代理已停止" && center.Item("power").IsEnabled && !center.Item("panel").IsEnabled, "stopped popup state");
                        Click(center.Item("power")); WaitFor(() => !context.IsBusy);
                        Check(backend.Actions.SequenceEqual(new[] { "start" }) && center.Heading == "代理已启动", "popup start routing");
                        Click(center.Item("mode-global")); WaitFor(() => !context.IsBusy);
                        Check(backend.State.Mode == "global" && context.Item("mode-global").Checked, "popup and menu mode disagree");
                        foreach (string id in new[] { "panel", "config", "logs" }) Click(center.Item(id));
                        Check(backend.Opened.SequenceEqual(new[] { "panel", "config", "logs" }), "popup open routing");
                        Click(center.Item("restart")); WaitFor(() => !context.IsBusy);
                        Check(backend.Actions.Last() == "restart", "popup restart routing");
                        backend.State.Tun = false; Pump(context.RefreshAsync()); Check(center.TunStatus == "已关闭", "TUN false state");
                        backend.State.Status = CoreStatus.Authentication; backend.State.Detail = "认证失败"; Pump(context.RefreshAsync());
                        Check(!center.Item("mode-rule").IsEnabled && !center.Item("panel").IsEnabled && center.Item("power").IsEnabled && center.Detail == "认证失败", "authentication popup controls");
                        backend.State = FakeBackend.Ready(); backend.FailStop = true; Pump(context.RefreshAsync());
                        Click(center.Item("power")); WaitFor(() => !context.IsBusy);
                        Check(!context.IsClosing && backend.State.ProcessExists && center.Detail.Contains("Fixture stop failed"), "popup lost failed stop feedback");
                        backend.FailStop = false; Click(center.Item("power")); WaitFor(() => !context.IsBusy);
                        Check(!backend.State.ProcessExists && center.Heading == "代理已停止", "popup stop routing");
                        center.Close(); Application.DoEvents();
                        Check(!center.IsVisible && !context.IsClosing, "closing popup exited the tray");
                    }
                });
                Test("Fluent popup serializes commands while a background operation is pending", delegate
                {
                    var backend = new FakeBackend(); var gate = new TaskCompletionSource<int>(); backend.StartGate = gate;
                    using (var context = new TrayContext(backend, null, false, false, false))
                    {
                        Pump(context.RefreshAsync()); context.ShowControlCenter(new Point(400, 500), false);
                        var center = context.Center; Click(center.Item("power"));
                        Check(context.IsBusy && !center.Item("power").IsEnabled && !center.Item("mode-rule").IsEnabled, "busy popup allows command overlap");
                        center.Item("power").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        Check(backend.Actions.Count == 1, "duplicate popup command");
                        gate.SetResult(0); WaitFor(() => !context.IsBusy);
                        Check(center.Item("power").IsEnabled && center.Item("mode-rule").IsEnabled, "popup did not recover after operation");
                        center.Dismiss();
                    }
                });
                Test("Fluent single click double click Escape and outside focus preserve tray lifetime", delegate
                {
                    var backend = new FakeBackend(); backend.State = FakeBackend.Ready();
                    using (var context = new TrayContext(backend, null, false, false, false))
                    {
                        Pump(context.RefreshAsync());
                        context.ScheduleControlCenter(new Point(400, 500)); Check(context.Center == null, "single click not debounced");
                        context.HandleDoubleClick(); DelayUi(SystemInformation.DoubleClickTime + 80);
                        Check(context.Center == null && backend.Opened.SequenceEqual(new[] { "panel" }), "double click also opened popup");
                        context.ScheduleControlCenter(new Point(400, 500)); WaitFor(() => context.Center != null && context.Center.IsVisible);
                        var center = context.Center;
                        var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                            System.Windows.PresentationSource.FromVisual(center), 0, System.Windows.Input.Key.Escape)
                            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent };
                        center.RaiseEvent(key); Check(!center.IsVisible && !context.IsClosing, "Escape exited the tray");
                        context.ShowControlCenter(new Point(400, 500), false);
                        using (var outside = new Form { Width = 100, Height = 100, ShowInTaskbar = false })
                        {
                            outside.Show(); outside.Activate(); WaitFor(() => !center.IsVisible);
                            context.ScheduleControlCenter(new Point(400, 500)); DelayUi(SystemInformation.DoubleClickTime + 80);
                            Check(!center.IsVisible, "tray click reopened the popup immediately after losing focus"); outside.Close();
                        }
                        Check(!context.IsClosing && backend.State.ProcessExists, "outside focus stopped core");
                    }
                });
                Test("Fluent light and dark surfaces render at 100 125 150 and 200 percent", delegate
                {
                    var backend = new FakeBackend(); backend.State = FakeBackend.Ready();
                    using (var context = new TrayContext(backend, null, false, false, false))
                    {
                        Pump(context.RefreshAsync()); context.ShowControlCenter(new Point(700, 700), false);
                        var center = context.Center;
                        foreach (bool dark in new[] { false, true })
                        {
                            center.SetAppearance(dark); Check(center.DarkAppearance == dark, "appearance not applied");
                            DelayUi(80);
                            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                            {
                                string path = Path.Combine(parent, "fluent-" + (dark ? "dark" : "light") + "-" + (int)(scale * 100) + ".png");
                                center.SavePreview(path, scale);
                                using (var image = Image.FromFile(path))
                                    Check(image.Width >= 360 * scale && image.Height >= 300 * scale, "scaled preview is empty");
                            }
                        }
                        center.SetAppearance(null); center.Dismiss();
                    }
                });
                File.WriteAllText(resultPath, Json.Serialize(new { success = true, passed = Passed, skipped = Skipped })); return 0;
            }
            catch (Exception e)
            { File.WriteAllText(resultPath, Json.Serialize(new { success = false, passed = Passed, skipped = Skipped, error = e.ToString() })); return 1; }
            finally
            {
                foreach (string root in Roots)
                    try { CoreManager.Execute(root, "stop"); } catch { }
            }
        }

        private sealed class FakeBackend : ITrayBackend
        {
            internal TrayState State = new TrayState { Status = CoreStatus.Stopped, Detail = "已停止" };
            internal readonly List<string> Actions = new List<string>(), Opened = new List<string>();
            internal bool FailStop;
            internal Exception LastError;
            internal TaskCompletionSource<TrayState> ReadGate;
            internal TaskCompletionSource<int> StartGate;
            internal static TrayState Ready()
            { return new TrayState { Status = CoreStatus.Ready, ProcessExists = true, Detail = "运行中", Version = "1.19.34", Mode = "rule", Tun = true }; }
            public Task<TrayState> ReadStateAsync()
            {
                if (ReadGate != null) { var gate = ReadGate; ReadGate = null; return gate.Task; }
                return Task.FromResult(State);
            }
            public Task ExecuteAsync(string action)
            {
                Actions.Add(action);
                if (action == "stop" && FailStop) throw new TrayException("Fixture stop failed");
                State = action == "stop" ? new TrayState { Status = CoreStatus.Stopped, Detail = "已停止" } : Ready();
                return action == "start" && StartGate != null ? StartGate.Task : Task.FromResult(0);
            }
            public Task ChangeModeAsync(string mode) { State.Mode = mode; return Task.FromResult(0); }
            public void Open(string target) { Opened.Add(target); }
            public void ReportError(Exception error) { LastError = error; }
        }
    }
}
