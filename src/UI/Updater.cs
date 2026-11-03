using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace KeyboardWriterHelper
{
    // ---- Updates ----

    sealed class UpdateInfo
    {
        public string Version, Notes, ExeUrl, SumsUrl, PageUrl;
    }

    // Looks at the latest GitHub release and, when the user agrees, swaps the exe
    // for it. Only the release list and the one exe are fetched; nothing is sent
    // but the request itself. A download that does not match the published
    // SHA-256 is thrown away.
    static class Updater
    {
        static readonly string Latest = Environment.GetEnvironmentVariable("KWH_UPDATE_API") ?? "https://api.github.com/repos/yateshr/KeyboardWriterHelper/releases/latest";
        public const string ReleasesPage = "https://github.com/yateshr/KeyboardWriterHelper/releases";

        // "1.10 (rev 52)" -> "1.10"
        public static string Current()
        {
            Match m = Regex.Match(Program.Version, @"^\s*v?(\d+)\.(\d+)");
            return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value : "0.0";
        }

        // 1.10 is newer than 1.9, and 2.1 than 1.10.
        public static bool IsNewer(string candidate, string current)
        {
            int[] a = Parts(candidate), b = Parts(current);
            return a[0] != b[0] ? a[0] > b[0] : a[1] > b[1];
        }

        static int[] Parts(string v)
        {
            Match m = Regex.Match(v ?? "", @"^\s*v?(\d+)\.(\d+)");
            return m.Success ? new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value) } : new[] { 0, 0 };
        }

        static WebClient Client()
        {
            // GitHub needs TLS 1.2, which .NET Framework 4.0 does not offer by default.
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | ServicePointManager.SecurityProtocol;
            WebClient c = new WebClient();
            c.Headers[HttpRequestHeader.UserAgent] = "KeyboardWriterHelper/" + Current();
            c.Encoding = Encoding.UTF8;
            return c;
        }

        // The newer release, or null when this one is the latest. Throws when
        // GitHub cannot be reached.
        public static UpdateInfo Find()
        {
            string json;
            using (WebClient c = Client()) json = c.DownloadString(Latest);
            Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!tag.Success || !IsNewer(tag.Groups[1].Value, Current())) return null;

            // The build that runs on the .NET Framework, or the .NET 10 one if that is what runs now.
            string exeName = Environment.Version.Major >= 5 ? "KeyboardWriterHelper-net10-x64.exe" : "KeyboardWriterHelper-net40.exe";
            UpdateInfo info = new UpdateInfo { Version = tag.Groups[1].Value.TrimStart('v'), PageUrl = ReleasesPage };
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string url = m.Groups[1].Value;
                if (url.EndsWith("/" + exeName, StringComparison.OrdinalIgnoreCase)) info.ExeUrl = url;
                else if (url.EndsWith("/SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) info.SumsUrl = url;
            }
            Match body = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (body.Success)
                info.Notes = body.Groups[1].Value.Replace("\\r", "").Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\").Trim();
            // A release without the exe or its checksum cannot be installed here.
            return info.ExeUrl == null || info.SumsUrl == null ? null : info;
        }

        // Downloads, checks and swaps in the new exe. Null on success (the caller
        // then starts it and exits), otherwise what went wrong.
        public static string Install(UpdateInfo info, string exePath)
        {
            string fresh = exePath + ".update", old = exePath + ".old";
            try
            {
                using (WebClient c = Client())
                {
                    string sums = c.DownloadString(info.SumsUrl);
                    string name = Path.GetFileName(new Uri(info.ExeUrl).LocalPath);
                    Match line = Regex.Match(sums, @"^\s*([0-9A-Fa-f]{64})\s+\*?" + Regex.Escape(name) + @"\s*$", RegexOptions.Multiline);
                    if (!line.Success) return "The release has no checksum for " + name + ".";
                    c.DownloadFile(info.ExeUrl, fresh);
                    string actual;
                    using (SHA256 sha = SHA256.Create())
                    using (FileStream f = File.OpenRead(fresh))
                        actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
                    if (!string.Equals(actual, line.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(fresh);
                        return "The download did not match its checksum, so it was not installed.";
                    }
                }
                // Windows lets a running exe be renamed, not overwritten.
                if (File.Exists(old)) File.Delete(old);
                File.Move(exePath, old);
                try { File.Move(fresh, exePath); }
                catch { File.Move(old, exePath); throw; }
                return null;
            }
            catch (UnauthorizedAccessException) { return "This folder is protected. Download the new version from " + ReleasesPage; }
            catch (Exception ex)
            {
                try { if (File.Exists(fresh)) File.Delete(fresh); } catch (IOException) { }
                return ex.Message;
            }
        }

        // The new exe, told to wait for this one to finish.
        public static void Restart(string exePath)
        {
            Process.Start(new ProcessStartInfo(exePath, "--after-update") { UseShellExecute = false });
        }

        // The old exe left by the last update.
        public static void CleanUp()
        {
            try { File.Delete(Application.ExecutablePath + ".old"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
