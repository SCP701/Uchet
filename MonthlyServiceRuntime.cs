using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
namespace WorkLedger
{
    public static class MonthlyServiceRuntime
    {
        public static DateTime Today { get { return DateTime.UtcNow.AddHours(3).Date; } }
        public static string MutexName(string dataDir)
        {
            string normalized = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
            using (var hash = SHA256.Create())
                return "Local\\WorkLedger-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "");
        }
        public static void Log(string dataDir, string text)
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                string path = Path.Combine(dataDir, "monthly-service.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.UtcNow.AddHours(3).ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
        public static int RunBackground(string dataDir)
        {
            try
            {
                if (!File.Exists(Path.Combine(dataDir, "ledger.json"))) return 0;
                if (UpdateManager.IsActiveMarker(Path.GetDirectoryName(typeof(MonthlyServiceRuntime).Assembly.Location))) return 0;
                bool first;
                using (var mutex = new Mutex(true, MutexName(dataDir), out first))
                {
                    if (!first) return 0;
                    try
                    {
                        var store = new Store(dataDir);
                        var acts = store.GenerateMonthlyServiceActs(Today);
                        if (acts.Count > 0) Log(dataDir, "Создано актов обслуживания: " + acts.Count + ". Номера: " + String.Join(", ", acts.Select(a => a.Get("number"))));
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception e) { Log(dataDir, "Не удалось создать ежемесячные акты: " + e.Message); return 1; }
        }
    }
}
