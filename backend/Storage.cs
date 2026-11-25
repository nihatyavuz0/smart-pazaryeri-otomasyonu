using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SmartMarketplace
{
    public static class Storage
    {
        private static readonly object _lock = new object();
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
        private static long _dataVersionCounter = DateTime.UtcNow.Ticks;
        public static string DataDir { get; private set; }
        public static string UploadsDir { get; private set; }
        public static string DbPath { get; private set; }
        public static string BackupPath { get; private set; }

        public static string GetDataVersion()
        {
            return System.Threading.Interlocked.Read(ref _dataVersionCounter).ToString("x");
        }

        public static void Initialize(string rootDir)
        {
            DataDir = Path.Combine(rootDir, "data");
            UploadsDir = Path.Combine(DataDir, "uploads");
            DbPath = Path.Combine(DataDir, "smart_db.json");
            BackupPath = Path.Combine(DataDir, "smart_db.backup.json");

            if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
            if (!Directory.Exists(UploadsDir)) Directory.CreateDirectory(UploadsDir);

            if (!File.Exists(DbPath))
            {
                if (File.Exists(BackupPath))
                {
                    File.Copy(BackupPath, DbPath, true);
                }
                else
                {
                    var db = CreateInitialDatabase();
                    Save(db);
                }
            }
            else
            {
                // Migrate any existing plaintext secrets to DPAPI AND auto-enable Live Mode where real API credentials exist
                var existing = Load();
                foreach (var mp in existing.Marketplaces)
                {
                    if (mp.SimulationMode && MarketplaceConnectors.HasRequiredCredentials(mp))
                    {
                        mp.SimulationMode = false;
                        mp.StatusMessage = "Canlı API Modu otomatik etkinleştirildi — Gerçek hesabınıza yayınlamaya hazır";
                        AddLog(existing, "api", "info", mp.Name, mp.Name + " API anahtarları algılandı ve Canlı API Modu otomatik aktif edildi.");
                    }
                }
                Save(existing);
            }
        }

        public static AppDatabase Load()
        {
            lock (_lock)
            {
                AppDatabase db = TryReadFile(DbPath);
                if (db == null && File.Exists(BackupPath))
                {
                    db = TryReadFile(BackupPath);
                    if (db != null) SaveInternal(db);
                }
                if (db == null)
                {
                    db = CreateInitialDatabase();
                    SaveInternal(db);
                }

                EnsureDefaults(db);
                Security.DecryptDatabaseSecrets(db);
                return db;
            }
        }

        private static AppDatabase TryReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string raw = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                return _json.Deserialize<AppDatabase>(raw);
            }
            catch
            {
                return null;
            }
        }

        public static void Save(AppDatabase db)
        {
            lock (_lock)
            {
                EnsureDefaults(db);
                SaveInternal(db);
            }
        }

        public static string ExportSafeBackupJson()
        {
            lock (_lock)
            {
                var db = Load();
                // Strip raw secrets from exported backup JSON so sharing a backup never exposes API keys
                var safeClone = Security.CloneWithMaskedSecrets(db, true);
                return _json.Serialize(safeClone);
            }
        }

        public static AppDatabase ImportRawJson(string rawJson)
        {
            lock (_lock)
            {
                var current = Load();
                var imported = _json.Deserialize<AppDatabase>(rawJson);
                if (imported == null) throw new Exception("Geçersiz yedek dosyası formatı.");
                EnsureDefaults(imported);
                Security.SanitizeImportedDatabase(imported);

                // Preserve existing secrets if backup had stripped/masked secrets
                if (string.IsNullOrWhiteSpace(imported.AiConfig.GeminiApiKey) || Security.IsMaskedPlaceholder(imported.AiConfig.GeminiApiKey))
                    imported.AiConfig.GeminiApiKey = current.AiConfig.GeminiApiKey;
                if (string.IsNullOrWhiteSpace(imported.AiConfig.OpenAiApiKey) || Security.IsMaskedPlaceholder(imported.AiConfig.OpenAiApiKey))
                    imported.AiConfig.OpenAiApiKey = current.AiConfig.OpenAiApiKey;

                foreach (var impMp in imported.Marketplaces)
                {
                    var curMp = current.Marketplaces.Find(m => m.Id == impMp.Id);
                    if (curMp == null || impMp.Credentials == null || curMp.Credentials == null) continue;
                    var keys = new List<string>(impMp.Credentials.Keys);
                    foreach (var k in keys)
                    {
                        if (Security.IsSecretKeyName(k))
                        {
                            string v = impMp.Credentials[k];
                            if (string.IsNullOrWhiteSpace(v) || Security.IsMaskedPlaceholder(v))
                            {
                                if (curMp.Credentials.ContainsKey(k))
                                    impMp.Credentials[k] = curMp.Credentials[k];
                            }
                        }
                    }
                }

                AddLog(imported, "system", "success", "Smart", "Yedek dosyası doğrulandı ve güvenle geri yüklendi.");
                SaveInternal(imported);
                return imported;
            }
        }

        private static void SaveInternal(AppDatabase db)
        {
            if (db.Logs != null && db.Logs.Count > 300)
            {
                db.Logs = db.Logs.GetRange(0, 300);
            }

            // Serialize an encrypted copy so in-memory object stays decrypted while disk is 100% DPAPI-encrypted
            var diskCopy = _json.Deserialize<AppDatabase>(_json.Serialize(db));
            Security.EncryptDatabaseSecrets(diskCopy);

            string json = _json.Serialize(diskCopy);
            string tmp = DbPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));

            if (File.Exists(DbPath))
            {
                try { File.Copy(DbPath, BackupPath, true); } catch { }
                File.Delete(DbPath);
            }
            File.Move(tmp, DbPath);
            System.Threading.Interlocked.Increment(ref _dataVersionCounter);
        }

        public static void AddLog(AppDatabase db, string type, string level, string marketplace, string message, string details = "")
        {
            db.Logs.Insert(0, new ActivityLog
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Type = type,
                Level = level,
                Marketplace = marketplace ?? "Smart",
                Message = Security.RedactSensitiveText(message ?? ""),
                Details = Security.RedactSensitiveText(details ?? "")
            });
            if (db.Logs.Count > 300)
            {
                db.Logs.RemoveRange(300, db.Logs.Count - 300);
            }
        }

        public static string SaveBase64Image(string fileName, string base64Data)
        {
            if (string.IsNullOrWhiteSpace(base64Data))
                throw new Exception("Görsel verisi boş.");
            if (base64Data.Length > 7 * 1024 * 1024)
                throw new Exception("Görsel boyutu izin verilen sınırı (5 MB) aşıyor.");

            string cleanBase64 = base64Data;
            if (cleanBase64.Contains(","))
            {
                cleanBase64 = cleanBase64.Substring(cleanBase64.IndexOf(",") + 1);
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(cleanBase64);
            }
            catch
            {
                throw new Exception("Geçersiz görsel verisi.");
            }

            string safeExt;
            string validationError;
            if (!Security.ValidateImageBytes(bytes, out safeExt, out validationError))
            {
                throw new Exception(validationError);
            }

            string safeName = "img_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Security.GenerateSecureToken(6) + safeExt;
            string fullPath;
            if (!Security.TryResolveSafePath(UploadsDir, safeName, out fullPath))
                throw new Exception("Güvenli dosya yolu oluşturulamadı.");

            File.WriteAllBytes(fullPath, bytes);
            return "/uploads/" + safeName;
        }

        public static string Serialize(object obj)
        {
            return _json.Serialize(obj);
        }

        public static T Deserialize<T>(string json)
        {
            return _json.Deserialize<T>(json);
        }

        private static AppDatabase CreateInitialDatabase()
        {
            var db = new AppDatabase();
            EnsureDefaults(db);
            AddLog(db, "system", "info", "Smart", "Smart Pazaryeri Otomasyon Sistemi güvenlik kalkanı ile başlatıldı.", "API anahtarları Windows DPAPI ile şifrelenir; tüm ayarlarınız ve ürünleriniz kalıcı saklanır.");
            return db;
        }

        private static void EnsureDefaults(AppDatabase db)
        {
            if (db.AiConfig == null) db.AiConfig = new AiSettings();
            if (db.SyncConfig == null) db.SyncConfig = new SyncSettings();
            if (db.RemoteConfig == null) db.RemoteConfig = new RemoteAccessSettings();
            if (string.IsNullOrWhiteSpace(db.RemoteConfig.AccessPin)) db.RemoteConfig.AccessPin = Security.Generate6DigitPin();
            if (db.Draft == null) db.Draft = new DraftWorkspace();
            if (db.Draft.UploadedImages == null) db.Draft.UploadedImages = new List<string>();
            if (db.Draft.SelectedTargetMps == null) db.Draft.SelectedTargetMps = new List<string>();
            if (db.Products == null) db.Products = new List<ProductRecord>();
            if (db.Logs == null) db.Logs = new List<ActivityLog>();
            EnsureAllMarketplaces(db);
        }

        private static void EnsureAllMarketplaces(AppDatabase db)
        {
            if (db.Marketplaces == null) db.Marketplaces = new List<MarketplaceConfig>();
            var defaults = MarketplaceSchema.GetDefaultConfigs();
            foreach (var def in defaults)
            {
                var existing = db.Marketplaces.Find(m => m.Id == def.Id);
                if (existing == null)
                {
                    db.Marketplaces.Add(def);
                }
                else
                {
                    if (existing.Credentials == null) existing.Credentials = new Dictionary<string, string>();
                    foreach (var kv in def.Credentials)
                    {
                        if (!existing.Credentials.ContainsKey(kv.Key))
                            existing.Credentials[kv.Key] = kv.Value;
                    }
                }
            }
        }
    }
}
