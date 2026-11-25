using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SmartMarketplace
{
    public class SimpleHttpRequest
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public string Body { get; set; }
        public bool PayloadTooLarge { get; set; }

        public SimpleHttpRequest()
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Body = "";
        }
    }

    class Program
    {
        private const int MaxDefaultBodyBytes = 2 * 1024 * 1024;  // 2 MB
        private const int MaxUploadBodyBytes = 8 * 1024 * 1024;   // 8 MB (base64 of <= 5 MB image)
        private const int MaxBackupBodyBytes = 10 * 1024 * 1024;  // 10 MB

        private static string _rootDir;
        private static string _frontendDir;
        private static int _port = 8765;
        private static Timer _syncTimer;
        private static Timer _updateTimer;
        private static DateTime _lastAutoSync = DateTime.MinValue;
        private static volatile bool _restartingForUpdate = false;
        private static bool _isSupervised = false;
        private static TcpListener _listener;
        private static readonly string _serverInstanceId = Security.GenerateSecureToken(8);

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            bool openBrowser = true;

            foreach (var a in args)
            {
                if (a.StartsWith("--port="))
                {
                    int p;
                    if (int.TryParse(a.Substring(7), out p) && p >= 1024 && p <= 65535) _port = p;
                }
                if (a == "--no-browser") openBrowser = false;
                if (a == "--supervised") _isSupervised = true;
            }

            _rootDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!Directory.Exists(Path.Combine(_rootDir, "frontend")))
            {
                var parent = Directory.GetParent(_rootDir.TrimEnd('\\', '/'));
                if (parent != null && Directory.Exists(Path.Combine(parent.FullName, "frontend")))
                    _rootDir = parent.FullName;
            }
            _frontendDir = Path.Combine(_rootDir, "frontend");
            Storage.Initialize(_rootDir);

            // Background stock sync check every 60 seconds
            _syncTimer = new Timer(BackgroundSyncTick, null, 60000, 60000);
            // Hot-update watcher: checks every 1.5s for SmartServer_update.exe
            _updateTimer = new Timer(CheckForHotUpdate, null, 1500, 1500);
            // Pre-warm Trendyol official category tree & auto-resume remote tunnel if user enabled it
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var dbInit = Storage.Load();
                    if (dbInit.RemoteConfig != null && dbInit.RemoteConfig.AutoStartTunnel)
                    {
                        string tUrl, tErr;
                        if (TunnelService.StartTunnel(_port, out tUrl, out tErr))
                            Console.WriteLine("  Her Yerden: " + tUrl + " (PIN Korumalı)");
                    }
                    var tyCfg = dbInit.Marketplaces.Find(m => m.Id == "trendyol");
                    if (tyCfg != null && !tyCfg.SimulationMode && MarketplaceConnectors.HasRequiredCredentials(tyCfg))
                    {
                        string warmErr;
                        TrendyolCategoryEngine.EnsureCategoryTreeLoaded(tyCfg, false, out warmErr);
                    }
                }
                catch { }
            });

            try
            {
                // Bind to IPAddress.Any so mobile devices on the same private Wi-Fi LAN can connect;
                // HandleClient strictly enforces RFC1918 Private-LAN / Loopback remote IP check!
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Sunucu başlatılamadı (Port " + _port + "): " + ex.Message);
                Console.WriteLine("Port meşgul olabilir. Farklı port için: SmartServer.exe --port=8766");
                return;
            }

            string url = "http://localhost:" + _port + "/";
            var lanIps = Security.GetLocalLanIpv4Addresses();
            Console.WriteLine("==================================================");
            Console.WriteLine("  SMART - Pazaryeri Ürün Yükleme ve Stok Otomasyonu");
            Console.WriteLine("  Masaüstü: " + url);
            if (lanIps.Count > 0)
                Console.WriteLine("  Mobil   : http://" + lanIps[0] + ":" + _port + "/ (Aynı Wi-Fi ağı)");
            Console.WriteLine("  Güvenlik: DPAPI Şifreleme + 256-bit Oturum + Yalnızca Yerel Ağ");
            Console.WriteLine("  Güncelleme: Otomatik Canlı Senkronizasyon Aktif");
            Console.WriteLine("  Kapatmak için bu pencereyi kapatabilir veya Ctrl+C basabilirsiniz.");
            Console.WriteLine("==================================================");

            if (openBrowser)
            {
                try { Process.Start(url); } catch { }
            }

            while (!_restartingForUpdate)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch
                {
                    break;
                }
            }

            if (_restartingForUpdate)
            {
                TunnelService.StopTunnel();
                if (!_isSupervised)
                {
                    // Self-swap and relaunch even if user launched SmartServer.exe directly without Smart.bat
                    try
                    {
                        string curExe = Path.Combine(_rootDir, "SmartServer.exe");
                        string updExe = Path.Combine(_rootDir, "SmartServer_update.exe");
                        string cmdArgs = "/c timeout /t 1 /nobreak >nul & move /y \"" + updExe + "\" \"" + curExe + "\" >nul 2>&1 & start \"Smart\" \"" + curExe + "\" --no-browser";
                        Process.Start(new ProcessStartInfo("cmd.exe", cmdArgs) { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
                    }
                    catch { }
                }
                Environment.Exit(42);
            }
        }

        private static void CheckForHotUpdate(object state)
        {
            if (_restartingForUpdate) return;
            try
            {
                string updateExe = Path.Combine(_rootDir, "SmartServer_update.exe");
                if (!File.Exists(updateExe)) return;
                var info = new FileInfo(updateExe);
                if (info.Length < 16384) return;
                // Ensure compiler finished writing
                using (File.Open(updateExe, FileMode.Open, FileAccess.Read, FileShare.Read)) { }

                _restartingForUpdate = true;
                Console.WriteLine("[Smart] Yeni sürüm algılandı, sistem otomatik olarak güncellenip yeniden başlatılıyor...");
                try { if (_listener != null) _listener.Stop(); } catch { }
            }
            catch { }
        }

        private static string ComputeFrontendVersion()
        {
            long sum = 0;
            try
            {
                string[] files = { "index.html", "styles.css", "app.js" };
                foreach (var f in files)
                {
                    string p = Path.Combine(_frontendDir, f);
                    if (File.Exists(p)) sum ^= File.GetLastWriteTimeUtc(p).Ticks;
                }
            }
            catch { }
            return sum.ToString("x");
        }

        private static void BackgroundSyncTick(object state)
        {
            try
            {
                var db = Storage.Load();
                if (!db.SyncConfig.AutoSyncEnabled || db.Products.Count == 0) return;
                int mins = Math.Max(1, db.SyncConfig.IntervalMinutes);
                if ((DateTime.Now - _lastAutoSync).TotalMinutes < mins) return;
                _lastAutoSync = DateTime.Now;
                RunFullStockSync(db, "Otomatik Zamanlayıcı");
            }
            catch { }
        }

        private static void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    // 0. Network Boundary Guard: ONLY allow localhost or same private Wi-Fi LAN (RFC1918) — reject public internet IPs!
                    var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                    if (remoteEp == null || !Security.IsPrivateLanIp(remoteEp.Address))
                    {
                        return;
                    }

                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;
                    using (var stream = client.GetStream())
                    {
                        var req = ParseHttpRequest(stream);
                        if (req == null) return;

                        if (req.PayloadTooLarge)
                        {
                            WriteJson(stream, 413, new { ok = false, error = "İstek boyutu güvenlik sınırını aşıyor." });
                            return;
                        }

                        // 1. DNS Rebinding Guard: strictly validate Host header
                        string host = req.Headers.ContainsKey("Host") ? req.Headers["Host"] : "";
                        if (!Security.IsValidHostHeader(host, _port))
                        {
                            Console.WriteLine("[Smart] Engellenen Host: " + host + " (Aktif Tünel: " + Security.ActiveTunnelHost + ")");
                            WriteJson(stream, 403, new { ok = false, error = "Geçersiz Host başlığı engellendi." });
                            return;
                        }

                        // 2. Cross-Site Origin Guard
                        if (!Security.IsValidOriginHeader(req.Headers, _port, req.Method))
                        {
                            string org = req.Headers.ContainsKey("Origin") ? req.Headers["Origin"] : "";
                            string sfs = req.Headers.ContainsKey("Sec-Fetch-Site") ? req.Headers["Sec-Fetch-Site"] : "";
                            Console.WriteLine("[Smart] Engellenen Origin/Site: Origin=" + org + " Sec-Fetch-Site=" + sfs + " Method=" + req.Method);
                            WriteJson(stream, 403, new { ok = false, error = "Çapraz kaynak (Cross-Origin) isteği güvenlik nedeniyle engellendi." });
                            return;
                        }

                        if (req.Method == "OPTIONS")
                        {
                            WriteResponse(stream, 204, "text/plain", new byte[0]);
                            return;
                        }

                        string rawPath = req.Path ?? "/";
                        int qIdx = rawPath.IndexOf('?');
                        if (qIdx >= 0) rawPath = rawPath.Substring(0, qIdx);

                        // Block explicit traversal sequences immediately before normalization
                        if (rawPath.Contains("..") || rawPath.Contains("\\") || rawPath.Contains("\0"))
                        {
                            WriteJson(stream, 403, new { ok = false, error = "Geçersiz yol isteği engellendi." });
                            return;
                        }

                        string path = rawPath.TrimEnd('/');
                        if (string.IsNullOrEmpty(path)) path = "/";

                        // 3. Remote HTTPS Tunnel PIN Gate (protects remote access from any network with 6-digit PIN + HttpOnly cookie)
                        bool isTunnel = Security.IsTunnelRequest(req.Headers);
                        bool remoteAuthed = !isTunnel || Security.HasValidRemoteCookie(req.Headers);

                        if (path == "/api/auth/pin" && req.Method == "POST")
                        {
                            HandlePinLogin(req, stream, isTunnel);
                            return;
                        }

                        if (!remoteAuthed)
                        {
                            if (path == "/styles.css" || path == "/pin.js" || path == "/logo.png" || path == "/favicon.png" || path == "/manifest.json")
                            {
                                string assetPath;
                                if (Security.TryResolveSafePath(_frontendDir, path.TrimStart('/'), out assetPath) && File.Exists(assetPath))
                                {
                                    ServeFile(assetPath, stream, false);
                                    return;
                                }
                            }
                            if (path.StartsWith("/api/"))
                            {
                                WriteJson(stream, 401, new { ok = false, error = "Uzaktan erişim için PIN doğrulaması gereklidir." });
                                return;
                            }
                            string pinHtmlPath;
                            if (Security.TryResolveSafePath(_frontendDir, "pin.html", out pinHtmlPath) && File.Exists(pinHtmlPath))
                                ServeFile(pinHtmlPath, stream, false);
                            else
                                WriteResponse(stream, 401, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("PIN Gerekli"));
                            return;
                        }

                        if (path == "/api/version" && req.Method == "GET")
                        {
                            // Read-only non-sensitive version beacon (Host & Origin protected) so all browsers & devices stay in real-time sync
                            WriteJson(stream, 200, new
                            {
                                ok = true,
                                appVersion = "v3.7",
                                serverId = _serverInstanceId,
                                frontendVersion = ComputeFrontendVersion(),
                                dataVersion = Storage.GetDataVersion(),
                                tunnelActive = TunnelService.IsActive
                            });
                            return;
                        }

                        if (path == "/api/network-info" && req.Method == "GET")
                        {
                            if (!Security.IsValidSessionToken(req.Headers))
                            {
                                WriteJson(stream, 403, new { ok = false, error = "Yetkisiz istek." });
                                return;
                            }
                            var dbNet = Storage.Load();
                            var lanIps = Security.GetLocalLanIpv4Addresses();
                            WriteJson(stream, 200, new
                            {
                                ok = true,
                                port = _port,
                                localUrl = "http://localhost:" + _port + "/",
                                mobileUrls = lanIps.Select(ip => "http://" + ip + ":" + _port + "/").ToArray(),
                                tunnelActive = TunnelService.IsActive,
                                tunnelUrl = TunnelService.PublicUrl,
                                accessPin = dbNet.RemoteConfig.AccessPin,
                                autoStartTunnel = dbNet.RemoteConfig.AutoStartTunnel
                            });
                            return;
                        }

                        if (path == "/api/remote/start" && req.Method == "POST")
                        {
                            if (!Security.IsValidSessionToken(req.Headers))
                            {
                                WriteJson(stream, 403, new { ok = false, error = "Yetkisiz istek." });
                                return;
                            }
                            string tUrl, tErr;
                            bool started = TunnelService.StartTunnel(_port, out tUrl, out tErr);
                            if (!started)
                            {
                                WriteJson(stream, 500, new { ok = false, error = tErr });
                                return;
                            }
                            var dbRem = Storage.Load();
                            dbRem.RemoteConfig.AutoStartTunnel = true;
                            Storage.AddLog(dbRem, "system", "success", "Uzaktan Erişim", "Her yerden güvenli HTTPS erişim bağlantısı başlatıldı (PIN korumalı).", tUrl);
                            Storage.Save(dbRem);
                            WriteJson(stream, 200, new
                            {
                                ok = true,
                                tunnelActive = true,
                                tunnelUrl = tUrl,
                                accessPin = dbRem.RemoteConfig.AccessPin
                            });
                            return;
                        }

                        if (path == "/api/remote/stop" && req.Method == "POST")
                        {
                            if (!Security.IsValidSessionToken(req.Headers))
                            {
                                WriteJson(stream, 403, new { ok = false, error = "Yetkisiz istek." });
                                return;
                            }
                            TunnelService.StopTunnel();
                            var dbRem = Storage.Load();
                            dbRem.RemoteConfig.AutoStartTunnel = false;
                            Storage.AddLog(dbRem, "system", "info", "Uzaktan Erişim", "Her yerden erişim tünel bağlantısı kapatıldı.");
                            Storage.Save(dbRem);
                            WriteJson(stream, 200, new { ok = true, tunnelActive = false });
                            return;
                        }

                        if (path == "/api/remote/regenerate-pin" && req.Method == "POST")
                        {
                            if (!Security.IsValidSessionToken(req.Headers))
                            {
                                WriteJson(stream, 403, new { ok = false, error = "Yetkisiz istek." });
                                return;
                            }
                            var dbRem = Storage.Load();
                            dbRem.RemoteConfig.AccessPin = Security.Generate6DigitPin();
                            Security.RotateRemoteCookieToken();
                            Storage.AddLog(dbRem, "system", "info", "Uzaktan Erişim", "Uzaktan erişim güvenlik PIN'i yenilendi; eski uzak oturumlar sonlandırıldı.");
                            Storage.Save(dbRem);
                            WriteJson(stream, 200, new { ok = true, accessPin = dbRem.RemoteConfig.AccessPin });
                            return;
                        }

                        if (path.StartsWith("/api/"))
                        {
                            // 3. Per-session 256-bit CSRF / API Token Guard on ALL API routes
                            if (!Security.IsValidSessionToken(req.Headers))
                            {
                                WriteJson(stream, 403, new { ok = false, error = "Yetkisiz API isteği: geçerli oturum anahtarı eksik." });
                                return;
                            }
                            HandleApi(path, req, stream);
                        }
                        else if (path.StartsWith("/uploads/"))
                        {
                            string fileName = path.Substring("/uploads/".Length);
                            string fullUploadPath;
                            if (fileName.Contains("/") || !Security.TryResolveSafePath(Storage.UploadsDir, fileName, out fullUploadPath))
                            {
                                WriteJson(stream, 403, new { ok = false, error = "Erişim engellendi." });
                                return;
                            }
                            ServeFile(fullUploadPath, stream, true);
                        }
                        else
                        {
                            string rel = path == "/" ? "index.html" : path.TrimStart('/');
                            string fullFrontendPath;
                            if (!Security.TryResolveSafePath(_frontendDir, rel, out fullFrontendPath))
                            {
                                WriteJson(stream, 403, new { ok = false, error = "Erişim engellendi." });
                                return;
                            }
                            if (!File.Exists(fullFrontendPath))
                            {
                                WriteResponse(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bulunamadı"));
                                return;
                            }
                            if (Path.GetFileName(fullFrontendPath).Equals("index.html", StringComparison.OrdinalIgnoreCase))
                            {
                                ServeIndexWithSessionToken(fullFrontendPath, stream);
                            }
                            else
                            {
                                ServeFile(fullFrontendPath, stream, false);
                            }
                        }
                    }
                }
                catch
                {
                    // Safe silent close on malformed socket
                }
            }
        }

        private static SimpleHttpRequest ParseHttpRequest(NetworkStream stream)
        {
            var headerBytes = new MemoryStream();
            var buf = new byte[4096];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = stream.Read(buf, 0, buf.Length);
                if (read <= 0) return null;
                headerBytes.Write(buf, 0, read);
                byte[] arr = headerBytes.ToArray();
                headerEnd = FindHeaderEnd(arr);
                if (arr.Length > 32768 && headerEnd < 0) return null; // 32 KB max header
            }

            byte[] allInitial = headerBytes.ToArray();
            string headerText = Encoding.ASCII.GetString(allInitial, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || lines.Length > 100) return null;

            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return null;

            string decodedPath;
            try { decodedPath = Uri.UnescapeDataString(parts[1]); }
            catch { return null; }

            var req = new SimpleHttpRequest
            {
                Method = parts[0].ToUpperInvariant(),
                Path = decodedPath
            };

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    string k = lines[i].Substring(0, colon).Trim();
                    string v = lines[i].Substring(colon + 1).Trim();
                    if (req.Headers.ContainsKey(k) && k.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                        req.Headers[k] = req.Headers[k] + "; " + v;
                    else
                        req.Headers[k] = v;
                }
            }

            // Honor Expect: 100-continue so Cloudflare / Go reverse proxy never stalls on mobile uploads
            if (req.Headers.ContainsKey("Expect") && req.Headers["Expect"].IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    stream.Write(cont, 0, cont.Length);
                    stream.Flush();
                }
                catch { }
            }

            int maxAllowed = MaxDefaultBodyBytes;
            if (decodedPath.StartsWith("/api/upload", StringComparison.OrdinalIgnoreCase)) maxAllowed = MaxUploadBodyBytes;
            else if (decodedPath.StartsWith("/api/backup/import", StringComparison.OrdinalIgnoreCase)) maxAllowed = MaxBackupBodyBytes;

            int bodyStart = headerEnd + 4;
            int alreadyRead = allInitial.Length - bodyStart;

            bool isChunked = req.Headers.ContainsKey("Transfer-Encoding")
                && req.Headers["Transfer-Encoding"].IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isChunked)
            {
                bool tooLarge;
                req.Body = ReadChunkedBody(stream, allInitial, bodyStart, alreadyRead, maxAllowed, out tooLarge);
                if (tooLarge)
                {
                    req.PayloadTooLarge = true;
                    return req;
                }
            }
            else
            {
                int contentLength = 0;
                if (req.Headers.ContainsKey("Content-Length"))
                {
                    if (!int.TryParse(req.Headers["Content-Length"], out contentLength) || contentLength < 0)
                        return null;
                }

                if (contentLength > maxAllowed)
                {
                    req.PayloadTooLarge = true;
                    return req;
                }

                if (contentLength > 0)
                {
                    var bodyMs = new MemoryStream(Math.Min(contentLength, 65536));
                    if (alreadyRead > 0)
                        bodyMs.Write(allInitial, bodyStart, Math.Min(alreadyRead, contentLength));

                    while (bodyMs.Length < contentLength)
                    {
                        int toRead = (int)Math.Min(buf.Length, contentLength - bodyMs.Length);
                        int r = stream.Read(buf, 0, toRead);
                        if (r <= 0) break;
                        bodyMs.Write(buf, 0, r);
                    }
                    req.Body = Encoding.UTF8.GetString(bodyMs.ToArray());
                }
            }

            return req;
        }

        private static string ReadChunkedBody(NetworkStream stream, byte[] initialBuf, int bodyStart, int alreadyRead, int maxAllowed, out bool payloadTooLarge)
        {
            payloadTooLarge = false;
            var raw = new List<byte>(Math.Max(1024, alreadyRead));
            for (int i = 0; i < alreadyRead; i++) raw.Add(initialBuf[bodyStart + i]);

            var decoded = new MemoryStream();
            var tmp = new byte[4096];
            int pos = 0;

            Func<int, bool> ensureBytes = needed =>
            {
                while (raw.Count - pos < needed)
                {
                    int r = stream.Read(tmp, 0, tmp.Length);
                    if (r <= 0) return false;
                    for (int i = 0; i < r; i++) raw.Add(tmp[i]);
                    if (raw.Count > maxAllowed + 65536) return false;
                }
                return true;
            };

            while (true)
            {
                // Find CRLF terminating chunk-size line
                int lineEnd = -1;
                while (lineEnd < 0)
                {
                    for (int i = pos; i <= raw.Count - 2; i++)
                    {
                        if (raw[i] == 13 && raw[i + 1] == 10)
                        {
                            lineEnd = i;
                            break;
                        }
                    }
                    if (lineEnd < 0)
                    {
                        int r = stream.Read(tmp, 0, tmp.Length);
                        if (r <= 0) break;
                        for (int i = 0; i < r; i++) raw.Add(tmp[i]);
                        if (raw.Count > maxAllowed + 65536) { payloadTooLarge = true; return ""; }
                    }
                }
                if (lineEnd < 0) break;

                string sizeLine = Encoding.ASCII.GetString(raw.GetRange(pos, lineEnd - pos).ToArray()).Trim();
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi).Trim();
                pos = lineEnd + 2;

                int chunkSize;
                if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out chunkSize) || chunkSize < 0)
                    break;
                if (chunkSize == 0) break;

                if (decoded.Length + chunkSize > maxAllowed)
                {
                    payloadTooLarge = true;
                    return "";
                }

                if (!ensureBytes(chunkSize + 2)) break;
                decoded.Write(raw.GetRange(pos, chunkSize).ToArray(), 0, chunkSize);
                pos += chunkSize + 2; // skip chunk data + trailing CRLF
            }

            return Encoding.UTF8.GetString(decoded.ToArray());
        }

        private static int FindHeaderEnd(byte[] arr)
        {
            for (int i = 0; i <= arr.Length - 4; i++)
            {
                if (arr[i] == 13 && arr[i + 1] == 10 && arr[i + 2] == 13 && arr[i + 3] == 10)
                    return i;
            }
            return -1;
        }

        private static void HandleApi(string path, SimpleHttpRequest req, NetworkStream stream)
        {
            try
            {
                string method = req.Method;

                if (path == "/api/state" && method == "GET")
                {
                    var db = Storage.Load();
                    // Never send raw decrypted secrets back to browser DOM — send masked placeholders
                    var safeView = Security.CloneWithMaskedSecrets(db, false);
                    WriteJson(stream, 200, new
                    {
                        ok = true,
                        aiConfig = safeView.AiConfig,
                        syncConfig = safeView.SyncConfig,
                        draft = safeView.Draft,
                        marketplaces = safeView.Marketplaces,
                        products = safeView.Products,
                        logs = safeView.Logs,
                        security = new
                        {
                            dpapiActive = true,
                            sessionTokenActive = true,
                            cspStrict = true,
                            loopbackOnly = true,
                            backupProtected = File.Exists(Storage.BackupPath)
                        }
                    });
                    return;
                }

                if (path == "/api/draft/save" && method == "POST")
                {
                    var incomingDraft = Storage.Deserialize<DraftWorkspace>(req.Body);
                    if (incomingDraft != null)
                    {
                        var db = Storage.Load();
                        incomingDraft.UploadedImages = (incomingDraft.UploadedImages ?? new List<string>()).Where(Security.IsSafeImageUrl).Take(12).ToList();
                        incomingDraft.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        db.Draft = incomingDraft;
                        Storage.Save(db);
                    }
                    WriteJson(stream, 200, new { ok = true, savedAt = DateTime.Now.ToString("HH:mm:ss") });
                    return;
                }

                if (path == "/api/draft/clear" && method == "POST")
                {
                    var db = Storage.Load();
                    string keepView = db.Draft != null ? db.Draft.ActiveView : "add-product";
                    var keepMps = db.Draft != null && db.Draft.SelectedTargetMps != null && db.Draft.SelectedTargetMps.Count > 0
                        ? db.Draft.SelectedTargetMps
                        : db.Marketplaces.Where(m => m.Enabled).Select(m => m.Id).ToList();
                    db.Draft = new DraftWorkspace
                    {
                        ActiveView = keepView,
                        SelectedTargetMps = keepMps,
                        UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    };
                    Storage.Save(db);
                    WriteJson(stream, 200, new { ok = true, draft = db.Draft });
                    return;
                }

                if (path == "/api/backup/export" && method == "GET")
                {
                    string raw = Storage.ExportSafeBackupJson();
                    byte[] bytes = Encoding.UTF8.GetBytes(raw);
                    WriteResponse(stream, 200, "application/json; charset=utf-8", bytes);
                    return;
                }

                if (path == "/api/backup/import" && method == "POST")
                {
                    if (!Security.CheckRateLimit("backup_import", 10))
                    {
                        WriteJson(stream, 429, new { ok = false, error = "Çok sık yedek yükleme isteği. Lütfen biraz bekleyin." });
                        return;
                    }
                    var imported = Storage.ImportRawJson(req.Body);
                    WriteJson(stream, 200, new { ok = true, productsCount = imported.Products.Count });
                    return;
                }

                if (path == "/api/upload" && method == "POST")
                {
                    if (!Security.CheckRateLimit("upload", 40))
                    {
                        WriteJson(stream, 429, new { ok = false, error = "Çok sık görsel yükleme isteği. Lütfen biraz bekleyin." });
                        return;
                    }
                    var body = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    string fileName = body != null && body.ContainsKey("fileName") ? Convert.ToString(body["fileName"]) : "image.jpg";
                    string data = body != null && body.ContainsKey("data") ? Convert.ToString(body["data"]) : "";
                    try
                    {
                        string url = Storage.SaveBase64Image(fileName, data);
                        WriteJson(stream, 200, new { ok = true, url = url });
                    }
                    catch (Exception ex)
                    {
                        WriteJson(stream, 400, new { ok = false, error = ex.Message });
                    }
                    return;
                }

                if (path == "/api/vision/scan" && method == "POST")
                {
                    if (!Security.CheckRateLimit("analyze", 30))
                    {
                        WriteJson(stream, 429, new { ok = false, error = "Çok sık görsel tarama isteği. Lütfen birkaç saniye bekleyin." });
                        return;
                    }
                    var scanReq = Storage.Deserialize<AnalyzeRequest>(req.Body);
                    if (scanReq == null || scanReq.Images == null || scanReq.Images.Count == 0)
                    {
                        WriteJson(stream, 400, new { ok = false, error = "Lütfen önce en az bir ürün görseli yükleyin." });
                        return;
                    }
                    scanReq.Images = scanReq.Images.Where(Security.IsSafeImageUrl).Take(6).ToList();
                    var db = Storage.Load();
                    var vision = VisionAnalyzer.AnalyzeProductImages(scanReq.Images, scanReq.OriginalFileNames, scanReq.Prompt, db.AiConfig);
                    Storage.AddLog(db, "analysis", "info", "Smart Vision",
                        "Görsel tarama tamamlandı: '" + vision.DetectedProductName + "' (" + vision.DominantColor + ").",
                        "Motor: " + vision.EngineUsed);
                    Storage.Save(db);
                    WriteJson(stream, 200, new { ok = true, vision = vision });
                    return;
                }

                if (path == "/api/analyze" && method == "POST")
                {
                    if (!Security.CheckRateLimit("analyze", 30))
                    {
                        WriteJson(stream, 429, new { ok = false, error = "Çok sık analiz isteği. Lütfen birkaç saniye bekleyin." });
                        return;
                    }
                    var analyzeReq = Storage.Deserialize<AnalyzeRequest>(req.Body);
                    bool hasImages = analyzeReq != null && analyzeReq.Images != null && analyzeReq.Images.Count > 0;
                    if (analyzeReq == null || (string.IsNullOrWhiteSpace(analyzeReq.Prompt) && !hasImages))
                    {
                        WriteJson(stream, 400, new { ok = false, error = "Lütfen en az bir ürün görseli yükleyin veya ürün hakkında kısa bir açıklama yazın." });
                        return;
                    }
                    if (!string.IsNullOrEmpty(analyzeReq.Prompt) && analyzeReq.Prompt.Length > 4000)
                        analyzeReq.Prompt = analyzeReq.Prompt.Substring(0, 4000);
                    analyzeReq.Images = (analyzeReq.Images ?? new List<string>()).Where(Security.IsSafeImageUrl).Take(12).ToList();

                    var db = Storage.Load();
                    string engineUsed;
                    var draft = AiContentEngine.GenerateSmartProduct(analyzeReq, db, out engineUsed);

                    db.Draft.Prompt = draft.Prompt ?? "";
                    db.Draft.UploadedImages = analyzeReq.Images;
                    db.Draft.SelectedTargetMps = analyzeReq.TargetMarketplaces ?? new List<string>();
                    db.Draft.GeneratedDraft = draft;
                    db.Draft.EngineUsed = engineUsed;
                    db.Draft.ActiveListingTab = (analyzeReq.TargetMarketplaces != null && analyzeReq.TargetMarketplaces.Count > 0) ? analyzeReq.TargetMarketplaces[0] : "trendyol";
                    db.Draft.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                    Storage.AddLog(db, "analysis", "info", "Smart AI",
                        "'" + draft.BaseName + "' görsel + her pazaryerine özel izole rakip araştırmasıyla hazırlandı.",
                        "Motor: " + engineUsed + " | Kategori: " + draft.Category + " | Önerilen Fiyat: " + draft.Price.ToString("N0") + " TL");
                    Storage.Save(db);
                    WriteJson(stream, 200, new { ok = true, engineUsed = engineUsed, product = draft });
                    return;
                }

                if (path == "/api/products/publish" && method == "POST")
                {
                    var product = Storage.Deserialize<ProductRecord>(req.Body);
                    if (product == null)
                    {
                        WriteJson(stream, 400, new { ok = false, error = "Geçersiz ürün verisi." });
                        return;
                    }

                    var db = Storage.Load();
                    product.Id = Security.SanitizeId(product.Id, "prd_");
                    product.Stock = Math.Max(0, Math.Min(1000000, product.Stock));
                    product.Price = Math.Max(0, Math.Min(10000000, product.Price));
                    product.Images = (product.Images ?? new List<string>()).Where(Security.IsSafeImageUrl).Take(12).ToList();
                    product.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    if (string.IsNullOrWhiteSpace(product.CreatedAt)) product.CreatedAt = product.UpdatedAt;

                    int successCount = 0;
                    int liveSuccessCount = 0;
                    int simulatedCount = 0;
                    int failCount = 0;
                    var summaryList = new List<object>();

                    foreach (var mp in db.Marketplaces)
                    {
                        if (!product.Listings.ContainsKey(mp.Id)) continue;
                        var listing = product.Listings[mp.Id];
                        if (!listing.Selected) continue;

                        var result = MarketplaceConnectors.PublishProduct(mp, product, listing);
                        listing.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        listing.SyncedStock = product.Stock;
                        listing.StatusMessage = Security.RedactSensitiveText(result.Message);

                        if (result.Success)
                        {
                            successCount++;
                            if (result.IsSimulated) simulatedCount++;
                            else liveSuccessCount++;
                            listing.Status = result.IsSimulated ? "simulated" : "published";
                            listing.RemoteId = result.RemoteId;
                            Storage.AddLog(db, "publish", "success", mp.Name,
                                "'" + product.BaseName + "' yayınlandı (" + (result.IsSimulated ? "Test Modu" : "Canlı API") + ").",
                                result.RawDetails ?? result.Message);
                        }
                        else
                        {
                            failCount++;
                            listing.Status = "error";
                            Storage.AddLog(db, "publish", "error", mp.Name,
                                "'" + product.BaseName + "' yayınlanamadı: " + result.Message,
                                result.RawDetails ?? "");
                        }
                        summaryList.Add(new { marketplace = mp.Name, marketplaceId = mp.Id, success = result.Success, simulated = result.IsSimulated, message = listing.StatusMessage, remoteId = result.RemoteId });
                    }

                    var existingIdx = db.Products.FindIndex(p => p.Id == product.Id);
                    if (existingIdx >= 0) db.Products[existingIdx] = product;
                    else db.Products.Insert(0, product);

                    var keepMps = db.Draft != null && db.Draft.SelectedTargetMps != null && db.Draft.SelectedTargetMps.Count > 0
                        ? db.Draft.SelectedTargetMps
                        : db.Marketplaces.Where(m => m.Enabled).Select(m => m.Id).ToList();
                    db.Draft = new DraftWorkspace
                    {
                        ActiveView = "inventory",
                        SelectedTargetMps = keepMps,
                        UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    };

                    Storage.Save(db);
                    WriteJson(stream, 200, new
                    {
                        ok = true,
                        product = product,
                        successCount = successCount,
                        liveSuccessCount = liveSuccessCount,
                        simulatedCount = simulatedCount,
                        failCount = failCount,
                        results = summaryList
                    });
                    return;
                }

                if (path == "/api/products/republish" && method == "POST")
                {
                    var body = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    string productId = body != null && body.ContainsKey("productId") ? Convert.ToString(body["productId"]) : "";
                    string onlyMpId = body != null && body.ContainsKey("marketplaceId") && body["marketplaceId"] != null ? Convert.ToString(body["marketplaceId"]) : "";
                    bool liveOnly = body != null && body.ContainsKey("liveOnly") && Convert.ToBoolean(body["liveOnly"]);

                    var db = Storage.Load();
                    var product = db.Products.Find(p => p.Id == productId);
                    if (product == null)
                    {
                        WriteJson(stream, 404, new { ok = false, error = "Ürün bulunamadı." });
                        return;
                    }

                    int liveSuccess = 0, simSuccess = 0, failCount = 0;
                    var results = new List<object>();

                    foreach (var mp in db.Marketplaces)
                    {
                        if (!string.IsNullOrWhiteSpace(onlyMpId) && mp.Id != onlyMpId) continue;
                        if (liveOnly && mp.SimulationMode) continue;
                        if (!product.Listings.ContainsKey(mp.Id)) continue;
                        var listing = product.Listings[mp.Id];
                        if (!listing.Selected && string.IsNullOrWhiteSpace(onlyMpId)) continue;
                        listing.Selected = true;

                        var res = MarketplaceConnectors.PublishProduct(mp, product, listing);
                        listing.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        listing.SyncedStock = product.Stock;
                        listing.StatusMessage = Security.RedactSensitiveText(res.Message);

                        if (res.Success)
                        {
                            if (res.IsSimulated) simSuccess++; else liveSuccess++;
                            listing.Status = res.IsSimulated ? "simulated" : "published";
                            listing.RemoteId = res.RemoteId;
                            Storage.AddLog(db, "publish", "success", mp.Name,
                                "'" + product.BaseName + "' canlı senkronize edildi (" + (res.IsSimulated ? "Test Modu" : "Canlı API") + ").",
                                res.RawDetails ?? res.Message);
                        }
                        else
                        {
                            failCount++;
                            listing.Status = "error";
                            Storage.AddLog(db, "publish", "error", mp.Name,
                                "'" + product.BaseName + "' gönderilemedi: " + res.Message,
                                res.RawDetails ?? "");
                        }
                        results.Add(new { marketplace = mp.Name, marketplaceId = mp.Id, success = res.Success, simulated = res.IsSimulated, message = listing.StatusMessage, remoteId = res.RemoteId });
                    }

                    product.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    Storage.Save(db);
                    WriteJson(stream, 200, new
                    {
                        ok = true,
                        product = product,
                        liveSuccessCount = liveSuccess,
                        simulatedCount = simSuccess,
                        failCount = failCount,
                        results = results
                    });
                    return;
                }

                if (path == "/api/trendyol/categories/search" && method == "POST")
                {
                    var body = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    string query = body != null && body.ContainsKey("query") ? Convert.ToString(body["query"]) : "";
                    var db = Storage.Load();
                    var tyCfg = db.Marketplaces.Find(m => m.Id == "trendyol");
                    string err;
                    var items = TrendyolCategoryEngine.SearchCategories(tyCfg, query, 15, out err);
                    WriteJson(stream, 200, new { ok = true, categories = items, error = err });
                    return;
                }

                if (path == "/api/trendyol/categories/match" && method == "POST")
                {
                    var body = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    string baseName = body != null && body.ContainsKey("baseName") ? Convert.ToString(body["baseName"]) : "";
                    string title = body != null && body.ContainsKey("title") ? Convert.ToString(body["title"]) : baseName;
                    string prompt = body != null && body.ContainsKey("prompt") ? Convert.ToString(body["prompt"]) : baseName;
                    string coarse = body != null && body.ContainsKey("category") ? Convert.ToString(body["category"]) : "";
                    int prefId = 0;
                    if (body != null && body.ContainsKey("preferredCategoryId") && body["preferredCategoryId"] != null)
                        int.TryParse(Convert.ToString(body["preferredCategoryId"]), out prefId);

                    var db = Storage.Load();
                    var tyCfg = db.Marketplaces.Find(m => m.Id == "trendyol");
                    var match = TrendyolCategoryEngine.ResolveBestCategory(tyCfg, baseName, title, prompt, coarse, prefId);
                    if (match.Success && match.CategoryId > 0 && tyCfg != null && MarketplaceConnectors.HasRequiredCredentials(tyCfg))
                    {
                        match.ResolvedAttributes = TrendyolCategoryEngine.BuildUiAttributesForCategory(tyCfg, match.CategoryId, baseName, title, prompt, null);
                    }
                    WriteJson(stream, 200, new { ok = match.Success, match = match });
                    return;
                }

                if (path == "/api/trendyol/check-batch" && method == "POST")
                {
                    var body = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    string productId = body != null && body.ContainsKey("productId") ? Convert.ToString(body["productId"]) : "";
                    var db = Storage.Load();
                    var mp = db.Marketplaces.Find(m => m.Id == "trendyol");
                    var product = db.Products.Find(p => p.Id == productId);
                    if (mp == null || product == null || !product.Listings.ContainsKey("trendyol"))
                    {
                        WriteJson(stream, 404, new { ok = false, error = "Trendyol kaydı bulunamadı." });
                        return;
                    }
                    var listing = product.Listings["trendyol"];
                    if (string.IsNullOrWhiteSpace(listing.RemoteId) || mp.SimulationMode)
                    {
                        WriteJson(stream, 400, new { ok = false, error = "Bu ürün henüz canlı Trendyol API'ye gönderilmedi." });
                        return;
                    }
                    var res = MarketplaceConnectors.CheckTrendyolBatch(mp, listing.RemoteId);
                    listing.Status = res.Success ? "published" : "error";
                    listing.StatusMessage = Security.RedactSensitiveText(res.Message);
                    listing.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    Storage.AddLog(db, "publish", res.Success ? "success" : "error", "Trendyol", listing.StatusMessage, res.RawDetails ?? "");
                    Storage.Save(db);
                    WriteJson(stream, 200, new { ok = res.Success, result = res, listing = listing });
                    return;
                }

                if (path.StartsWith("/api/products/") && method == "DELETE")
                {
                    string id = Security.SanitizeId(path.Substring("/api/products/".Length), "");
                    var db = Storage.Load();
                    var target = db.Products.Find(p => p.Id == id);
                    if (target != null)
                    {
                        db.Products.Remove(target);
                        Storage.AddLog(db, "stock", "warning", "Smart", "'" + target.BaseName + "' ürün kaydı listeden kaldırıldı.");
                        Storage.Save(db);
                    }
                    WriteJson(stream, 200, new { ok = true });
                    return;
                }

                if (path == "/api/stock/update" && method == "POST")
                {
                    var body = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    string productId = Convert.ToString(body["productId"]);
                    int newStock = Math.Max(0, Math.Min(1000000, Convert.ToInt32(body["newStock"])));
                    decimal? newPrice = body.ContainsKey("newPrice") && body["newPrice"] != null ? Convert.ToDecimal(body["newPrice"]) : (decimal?)null;
                    string source = body.ContainsKey("source") && body["source"] != null ? Convert.ToString(body["source"]) : "Merkez Panel";

                    var db = Storage.Load();
                    var product = db.Products.Find(p => p.Id == productId);
                    if (product == null)
                    {
                        WriteJson(stream, 404, new { ok = false, error = "Ürün bulunamadı." });
                        return;
                    }

                    int oldStock = product.Stock;
                    product.Stock = newStock;
                    if (newPrice.HasValue && newPrice.Value > 0 && newPrice.Value <= 10000000) product.Price = newPrice.Value;
                    product.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                    int syncedChannels = SyncSingleProductAcrossChannels(db, product, source, oldStock);
                    db.SyncConfig.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    Storage.Save(db);

                    WriteJson(stream, 200, new { ok = true, product = product, syncedChannels = syncedChannels });
                    return;
                }

                if (path == "/api/stock/simulate-order" && method == "POST")
                {
                    var body = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    string productId = Convert.ToString(body["productId"]);
                    string marketplaceId = body.ContainsKey("marketplaceId") ? Convert.ToString(body["marketplaceId"]) : "trendyol";
                    int qty = body.ContainsKey("quantity") ? Math.Max(1, Math.Min(1000, Convert.ToInt32(body["quantity"]))) : 1;

                    var db = Storage.Load();
                    var product = db.Products.Find(p => p.Id == productId);
                    if (product == null)
                    {
                        WriteJson(stream, 404, new { ok = false, error = "Ürün bulunamadı." });
                        return;
                    }

                    int oldStock = product.Stock;
                    product.Stock = Math.Max(0, product.Stock - qty);
                    product.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                    string mpName = MarketplaceSchema.GetMarketplaceName(marketplaceId);
                    Storage.AddLog(db, "stock", "info", mpName,
                        mpName + " üzerinden " + qty + " adet sipariş geldi ('" + product.BaseName + "').",
                        "Merkez stok " + oldStock + " → " + product.Stock + " olarak düşüldü; diğer pazaryerleri senkronize ediliyor.");

                    int synced = SyncSingleProductAcrossChannels(db, product, mpName + " Sipariş Senkronu", oldStock);
                    db.SyncConfig.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    Storage.Save(db);

                    WriteJson(stream, 200, new { ok = true, product = product, syncedChannels = synced });
                    return;
                }

                if (path == "/api/stock/sync-all" && method == "POST")
                {
                    var db = Storage.Load();
                    int count = RunFullStockSync(db, "Manuel Toplu Senkronizasyon");
                    WriteJson(stream, 200, new { ok = true, syncedOperations = count, lastSyncAt = db.SyncConfig.LastSyncAt });
                    return;
                }

                if (path == "/api/marketplaces/save" && method == "POST")
                {
                    var rawDict = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    bool silent = rawDict != null && rawDict.ContainsKey("silent") && Convert.ToBoolean(rawDict["silent"]);
                    var incoming = Storage.Deserialize<MarketplaceConfig>(req.Body);
                    var db = Storage.Load();
                    var existing = db.Marketplaces.Find(m => m.Id == incoming.Id);
                    if (existing == null)
                    {
                        WriteJson(stream, 404, new { ok = false, error = "Pazaryeri bulunamadı." });
                        return;
                    }
                    bool hadFullCredsBefore = MarketplaceConnectors.HasRequiredCredentials(existing);
                    existing.Enabled = incoming.Enabled;
                    existing.SimulationMode = incoming.SimulationMode;
                    if (incoming.Credentials != null)
                    {
                        foreach (var kv in incoming.Credentials)
                        {
                            string val = (kv.Value ?? "").Trim();
                            if (Security.IsSecretKeyName(kv.Key) && Security.IsMaskedPlaceholder(val))
                                continue;
                            existing.Credentials[kv.Key] = val;
                        }
                    }
                    // If user just filled in all required credentials for the first time, auto-switch to LIVE mode!
                    bool hasFullCredsNow = MarketplaceConnectors.HasRequiredCredentials(existing);
                    bool autoSwitchedLive = false;
                    if (!hadFullCredsBefore && hasFullCredsNow && existing.SimulationMode)
                    {
                        existing.SimulationMode = false;
                        autoSwitchedLive = true;
                    }
                    if (!silent)
                    {
                        existing.StatusMessage = existing.SimulationMode
                            ? "Test Modu Aktif — Ayarlar güvenle şifrelenerek kaydedildi"
                            : (autoSwitchedLive
                                ? "Canlı API Modu otomatik açıldı — Artık gerçek hesabınıza yayınlanır"
                                : "Canlı Mod — Anahtarlar şifrelendi, bağlantıyı test edebilirsiniz");
                        Storage.AddLog(db, "api", "info", existing.Name, existing.Name + " API ayarları şifrelenerek güncellendi (" + (existing.SimulationMode ? "Test Modu" : "Canlı API Modu") + ").");
                    }
                    Storage.Save(db);
                    var maskedClone = Security.CloneWithMaskedSecrets(db, false).Marketplaces.Find(m => m.Id == existing.Id);
                    WriteJson(stream, 200, new { ok = true, marketplace = maskedClone });
                    return;
                }

                if (path == "/api/marketplaces/test" && method == "POST")
                {
                    if (!Security.CheckRateLimit("mp_test", 20))
                    {
                        WriteJson(stream, 429, new { ok = false, error = "Çok sık bağlantı testi. Lütfen birkaç saniye bekleyin." });
                        return;
                    }
                    var body = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    string id = Convert.ToString(body["id"]);
                    var db = Storage.Load();
                    var mp = db.Marketplaces.Find(m => m.Id == id);
                    if (mp == null)
                    {
                        WriteJson(stream, 404, new { ok = false, error = "Pazaryeri bulunamadı." });
                        return;
                    }

                    var result = MarketplaceConnectors.TestConnection(mp);
                    mp.Connected = result.Success;
                    mp.LastTestedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    mp.StatusMessage = Security.RedactSensitiveText(result.Message);

                    Storage.AddLog(db, "api", result.Success ? "success" : "error", mp.Name, mp.StatusMessage, result.RawDetails ?? "");
                    Storage.Save(db);
                    var maskedClone = Security.CloneWithMaskedSecrets(db, false).Marketplaces.Find(m => m.Id == mp.Id);
                    WriteJson(stream, 200, new { ok = result.Success, result = result, marketplace = maskedClone });
                    return;
                }

                if (path == "/api/settings/ai" && method == "POST")
                {
                    var rawDict = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    bool silent = rawDict != null && rawDict.ContainsKey("silent") && Convert.ToBoolean(rawDict["silent"]);
                    var cfg = Storage.Deserialize<AiSettings>(req.Body);
                    var db = Storage.Load();

                    // Preserve existing AI keys if masked placeholder was sent back
                    if (Security.IsMaskedPlaceholder(cfg.GeminiApiKey))
                        cfg.GeminiApiKey = db.AiConfig.GeminiApiKey;
                    if (Security.IsMaskedPlaceholder(cfg.OpenAiApiKey))
                        cfg.OpenAiApiKey = db.AiConfig.OpenAiApiKey;

                    db.AiConfig = cfg;
                    if (!silent)
                        Storage.AddLog(db, "system", "info", "Smart AI", "Yapay zeka ve rakip analiz ayarları şifrelenerek güncellendi (Mod: " + cfg.Provider + ").");
                    Storage.Save(db);
                    var maskedAi = Security.CloneWithMaskedSecrets(db, false).AiConfig;
                    WriteJson(stream, 200, new { ok = true, aiConfig = maskedAi });
                    return;
                }

                if (path == "/api/settings/sync" && method == "POST")
                {
                    var rawDict = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    bool silent = rawDict != null && rawDict.ContainsKey("silent") && Convert.ToBoolean(rawDict["silent"]);
                    var cfg = Storage.Deserialize<SyncSettings>(req.Body);
                    var db = Storage.Load();
                    db.SyncConfig.AutoSyncEnabled = cfg.AutoSyncEnabled;
                    db.SyncConfig.IntervalMinutes = Math.Max(1, Math.Min(1440, cfg.IntervalMinutes));
                    db.SyncConfig.LowStockThreshold = Math.Max(0, Math.Min(100000, cfg.LowStockThreshold));
                    if (!silent)
                        Storage.AddLog(db, "sync", "info", "Stok Motoru",
                            "Otomatik stok senkronizasyonu " + (cfg.AutoSyncEnabled ? ("açık (her " + db.SyncConfig.IntervalMinutes + " dk)") : "kapalı") + ".");
                    Storage.Save(db);
                    WriteJson(stream, 200, new { ok = true, syncConfig = db.SyncConfig });
                    return;
                }

                if (path == "/api/logs/clear" && method == "POST")
                {
                    var db = Storage.Load();
                    db.Logs.Clear();
                    Storage.AddLog(db, "system", "info", "Smart", "İşlem kayıtları temizlendi.");
                    Storage.Save(db);
                    WriteJson(stream, 200, new { ok = true });
                    return;
                }

                WriteJson(stream, 404, new { ok = false, error = "Bilinmeyen API yolu." });
            }
            catch
            {
                // Never leak internal stack traces or file paths to client
                WriteJson(stream, 500, new { ok = false, error = "Sunucu isteği işlenirken beklenmeyen bir hata oluştu." });
            }
        }

        private static int SyncSingleProductAcrossChannels(AppDatabase db, ProductRecord product, string source, int oldStock)
        {
            int synced = 0;
            foreach (var mp in db.Marketplaces)
            {
                if (!product.Listings.ContainsKey(mp.Id)) continue;
                var listing = product.Listings[mp.Id];
                if (!listing.Selected && listing.Status == "draft") continue;

                listing.Price = product.Price;
                var res = MarketplaceConnectors.UpdateStockAndPrice(mp, product, listing, product.Stock, listing.Price);
                listing.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (res.Success)
                {
                    listing.SyncedStock = product.Stock;
                    listing.StatusMessage = Security.RedactSensitiveText(res.Message);
                    synced++;
                }
                else
                {
                    listing.StatusMessage = Security.RedactSensitiveText(res.Message);
                    Storage.AddLog(db, "sync", "error", mp.Name, listing.StatusMessage, res.RawDetails ?? "");
                }
            }

            Storage.AddLog(db, "sync", "success", "Tüm Kanallar",
                "'" + product.BaseName + "' stoğu " + oldStock + " → " + product.Stock + " adet olarak " + synced + " pazaryerine senkronize edildi.",
                "Kaynak: " + source);
            return synced;
        }

        private static int RunFullStockSync(AppDatabase db, string triggerName)
        {
            int ops = 0;
            foreach (var p in db.Products)
            {
                foreach (var mp in db.Marketplaces)
                {
                    if (!mp.Enabled || !p.Listings.ContainsKey(mp.Id)) continue;
                    var listing = p.Listings[mp.Id];
                    if (!listing.Selected) continue;
                    var r = MarketplaceConnectors.UpdateStockAndPrice(mp, p, listing, p.Stock, listing.Price);
                    listing.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    if (r.Success)
                    {
                        listing.SyncedStock = p.Stock;
                        listing.StatusMessage = Security.RedactSensitiveText(r.Message);
                        ops++;
                    }
                }
            }
            db.SyncConfig.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Storage.AddLog(db, "sync", "success", "Stok Motoru",
                triggerName + " tamamlandı: " + db.Products.Count + " ürün, " + ops + " kanal güncellemesi eşitlendi.");
            Storage.Save(db);
            return ops;
        }

        private static void ServeIndexWithSessionToken(string indexPath, NetworkStream stream)
        {
            string html = File.ReadAllText(indexPath, Encoding.UTF8);
            string metaTag = "<meta name=\"smart-token\" content=\"" + Security.SessionToken + "\" />";
            if (html.Contains("<!--SMART_SESSION_TOKEN-->"))
                html = html.Replace("<!--SMART_SESSION_TOKEN-->", metaTag);
            else
                html = html.Replace("</head>", "  " + metaTag + "\n</head>");
            WriteResponse(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
        }

        private static void HandlePinLogin(SimpleHttpRequest req, NetworkStream stream, bool isHttps)
        {
            if (!Security.CheckRateLimit("pin_login", 12))
            {
                WriteJson(stream, 429, new { ok = false, error = "Çok fazla hatalı deneme. Lütfen 1 dakika bekleyin." });
                return;
            }
            try
            {
                string ct = req.Headers.ContainsKey("Content-Type") ? (req.Headers["Content-Type"] ?? "") : "";
                bool isForm = ct.IndexOf("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0;
                string rawPin = "";
                if (isForm)
                {
                    foreach (var pair in (req.Body ?? "").Split('&'))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq > 0 && pair.Substring(0, eq).Equals("pin", StringComparison.OrdinalIgnoreCase))
                        {
                            rawPin = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace("+", " "));
                            break;
                        }
                    }
                }
                else
                {
                    var body = Storage.Deserialize<Dictionary<string, object>>(req.Body);
                    rawPin = body != null && body.ContainsKey("pin") ? Convert.ToString(body["pin"]) : "";
                }
                // Strip any spaces or dashes (e.g. "350 377" -> "350377")
                string pin = System.Text.RegularExpressions.Regex.Replace(rawPin ?? "", @"\D+", "");
                var db = Storage.Load();
                if (!Security.FixedTimeEquals(pin, db.RemoteConfig.AccessPin))
                {
                    WriteJson(stream, 401, new { ok = false, error = "Güvenlik PIN kodu hatalı. Bilgisayarınızdaki Smart > Mobil penceresindeki 6 haneli PIN kodunu girin." });
                    return;
                }
                string cookie = "smart_remote_auth=" + Security.RemoteAuthCookieToken + "; Path=/; HttpOnly; SameSite=Lax; Max-Age=86400" + (isHttps ? "; Secure" : "");
                if (isForm)
                {
                    WriteResponse(stream, 303, "text/plain; charset=utf-8", new byte[0], cookie, "/?ok=" + DateTime.UtcNow.Ticks);
                }
                else
                {
                    string json = Storage.Serialize(new { ok = true });
                    WriteResponse(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), cookie);
                }
            }
            catch
            {
                WriteJson(stream, 400, new { ok = false, error = "Geçersiz PIN isteği." });
            }
        }

        private static void WriteJson(NetworkStream stream, int statusCode, object data)
        {
            string json = Storage.Serialize(data);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            WriteResponse(stream, statusCode, "application/json; charset=utf-8", bytes, null);
        }

        private static void ServeFile(string filePath, NetworkStream stream, bool isUpload)
        {
            if (!File.Exists(filePath))
            {
                WriteResponse(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bulunamadı"));
                return;
            }
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string ct = "application/octet-stream";
            if (isUpload)
            {
                // Uploads only allow verified raster images (never HTML/JS/SVG)
                switch (ext)
                {
                    case ".png": ct = "image/png"; break;
                    case ".jpg":
                    case ".jpeg": ct = "image/jpeg"; break;
                    case ".webp": ct = "image/webp"; break;
                    default:
                        WriteResponse(stream, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Engellendi"));
                        return;
                }
            }
            else
            {
                switch (ext)
                {
                    case ".html": ct = "text/html; charset=utf-8"; break;
                    case ".css": ct = "text/css; charset=utf-8"; break;
                    case ".js": ct = "application/javascript; charset=utf-8"; break;
                    case ".json": ct = "application/manifest+json; charset=utf-8"; break;
                    case ".svg": ct = "image/svg+xml"; break;
                    case ".png": ct = "image/png"; break;
                    case ".jpg":
                    case ".jpeg": ct = "image/jpeg"; break;
                    case ".webp": ct = "image/webp"; break;
                }
            }
            byte[] bytes = File.ReadAllBytes(filePath);
            WriteResponse(stream, 200, ct, bytes);
        }

        private static void WriteResponse(NetworkStream stream, int statusCode, string contentType, byte[] body, string setCookieHeader = null, string locationHeader = null)
        {
            string statusText;
            switch (statusCode)
            {
                case 200: statusText = "OK"; break;
                case 204: statusText = "No Content"; break;
                case 303: statusText = "See Other"; break;
                case 400: statusText = "Bad Request"; break;
                case 401: statusText = "Unauthorized"; break;
                case 403: statusText = "Forbidden"; break;
                case 404: statusText = "Not Found"; break;
                case 413: statusText = "Payload Too Large"; break;
                case 429: statusText = "Too Many Requests"; break;
                default: statusText = "Internal Server Error"; break;
            }

            // Hardened security headers — NO wildcard CORS, strict CSP without unsafe-inline scripts
            string csp = "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
            string cookieLine = string.IsNullOrEmpty(setCookieHeader) ? "" : ("Set-Cookie: " + setCookieHeader + "\r\n");
            string locLine = string.IsNullOrEmpty(locationHeader) ? "" : ("Location: " + locationHeader + "\r\n");
            string header = "HTTP/1.1 " + statusCode + " " + statusText + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + cookieLine
                + locLine
                + "X-Content-Type-Options: nosniff\r\n"
                + "X-Frame-Options: DENY\r\n"
                + "Referrer-Policy: no-referrer\r\n"
                + "Permissions-Policy: camera=(), microphone=(), geolocation=()\r\n"
                + "Content-Security-Policy: " + csp + "\r\n"
                + "Cache-Control: no-store, no-cache, must-revalidate, max-age=0\r\n"
                + "Pragma: no-cache\r\n"
                + "Expires: 0\r\n"
                + "Connection: close\r\n\r\n";
            byte[] hBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(hBytes, 0, hBytes.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }
    }
}
