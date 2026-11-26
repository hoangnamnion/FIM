using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FimCinema
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            int port = 8765;
            bool noBrowser = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--no-browser", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[i], "-NoBrowser", StringComparison.OrdinalIgnoreCase))
                {
                    noBrowser = true;
                }
                else if ((string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(args[i], "-Port", StringComparison.OrdinalIgnoreCase)) &&
                         i + 1 < args.Length)
                {
                    int parsed;
                    if (int.TryParse(args[++i], out parsed) && parsed > 0 && parsed < 65536)
                    {
                        port = parsed;
                    }
                }
            }

            string rootDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string webDir = Path.Combine(rootDir, "web");
            string dataDir = Path.Combine(rootDir, ".fim-data");
            string historyFile = Path.Combine(dataDir, "watch-history.json");
            string configFile = Path.Combine(dataDir, "config.json");

            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            FimServer server = null;
            int chosenPort = port;
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var candidate = new FimServer(rootDir, webDir, historyFile, configFile, chosenPort);
                    candidate.Start();
                    server = candidate;
                    break;
                }
                catch
                {
                    chosenPort++;
                }
            }

            if (server == null)
            {
                MessageBox.Show("Khong the mo cong mang bat dau tu " + port, "FIM", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            string url = "http://localhost:" + chosenPort + "/";
            var cts = new CancellationTokenSource();
            var serverTask = Task.Run(() => server.RunLoop(cts.Token));

            if (noBrowser)
            {
                serverTask.Wait();
                return 0;
            }

            Process appProcess = LaunchAppWindow(url, dataDir);
            if (appProcess != null && !appProcess.WaitForExit(3000))
            {
                appProcess.WaitForExit();
                cts.Cancel();
                server.Stop();
                return 0;
            }

            if (appProcess == null)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }

            Application.EnableVisualStyles();
            var tray = new NotifyIcon
            {
                Text = "FIM - Rap Phim Ca Nhan (" + url + ")",
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath),
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Mo FIM", null, (s, e) => { LaunchAppWindow(url, dataDir); });
            menu.Items.Add("Mo thu muc phim", null, (s, e) => { try { Process.Start("explorer.exe", rootDir); } catch { } });
            menu.Items.Add("Thoat", null, (s, e) => { tray.Visible = false; Application.Exit(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => { LaunchAppWindow(url, dataDir); };

            var watchdog = new System.Windows.Forms.Timer { Interval = 2000 };
            watchdog.Tick += (s, e) =>
            {
                if (server.HasReceivedPing && (DateTime.UtcNow - server.LastPingUtc).TotalSeconds > 10)
                {
                    watchdog.Stop();
                    tray.Visible = false;
                    Application.Exit();
                }
            };
            watchdog.Start();

            Application.Run();
            cts.Cancel();
            server.Stop();
            return 0;
        }

        private static Process LaunchAppWindow(string url, string dataDir)
        {
            string[] candidates = new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
            };

            string profileDir = Path.Combine(dataDir, "app-profile");
            foreach (string exe in candidates)
            {
                if (!File.Exists(exe)) continue;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = string.Format("--app=\"{0}\" --user-data-dir=\"{1}\" --window-size=1280,820 --no-first-run --no-default-browser-check", url, profileDir),
                        UseShellExecute = false
                    };
                    return Process.Start(psi);
                }
                catch { }
            }
            return null;
        }
    }

    internal class FimServer
    {
        private readonly string _rootDir;
        private readonly string _webDir;
        private readonly string _historyFile;
        private readonly string _configFile;
        private readonly HttpListener _listener;
        private readonly object _fileLock = new object();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer() { MaxJsonLength = 52428800 };
        private readonly byte[] _embeddedIndex;
        public volatile bool HasReceivedPing = false;
        private long _lastPingTicks = DateTime.UtcNow.Ticks;
        public DateTime LastPingUtc { get { return new DateTime(Interlocked.Read(ref _lastPingTicks), DateTimeKind.Utc); } }

        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".ts", ".flv", ".wmv"
        };

        private static readonly HashSet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".vtt", ".srt"
        };

        private static readonly Dictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" },
            { ".js", "application/javascript; charset=utf-8" },
            { ".json", "application/json; charset=utf-8" },
            { ".mp4", "video/mp4" },
            { ".m4v", "video/mp4" },
            { ".webm", "video/webm" },
            { ".mov", "video/quicktime" },
            { ".mkv", "video/x-matroska" },
            { ".avi", "video/x-msvideo" },
            { ".ts", "video/mp2t" },
            { ".flv", "video/x-flv" },
            { ".wmv", "video/x-ms-wmv" }
        };

        public FimServer(string rootDir, string webDir, string historyFile, string configFile, int port)
        {
            _rootDir = Path.GetFullPath(rootDir);
            _webDir = Path.GetFullPath(webDir);
            _historyFile = Path.GetFullPath(historyFile);
            _configFile = Path.GetFullPath(configFile);
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:" + port + "/");
            _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            _embeddedIndex = LoadEmbeddedResource("index.html");
        }

        private static byte[] LoadEmbeddedResource(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream(name))
            {
                if (stream == null) return null;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        public void Start()
        {
            _listener.Start();
        }

        public void Stop()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        public void RunLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    Task.Run(() => HandleRequest(ctx));
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                string path = req.Url.AbsolutePath;
                res.Headers["Access-Control-Allow-Origin"] = "*";
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type";

                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 204;
                    res.Close();
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    string diskIndex = Path.Combine(_webDir, "index.html");
                    if (File.Exists(diskIndex))
                    {
                        ServeFile(res, diskIndex, "text/html; charset=utf-8");
                    }
                    else if (_embeddedIndex != null)
                    {
                        res.ContentType = "text/html; charset=utf-8";
                        res.ContentLength64 = _embeddedIndex.Length;
                        res.OutputStream.Write(_embeddedIndex, 0, _embeddedIndex.Length);
                        res.Close();
                    }
                    else
                    {
                        res.StatusCode = 404;
                        res.Close();
                    }
                    return;
                }

                if (path == "/api/health" || path == "/api/ping")
                {
                    HasReceivedPing = true;
                    Interlocked.Exchange(ref _lastPingTicks, DateTime.UtcNow.Ticks);
                    SendJson(res, new Dictionary<string, object> { { "ok", true } });
                    return;
                }

                if (path == "/api/videos" && req.HttpMethod == "GET")
                {
                    HandleGetVideos(res);
                    return;
                }

                if (path == "/api/video" && req.HttpMethod == "GET")
                {
                    HandleStreamVideo(req, res, req.QueryString["path"]);
                    return;
                }

                if (path == "/api/subtitle" && req.HttpMethod == "GET")
                {
                    HandleSubtitle(res, req.QueryString["path"]);
                    return;
                }

                if (path == "/api/progress" && req.HttpMethod == "POST")
                {
                    HandleSaveProgress(req, res);
                    return;
                }

                if (path == "/api/pick-folder" && req.HttpMethod == "POST")
                {
                    HandlePickFolder(res);
                    return;
                }

                if (path == "/api/remove-folder" && req.HttpMethod == "POST")
                {
                    HandleRemoveFolder(req, res);
                    return;
                }

                if (path == "/api/cleanup-history" && req.HttpMethod == "POST")
                {
                    HandleCleanupHistory(res);
                    return;
                }

                if (path == "/api/open-external" && req.HttpMethod == "POST")
                {
                    HandleOpenExternal(req, res);
                    return;
                }

                res.StatusCode = 404;
                SendJson(res, new Dictionary<string, object> { { "error", "Not found" } });
            }
            catch (Exception ex)
            {
                try
                {
                    res.StatusCode = 500;
                    SendJson(res, new Dictionary<string, object> { { "error", ex.Message } });
                }
                catch { }
            }
        }

        private void ServeFile(HttpListenerResponse res, string filePath, string contentType)
        {
            if (!File.Exists(filePath))
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }
            byte[] data = File.ReadAllBytes(filePath);
            res.ContentType = contentType;
            res.ContentLength64 = data.Length;
            res.OutputStream.Write(data, 0, data.Length);
            res.Close();
        }

        private void SendJson(HttpListenerResponse res, object obj)
        {
            string json = _json.Serialize(obj);
            byte[] data = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = data.Length;
            res.OutputStream.Write(data, 0, data.Length);
            res.Close();
        }

        private string ReadBody(HttpListenerRequest req)
        {
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private List<string> LoadExtraFolders()
        {
            lock (_fileLock)
            {
                if (!File.Exists(_configFile)) return new List<string>();
                try
                {
                    string raw = File.ReadAllText(_configFile, Encoding.UTF8);
                    var cfg = _json.Deserialize<Dictionary<string, object>>(raw);
                    if (cfg != null && cfg.ContainsKey("extraFolders"))
                    {
                        var arr = cfg["extraFolders"] as System.Collections.ArrayList;
                        var res = new List<string>();
                        if (arr != null)
                        {
                            foreach (var item in arr)
                            {
                                string s = Convert.ToString(item);
                                if (!string.IsNullOrWhiteSpace(s) && Directory.Exists(s))
                                    res.Add(Path.GetFullPath(s));
                            }
                        }
                        return res;
                    }
                }
                catch { }
                return new List<string>();
            }
        }

        private void SaveExtraFolders(List<string> folders)
        {
            lock (_fileLock)
            {
                string dir = Path.GetDirectoryName(_configFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var cfg = new Dictionary<string, object> { { "extraFolders", folders } };
                File.WriteAllText(_configFile, _json.Serialize(cfg), Encoding.UTF8);
            }
        }

        private Dictionary<string, Dictionary<string, object>> LoadHistory()
        {
            lock (_fileLock)
            {
                if (!File.Exists(_historyFile)) return new Dictionary<string, Dictionary<string, object>>();
                try
                {
                    string raw = File.ReadAllText(_historyFile, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<string, Dictionary<string, object>>();
                    return _json.Deserialize<Dictionary<string, Dictionary<string, object>>>(raw)
                        ?? new Dictionary<string, Dictionary<string, object>>();
                }
                catch
                {
                    return new Dictionary<string, Dictionary<string, object>>();
                }
            }
        }

        private void SaveHistory(Dictionary<string, Dictionary<string, object>> history)
        {
            lock (_fileLock)
            {
                string dir = Path.GetDirectoryName(_historyFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string json = _json.Serialize(history);
                string tmp = _historyFile + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                if (File.Exists(_historyFile)) File.Delete(_historyFile);
                File.Move(tmp, _historyFile);
            }
        }

        private List<string> GetAllRoots()
        {
            var roots = new List<string> { _rootDir };
            foreach (string extra in LoadExtraFolders())
            {
                if (!roots.Contains(extra)) roots.Add(extra);
            }
            return roots;
        }

        private string ResolveMediaPath(string encodedKey)
        {
            if (string.IsNullOrWhiteSpace(encodedKey)) return null;
            var roots = GetAllRoots();

            // Key format: "0/sub/movie.mp4" (index into roots) or legacy "sub/movie.mp4" (root 0)
            int rootIdx = 0;
            string rel = encodedKey;
            int slash = encodedKey.IndexOf('/');
            if (slash > 0)
            {
                int parsed;
                if (int.TryParse(encodedKey.Substring(0, slash), out parsed) && parsed >= 0 && parsed < roots.Count)
                {
                    rootIdx = parsed;
                    rel = encodedKey.Substring(slash + 1);
                }
            }

            string baseRoot = roots[rootIdx];
            string normalized = rel.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(baseRoot, normalized));

            if (!fullPath.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullPath, baseRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return fullPath;
        }

        private void HandleGetVideos(HttpListenerResponse res)
        {
            var history = LoadHistory();
            var list = new List<Dictionary<string, object>>();
            var roots = GetAllRoots();

            for (int i = 0; i < roots.Count; i++)
            {
                if (Directory.Exists(roots[i]))
                {
                    ScanDirectory(roots[i], roots[i], i, list, history);
                }
            }

            var result = new Dictionary<string, object>
            {
                { "root", _rootDir },
                { "folders", roots },
                { "videos", list },
                { "scannedAt", DateTime.UtcNow.ToString("o") }
            };
            SendJson(res, result);
        }

        private void ScanDirectory(string baseRoot, string dir, int rootIdx, List<Dictionary<string, object>> list, Dictionary<string, Dictionary<string, object>> history)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { return; }

            var subsInDir = new List<string>();
            foreach (string f in files)
            {
                if (SubtitleExtensions.Contains(Path.GetExtension(f)))
                    subsInDir.Add(f);
            }

            foreach (string file in files)
            {
                string ext = Path.GetExtension(file);
                if (!VideoExtensions.Contains(ext)) continue;

                var fi = new FileInfo(file);
                string rel = file.Substring(baseRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                string id = rootIdx == 0 ? rel : (rootIdx + "/" + rel);

                string folder = Path.GetDirectoryName(rel);
                if (folder == null) folder = "";
                folder = folder.Replace('\\', '/');
                if (rootIdx > 0)
                {
                    string rootName = Path.GetFileName(baseRoot);
                    folder = string.IsNullOrEmpty(folder) ? rootName : (rootName + "/" + folder);
                }

                Dictionary<string, object> state = null;
                if (history.ContainsKey(id))
                {
                    state = history[id];
                }

                string baseName = Path.GetFileNameWithoutExtension(file);
                var matchedSubs = new List<Dictionary<string, object>>();
                foreach (string subFile in subsInDir)
                {
                    string subBase = Path.GetFileNameWithoutExtension(subFile);
                    if (subBase.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        string subRel = subFile.Substring(baseRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                        string subId = rootIdx == 0 ? subRel : (rootIdx + "/" + subRel);
                        string suffix = subBase.Substring(baseName.Length).Trim('.', '_', '-', ' ');
                        string label = string.IsNullOrEmpty(suffix) ? "Phụ đề" : suffix.ToUpperInvariant();
                        matchedSubs.Add(new Dictionary<string, object>
                        {
                            { "label", label },
                            { "path", subId }
                        });
                    }
                }

                var item = new Dictionary<string, object>
                {
                    { "id", id },
                    { "name", baseName },
                    { "fileName", fi.Name },
                    { "ext", ext.ToLowerInvariant() },
                    { "folder", folder },
                    { "size", fi.Length },
                    { "modifiedAt", fi.LastWriteTimeUtc.ToString("o") },
                    { "subtitles", matchedSubs },
                    { "watchState", state }
                };
                list.Add(item);
            }

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); } catch { return; }

            foreach (string sub in subDirs)
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith(".") ||
                    string.Equals(name, "web", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "src", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                ScanDirectory(baseRoot, sub, rootIdx, list, history);
            }
        }

        private void HandleSubtitle(HttpListenerResponse res, string relPath)
        {
            string fullPath = ResolveMediaPath(relPath);
            if (fullPath == null || !File.Exists(fullPath))
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }

            string raw = File.ReadAllText(fullPath, Encoding.UTF8);
            string ext = Path.GetExtension(fullPath);
            string vtt = raw;
            if (string.Equals(ext, ".srt", StringComparison.OrdinalIgnoreCase))
            {
                vtt = ConvertSrtToVtt(raw);
            }

            byte[] data = Encoding.UTF8.GetBytes(vtt);
            res.ContentType = "text/vtt; charset=utf-8";
            res.ContentLength64 = data.Length;
            res.OutputStream.Write(data, 0, data.Length);
            res.Close();
        }

        private static string ConvertSrtToVtt(string srt)
        {
            if (string.IsNullOrEmpty(srt)) return "WEBVTT\n\n";
            srt = srt.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace("\r", "\n");
            string converted = Regex.Replace(srt, @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2");
            return "WEBVTT\n\n" + converted;
        }

        private void HandleSaveProgress(HttpListenerRequest req, HttpListenerResponse res)
        {
            var payload = _json.Deserialize<Dictionary<string, object>>(ReadBody(req));
            if (payload == null || !payload.ContainsKey("id"))
            {
                res.StatusCode = 400;
                SendJson(res, new Dictionary<string, object> { { "error", "Missing id" } });
                return;
            }

            string id = Convert.ToString(payload["id"]);
            lock (_fileLock)
            {
                var history = LoadHistory();
                Dictionary<string, object> entry;
                if (!history.TryGetValue(id, out entry) || entry == null)
                {
                    entry = new Dictionary<string, object>();
                }

                if (payload.ContainsKey("currentTime")) entry["currentTime"] = Convert.ToDouble(payload["currentTime"]);
                if (payload.ContainsKey("duration")) entry["duration"] = Convert.ToDouble(payload["duration"]);
                if (payload.ContainsKey("watched")) entry["watched"] = Convert.ToBoolean(payload["watched"]);
                entry["lastWatchedAt"] = DateTime.UtcNow.ToString("o");

                history[id] = entry;
                SaveHistory(history);
            }

            SendJson(res, new Dictionary<string, object> { { "ok", true } });
        }

        private void HandlePickFolder(HttpListenerResponse res)
        {
            string selected = null;
            var t = new Thread(() =>
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Chon thu muc chua phim de them vao FIM";
                    dlg.ShowNewFolderButton = false;
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        selected = dlg.SelectedPath;
                    }
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();

            if (!string.IsNullOrWhiteSpace(selected) && Directory.Exists(selected))
            {
                string full = Path.GetFullPath(selected);
                var extras = LoadExtraFolders();
                if (!string.Equals(full, _rootDir, StringComparison.OrdinalIgnoreCase) && !extras.Contains(full))
                {
                    extras.Add(full);
                    SaveExtraFolders(extras);
                }
                SendJson(res, new Dictionary<string, object> { { "ok", true }, { "added", full } });
            }
            else
            {
                SendJson(res, new Dictionary<string, object> { { "ok", false } });
            }
        }

        private void HandleRemoveFolder(HttpListenerRequest req, HttpListenerResponse res)
        {
            var payload = _json.Deserialize<Dictionary<string, object>>(ReadBody(req));
            string target = payload != null && payload.ContainsKey("folder") ? Convert.ToString(payload["folder"]) : null;
            if (!string.IsNullOrWhiteSpace(target))
            {
                var extras = LoadExtraFolders();
                extras.RemoveAll(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase));
                SaveExtraFolders(extras);
            }
            SendJson(res, new Dictionary<string, object> { { "ok", true } });
        }

        private void HandleCleanupHistory(HttpListenerResponse res)
        {
            int removed = 0;
            lock (_fileLock)
            {
                var history = LoadHistory();
                var toRemove = new List<string>();
                foreach (var key in history.Keys)
                {
                    string fullPath = ResolveMediaPath(key);
                    if (fullPath == null || !File.Exists(fullPath))
                    {
                        toRemove.Add(key);
                    }
                }
                foreach (var k in toRemove)
                {
                    history.Remove(k);
                    removed++;
                }
                if (removed > 0) SaveHistory(history);
            }
            SendJson(res, new Dictionary<string, object> { { "ok", true }, { "removed", removed } });
        }

        private void HandleOpenExternal(HttpListenerRequest req, HttpListenerResponse res)
        {
            var payload = _json.Deserialize<Dictionary<string, object>>(ReadBody(req));
            string id = payload != null && payload.ContainsKey("id") ? Convert.ToString(payload["id"]) : null;
            string fullPath = ResolveMediaPath(id);
            if (fullPath != null && File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                SendJson(res, new Dictionary<string, object> { { "ok", true } });
                return;
            }
            res.StatusCode = 404;
            SendJson(res, new Dictionary<string, object> { { "error", "File not found" } });
        }

        private void HandleStreamVideo(HttpListenerRequest req, HttpListenerResponse res, string relPath)
        {
            string fullPath = ResolveMediaPath(relPath);
            if (fullPath == null)
            {
                res.StatusCode = 403;
                res.Close();
                return;
            }

            if (!File.Exists(fullPath))
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }

            var fi = new FileInfo(fullPath);
            long totalLength = fi.Length;
            string ext = fi.Extension;
            string mime = MimeTypes.ContainsKey(ext) ? MimeTypes[ext] : "application/octet-stream";

            res.ContentType = mime;
            res.Headers["Accept-Ranges"] = "bytes";

            long start = 0;
            long end = totalLength - 1;
            bool isRange = false;

            string rangeHeader = req.Headers["Range"];
            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                isRange = true;
                string[] parts = rangeHeader.Substring(6).Split('-');
                if (!string.IsNullOrEmpty(parts[0])) start = long.Parse(parts[0]);
                if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1])) end = long.Parse(parts[1]);
                if (start < 0) start = 0;
                if (end >= totalLength) end = totalLength - 1;
                if (start > end)
                {
                    res.StatusCode = 416;
                    res.Headers["Content-Range"] = "bytes */" + totalLength;
                    res.Close();
                    return;
                }
            }

            long contentLength = end - start + 1;
            res.StatusCode = isRange ? 206 : 200;
            if (isRange)
            {
                res.Headers["Content-Range"] = string.Format("bytes {0}-{1}/{2}", start, end, totalLength);
            }
            res.ContentLength64 = contentLength;

            byte[] buffer = new byte[65536];
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(start, SeekOrigin.Begin);
                long remaining = contentLength;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = fs.Read(buffer, 0, toRead);
                    if (read <= 0) break;
                    try
                    {
                        res.OutputStream.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            try { res.OutputStream.Close(); } catch { }
            try { res.Close(); } catch { }
        }
    }
}
