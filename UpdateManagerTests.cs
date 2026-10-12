using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
namespace WorkLedger
{
    public static class UpdateManagerTests
    {
        private static void Check(bool passed, string reason) { if (!passed) throw new Exception("Update manager test: " + reason); }
        public static void Run(string path)
        {
            path = Path.Combine(path, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            string dataDir = Path.Combine(path, "data");
            Check(UpdateManager.ReadConfiguration(dataDir).Repository == "SCP701/Uchet", "GitHub source preconfigured without user setup");
            Check(UpdateManager.CacheRoot(dataDir) == UpdateManager.CacheRoot(dataDir + Path.DirectorySeparatorChar), "trailing directory slash uses same cache root");
            var store = new Store(dataDir); var settings = new Record("settings"); settings.Set("currency", "BYN"); store.Upsert(settings);
            var party = new Record("party"); party.Set("name", "Данные остаются на месте"); party.Set("role", "client"); store.Upsert(party);
            byte[] data = File.ReadAllBytes(store.DataPath);
            var config = new UpdateConfiguration { Repository = "https://github.com/example-owner/akti-oplaty-updates", LastCheckUtc = DateTime.UtcNow.ToString("o"), AvailableVersion = "" };
            UpdateManager.SaveConfiguration(dataDir, config);
            Check(UpdateManager.ReadConfiguration(dataDir).Repository == "example-owner/akti-oplaty-updates", "repository saved and normalized");
            Check(data.SequenceEqual(File.ReadAllBytes(store.DataPath)), "update source does not modify ledger");
            bool refused = false; try { UpdateManager.SaveConfiguration(dataDir, new UpdateConfiguration { Repository = "https://example.com/x/y" }); } catch { refused = true; }
            Check(refused && UpdateManager.ReadConfiguration(dataDir).Repository == "example-owner/akti-oplaty-updates", "invalid source preserves previous settings");
            File.WriteAllText(Path.Combine(UpdateManager.CacheRoot(dataDir), "source.json"), "{bad", Encoding.UTF8);
            Check(UpdateManager.ReadConfiguration(dataDir).Repository == "SCP701/Uchet", "corrupt update settings restore default source and leave ledger usable");
            Check(new Store(dataDir).Find(party.Id).Get("name") == "Данные остаются на месте", "data readable after corrupt settings");
            UpdateManager.CheckWritableDirectory(path);
            Check(!Directory.GetFiles(path, ".update-write-*").Any(), "write probe cleaned up");
            Check(UpdateManager.QuoteArgument(@"D:\Папка с пробелами\job.json") == "\"D:\\Папка с пробелами\\job.json\"", "path with spaces quoted");
            Check(UpdateManager.QuoteArgument("a\"b\\") == "\"a\\\"b\\\\\"", "quotes and trailing slash escaped");
            File.WriteAllText(Path.Combine(path, ".update-in-progress.json"), new JavaScriptSerializer().Serialize(new UpdateMarker { HelperPid = Int32.MaxValue, HelperStartUtcTicks = 0, TargetDir = path, SessionDir = path }), Encoding.UTF8);
            Check(!UpdateManager.IsActiveMarker(path), "stale updater marker does not block startup");
            using (var form = new UpdateWindow(store, delegate(bool value) { }, false))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(Path.Combine(path, "update-window.png")); }
                form.Close();
            }
            File.WriteAllText(Path.Combine(path, "PASS.txt"), "PASS: update source settings, ledger isolation, malformed settings, stale marker, quoting and UI.", Encoding.UTF8);
        }
    }
}

