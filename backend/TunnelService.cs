using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace SmartMarketplace
{
    public static class TunnelService
    {
        private static readonly object _lock = new object();
        private static Process _proc;
        private static string _publicUrl = "";
        private static string _lastError = "";

        public static bool IsActive
        {
            get
            {
                lock (_lock)
                {
                    return _proc != null && !_proc.HasExited && !string.IsNullOrEmpty(_publicUrl);
                }
            }
        }

        public static string PublicUrl
        {
            get { lock (_lock) return _publicUrl ?? ""; }
        }

        public static string LastError
        {
            get { lock (_lock) return _lastError ?? ""; }
        }

        static TunnelService()
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => StopTunnel();
        }

        private static string GetCloudflaredPath()
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string p1 = Path.Combine(localApp, "Programs", "SmartGitTools", "cloudflared.exe");
            if (File.Exists(p1)) return p1;

            string p2 = Path.Combine(Storage.DataDir, "cloudflared.exe");
            if (File.Exists(p2)) return p2;

            return p1;
        }

        private static bool EnsureBinaryAvailable(out string binPath, out string error)
        {
            error = "";
            binPath = GetCloudflaredPath();
            if (File.Exists(binPath) && new FileInfo(binPath).Length > 1024 * 1024)
                return true;

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string dir = Path.GetDirectoryName(binPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = binPath + ".download";
                using (var wc = new WebClient())
                {
                    wc.Headers["User-Agent"] = "SmartOtomasyon/3.6";
                    wc.DownloadFile("https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe", tmp);
                }
                if (File.Exists(binPath)) File.Delete(binPath);
                File.Move(tmp, binPath);
                return File.Exists(binPath);
            }
            catch (Exception ex)
            {
                error = "Cloudflare tünel bileşeni indirilemedi: " + ex.Message;
                return false;
            }
        }

        public static bool StartTunnel(int port, out string url, out string error)
        {
            url = "";
            error = "";

            lock (_lock)
            {
                if (_proc != null && !_proc.HasExited && !string.IsNullOrEmpty(_publicUrl))
                {
                    url = _publicUrl;
                    return true;
                }
                StopInternal();
            }

            string binPath;
            if (!EnsureBinaryAvailable(out binPath, out error))
            {
                lock (_lock) { _lastError = error; }
                return false;
            }

            var readyEvent = new ManualResetEventSlim(false);
            string foundUrl = "";
            string capturedErr = "";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = binPath,
                    Arguments = "tunnel --url http://127.0.0.1:" + port + " --no-autoupdate",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                DataReceivedEventHandler onLine = (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    var m = Regex.Match(e.Data, @"https://[a-z0-9\-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);
                    if (m.Success && string.IsNullOrEmpty(foundUrl))
                    {
                        foundUrl = m.Value.Trim();
                        readyEvent.Set();
                    }
                    else if (e.Data.IndexOf("ERR", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        capturedErr = e.Data;
                    }
                };

                proc.ErrorDataReceived += onLine;
                proc.OutputDataReceived += onLine;
                proc.Start();
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();

                lock (_lock) { _proc = proc; }

                bool signaled = readyEvent.Wait(18000);
                if (signaled && !string.IsNullOrEmpty(foundUrl))
                {
                    Uri parsed;
                    if (Uri.TryCreate(foundUrl, UriKind.Absolute, out parsed))
                    {
                        Security.ActiveTunnelHost = parsed.Host;
                    }
                    lock (_lock)
                    {
                        _publicUrl = foundUrl + "/";
                        _lastError = "";
                        url = _publicUrl;
                    }
                    return true;
                }

                error = string.IsNullOrWhiteSpace(capturedErr)
                    ? "Güvenli HTTPS tünel linki 18 saniye içinde oluşturulamadı. İnternet bağlantınızı kontrol edin."
                    : capturedErr;
                lock (_lock)
                {
                    _lastError = error;
                    StopInternal();
                }
                return false;
            }
            catch (Exception ex)
            {
                error = "Tünel başlatılamadı: " + ex.Message;
                lock (_lock)
                {
                    _lastError = error;
                    StopInternal();
                }
                return false;
            }
        }

        public static void StopTunnel()
        {
            lock (_lock) { StopInternal(); }
        }

        private static void StopInternal()
        {
            Security.ActiveTunnelHost = "";
            _publicUrl = "";
            if (_proc != null)
            {
                try
                {
                    if (!_proc.HasExited) _proc.Kill();
                }
                catch { }
                try { _proc.Dispose(); } catch { }
                _proc = null;
            }
        }
    }
}
