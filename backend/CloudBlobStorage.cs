using System;
using System.IO;
using System.Net;
using System.Text;

namespace SmartMarketplace
{
    /// <summary>
    /// Lightweight zero-dependency REST client for Vercel Private Blob storage.
    /// Active only when BLOB_READ_WRITE_TOKEN is present in the environment (Vercel cloud deployments).
    /// Local Windows Smart.bat runs remain 100% local-disk with zero network calls.
    /// </summary>
    public static class CloudBlobStorage
    {
        private const string BlobApiVersion = "12";
        private static readonly string _token;
        private static readonly string _storeId;
        private static readonly string _apiBaseUrl;

        public static bool IsConfigured
        {
            get { return !string.IsNullOrEmpty(_token) && !string.IsNullOrEmpty(_storeId); }
        }

        public static string StoreId
        {
            get { return _storeId ?? ""; }
        }

        static CloudBlobStorage()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            _token = (Environment.GetEnvironmentVariable("BLOB_READ_WRITE_TOKEN") ?? "").Trim();
            _apiBaseUrl = (Environment.GetEnvironmentVariable("VERCEL_BLOB_API_URL") ?? "https://blob.vercel-storage.com").Trim().TrimEnd('/');

            string explicitStore = (Environment.GetEnvironmentVariable("BLOB_STORE_ID") ?? "").Trim();
            if (!string.IsNullOrEmpty(explicitStore))
            {
                if (explicitStore.StartsWith("store_", StringComparison.OrdinalIgnoreCase))
                    explicitStore = explicitStore.Substring(6);
                _storeId = explicitStore.ToLowerInvariant();
            }
            else if (!string.IsNullOrEmpty(_token))
            {
                // Format: vercel_blob_rw_<storeId>_<randomSecret>
                var parts = _token.Split('_');
                if (parts.Length >= 4 && parts[0] == "vercel" && parts[1] == "blob")
                {
                    _storeId = parts[3].ToLowerInvariant();
                }
            }
        }

        private static string BuildPrivateObjectUrl(string pathname, bool bypassCache)
        {
            string clean = (pathname ?? "").TrimStart('/');
            string url = "https://" + _storeId + ".private.blob.vercel-storage.com/" + clean;
            if (bypassCache) url += "?cache=0";
            return url;
        }

        public static byte[] GetBytes(string pathname, bool bypassCache, out string etag)
        {
            etag = null;
            if (!IsConfigured) return null;

            string url = BuildPrivateObjectUrl(pathname, bypassCache);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.Headers["Authorization"] = "Bearer " + _token;
                req.Headers["x-api-version"] = BlobApiVersion;
                if (bypassCache)
                {
                    req.Headers["Cache-Control"] = "no-cache, no-store";
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    etag = resp.Headers["ETag"] ?? resp.Headers["Last-Modified"];
                    using (var s = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        if (s != null) s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch (WebException wex)
            {
                var hresp = wex.Response as HttpWebResponse;
                if (hresp != null && hresp.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                Console.WriteLine("[CloudBlob] GET hata (" + pathname + "): " + wex.Message);
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[CloudBlob] GET hata (" + pathname + "): " + ex.Message);
                return null;
            }
        }

        public static string GetText(string pathname, bool bypassCache, out string etag)
        {
            byte[] bytes = GetBytes(pathname, bypassCache, out etag);
            if (bytes == null || bytes.Length == 0) return null;
            return Encoding.UTF8.GetString(bytes);
        }

        public static string HeadEtag(string pathname)
        {
            if (!IsConfigured) return null;

            string url = BuildPrivateObjectUrl(pathname, true);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                req.Timeout = 6000;
                req.ReadWriteTimeout = 6000;
                req.Headers["Authorization"] = "Bearer " + _token;
                req.Headers["x-api-version"] = BlobApiVersion;
                req.Headers["Cache-Control"] = "no-cache, no-store";

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    return resp.Headers["ETag"] ?? resp.Headers["Last-Modified"];
                }
            }
            catch
            {
                return null;
            }
        }

        public static bool PutBytes(string pathname, byte[] data, string contentType, int cacheMaxAgeSeconds, out string error)
        {
            error = "";
            if (!IsConfigured)
            {
                error = "CloudBlobStorage yapılandırılmadı.";
                return false;
            }
            if (data == null) data = new byte[0];

            string clean = (pathname ?? "").TrimStart('/');
            string url = _apiBaseUrl + "/" + clean + "?pathname=" + Uri.EscapeDataString(clean);

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "PUT";
                req.Timeout = 25000;
                req.ReadWriteTimeout = 25000;
                req.ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
                req.ContentLength = data.Length;
                req.Headers["Authorization"] = "Bearer " + _token;
                req.Headers["x-api-version"] = BlobApiVersion;
                req.Headers["x-vercel-blob-access"] = "private";
                req.Headers["x-add-random-suffix"] = "0";
                req.Headers["x-allow-overwrite"] = "1";
                req.Headers["x-cache-control-max-age"] = Math.Max(0, cacheMaxAgeSeconds).ToString();

                using (var s = req.GetRequestStream())
                {
                    s.Write(data, 0, data.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
                }
            }
            catch (WebException wex)
            {
                string detail = wex.Message;
                try
                {
                    var hr = wex.Response as HttpWebResponse;
                    if (hr != null)
                    {
                        using (var sr = new StreamReader(hr.GetResponseStream(), Encoding.UTF8))
                            detail = ((int)hr.StatusCode) + " " + sr.ReadToEnd();
                    }
                }
                catch { }
                error = detail;
                Console.WriteLine("[CloudBlob] PUT hata (" + pathname + "): " + detail);
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Console.WriteLine("[CloudBlob] PUT hata (" + pathname + "): " + ex.Message);
                return false;
            }
        }

        public static bool PutText(string pathname, string text, string contentType, out string error)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            return PutBytes(pathname, bytes, contentType, 0, out error);
        }
    }
}
