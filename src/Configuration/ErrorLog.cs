using System;
using System.IO;
using System.Text;

namespace KeyboardWriterHelper
{
    // ---- Error log ----

    // Problems the app caught are written here, so there is something to look at
    // (Settings > Diagnostics > Open error log) when something does not work.
    // Only the kind of error, its message and where in the code it happened:
    // never what was typed. The file stays small: the older half is dropped
    // when it passes 256 KB.
    static class ErrorLog
    {
        const int MaxBytes = 256 * 1024;
        static readonly object gate = new object();
        static string path;

        public static string Path { get { return path; } }

        public static void Init(string folder)
        {
            path = System.IO.Path.Combine(folder, "errors.log");
        }

        public static void Write(Exception ex)
        {
            if (path == null || ex == null) return;
            lock (gate)
            {
                try
                {
                    StringBuilder text = new StringBuilder();
                    text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ")
                        .Append(Program.Version).Append("  ")
                        .Append(ex.GetType().Name).Append(": ").AppendLine(ex.Message.Trim());
                    string[] lines = (ex.StackTrace ?? "").Split('\n');
                    for (int i = 0; i < lines.Length && i < 6; i++)
                        if (lines[i].Trim().Length > 0) text.Append("    ").AppendLine(lines[i].Trim());
                    Trim();
                    File.AppendAllText(path, text.ToString(), Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        static void Trim()
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            string[] lines = File.ReadAllLines(path);
            int keep = lines.Length / 2;
            string[] rest = new string[lines.Length - keep];
            Array.Copy(lines, keep, rest, 0, rest.Length);
            File.WriteAllLines(path, rest, Encoding.UTF8);
        }
    }
}
