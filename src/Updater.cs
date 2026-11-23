using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace QLock
{
    [DataContract]
    public class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "browser_download_url")] public string Url;
        [DataMember(Name = "size")] public long Size;
    }

    [DataContract]
    public class ReleaseInfo
    {
        [DataMember(Name = "tag_name")] public string Tag;
        [DataMember(Name = "body")] public string Body;
        [DataMember(Name = "html_url")] public string HtmlUrl;
        [DataMember(Name = "assets")] public List<ReleaseAsset> Assets;

        public ReleaseAsset Find(string name)
        {
            if (Assets == null) return null;
            for (int i = 0; i < Assets.Count; i++)
                if (string.Equals(Assets[i].Name, name, StringComparison.OrdinalIgnoreCase)) return Assets[i];
            return null;
        }
    }

    // 检查、下载、校验、原地替换。这里不碰 UI，进度和结果都通过回调交出去。
    public static class Updater
    {
        public const string ExeAssetName = "QLock.exe";
        public const string SumsAssetName = "SHA256SUMS.txt";
        const string DefaultApiUrl = "https://api.github.com/repos/haha0614/QLock-CSharp/releases/latest";
        const string DefaultSumsUrl = "https://github.com/haha0614/QLock-CSharp/releases/latest/download/SHA256SUMS.txt";
        const string UserAgent = "QLock-Updater";

        public static string ApiUrl()
        {
            string custom = Environment.GetEnvironmentVariable("QLOCK_UPDATE_URL");
            return string.IsNullOrEmpty(custom) ? DefaultApiUrl : custom;
        }

        public static Version CurrentVersion()
        {
            try
            {
                Version v = typeof(Updater).Assembly.GetName().Version;
                if (v != null) return v;
            }
            catch { }
            return new Version(0, 0, 0);
        }

        public static string VersionText(Version v)
        {
            if (v == null) return "0.0.0";
            return v.Major + "." + v.Minor + "." + Math.Max(v.Build, 0);
        }

        public static bool IsNewer(string tag, Version current)
        {
            Version remote;
            if (!TryParseTag(tag, out remote)) return false;
            return Normalize(remote) > Normalize(current);
        }

        public static bool TryParseTag(string tag, out Version value)
        {
            value = null;
            if (tag == null) return false;
            string text = tag.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            Version parsed;
            if (!Version.TryParse(text, out parsed)) return false;
            value = parsed;
            return true;
        }

        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }

        public static ReleaseInfo Check()
        {
            string json = GetText(ApiUrl(), 12000);
            DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(ReleaseInfo));
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (ReleaseInfo)ser.ReadObject(ms);
        }

        public static string ExeDownloadUrl(ReleaseInfo info)
        {
            ReleaseAsset asset = info.Find(ExeAssetName);
            if (asset != null && !string.IsNullOrEmpty(asset.Url)) return asset.Url;
            return "https://github.com/haha0614/QLock-CSharp/releases/latest/download/" + ExeAssetName;
        }

        public static string SumsDownloadUrl(ReleaseInfo info)
        {
            ReleaseAsset asset = info.Find(SumsAssetName);
            if (asset != null && !string.IsNullOrEmpty(asset.Url)) return asset.Url;
            return DefaultSumsUrl;
        }

        public static string DownloadDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "QLock-update");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void Download(string url, string dest, Action<long, long> progress)
        {
            WebRequest req = CreateRequest(url, 60000);
            using (WebResponse resp = req.GetResponse())
            using (Stream input = resp.GetResponseStream())
            using (FileStream output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long total = resp.ContentLength;
                byte[] buffer = new byte[65536];
                long got = 0;
                if (progress != null) progress(0, total);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    got += read;
                    if (progress != null) progress(got, total);
                }
            }
        }

        public static string ExpectedHash(string sumsText)
        {
            if (string.IsNullOrEmpty(sumsText)) return null;
            string[] lines = sumsText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length < 64) continue;
                if (line.IndexOf(ExeAssetName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string head = line.Substring(0, 64);
                bool hex = true;
                for (int c = 0; c < head.Length; c++)
                {
                    char ch = head[c];
                    bool digit = ch >= '0' && ch <= '9';
                    bool lower = ch >= 'a' && ch <= 'f';
                    bool upper = ch >= 'A' && ch <= 'F';
                    if (!digit && !lower && !upper) { hex = false; break; }
                }
                if (hex) return head.ToLowerInvariant();
            }
            return null;
        }

        public static string Sha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // 下载好的新 exe 就位：旧文件改名留底，新的写到原路径。启动由 StartInstalled 负责。
        public static void Install(string downloadedExe)
        {
            string exe = Autostart.ExePath();
            string backup = exe + ".old";
            try { if (File.Exists(backup)) File.Delete(backup); }
            catch (Exception ex) { Log.Write("update: cannot remove old backup: " + ex.Message); }

            bool movedOld = false;
            try
            {
                File.Move(exe, backup);
                movedOld = true;
                File.Move(downloadedExe, exe);
            }
            catch
            {
                if (movedOld)
                {
                    try { if (!File.Exists(exe)) File.Move(backup, exe); }
                    catch (Exception ex) { Log.Write("update: rollback failed: " + ex.Message); }
                }
                throw;
            }

        }

        // 新版本已经就位，但启动它要等旧进程放掉单实例锁，所以交给 Program 调用。
        public static void StartInstalled()
        {
            string exe = Autostart.ExePath();
            ProcessStartInfo psi = new ProcessStartInfo(exe, "--tray");
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            psi.UseShellExecute = false;
            Process.Start(psi);
        }

        public static string FetchText(string url)
        {
            return GetText(url, 12000);
        }

        public static void CleanupOld()
        {
            try
            {
                string backup = Autostart.ExePath() + ".old";
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch (Exception ex) { Log.Write("update: leftover backup kept: " + ex.Message); }
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "QLock-update");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }

        public static bool ShouldAutoCheck(AppConfig cfg)
        {
            if (cfg == null || cfg.LastUpdateCheckTicks <= 0) return true;
            try
            {
                DateTime last = new DateTime(cfg.LastUpdateCheckTicks, DateTimeKind.Utc);
                return DateTime.UtcNow - last >= TimeSpan.FromHours(24);
            }
            catch { return true; }
        }

        public static void MarkChecked(AppConfig cfg)
        {
            if (cfg == null) return;
            cfg.LastUpdateCheckTicks = DateTime.UtcNow.Ticks;
        }

        static WebRequest CreateRequest(string url, int timeoutMs)
        {
            try { ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12; }
            catch { }
            WebRequest req = WebRequest.Create(url);
            HttpWebRequest http = req as HttpWebRequest;
            if (http != null)
            {
                http.UserAgent = UserAgent;
                http.Accept = "application/vnd.github+json, application/octet-stream";
                http.Timeout = timeoutMs;
                http.ReadWriteTimeout = timeoutMs;
                http.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }
            return req;
        }

        static string GetText(string url, int timeoutMs)
        {
            WebRequest req = CreateRequest(url, timeoutMs);
            try
            {
                using (WebResponse resp = req.GetResponse())
                using (Stream stream = resp.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                throw new Exception(DescribeWebError(ex));
            }
        }

        public static string DescribeWebError(Exception ex)
        {
            WebException web = ex as WebException;
            if (web == null) return ex.Message;
            HttpWebResponse resp = web.Response as HttpWebResponse;
            if (resp != null)
            {
                int code = (int)resp.StatusCode;
                if (code == 403 || code == 429) return "GitHub 返回 " + code + "，可能是请求太频繁，稍后再试";
                if (code == 404) return "GitHub 返回 404，没有找到发布信息";
                return "GitHub 返回 " + code + " " + resp.StatusDescription;
            }
            if (web.Status == WebExceptionStatus.Timeout) return "连接超时，可能是网络不通（需要代理才能访问 GitHub）";
            if (web.Status == WebExceptionStatus.NameResolutionFailure) return "域名解析失败，检查网络或代理";
            return web.Message;
        }
    }
}
