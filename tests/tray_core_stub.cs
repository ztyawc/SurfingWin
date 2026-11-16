// Harmless loopback-only core used by the native Windows integration tests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class TrayCoreStub
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static Dictionary<string, object> Settings(string directory)
    { return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(directory, "stub.json"))); }
    private static bool Flag(Dictionary<string, object> settings, string key)
    { object value; return settings.TryGetValue(key, out value) && value is bool && (bool)value; }

    private static int Main(string[] args)
    {
        string executable = Assembly.GetExecutingAssembly().Location;
        if (Path.GetFileName(executable).Equals("SurfingTray.exe", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable), "launcher.json"), Json.Serialize(
                new { arguments = args, cwd = Environment.CurrentDirectory }));
            return 0;
        }
        string directory = args[Array.IndexOf(args, "-d") + 1];
        var settings = Settings(directory);
        if (args.Contains("-t"))
        {
            if (Flag(settings, "validation-fail")) { Console.WriteLine("fixture validation error"); return 2; }
            Console.WriteLine("configuration file test is successful"); return 0;
        }
        var listener = new TcpListener(IPAddress.Parse(Convert.ToString(settings["bind"])), Convert.ToInt32(settings["port"]));
        bool stopping = false;
        string mode = "rule";
        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            File.AppendAllText(Path.Combine(directory, "graceful.marker"), e.SpecialKey.ToString() + "\n");
            stopping = true; listener.Stop();
        };
        listener.Start();
        File.WriteAllText(Path.Combine(directory, "started.json"), Json.Serialize(args));
        while (!stopping)
        {
            try
            {
                using (var client = listener.AcceptTcpClient())
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                {
                    client.ReceiveTimeout = 5000;
                    string request = reader.ReadLine();
                    if (request == null) continue;
                    string auth = "", line;
                    int length = 0;
                    while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                    {
                        int separator = line.IndexOf(':');
                        if (separator < 0) continue;
                        string key = line.Substring(0, separator).ToLowerInvariant();
                        string value = line.Substring(separator + 1).Trim();
                        if (key == "authorization") auth = value;
                        if (key == "content-length") length = Int32.Parse(value);
                    }
                    char[] body = new char[length];
                    int offset = 0;
                    while (offset < length) { int count = reader.Read(body, offset, length - offset); if (count == 0) break; offset += count; }
                    File.AppendAllText(Path.Combine(directory, "requests.log"), request + "\n");
                    settings = Settings(directory);
                    string expected = Convert.ToString(settings["secret"]);
                    string fault = settings.ContainsKey("fault") ? Convert.ToString(settings["fault"]) : "";
                    int status = 200;
                    string response;
                    if (auth != (expected.Length == 0 ? "" : "Bearer " + expected)) { status = 401; response = "{}"; }
                    else if (fault == "invalid-json") response = "<html>invalid</html>";
                    else if (fault == "timeout") { Thread.Sleep(3000); response = "{}"; }
                    else if (request.StartsWith("GET /version ")) response = "{\"meta\":true,\"version\":\"fixture-1.0\"}";
                    else if (request.StartsWith("GET /configs ")) response = Json.Serialize(new { mode, tun = new { enable = Flag(settings, "tun") } });
                    else if (request.StartsWith("PATCH /configs "))
                    {
                        if (!Flag(settings, "ignore-mode")) mode = Convert.ToString(Json.Deserialize<Dictionary<string, object>>(new String(body))["mode"]);
                        status = 204; response = "";
                    }
                    else { status = 404; response = "{}"; }
                    byte[] bytes = Encoding.UTF8.GetBytes(response);
                    byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Fixture\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(headers, 0, headers.Length); stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (IOException) { }
            catch (SocketException) { if (!stopping) throw; }
        }
        return 0;
    }
}
