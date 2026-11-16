using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("SurfingWin Tray")]
[assembly: AssemblyDescription("Portable Mihomo tray controller")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace SurfingWin.Tray
{
    internal interface ITrayBackend
    {
        Task<TrayState> ReadStateAsync();
        Task ExecuteAsync(string action);
        Task ChangeModeAsync(string mode);
        void Open(string target);
        void ReportError(Exception error);
    }

    internal sealed class TrayBackend : ITrayBackend
    {
        private readonly string root;
        internal TrayBackend(string root) { this.root = root; }
        internal static Uri PanelAddress(string root)
        { return new Uri(ControllerSettings.Load(Project.Config(root)).Address, "ui/"); }
        public Task<TrayState> ReadStateAsync() { return Task.Run(() => CoreApi.ReadAsync(root)); }
        public Task ExecuteAsync(string action) { return Task.Run(() => Program.RunCommand(root, action)); }
        public Task ChangeModeAsync(string mode) { return Task.Run(() => CoreApi.ChangeModeAsync(root, mode)); }
        public void ReportError(Exception error)
        {
            Project.Log(root, error.GetType().Name + ": " + (error is TrayException ? error.Message : "托盘操作失败。"));
        }
        public void Open(string target)
        {
            if (target == "panel")
            {
                // Deliberately omit the API secret from the browser URL and history.
                string url = PanelAddress(root).AbsoluteUri;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (target == "config")
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "notepad.exe"), Project.Quote(Project.Config(root))) { UseShellExecute = true });
            else if (target == "logs")
            {
                Directory.CreateDirectory(Project.Logs(root));
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe"), Project.Quote(Project.Logs(root))) { UseShellExecute = true });
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly ITrayBackend backend;
        private readonly NotifyIcon icon;
        private readonly System.Windows.Forms.Timer timer;
        private readonly System.Windows.Forms.Timer clickTimer;
        private readonly Font menuFont;
        private readonly EventWaitHandle startSignal;
        private readonly Icon readyIcon, stoppedIcon, warningIcon;
        private readonly Dictionary<string, ToolStripMenuItem> items = new Dictionary<string, ToolStripMenuItem>();
        private TrayState state = new TrayState();
        private bool busy, refreshing, closing, pendingStart, errorTip;
        private FluentControlCenter center;
        private Point clickPoint;
        private DateTime lastDoubleClick = DateTime.MinValue;
        private string operationError;
        private bool clickClosesCenter;
        internal readonly ContextMenuStrip Menu;
        internal bool IsClosing { get { return closing; } }
        internal bool IsBusy { get { return busy || refreshing; } }
        internal FluentControlCenter Center { get { return center; } }

        internal TrayContext(ITrayBackend backend, EventWaitHandle signal, bool autoStart, bool visible = true,
            bool poll = true)
        {
            this.backend = backend;
            startSignal = signal;
            pendingStart = autoStart;
            menuFont = new Font("Segoe UI", 9.5f);
            Menu = new ContextMenuStrip { ShowImageMargin = false, Font = menuFont, Padding = new Padding(5) };
            Add("status", "SurfingWin · 正在检查", null).Enabled = false;
            Add("summary", "模式：—    TUN：—", null).Enabled = false;
            Menu.Items.Add(new ToolStripSeparator());
            Add("panel", "打开 Zashboard 面板", delegate { Open("panel"); });
            Menu.Items.Add(new ToolStripSeparator());
            Add("start", "启动代理", async delegate { await ExecuteAsync("start", false); });
            Add("stop", "停止代理", async delegate { await ExecuteAsync("stop", false); });
            Add("restart", "重启代理", async delegate { await ExecuteAsync("restart", false); });
            var modes = Add("modes", "代理模式（当前运行）", null);
            foreach (string mode in new[] { "rule", "global", "direct" })
            {
                string captured = mode;
                var entry = new ToolStripMenuItem(ModeName(mode)) { Name = "mode-" + mode };
                entry.Click += async delegate { await ChangeModeAsync(captured); };
                modes.DropDownItems.Add(entry);
                items.Add(entry.Name, entry);
            }
            Menu.Items.Add(new ToolStripSeparator());
            Add("config", "编辑配置文件", delegate { Open("config"); });
            Add("logs", "打开日志目录", delegate { Open("logs"); });
            Menu.Items.Add(new ToolStripSeparator());
            Add("exit-tray", "退出托盘，保持代理运行", delegate { ExitThread(); });
            Add("exit-stop", "停止代理并退出", async delegate { await ExecuteAsync("stop", true); });
            Menu.Opening += delegate { CancelPendingClick(); if (center != null) center.Dismiss(); ApplyMenuTheme(); };
            readyIcon = DrawIcon(Color.FromArgb(0, 143, 107));
            stoppedIcon = DrawIcon(Color.FromArgb(118, 125, 136));
            warningIcon = DrawIcon(Color.FromArgb(205, 139, 0));
            icon = new NotifyIcon { Icon = warningIcon, Text = "SurfingWin · 正在检查", ContextMenuStrip = Menu,
                Visible = visible };
            clickTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime };
            clickTimer.Tick += delegate
            {
                clickTimer.Stop();
                if (closing) return;
                if (clickClosesCenter && center != null) center.Dismiss(); else ToggleControlCenter(clickPoint);
            };
            icon.MouseClick += delegate(object sender, MouseEventArgs e)
            { if (e.Button == MouseButtons.Left) ScheduleControlCenter(Cursor.Position); };
            icon.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            { if (e.Button == MouseButtons.Left) HandleDoubleClick(); };
            icon.BalloonTipClicked += delegate { if (errorTip) Open("logs"); };
            timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += async delegate
            {
                if (closing || busy || refreshing) return;
                timer.Interval = 3000;
                if (startSignal != null && startSignal.WaitOne(0)) pendingStart = true;
                if (pendingStart) { pendingStart = false; await ExecuteAsync("start", false); }
                else await RefreshAsync();
            };
            Render(null);
            if (poll) timer.Start();
        }

        private ToolStripMenuItem Add(string id, string label, EventHandler handler)
        {
            var item = new ToolStripMenuItem(label) { Name = id };
            if (handler != null) item.Click += handler;
            items.Add(id, item); Menu.Items.Add(item); return item;
        }
        internal ToolStripMenuItem Item(string id) { return items[id]; }
        internal void ScheduleControlCenter(Point point)
        {
            if (closing || (DateTime.UtcNow - lastDoubleClick).TotalMilliseconds < SystemInformation.DoubleClickTime) return;
            clickClosesCenter = center != null && (center.IsVisible || center.JustLostFocus);
            clickPoint = point; clickTimer.Stop(); clickTimer.Start();
        }
        private void CancelPendingClick() { if (clickTimer != null) clickTimer.Stop(); }
        internal void HandleDoubleClick()
        {
            lastDoubleClick = DateTime.UtcNow; CancelPendingClick();
            if (center != null) center.Dismiss();
            if (state.Status == CoreStatus.Ready) Open("panel");
        }
        private void ToggleControlCenter(Point point)
        {
            if (center != null && center.IsVisible) { center.Dismiss(); return; }
            ShowControlCenter(point, true);
        }
        internal void ShowControlCenter(Point point, bool animate)
        {
            if (closing) return;
            try
            {
                if (center == null)
                {
                    center = new FluentControlCenter();
                    center.ActionRequested += async delegate(string action)
                    {
                        if (action == "start" || action == "stop" || action == "restart") await ExecuteAsync(action, false);
                        else Open(action);
                    };
                    center.ModeRequested += async delegate(string mode) { await ChangeModeAsync(mode); };
                    center.MenuRequested += delegate(Point anchor) { center.Dismiss(); Menu.Show(anchor); };
                }
                center.Render(state, busy, null, operationError);
                center.ShowAt(point, animate);
            }
            catch (Exception error) { ShowError(error); }
        }
        private void ApplyMenuTheme()
        {
            var renderer = new FluentMenuRenderer(FluentControlCenter.SystemUsesDarkTheme());
            Menu.Renderer = renderer; Menu.BackColor = renderer.Background; Menu.ForeColor = renderer.Foreground;
            foreach (var item in items.Values)
            {
                item.Padding = new Padding(10, 7, 10, 7);
                if (item.HasDropDownItems)
                {
                    item.DropDown.Renderer = renderer; item.DropDown.BackColor = renderer.Background;
                    item.DropDown.ForeColor = renderer.Foreground; item.DropDown.Font = menuFont;
                }
            }
        }
        internal static string ModeName(string mode)
        { return mode == "rule" ? "规则" : mode == "global" ? "全局" : mode == "direct" ? "直连" : "—"; }

        internal async Task RefreshAsync()
        {
            if (closing || refreshing) return;
            refreshing = true;
            try { state = await backend.ReadStateAsync(); }
            catch (Exception e)
            {
                backend.ReportError(e);
                state = new TrayState { Status = CoreStatus.Unavailable, Detail = "状态检查失败，请查看日志。" };
            }
            finally { refreshing = false; }
            if (!closing) Render(null);
        }

        private void Render(string operation)
        {
            bool ready = state.Status == CoreStatus.Ready && !busy;
            string detail = operation ?? state.Detail;
            items["status"].Text = "SurfingWin · " + detail;
            items["summary"].Text = "模式：" + ModeName(state.Mode) + "    TUN：" +
                (state.Status == CoreStatus.Ready ? (state.Tun ? "开启" : "关闭") : "—");
            items["panel"].Enabled = ready;
            items["start"].Enabled = !busy && !state.ProcessExists;
            items["stop"].Enabled = items["restart"].Enabled = !busy && state.ProcessExists;
            items["modes"].Enabled = ready;
            foreach (string mode in new[] { "rule", "global", "direct" })
            {
                items["mode-" + mode].Checked = state.Mode == mode;
                items["mode-" + mode].Enabled = ready;
            }
            items["exit-tray"].Enabled = items["exit-stop"].Enabled = !busy;
            icon.Icon = busy ? warningIcon : state.Status == CoreStatus.Ready ? readyIcon :
                state.Status == CoreStatus.Stopped ? stoppedIcon : warningIcon;
            string tooltip = "SurfingWin · " + detail;
            if (state.Status == CoreStatus.Ready) tooltip += " | " + state.Version + " | " + ModeName(state.Mode);
            icon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
            if (center != null) center.Render(state, busy, operation, operationError);
        }

        private async Task ExecuteAsync(string action, bool exitAfter)
        {
            if (closing || busy) return;
            while (refreshing && !closing) await Task.Delay(25);
            if (closing || busy) return;
            busy = true;
            operationError = null;
            Render(action == "start" ? "正在启动…" : action == "stop" ? "正在停止…" : "正在重启…");
            bool success = false;
            try
            {
                await backend.ExecuteAsync(action);
                state = await backend.ReadStateAsync();
                success = action == "stop" ? !state.ProcessExists : state.Status == CoreStatus.Ready;
                if (!success) throw new TrayException("操作结果未能确认，请查看日志。");
            }
            catch (Exception e) { ShowError(e); }
            finally { busy = false; }
            if (closing) return;
            if (success && exitAfter) { ExitThread(); return; }
            await RefreshAsync();
        }

        private async Task ChangeModeAsync(string mode)
        {
            if (closing || busy) return;
            while (refreshing && !closing) await Task.Delay(25);
            if (closing || busy || state.Status != CoreStatus.Ready) return;
            busy = true; operationError = null; Render("正在切换模式…");
            try { await backend.ChangeModeAsync(mode); }
            catch (Exception e) { ShowError(e); }
            finally { busy = false; }
            await RefreshAsync();
        }

        private void Open(string target)
        {
            try { backend.Open(target); }
            catch (Exception e) { ShowError(e); }
        }
        private void ShowError(Exception error)
        {
            backend.ReportError(error);
            errorTip = true;
            string message = error is TrayException ? error.Message : "操作失败，请打开日志目录查看详细信息。";
            operationError = message;
            icon.ShowBalloonTip(8000, "SurfingWin", message + " 点击此通知可打开日志目录。", ToolTipIcon.Warning);
            if (center != null) center.Render(state, busy, null, operationError);
        }

        protected override void ExitThreadCore()
        {
            if (closing) return;
            closing = true;
            timer.Stop();
            clickTimer.Stop(); clickTimer.Dispose();
            if (center != null) { center.Dispose(); center = null; }
            icon.Visible = false;
            icon.Dispose();
            timer.Dispose(); Menu.Dispose(); menuFont.Dispose();
            readyIcon.Dispose(); stoppedIcon.Dispose(); warningIcon.Dispose();
            base.ExitThreadCore();
        }

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        private static Icon DrawIcon(Color color)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(color))
            using (var pen = new Pen(Color.White, 3.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                graphics.FillEllipse(brush, 1, 1, 30, 30);
                graphics.DrawBezier(pen, 8, 21, 12, 9, 20, 25, 24, 12);
                IntPtr handle = bitmap.GetHicon();
                try { using (var source = Icon.FromHandle(handle)) return (Icon)source.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }

    internal static class Program
    {
        internal static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        internal static void RunCommand(string root, string action)
        {
            if (action != "start" && action != "stop" && action != "restart") throw new TrayException("未知操作。");
            string id = Guid.NewGuid().ToString("N");
            string resultPath = ResultPath(root, id);
            try
            {
                var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                    "--core-action " + action + " " + id)
                {
                    WorkingDirectory = root, UseShellExecute = true,
                    Verb = IsAdministrator() ? "open" : "runas", WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var helper = Process.Start(info))
                {
                    helper.WaitForExit();
                    if (!File.Exists(resultPath)) throw new TrayException("启停操作未完成，请查看日志并检查管理员权限。");
                    var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(resultPath));
                    if (helper.ExitCode != 0 || !(bool)result["success"])
                        throw new TrayException(Convert.ToString(result["message"]));
                }
            }
            catch (Win32Exception e)
            {
                if (e.NativeErrorCode == 1223) throw new TrayException("已取消管理员授权，代理状态未由本次操作更改。");
                throw new TrayException("无法启动管理程序，请检查系统权限。");
            }
            finally
            {
                try { if (File.Exists(resultPath)) File.Delete(resultPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string ResultPath(string root, string id)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id, "N", out parsed)) throw new TrayException("无效的操作编号。");
            return Path.Combine(Project.Logs(root), "tray-result-" + parsed.ToString("N") + ".json");
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            try
            {
                if (args.Length == 3 && args[0] == "--core-action")
                {
                    string resultPath = ResultPath(root, args[2]);
                    bool success = false;
                    string message = "";
                    try
                    {
                        if (!IsAdministrator()) throw new TrayException("此操作需要管理员权限。");
                        CoreManager.Execute(root, args[1]); success = true;
                    }
                    catch (Exception e)
                    {
                        message = e is TrayException ? e.Message : "内核管理操作失败，请检查日志和系统权限。";
                        Project.Log(root, e.GetType().Name + ": " + message);
                    }
                    Directory.CreateDirectory(Project.Logs(root));
                    File.WriteAllText(resultPath, new JavaScriptSerializer().Serialize(new { success, message }));
                    return success ? 0 : 1;
                }
                if (args.Length == 1 && (args[0] == "--stop" || args[0] == "--restart"))
                {
                    RunCommand(root, args[0].Substring(2)); return 0;
                }
                if (args.Length > 1 || (args.Length == 1 && args[0] != "--start"))
                    throw new TrayException("不支持的启动参数。");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset,
                    @"Local\SurfingWin.Start." + Project.Identity(root)))
                using (var instance = new Mutex(false, @"Local\SurfingWin.Tray." + Project.Identity(root)))
                {
                    bool acquired;
                    try { acquired = instance.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                    {
                        if (args.Length != 0) signal.Set();
                        return 0;
                    }
                    try
                    {
                        using (var context = new TrayContext(new TrayBackend(root), signal, args.Length != 0))
                            Application.Run(context);
                    }
                    finally { instance.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception e)
            {
                string message = e is TrayException ? e.Message : "托盘管理程序发生错误，请查看 config\\log\\tray-error.log。";
                Project.Log(root, e.GetType().Name + ": " + e.Message);
                MessageBox.Show(message, "SurfingWin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
    }
}
