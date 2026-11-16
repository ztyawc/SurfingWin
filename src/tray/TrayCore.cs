using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SurfingWin.Tray
{
    internal sealed class TrayException : Exception
    {
        internal readonly bool Authentication;
        internal TrayException(string message, bool authentication = false) : base(message)
        { Authentication = authentication; }
    }

    internal static class Project
    {
        internal static string Core(string root) { return Path.Combine(root, "bin", "clash-amd64.exe"); }
        internal static string Config(string root) { return Path.Combine(root, "config", "config.yaml"); }
        internal static string Logs(string root) { return Path.Combine(root, "config", "log"); }
        internal static string Identity(string root)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant()))).Replace("-", "").Substring(0, 32);
        }
        internal static string Quote(string value)
        {
            // CommandLineToArgvW quoting. No cmd.exe or PowerShell is involved.
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        internal static void Log(string root, string message)
        {
            try
            {
                Directory.CreateDirectory(Logs(root));
                string path = Path.Combine(Logs(root), "tray-error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                {
                    string old = path + ".previous";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class ControllerSettings
    {
        internal readonly Uri Address;
        internal readonly string Secret;
        internal ControllerSettings(Uri address, string secret) { Address = address; Secret = secret; }

        internal static ControllerSettings Load(string path)
        {
            string controller = null, secret = "";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                // Read only the two top-level scalar fields; never rewrite the YAML.
                var match = Regex.Match(line, "^(?:[\"']?)(external-controller|secret)(?:[\"']?)\\s*:(.*)$");
                if (!match.Success) continue;
                string key = match.Groups[1].Value;
                if (!seen.Add(key)) throw new TrayException("控制接口配置存在重复字段，请检查 config.yaml。");
                string value = Scalar(match.Groups[2].Value);
                if (key == "external-controller") controller = value; else secret = value;
            }
            if (String.IsNullOrWhiteSpace(controller))
                throw new TrayException("请在 config.yaml 中设置 external-controller，托盘需要控制接口。");
            if (secret.Any(c => c < 32 || c == 127))
                throw new TrayException("控制接口密码不能包含换行或控制字符。");
            string normalized = controller.StartsWith("*:", StringComparison.Ordinal)
                ? "127.0.0.1:" + controller.Substring(2) : controller;
            Uri uri;
            if (!Regex.IsMatch(normalized, @":\d+$") || !Uri.TryCreate("http://" + normalized, UriKind.Absolute, out uri) ||
                uri.Port < 1 || uri.Port > 65535 || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new TrayException("external-controller 必须是本机的 地址:端口。");
            string host = uri.Host.Trim('[', ']');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "0.0.0.0") host = "127.0.0.1";
            if (host == "::") host = "::1";
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
                throw new TrayException("托盘仅管理本机控制接口，请使用本机 IP 或 localhost。");
            if (!IPAddress.IsLoopback(ip) && !NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(ip)))
                throw new TrayException("控制接口地址不属于本机，已拒绝连接。");
            return new ControllerSettings(new UriBuilder("http", host, uri.Port).Uri, secret);
        }

        internal static string Scalar(string raw)
        {
            string text = raw.Trim();
            if (text.Length == 0 || text.StartsWith("#")) return "";
            if (text[0] == '\'' || text[0] == '"')
            {
                char quote = text[0];
                for (int i = 1; i < text.Length; i++)
                {
                    if (quote == '"' && text[i] == '\\') { i++; continue; }
                    if (text[i] != quote) continue;
                    if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
                    string rest = text.Substring(i + 1).Trim();
                    if (rest.Length != 0 && !rest.StartsWith("#")) break;
                    if (quote == '\'') return text.Substring(1, i - 1).Replace("''", "'");
                    try { return new JavaScriptSerializer().Deserialize<string>(text.Substring(0, i + 1)); }
                    catch (ArgumentException) { break; }
                }
                throw new TrayException("控制接口地址或密码的引号格式无法读取，请使用单行普通字符串。");
            }
            text = Regex.Replace(text, "\\s+#.*$", "").Trim();
            if (text == "~" || text.Equals("null", StringComparison.OrdinalIgnoreCase)) return "";
            if ("|>*&!{[".IndexOf(text[0]) >= 0)
                throw new TrayException("控制接口地址和密码请使用单行字符串，不支持别名或多行写法。");
            return text;
        }
    }

    internal enum CoreStatus { Stopped, Ready, Unavailable, Authentication, Configuration }

    internal sealed class TrayState
    {
        internal CoreStatus Status;
        internal bool ProcessExists;
        internal string Detail = "正在检查";
        internal string Version = "";
        internal string Mode = "";
        internal bool Tun;
    }

    internal static class CoreApi
    {
        internal static async Task<TrayState> ReadAsync(string root)
        {
            bool running = Native.OwnedProcesses(Project.Core(root)).Count != 0;
            var state = new TrayState { ProcessExists = running };
            if (!running)
            {
                state.Status = CoreStatus.Stopped;
                state.Detail = File.Exists(Project.Core(root)) ? "已停止" : "未找到内核文件";
                return state;
            }
            try
            {
                var settings = ControllerSettings.Load(Project.Config(root));
                var version = await SendAsync(root, settings, "version", HttpMethod.Get, null);
                var config = await SendAsync(root, settings, "configs", HttpMethod.Get, null);
                object value;
                if (!version.TryGetValue("version", out value) || !(value is string) || ((string)value).Length == 0)
                    throw new TrayException("控制接口返回了无效的版本信息。");
                state.Version = (string)value;
                state.Mode = config.TryGetValue("mode", out value) ? Convert.ToString(value).ToLowerInvariant() : "";
                if (!ValidMode(state.Mode)) throw new TrayException("控制接口返回了无效的代理模式。");
                var tun = config.TryGetValue("tun", out value) ? value as Dictionary<string, object> : null;
                state.Tun = tun != null && tun.TryGetValue("enable", out value) && value is bool && (bool)value;
                state.Status = CoreStatus.Ready;
                state.Detail = "运行中";
            }
            catch (TrayException e)
            {
                state.Status = e.Authentication ? CoreStatus.Authentication : CoreStatus.Unavailable;
                state.Detail = e.Message;
            }
            catch (IOException) { state.Status = CoreStatus.Configuration; state.Detail = "无法读取 config.yaml。"; }
            catch (UnauthorizedAccessException) { state.Status = CoreStatus.Configuration; state.Detail = "没有读取配置文件的权限。"; }
            return state;
        }

        internal static bool ValidMode(string mode) { return mode == "rule" || mode == "global" || mode == "direct"; }

        internal static async Task ChangeModeAsync(string root, string mode)
        {
            if (!ValidMode(mode)) throw new TrayException("无效的代理模式。");
            var settings = ControllerSettings.Load(Project.Config(root));
            await SendAsync(root, settings, "configs", new HttpMethod("PATCH"),
                new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "mode", mode } }));
            var state = await ReadAsync(root);
            if (state.Status != CoreStatus.Ready || state.Mode != mode)
                throw new TrayException("模式切换未能确认生效，请查看面板或日志。");
        }

        private static async Task<Dictionary<string, object>> SendAsync(string root, ControllerSettings settings,
            string path, HttpMethod method, string payload)
        {
            // Do not send credentials or commands to another project's controller.
            var owned = Native.OwnedProcesses(Project.Core(root));
            var listeners = Native.Listeners(settings.Address);
            if (listeners.Count == 0 || listeners.Any(pid => !owned.Contains(pid)))
                throw new TrayException("控制端口未就绪或属于其他程序，已拒绝连接。");
            using (var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
            using (var client = new HttpClient(handler))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            using (var request = new HttpRequestMessage(method, new Uri(settings.Address, path)))
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                if (settings.Secret.Length != 0)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Secret);
                if (payload != null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                            throw new TrayException("控制接口认证失败，请检查配置中的 secret。", true);
                        if (!response.IsSuccessStatusCode)
                            throw new TrayException("控制接口请求失败，HTTP " + (int)response.StatusCode + "。");
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var buffer = new MemoryStream())
                        {
                            byte[] chunk = new byte[4096];
                            int count;
                            while ((count = await stream.ReadAsync(chunk, 0, chunk.Length, timeout.Token)) != 0)
                            {
                                if (buffer.Length + count > 1024 * 1024) throw new TrayException("控制接口响应过大。");
                                buffer.Write(chunk, 0, count);
                            }
                            if (buffer.Length == 0) return new Dictionary<string, object>();
                            try
                            {
                                var json = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(buffer.ToArray()))
                                    as Dictionary<string, object>;
                                if (json == null) throw new ArgumentException();
                                return json;
                            }
                            catch (ArgumentException) { throw new TrayException("控制接口返回了无效的数据。"); }
                            catch (InvalidOperationException) { throw new TrayException("控制接口返回了无效的数据。"); }
                        }
                    }
                }
                catch (OperationCanceledException) { throw new TrayException("控制接口响应超时。"); }
                catch (HttpRequestException) { throw new TrayException("内核进程存在，但控制接口暂时无法访问。"); }
            }
        }
    }

    internal static class CoreManager
    {
        internal static void Execute(string root, string action)
        {
            if (action != "start" && action != "stop" && action != "restart") throw new TrayException("未知操作。");
            using (var gate = new Mutex(false, @"Local\SurfingWin.Core." + Project.Identity(root)))
            {
                bool acquired;
                try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new TrayException("另一个启停操作正在执行，请稍后重试。");
                try
                {
                    if (action != "start")
                        foreach (int pid in Native.OwnedProcesses(Project.Core(root)))
                            if (!Native.StopOwned(pid, Project.Core(root)))
                                Project.Log(root, "内核未响应正常退出信号，已结束本项目内核进程。");
                    if (action != "stop") Start(root);
                }
                finally { gate.ReleaseMutex(); }
            }
        }

        private static void Start(string root)
        {
            string executable = Project.Core(root);
            if (!File.Exists(executable)) throw new TrayException("未找到 bin\\clash-amd64.exe。");
            var settings = ControllerSettings.Load(Project.Config(root));
            if (Native.OwnedProcesses(executable).Count == 0)
            {
                if (Native.Listeners(settings.Address).Count != 0)
                    throw new TrayException("控制接口端口已被其他程序占用，请修改配置或停止冲突程序。");
                Directory.CreateDirectory(Project.Logs(root));
                Validate(root);
                string log = Path.Combine(Project.Logs(root), "clash_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log");
                Native.StartHidden(executable, "-d " + Project.Quote(Path.Combine(root, "config")), root, log);
            }
            var watch = Stopwatch.StartNew();
            string lastDetail = "控制接口尚未就绪。";
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                var state = CoreApi.ReadAsync(root).GetAwaiter().GetResult();
                lastDetail = state.Detail;
                if (state.Status == CoreStatus.Ready) return;
                if (state.Status == CoreStatus.Authentication) throw new TrayException(state.Detail, true);
                if (!state.ProcessExists) throw new TrayException("内核启动后已退出，请打开日志目录查看原因。");
                Thread.Sleep(350);
            }
            throw new TrayException("内核进程已启动，" + lastDetail + " 请查看日志。");
        }

        private static void Validate(string root)
        {
            var info = new ProcessStartInfo(Project.Core(root), "-t -d " + Project.Quote(Path.Combine(root, "config")))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    process.Kill(); process.WaitForExit();
                    throw new TrayException("配置检查超时，未启动代理。");
                }
                Task.WaitAll(output, error);
                File.WriteAllText(Path.Combine(Project.Logs(root), "tray-validation.log"), output.Result + error.Result,
                    new UTF8Encoding(false));
                if (process.ExitCode != 0) throw new TrayException("配置检查失败，未启动代理。请查看 tray-validation.log。");
            }
        }
    }

    internal static class Native
    {
        private const uint Query = 0x1000, Synchronize = 0x100000, Terminate = 1;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll")] private static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int type, int reserved);

        private static string ImagePath(IntPtr handle)
        {
            int length = 32768;
            var result = new StringBuilder(length);
            return QueryFullProcessImageName(handle, 0, result, ref length) ? result.ToString() : null;
        }
        internal static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            Func<string, string> normalize = p => Path.GetFullPath(p.Replace(@"\\?\UNC\", @"\\").Replace(@"\\?\", ""))
                .TrimEnd('\\');
            return String.Equals(normalize(a), normalize(b), StringComparison.OrdinalIgnoreCase);
        }
        internal static List<int> OwnedProcesses(string executable)
        {
            var result = new List<int>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
                using (process)
                {
                    IntPtr handle = OpenProcess(Query, false, process.Id);
                    if (handle == IntPtr.Zero) continue;
                    try { if (SamePath(ImagePath(handle), executable)) result.Add(process.Id); }
                    finally { CloseHandle(handle); }
                }
            return result;
        }

        internal static List<int> Listeners(Uri address)
        {
            var result = new List<int>();
            var target = IPAddress.Parse(address.Host.Trim('[', ']'));
            foreach (int family in new[] { 2, 23 })
            {
                int size = 0;
                uint code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
                if (code != 0 && code != 122) throw new TrayException("无法读取本机端口归属。");
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (size < 4 || size > 16 * 1024 * 1024) throw new TrayException("本机端口表大小异常。");
                    IntPtr table = Marshal.AllocHGlobal(size);
                    try
                    {
                        int allocated = size;
                        code = GetExtendedTcpTable(table, ref size, false, family, 3, 0);
                        if (code == 122) continue;
                        if (code != 0) throw new TrayException("无法读取本机端口归属。");
                        int rows = Marshal.ReadInt32(table), width = family == 2 ? 24 : 56;
                        if (rows < 0 || rows > (allocated - 4) / width) throw new TrayException("本机端口表格式异常。");
                        for (int i = 0; i < rows; i++)
                        {
                            IntPtr row = IntPtr.Add(table, 4 + i * width);
                            int portOffset = family == 2 ? 8 : 20;
                            int port = Marshal.ReadByte(row, portOffset) * 256 + Marshal.ReadByte(row, portOffset + 1);
                            if (port != address.Port) continue;
                            byte[] bytes = new byte[family == 2 ? 4 : 16];
                            Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                            var ip = new IPAddress(bytes);
                            bool match = ip.Equals(target) || (family == 2 && ip.Equals(IPAddress.Any) &&
                                target.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) || ip.Equals(IPAddress.IPv6Any);
                            if (match) result.Add(Marshal.ReadInt32(row, family == 2 ? 20 : 52));
                        }
                        break;
                    }
                    finally { Marshal.FreeHGlobal(table); }
                }
                if (code != 0) throw new TrayException("端口状态变化过快，请稍后重试。");
            }
            return result.Distinct().ToList();
        }

        internal static bool StopOwned(int pid, string executable)
        {
            // Keep this process handle open through termination, preventing PID reuse.
            IntPtr handle = OpenProcess(Query | Synchronize | Terminate, false, pid);
            if (handle == IntPtr.Zero)
            {
                if (!OwnedProcesses(executable).Contains(pid)) return true;
                throw new TrayException("无法停止内核，请允许管理员权限后重试。");
            }
            try
            {
                if (!SamePath(ImagePath(handle), executable)) return true;
                if (WaitForSingleObject(handle, 0) == 0) return true;
                bool sent = false;
                if (AttachConsole((uint)pid))
                {
                    try { sent = GenerateConsoleCtrlEvent(1, (uint)pid); } // Targeted CTRL_BREAK, never a console-wide broadcast.
                    finally { FreeConsole(); }
                }
                if (sent && WaitForSingleObject(handle, 5000) == 0) return true;
                if (!TerminateProcess(handle, 1) || WaitForSingleObject(handle, 5000) != 0)
                    throw new TrayException("内核停止失败，请查看日志或系统权限。");
                return false;
            }
            finally { CloseHandle(handle); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
        { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
        {
            internal int Size; internal string Reserved, Desktop, Title;
            internal int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
            internal short Show, ReservedCount; internal IntPtr ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo
        { internal IntPtr Process, Thread; internal int Pid, Tid; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, ref SecurityAttributes security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity,
            IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string cwd, ref StartupInfo startup,
            out ProcessInfo process);

        internal static void StartHidden(string executable, string arguments, string cwd, string log)
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = true };
            IntPtr output = CreateFile(log, 0x40000000, 3, ref security, 1, 0x80, IntPtr.Zero);
            if (output == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr input = IntPtr.Zero;
            try
            {
                input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0x80, IntPtr.Zero);
                if (input == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Flags = 0x101,
                    Show = 0, Input = input, Output = output, Error = output };
                ProcessInfo process;
                if (!CreateProcess(executable, new StringBuilder(Project.Quote(executable) + " " + arguments),
                    IntPtr.Zero, IntPtr.Zero, true, 0x210, IntPtr.Zero, cwd, ref startup, out process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                CloseHandle(process.Thread); CloseHandle(process.Process);
            }
            finally
            {
                CloseHandle(output);
                if (input != IntPtr.Zero && input != new IntPtr(-1)) CloseHandle(input);
            }
        }
    }
}
