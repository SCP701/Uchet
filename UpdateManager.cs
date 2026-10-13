using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
namespace WorkLedger
{
    public sealed class UpdateConfiguration
    {
        public string Repository { get; set; }
        public string LastCheckUtc { get; set; }
        public string AvailableVersion { get; set; }
    }
    public static class UpdateManager
    {
        public static string CurrentVersion { get { return typeof(Program).Assembly.GetName().Version.ToString(); } }
        public static string CacheRoot(string dataDir)
        {
            string canonical = Path.GetFullPath(dataDir); if (canonical.Length > Path.GetPathRoot(canonical).Length) canonical = canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(canonical), "WorkLedgerUpdates"));
        }
        public static UpdateConfiguration ReadConfiguration(string dataDir)
        {
            string path = Path.Combine(CacheRoot(dataDir), "source.json");
            if (!File.Exists(path)) return new UpdateConfiguration { Repository = UpdateDefaults.Repository };
            try
            {
                if (new FileInfo(path).Length > 8192) throw new InvalidDataException();
                var config = new JavaScriptSerializer().Deserialize<UpdateConfiguration>(File.ReadAllText(path, Encoding.UTF8));
                if (config == null) return new UpdateConfiguration { Repository = UpdateDefaults.Repository };
                config.Repository = String.IsNullOrWhiteSpace(config.Repository) ? UpdateDefaults.Repository : UpdateProtocol.NormalizeRepository(config.Repository);
                return config;
            }
            catch { return new UpdateConfiguration { Repository = UpdateDefaults.Repository }; }
        }
        public static void SaveConfiguration(string dataDir, UpdateConfiguration config)
        {
            if (!String.IsNullOrEmpty(config.Repository)) config.Repository = UpdateProtocol.NormalizeRepository(config.Repository);
            string root = CacheRoot(dataDir); Directory.CreateDirectory(root);
            string path = Path.Combine(root, "source.json"), temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(config), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static async void CheckInBackground(Form owner, Store store, Action<bool> notify)
        {
            await CheckInBackgroundAsync(owner, store, notify, delegate(string repo, string version) { return new GitHubUpdateClient().Check(repo, version); });
        }
        internal static bool HasNewerVersion(string version)
        {
            Version candidate;
            return Version.TryParse(version, out candidate) && candidate > new Version(CurrentVersion);
        }
        internal static async Task CheckInBackgroundAsync(Form owner, Store store, Action<bool> notify, Func<string, string, ReleaseUpdate> check)
        {
            try
            {
                if (owner.IsDisposed || owner.Disposing) return;
                var config = ReadConfiguration(store.DataDir);
                notify(HasNewerVersion(config.AvailableVersion));
                // Each application launch checks again, including after an offline launch.
                // The cached notice stays visible while the network request runs.
                var update = await Task.Run(delegate { return check(config.Repository, CurrentVersion); });
                if (owner.IsDisposed || owner.Disposing) return;
                if (!SaveCheckResult(store.DataDir, config.Repository, update)) return;
                notify(update != null && HasNewerVersion(update.Manifest.Version));
            }
            catch { /* An offline accounting session keeps its cached notice and remains usable. */ }
        }
        internal static bool SaveCheckResult(string dataDir, string repository, ReleaseUpdate update)
        {
            var config = ReadConfiguration(dataDir);
            if (config.Repository != repository) return false;
            if (update != null && (update.Manifest.MinDataVersion > 1 || update.Manifest.MaxDataVersion < 1))
                throw new InvalidDataException("Этот выпуск использует другой формат базы. Для него требуется отдельный перенос данных.");
            config.LastCheckUtc = DateTime.UtcNow.ToString("o");
            config.AvailableVersion = update != null && HasNewerVersion(update.Manifest.Version) ? update.Manifest.Version : "";
            SaveConfiguration(dataDir, config);
            return true;
        }
        public static void Show(Form owner, Store store, Action<bool> notify)
        {
            using (var window = new UpdateWindow(store, notify))
            {
                window.ShowDialog(owner);
                if (window.ReadyToExit) { owner.Close(); Application.Exit(); }
            }
        }
        public static string QuoteArgument(string text)
        {
            if (text == null || text.IndexOf('\0') >= 0) throw new ArgumentException("Некорректный параметр запуска.");
            var result = new StringBuilder("\""); int backslashes = 0;
            foreach (char c in text)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { result.Append('\\', backslashes * 2 + 1); result.Append(c); backslashes = 0; continue; }
                result.Append('\\', backslashes); result.Append(c); backslashes = 0;
            }
            result.Append('\\', backslashes * 2); result.Append('"'); return result.ToString();
        }
        public static void CheckWritableDirectory(string directory)
        {
            directory = Path.GetFullPath(directory);
            string probe = Path.Combine(directory, ".update-write-" + Guid.NewGuid().ToString("N"));
            try { using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.WriteByte(1); }
            catch (Exception ex) { throw new IOException("Нет доступа для обновления программы в этой папке. Переместите папку программы в «Документы» или на рабочий стол.", ex); }
            finally { if (File.Exists(probe)) File.Delete(probe); }
        }
        public static UpdateJob CreateJob(string sessionDir, Store store, UpdateManifest manifest)
        {
            var process = Process.GetCurrentProcess();
            var job = new UpdateJob
            {
                ParentPid = process.Id, ParentStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                TargetDir = Path.GetDirectoryName(Application.ExecutablePath), DataDir = store.DataDir,
                SessionDir = Path.GetFullPath(sessionDir), Version = manifest.Version,
                Nonce = Path.GetFileName(sessionDir)
            };
            return job;
        }
        public static bool IsActiveMarker(string targetDir)
        {
            string path = Path.Combine(targetDir, ".update-in-progress.json");
            if (!File.Exists(path)) return false;
            try
            {
                if (new FileInfo(path).Length > 16384) return false;
                var marker = new JavaScriptSerializer().Deserialize<UpdateMarker>(File.ReadAllText(path, Encoding.UTF8));
                if (marker == null || !SamePath(marker.TargetDir, targetDir)) return false;
                using (var process = Process.GetProcessById(marker.HelperPid))
                    return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == marker.HelperStartUtcTicks && SamePath(process.MainModule.FileName, Path.Combine(marker.SessionDir, "Updater.exe"));
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }
            catch { return false; }
        }
        private static bool SamePath(string a, string b)
        {
            return !String.IsNullOrEmpty(a) && !String.IsNullOrEmpty(b) && String.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        public static UpdateJob ValidateStartupJob(string jobPath, string dataDir)
        {
            var job = UpdateProtocol.ReadJob(jobPath);
            Guid nonce;
            if (job == null || !Guid.TryParseExact(job.Nonce, "N", out nonce)) throw new InvalidDataException("Некорректный запуск после обновления.");
            string expectedSession = Path.Combine(CacheRoot(dataDir), "jobs", job.Nonce);
            if (!SamePath(job.SessionDir, expectedSession) || !SamePath(jobPath, Path.Combine(expectedSession, "job.json")) || !SamePath(job.DataDir, dataDir) || !SamePath(job.TargetDir, Path.GetDirectoryName(Application.ExecutablePath)))
                throw new InvalidDataException("Параметры обновления не соответствуют этой программе и базе.");
            if (!IsActiveMarker(job.TargetDir)) throw new InvalidDataException("Обновление не было запущено модулем обновления.");
            var marker = new JavaScriptSerializer().Deserialize<UpdateMarker>(File.ReadAllText(Path.Combine(job.TargetDir, ".update-in-progress.json"), Encoding.UTF8));
            if (marker.Nonce != job.Nonce || marker.Version != job.Version || !SamePath(marker.DataDir, dataDir) || !SamePath(marker.SessionDir, job.SessionDir))
                throw new InvalidDataException("Не совпадают параметры обновления.");
            var manifest = UpdateProtocol.VerifyManifest(File.ReadAllBytes(Path.Combine(expectedSession, "manifest.json")), File.ReadAllBytes(Path.Combine(expectedSession, "manifest.sig")));
            if (manifest.Version != job.Version || job.Version != CurrentVersion || manifest.MinDataVersion > 1 || manifest.MaxDataVersion < 1)
                throw new InvalidDataException("Версия программы или базы не соответствует обновлению.");
            return job;
        }
    }
    public sealed class UpdateWindow : Form
    {
        private readonly Store store;
        private readonly Action<bool> notify;
        private TextBox notes = new TextBox();
        private Label status = new Label(), version = new Label();
        private Button check = new Button(), install = new Button();
        private ProgressBar progress = new ProgressBar();
        private ReleaseUpdate available;
        private bool busy, handedOff;
        public bool ReadyToExit { get; private set; }
        public UpdateWindow(Store data, Action<bool> callback, bool checkOnShow = true)
        {
            store = data; notify = callback;
            Text = "Обновления"; Font = new Font("Segoe UI", 10); Size = new Size(740, 560);
            MinimumSize = MaximumSize = Size; StartPosition = FormStartPosition.CenterParent; ShowIcon = false; MaximizeBox = MinimizeBox = false;
            BackColor = Color.FromArgb(246, 249, 251);
            var title = new Label { Text = "Обновления программы", Font = new Font("Segoe UI", 20, FontStyle.Bold), Left = 24, Top = 20, Width = 670, Height = 42 };
            version.SetBounds(26, 69, 660, 26); version.Text = "Установлена версия " + new Version(UpdateManager.CurrentVersion).ToString(3);
            var automatic = new Label { Text = "Программа проверяет обновления при каждом запуске.\r\nНовую версию можно установить в этом окне.", Left = 26, Top = 110, Width = 672, Height = 44, ForeColor = UiStyle.Muted };
            status.SetBounds(26, 162, 672, 48); status.Text = "Можно проверить, доступна ли новая версия.";
            notes.SetBounds(26, 216, 673, 150); notes.Multiline = true; notes.ReadOnly = true; notes.ScrollBars = ScrollBars.Vertical; notes.BackColor = Color.White;
            notes.Text = "При обновлении база и вложения сохраняются отдельно.\r\nПеред заменой программы будет создана резервная копия.\r\nБез интернета учёт продолжает работать.";
            progress.SetBounds(26, 382, 673, 12); progress.Visible = false; progress.Style = ProgressBarStyle.Marquee; progress.MarqueeAnimationSpeed = 25;
            check.SetBounds(26, 412, 225, 38); check.Text = "Проверить обновления"; check.Click += async delegate { await Check(); };
            install.SetBounds(457, 412, 241, 38); install.Text = "Обновить"; install.Enabled = false; install.BackColor = Color.FromArgb(0, 119, 116); install.ForeColor = Color.White;
            install.Click += async delegate { await Install(); };
            var footer = new Label { Text = "Обновление выполнится после закрытия текущего окна учёта.", Left = 26, Top = 468, Width = 680, Height = 42, ForeColor = UiStyle.Muted };
            Controls.AddRange(new Control[] { title, version, automatic, status, notes, progress, check, install, footer });
            Shown += async delegate { if (checkOnShow) await Check(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy && !handedOff) e.Cancel = true; };
        }
        private void SetBusy(bool value, string message)
        {
            busy = value; progress.Visible = value; check.Enabled = !value; install.Enabled = !value && available != null;
            if (message != null) status.Text = message;
        }
        private void ShowError(Exception ex)
        {
            status.Text = ex is System.Net.WebException ? "Сервис обновлений сейчас недоступен. Проверьте интернет и попробуйте ещё раз." : ex.Message; status.ForeColor = Color.Firebrick;
        }
        private async Task Check()
        {
            try
            {
                status.ForeColor = SystemColors.ControlText; SetBusy(true, "Проверяем новые версии…");
                string repo = UpdateManager.ReadConfiguration(store.DataDir).Repository;
                available = await Task.Run(delegate { return new GitHubUpdateClient().Check(repo, UpdateManager.CurrentVersion); });
                if (!UpdateManager.SaveCheckResult(store.DataDir, repo, available)) throw new InvalidOperationException("Настройки обновлений изменились. Проверьте обновления ещё раз.");
                if (available == null || !UpdateManager.HasNewerVersion(available.Manifest.Version)) { available = null; status.Text = "У вас установлена последняя доступная версия."; notify(false); }
                else
                {
                    status.Text = "Доступна версия " + new Version(available.Manifest.Version).ToString(3) + ". Для установки нажмите «Обновить».";
                    notes.Text = String.IsNullOrWhiteSpace(available.Notes) ? "Выпуск готов к установке. База и вложения будут сохранены." : available.Notes;
                    notify(true);
                }
            }
            catch (Exception ex) { available = null; ShowError(ex); }
            finally { SetBusy(false, null); }
        }
        private async Task Install()
        {
            if (available == null) return;
            string session = null;
            try
            {
                if (UpdateManager.IsActiveMarker(Path.GetDirectoryName(Application.ExecutablePath))) throw new InvalidOperationException("Другое обновление уже запущено. Подождите его завершения.");
                UpdateManager.CheckWritableDirectory(Path.GetDirectoryName(Application.ExecutablePath));
                string helper = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Updater.exe");
                if (!File.Exists(helper)) throw new FileNotFoundException("Модуль обновления не найден. Распакуйте оба файла программы в одну папку.");
                MainForm.ValidateSnapshot(store.DataDir);
                session = Path.Combine(UpdateManager.CacheRoot(store.DataDir), "jobs", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(session);
                SetBusy(true, "Скачиваем и проверяем обновление…"); status.ForeColor = SystemColors.ControlText;
                var update = available;
                await Task.Run(delegate { new GitHubUpdateClient().Download(update, session); });
                var job = UpdateManager.CreateJob(session, store, update.Manifest);
                string jobPath = Path.Combine(session, "job.json");
                UpdateProtocol.WriteJob(jobPath, job);
                File.Copy(helper, Path.Combine(session, "Updater.exe"), false);
                using (var process = Process.Start(new ProcessStartInfo(Path.Combine(session, "Updater.exe"), "--job " + UpdateManager.QuoteArgument(jobPath)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = session }))
                {
                    status.Text = "Подготавливаем обновление и резервное копирование…";
                    DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                    bool ready = false;
                    while (DateTime.UtcNow < deadline)
                    {
                        string readyPath = Path.Combine(session, "helper-ready");
                        if (File.Exists(readyPath) && File.ReadAllText(readyPath, Encoding.UTF8).Trim() == job.Nonce && UpdateManager.IsActiveMarker(job.TargetDir)) { ready = true; break; }
                        if (process.HasExited) throw new IOException("Не удалось подготовить обновление. Текущая программа продолжает работать.");
                        await Task.Delay(100);
                    }
                    if (!ready) throw new TimeoutException("Модуль обновления не ответил. Текущая программа продолжает работать.");
                }
                handedOff = true; ReadyToExit = true; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex)
            {
                ShowError(ex);
                if (session != null) try { File.WriteAllText(Path.Combine(session, "cancelled"), "cancelled", Encoding.UTF8); } catch { }
            }
            finally { if (!handedOff) SetBusy(false, null); }
        }
    }
}

