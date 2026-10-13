using System;
using System.Drawing;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        private static IEnumerable<Control> ControlsOf(Control parent)
        {
            yield return parent;
            foreach (Control child in parent.Controls) foreach (Control nested in ControlsOf(child)) yield return nested;
        }
        private static void Wait(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
            Check(task.IsCompleted, "background check completed without blocking UI"); task.GetAwaiter().GetResult();
        }
        private static ReleaseUpdate NewRelease(string version)
        { return new ReleaseUpdate { Manifest = new UpdateManifest { Version = version }, Notes = "Исправления и улучшения программы." }; }
        private static void StartupChecks(Store store)
        {
            string current = UpdateManager.CurrentVersion;
            var installed = new Version(current);
            string next = new Version(installed.Major, installed.Minor, installed.Build + 1).ToString(3);
            var values = new List<bool>(); int calls = 0;
            string recent = DateTime.UtcNow.ToString("o");
            UpdateManager.SaveConfiguration(store.DataDir, new UpdateConfiguration { Repository = UpdateDefaults.Repository, LastCheckUtc = recent });
            using (var owner = new Form { ShowInTaskbar = false, Opacity = 0 })
            {
                owner.Show(); Application.DoEvents();
                using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false))
                {
                    Task pending = UpdateManager.CheckInBackgroundAsync(owner, store, values.Add, delegate(string repo, string version)
                    {
                        Interlocked.Increment(ref calls); entered.Set();
                        if (!release.WaitOne(10000)) throw new TimeoutException();
                        if (repo != UpdateDefaults.Repository || version != current) throw new Exception("wrong request parameters");
                        return NewRelease(next);
                    });
                    try
                    {
                        Check(entered.WaitOne(3000) && !pending.IsCompleted, "check starts even with a recent successful check and runs in background");
                        Check(values.SequenceEqual(new[] { false }) && owner.Enabled, "accounting window stays usable while checking");
                    }
                    finally { release.Set(); }
                    Wait(pending);
                }
                Check(calls == 1 && values.Last(), "new release notifies the open window");
                Check(UpdateManager.ReadConfiguration(store.DataDir).AvailableVersion == next, "successful check caches newer version");
                var config = UpdateManager.ReadConfiguration(store.DataDir); config.LastCheckUtc = DateTime.UtcNow.AddDays(2).ToString("o");
                UpdateManager.SaveConfiguration(store.DataDir, config); values.Clear();
                Wait(UpdateManager.CheckInBackgroundAsync(owner, store, values.Add, delegate(string repo, string version) { calls++; return null; }));
                Check(calls == 2 && values.SequenceEqual(new[] { true, false }), "next launch checks again even with future timestamp and clears obsolete notice");
                Check(UpdateManager.ReadConfiguration(store.DataDir).AvailableVersion == "", "no new version clears cache");
                config.AvailableVersion = next; config.LastCheckUtc = recent; UpdateManager.SaveConfiguration(store.DataDir, config); values.Clear();
                Wait(UpdateManager.CheckInBackgroundAsync(owner, store, values.Add, delegate(string repo, string version) { calls++; throw new System.Net.WebException("offline"); }));
                Check(values.SequenceEqual(new[] { true }), "offline startup keeps known available version");
                config = UpdateManager.ReadConfiguration(store.DataDir);
                Check(config.AvailableVersion == next && config.LastCheckUtc == recent, "failed check does not claim success or lose cache");
                foreach (string obsolete in new[] { "invalid", current, "0.0.1" })
                {
                    config.AvailableVersion = obsolete; UpdateManager.SaveConfiguration(store.DataDir, config); values.Clear();
                    Wait(UpdateManager.CheckInBackgroundAsync(owner, store, values.Add, delegate(string repo, string version) { throw new IOException("offline"); }));
                    Check(values.SequenceEqual(new[] { false }), "malformed, installed and older cached versions do not show an update");
                }
                config.AvailableVersion = ""; config.LastCheckUtc = recent; UpdateManager.SaveConfiguration(store.DataDir, config); values.Clear();
                Wait(UpdateManager.CheckInBackgroundAsync(owner, store, values.Add, delegate(string repo, string version)
                { var result = NewRelease(next); result.Manifest.MinDataVersion = result.Manifest.MaxDataVersion = 2; return result; }));
                config = UpdateManager.ReadConfiguration(store.DataDir);
                Check(!values.Last() && config.AvailableVersion == "" && config.LastCheckUtc == recent, "incompatible release preserves update settings");
                values.Clear();
                Wait(UpdateManager.CheckInBackgroundAsync(owner, store, values.Add, delegate(string repo, string version)
                {
                    UpdateManager.SaveConfiguration(store.DataDir, new UpdateConfiguration { Repository = "example-owner/other-updates", AvailableVersion = "" });
                    return NewRelease(next);
                }));
                Check(values.SequenceEqual(new[] { false }) && UpdateManager.ReadConfiguration(store.DataDir).Repository == "example-owner/other-updates", "old source result cannot overwrite changed source");
                using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false))
                {
                    values.Clear();
                    Task pending = UpdateManager.CheckInBackgroundAsync(owner, store, values.Add, delegate(string repo, string version)
                    { entered.Set(); release.WaitOne(10000); return NewRelease(next); });
                    try { Check(entered.WaitOne(3000), "closing check started"); owner.Close(); }
                    finally { release.Set(); }
                    Wait(pending);
                    Check(values.SequenceEqual(new[] { false }) && UpdateManager.ReadConfiguration(store.DataDir).AvailableVersion == "", "closing application does not receive a late notice or save stale result");
                }
            }
            UpdateManager.SaveConfiguration(store.DataDir, new UpdateConfiguration { Repository = "" });
            Check(UpdateManager.ReadConfiguration(store.DataDir).Repository == UpdateDefaults.Repository, "legacy empty source uses built-in update channel");
        }
        private static void NoticeChecks(Store store, string path)
        {
            var preferences = new UiPreferences { AnimationsEnabled = false }; preferences.SetVisible("updates", false); preferences.Save(store.DataDir);
            using (var main = new MainForm(store))
            {
                main.ShowInTaskbar = false; main.Opacity = 0; main.Show(); main.Navigate("act"); Application.DoEvents();
                var notice = ControlsOf(main).OfType<Button>().Single(b => b.Name == "updateAvailable");
                var menu = main.Controls.Find("programUtilities", true).Single();
                Check(!notice.Visible, "normal header has no update notice");
                foreach (Size size in new[] { new Size(1440, 900), new Size(1120, 680) })
                {
                    main.Size = size; main.SetUpdateAvailable(true); Application.DoEvents();
                    Check(notice.Visible && notice.Enabled && notice.Text == "Доступно обновление", "notice remains reachable with hidden sidebar entry");
                    Check(notice.Left >= 0 && notice.Right <= menu.Left && notice.Bottom <= notice.Parent.ClientSize.Height, "notice fits compact header");
                    Check(CompactToolbarTests.CanReachCommand(main, "updates"), "updates remain available in the global menu");
                    Check(notice.ContextMenuStrip == null && ControlsOf(main).OfType<Button>().Any(b => b.Visible && b.AccessibleName == "Доступно обновление — открыть обновления"), "notice is an accessible direct action");
                    using (var bitmap = new Bitmap(main.Width, main.Height))
                    { main.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(path, "update-notice-" + size.Width + ".png")); }
                    main.Navigate("reports"); Application.DoEvents(); Check(notice.Visible, "navigation preserves available update notice");
                    main.SetUpdateAvailable(false); Check(!notice.Visible, "notice disappears after installing current version"); main.Navigate("act");
                }
                main.Close();
            }
        }
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
            StartupChecks(store);
            NoticeChecks(store, path);
            using (var form = new UpdateWindow(store, delegate(bool value) { }, false))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
                var visible = ControlsOf(form).Where(c => c.Visible).ToList();
                Check(!visible.OfType<LinkLabel>().Any() && !visible.OfType<TextBox>().Any(t => !t.ReadOnly), "update source and external release links are hidden");
                Check(!visible.Any(c => c.Text.Contains("github.com") || c.Text.Contains("GitHub") || c.Text.Contains("SCP701/Uchet")), "ordinary update window does not expose repository address");
                Check(visible.OfType<Button>().Any(b => b.Text == "Проверить обновления" && b.Enabled) && visible.OfType<Button>().Any(b => b.Text == "Обновить" && !b.Enabled), "manual checking remains available and installation waits for verified release");
                Check(visible.Where(c => c != form).All(c => c.Left >= 0 && c.Right <= form.ClientSize.Width && c.Bottom <= form.ClientSize.Height), "update window controls fit without clipping");
                using (var bitmap = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(Path.Combine(path, "update-window.png")); }
                form.Close();
            }
            Check(data.SequenceEqual(File.ReadAllBytes(store.DataPath)), "startup and notice checks preserve accounting data");
            File.WriteAllText(Path.Combine(path, "PASS.txt"), "PASS: update source settings, ledger isolation, malformed settings, stale marker, quoting, every-launch background checks, offline cache, late results, hidden source links and responsive update notice.", Encoding.UTF8);
        }
    }
}

