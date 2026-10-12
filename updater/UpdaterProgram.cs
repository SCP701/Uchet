using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WorkLedger
{
    internal sealed class UpdateTransactionException : Exception
    {
        internal bool RollbackIncomplete { get; private set; }
        internal UpdateTransactionException(string message, Exception inner, bool incomplete) : base(message, inner) { RollbackIncomplete = incomplete; }
    }

    internal sealed class UpdateCancelledException : OperationCanceledException
    {
        internal UpdateCancelledException() : base("Обновление отменено исходным приложением. Файлы программы и данные не изменены.") { }
    }

    internal static class UpdaterTransaction
    {
        internal static readonly string[] ProgramFiles = { "AktiOplaty.exe", "Updater.exe" };

        // Production manifests are authenticated by UpdateProtocol; tests sign separate fixture packages.
        internal static void Apply(UpdateJob job, string stagedDir, UpdateManifest manifest, Func<bool> launchAndAcknowledge, Action beforeSecondReplace)
        {
            if (job == null || manifest == null || launchAndAcknowledge == null) throw new ArgumentNullException("job");
            if (manifest.Version != job.Version) throw new InvalidOperationException("Версия пакета не совпадает с заданием.");
            UpdaterProgram.ThrowIfCancelled(job);
            string target = Path.GetFullPath(job.TargetDir), previous = Path.Combine(job.SessionDir, "previous");
            UpdaterSafety.NoLinks(target); UpdaterSafety.NoLinks(stagedDir); UpdaterSafety.NoLinks(job.SessionDir);
            var temporaries = new List<string>(); var replaced = new List<string>();
            try
            {
                Directory.CreateDirectory(previous); UpdaterSafety.NoLinks(previous);
                foreach (string name in ProgramFiles)
                {
                    string oldPath = Path.Combine(target, name), newPath = Path.Combine(stagedDir, name), previousPath = Path.Combine(previous, name);
                    UpdaterSafety.RegularFile(oldPath); UpdaterSafety.RegularFile(newPath);
                    if (File.Exists(previousPath)) throw new InvalidOperationException("Копия прежней программы уже существует. Используйте новое задание обновления.");
                    UpdateFile expected = manifest.Files.Single(f => f.Name == name);
                    if (new FileInfo(newPath).Length != expected.Size || !String.Equals(UpdaterSafety.HashFile(newPath), expected.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Файл обновления изменился после проверки: " + name);
                    if (AssemblyName.GetAssemblyName(newPath).Version.ToString() != job.Version) throw new InvalidOperationException("Версия файла обновления не совпадает: " + name);
                    UpdaterSafety.CopyDurably(oldPath, previousPath);
                    string temporary = Path.Combine(target, ".update-" + job.Nonce + "-" + name + ".tmp");
                    if (File.Exists(temporary)) throw new InvalidOperationException("Временный файл обновления уже существует.");
                    temporaries.Add(temporary); UpdaterSafety.CopyDurably(newPath, temporary);
                    if (!String.Equals(UpdaterSafety.HashFile(temporary), expected.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Не удалось проверить скопированный файл: " + name);
                }
                UpdaterProgram.ThrowIfCancelled(job);
                for (int i = 0; i < ProgramFiles.Length; i++)
                {
                    if (i == 1 && beforeSecondReplace != null) beforeSecondReplace();
                    string path = Path.Combine(target, ProgramFiles[i]);
                    string temporary = Path.Combine(target, ".update-" + job.Nonce + "-" + ProgramFiles[i] + ".tmp");
                    File.Replace(temporary, path, null); replaced.Add(ProgramFiles[i]);
                }
                if (!launchAndAcknowledge()) throw new InvalidOperationException("Новая версия не подтвердила успешный запуск.");
            }
            catch (Exception error)
            {
                if (error is UpdateCancelledException && replaced.Count == 0) throw;
                var rollbackErrors = new List<string>();
                foreach (string name in Enumerable.Reverse(replaced))
                {
                    string restore = Path.Combine(target, ".rollback-" + job.Nonce + "-" + name + ".tmp");
                    try
                    {
                        if (File.Exists(restore)) throw new IOException("Временный файл отката уже существует.");
                        UpdaterSafety.CopyDurably(Path.Combine(previous, name), restore);
                        File.Replace(restore, Path.Combine(target, name), null);
                    }
                    catch (Exception rollback) { rollbackErrors.Add(name + ": " + rollback.Message); }
                    finally { UpdaterSafety.TryDelete(restore); }
                }
                if (rollbackErrors.Count > 0) throw new UpdateTransactionException("Не удалось полностью вернуть прежнюю программу. Копии файлов: " + previous + ". " + String.Join("; ", rollbackErrors), error, true);
                throw new UpdateTransactionException("Обновление отменено. Прежняя программа восстановлена. " + error.Message, error, false);
            }
            finally { foreach (string temporary in temporaries) UpdaterSafety.TryDelete(temporary); }
        }
    }
    internal static class UpdaterSafety
    {
        internal static string FullDirectory(string path) { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        internal static bool SamePath(string left, string right) { return String.Equals(FullDirectory(left), FullDirectory(right), StringComparison.OrdinalIgnoreCase); }
        internal static void NoLinks(string path)
        {
            string full = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(full))
            {
                if ((File.Exists(full) || Directory.Exists(full)) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Ссылки на внешние файлы и папки не поддерживаются при обновлении.");
                string parent = Path.GetDirectoryName(full); if (String.Equals(full, parent, StringComparison.OrdinalIgnoreCase)) break; full = parent;
            }
        }
        internal static void RegularFile(string path)
        {
            NoLinks(path); if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new FileNotFoundException("Не найден файл программы.", path);
        }
        internal static string HashFile(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
        internal static string PathKey(string path)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(FullDirectory(path).ToLowerInvariant()))).Replace("-", "");
        }
        internal static void CopyDurably(string source, string destination)
        {
            RegularFile(source); NoLinks(destination);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
        }
        internal static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        internal static string QuoteArgument(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            var quoted = new StringBuilder("\""); int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') backslashes++;
                else if (c == '"') { quoted.Append('\\', backslashes * 2 + 1); quoted.Append(c); backslashes = 0; }
                else { quoted.Append('\\', backslashes); quoted.Append(c); backslashes = 0; }
            }
            quoted.Append('\\', backslashes * 2); quoted.Append('"'); return quoted.ToString();
        }
        internal static bool ProcessMatches(Process process, long startTicks, string expectedPath)
        {
            try { return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks && SamePath(process.MainModule.FileName, expectedPath); }
            catch (InvalidOperationException) { return false; } catch (System.ComponentModel.Win32Exception) { return false; }
        }
        internal static bool MarkerIsLive(UpdateMarker marker)
        {
            if (marker == null) return false;
            try { using (var process = Process.GetProcessById(marker.HelperPid)) return ProcessMatches(process, marker.HelperStartUtcTicks, Path.Combine(marker.SessionDir, "Updater.exe")); }
            catch (ArgumentException) { return false; }
        }
    }
    internal static class UpdaterProgram
    {
        private sealed class Context
        {
            internal UpdateJob Job;
            internal string JobPath;
            internal bool SafeSession, ParentClosed, MarkerOwned, RollbackIncomplete;
            internal long OwnStartTicks;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            var context = new Context();
            try
            {
                if (args.Length != 2 || args[0] != "--job") throw new ArgumentException("Помощник обновления запускается из приложения «Акты и оплаты».");
                context.JobPath = Path.GetFullPath(args[1]);
                context.Job = UpdateProtocol.ReadJob(context.JobPath);
                ValidateJobPaths(context.Job, context.JobPath);
                context.SafeSession = true;
                using (var own = Process.GetCurrentProcess()) context.OwnStartTicks = own.StartTime.ToUniversalTime().Ticks;
                bool created;
                using (var mutex = new Mutex(true, "Local\\WorkLedger-Update-" + UpdaterSafety.PathKey(context.Job.TargetDir), out created))
                {
                    if (!created) throw new InvalidOperationException("Другое обновление программы уже выполняется.");
                    Run(context);
                }
                return 0;
            }
            catch (Exception error)
            {
                UpdateTransactionException transaction = error as UpdateTransactionException;
                if (transaction != null) context.RollbackIncomplete = transaction.RollbackIncomplete;
                bool cancelled = error is UpdateCancelledException;
                Log(context, (cancelled ? "ОТМЕНА: " : "ОШИБКА: ") + error);
                RemoveOwnMarker(context);
                if (context.SafeSession && context.ParentClosed && !context.RollbackIncomplete)
                {
                    try { StartApplication(context.Job.TargetDir, context.Job.DataDir, null); Log(context, "Прежняя программа запущена."); }
                    catch (Exception restart) { Log(context, "Не удалось запустить прежнюю программу: " + restart); }
                }
                if (cancelled) { Log(context, "Обновление отменено; файлы программы и данные сохранены."); return 0; }
                Application.EnableVisualStyles();
                string cache = context.SafeSession ? "\n\nКопии программы, резервная копия данных и журнал:\n" + context.Job.SessionDir : "";
                MessageBox.Show("Обновление не установлено.\n" + error.Message + "\n\nДанные учета сохранены." + cache, "Акты и оплаты — обновление", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            finally { RemoveOwnMarker(context); }
        }

        private static void Run(Context context)
        {
            UpdateJob job = context.Job;
            ThrowIfCancelled(job);
            Log(context, "Начало обновления " + job.Version);
            using (var parent = Process.GetProcessById(job.ParentPid))
            {
                if (!UpdaterSafety.ProcessMatches(parent, job.ParentStartUtcTicks, Path.Combine(job.TargetDir, "AktiOplaty.exe"))) throw new InvalidOperationException("Не удалось проверить исходное приложение. Откройте программу и повторите обновление.");
                string manifestPath = Path.Combine(job.SessionDir, UpdateProtocol.ManifestName), signaturePath = Path.Combine(job.SessionDir, UpdateProtocol.SignatureName), packagePath = Path.Combine(job.SessionDir, UpdateProtocol.PackageName);
                UpdaterSafety.RegularFile(manifestPath); UpdaterSafety.RegularFile(signaturePath); UpdaterSafety.RegularFile(packagePath);
                if (new FileInfo(manifestPath).Length > 1024 * 1024 || new FileInfo(signaturePath).Length > 8192) throw new InvalidDataException("Файлы описания обновления слишком велики.");
                UpdateManifest manifest = UpdateProtocol.VerifyManifest(File.ReadAllBytes(manifestPath), File.ReadAllBytes(signaturePath));
                if (manifest.Version != job.Version) throw new InvalidOperationException("Версия подписанного обновления отличается от задания.");
                if (manifest.MinDataVersion != 1 || manifest.MaxDataVersion != 1) throw new InvalidOperationException("Обновления с изменением формата базы пока не поддерживаются.");
                Version nextVersion = new Version(manifest.Version), currentVersion = AssemblyName.GetAssemblyName(Path.Combine(job.TargetDir, "AktiOplaty.exe")).Version;
                if (nextVersion <= currentVersion) throw new InvalidOperationException("Обновление должно быть новее установленной программы.");
                UpdateProtocol.CheckManifestDataVersion(manifest, Path.Combine(job.DataDir, "ledger.json"));
                string staged = Path.Combine(job.SessionDir, "verified-" + Guid.NewGuid().ToString("N"));
                UpdateProtocol.VerifyAndExtract(packagePath, manifest, staged);
                Log(context, "Подпись, контрольные суммы и версии проверены.");
                ThrowIfCancelled(job);
                WriteOwnMarker(context);
                ThrowIfCancelled(job);
                string readyPath = Path.Combine(job.SessionDir, "helper-ready");
                UpdaterSafety.NoLinks(readyPath); File.WriteAllText(readyPath, job.Nonce, new UTF8Encoding(false));
                Log(context, "Ожидание закрытия исходного приложения.");
                try { WaitForParentExit(parent, job, 60000); }
                catch (UpdateCancelledException) { context.ParentClosed = parent.HasExited; throw; }
                context.ParentClosed = true;
                ThrowIfCancelled(job);
                // The same data mutex prevents another app instance from entering during the snapshot.
                bool dataCreated;
                using (var dataMutex = new Mutex(false, "Local\\WorkLedger-" + UpdaterSafety.PathKey(job.DataDir), out dataCreated))
                {
                    bool acquired;
                    try { acquired = dataMutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new InvalidOperationException("База открыта в другой программе. Обновление отменено.");
                    try
                    {
                        UpdateProtocol.CheckManifestDataVersion(manifest, Path.Combine(job.DataDir, "ledger.json"));
                        ValidateDataAndAttachments(job.DataDir);
                        string backupDir = Path.Combine(job.SessionDir, "backups"); Directory.CreateDirectory(backupDir); UpdaterSafety.NoLinks(backupDir);
                        string backup = Path.Combine(backupDir, "data-before-" + job.Version + ".zip");
                        if (File.Exists(backup)) throw new InvalidOperationException("Резервная копия задания уже существует.");
                        ThrowIfCancelled(job);
                        BackupArchive.Create(job.DataDir, backup);
                        Log(context, "Резервная копия создана: " + backup);
                    }
                    finally { dataMutex.ReleaseMutex(); }
                }
                ThrowIfCancelled(job);
                UpdaterTransaction.Apply(job, staged, manifest, delegate { return LaunchAndCheck(context); }, null);
                Log(context, "Новая программа подтвердила успешный запуск. Обновление завершено.");
                RemoveOwnMarker(context);
            }
        }

        internal static void ThrowIfCancelled(UpdateJob job)
        {
            string path = Path.Combine(job.SessionDir, "cancelled");
            if (File.Exists(path) || Directory.Exists(path)) throw new UpdateCancelledException();
        }
        internal static void WaitForParentExit(Process parent, UpdateJob job, int timeoutMilliseconds)
        {
            if (parent == null || job == null) throw new ArgumentNullException("parent");
            if (timeoutMilliseconds < 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            Stopwatch deadline = Stopwatch.StartNew();
            while (true)
            {
                ThrowIfCancelled(job);
                if (parent.WaitForExit(0)) { ThrowIfCancelled(job); return; }
                int remaining = timeoutMilliseconds - (int)Math.Min(deadline.ElapsedMilliseconds, Int32.MaxValue);
                if (remaining <= 0) throw new InvalidOperationException("Исходная программа не закрылась вовремя. Обновление отменено.");
                parent.WaitForExit(Math.Min(100, remaining));
            }
        }
        internal static void ValidateJobPaths(UpdateJob job, string jobPath)
        {
            if (job == null) throw new ArgumentNullException("job");
            string target = UpdaterSafety.FullDirectory(job.TargetDir), data = UpdaterSafety.FullDirectory(job.DataDir), session = UpdaterSafety.FullDirectory(job.SessionDir);
            string dataParent = Path.GetDirectoryName(data);
            if (String.IsNullOrEmpty(dataParent)) throw new InvalidOperationException("Некорректная папка базы.");
            string expectedSession = Path.Combine(dataParent, "WorkLedgerUpdates", "jobs", job.Nonce);
            if (!UpdaterSafety.SamePath(session, expectedSession) || !UpdaterSafety.SamePath(jobPath, Path.Combine(session, UpdateProtocol.JobName))) throw new InvalidOperationException("Задание обновления находится вне установленной папки кэша.");
            if (!Directory.Exists(target) || !Directory.Exists(data) || !Directory.Exists(session)) throw new InvalidOperationException("Не найдены папки программы, базы или обновления.");
            if (UpdaterSafety.SamePath(target, data) || UpdaterSafety.SamePath(target, session) || UpdaterSafety.SamePath(data, session)) throw new InvalidOperationException("Папки обновления, программы и базы должны различаться.");
            if (session.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Кэш обновления не должен находиться в папке базы.");
            UpdaterSafety.NoLinks(target); UpdaterSafety.NoLinks(data); UpdaterSafety.NoLinks(session); UpdaterSafety.RegularFile(jobPath);
            UpdaterSafety.RegularFile(Path.Combine(target, "AktiOplaty.exe")); UpdaterSafety.RegularFile(Path.Combine(target, "Updater.exe"));
            string runner = Application.ExecutablePath;
            if (!UpdaterSafety.SamePath(runner, Path.Combine(session, "Updater.exe"))) throw new InvalidOperationException("Помощник должен запускаться из отдельной папки обновления.");
            UpdaterSafety.RegularFile(runner);
            if (UpdaterSafety.HashFile(runner) != UpdaterSafety.HashFile(Path.Combine(target, "Updater.exe"))) throw new InvalidOperationException("Копия помощника отличается от установленной программы.");
        }

        internal static void ValidateDataAndAttachments(string dataDir)
        {
            UpdaterSafety.RegularFile(Path.Combine(dataDir, "ledger.json"));
            var store = new Store(dataDir);
            foreach (Record record in store.Records.Where(r => r.Kind == "document"))
            {
                string attachment = record.Get("filePath");
                if (String.IsNullOrWhiteSpace(attachment)) continue;
                string path = store.ResolveAttachment(attachment); UpdaterSafety.RegularFile(path);
            }
        }

        private static void WriteOwnMarker(Context context)
        {
            string path = Path.Combine(context.Job.TargetDir, UpdateProtocol.MarkerName);
            if (File.Exists(path))
            {
                UpdaterSafety.RegularFile(path); UpdateMarker old = UpdateProtocol.ReadMarker(path);
                if (UpdaterSafety.MarkerIsLive(old)) throw new InvalidOperationException("Обновление уже выполняется.");
                UpdaterSafety.CopyDurably(path, Path.Combine(context.Job.SessionDir, "previous-update-marker.json"));
                File.Delete(path);
            }
            UpdateProtocol.WriteMarker(path, new UpdateMarker { HelperPid = Process.GetCurrentProcess().Id, HelperStartUtcTicks = context.OwnStartTicks, TargetDir = context.Job.TargetDir, DataDir = context.Job.DataDir, SessionDir = context.Job.SessionDir, Nonce = context.Job.Nonce, Version = context.Job.Version });
            context.MarkerOwned = true;
        }
        private static void RemoveOwnMarker(Context context)
        {
            if (!context.MarkerOwned || !context.SafeSession || context.RollbackIncomplete) return;
            try
            {
                string path = Path.Combine(context.Job.TargetDir, UpdateProtocol.MarkerName);
                if (!File.Exists(path)) { context.MarkerOwned = false; return; }
                UpdateMarker marker = UpdateProtocol.ReadMarker(path);
                if (marker.Nonce == context.Job.Nonce && marker.HelperPid == Process.GetCurrentProcess().Id && marker.HelperStartUtcTicks == context.OwnStartTicks) { File.Delete(path); context.MarkerOwned = false; }
            }
            catch (Exception error) { Log(context, "Не удалось убрать отметку обновления: " + error.Message); }
        }

        private static bool LaunchAndCheck(Context context)
        {
            string ackPath = Path.Combine(context.Job.SessionDir, UpdateProtocol.AckName);
            if (File.Exists(ackPath)) { UpdaterSafety.RegularFile(ackPath); File.Delete(ackPath); }
            Process child = null; long startTicks = 0; bool acknowledged = false;
            try
            {
                child = StartApplication(context.Job.TargetDir, context.Job.DataDir, context.JobPath);
                if (child == null) throw new InvalidOperationException("Не удалось запустить новую программу.");
                startTicks = child.StartTime.ToUniversalTime().Ticks;
                string executable = Path.Combine(context.Job.TargetDir, "AktiOplaty.exe");
                Stopwatch deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 60000)
                {
                    if (child.HasExited) throw new InvalidOperationException("Новая программа завершилась до подтверждения запуска.");
                    if (UpdateProtocol.VerifyStartupAck(context.Job.SessionDir, context.Job.Nonce, context.Job.Version) && UpdaterSafety.ProcessMatches(child, startTicks, executable)) { acknowledged = true; return true; }
                    Thread.Sleep(100);
                }
                throw new InvalidOperationException("Новая программа не подтвердила запуск за 60 секунд.");
            }
            finally
            {
                if (!acknowledged && child != null) StopOwnedChild(child, startTicks, Path.Combine(context.Job.TargetDir, "AktiOplaty.exe"));
                if (child != null) child.Dispose();
            }
        }
        private static Process StartApplication(string targetDir, string dataDir, string jobPath)
        {
            string arguments = "--data-dir " + UpdaterSafety.QuoteArgument(dataDir);
            if (jobPath != null) arguments += " --update-job " + UpdaterSafety.QuoteArgument(jobPath);
            return Process.Start(new ProcessStartInfo { FileName = Path.Combine(targetDir, "AktiOplaty.exe"), Arguments = arguments, WorkingDirectory = targetDir, UseShellExecute = false });
        }
        private static void StopOwnedChild(Process child, long startTicks, string executable)
        {
            if (!UpdaterSafety.ProcessMatches(child, startTicks, executable)) return;
            try
            {
                child.CloseMainWindow();
                if (!child.WaitForExit(2000) && UpdaterSafety.ProcessMatches(child, startTicks, executable)) { child.Kill(); child.WaitForExit(5000); }
            }
            catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
        private static void Log(Context context, string message)
        {
            if (!context.SafeSession) return;
            try
            {
                string path = Path.Combine(context.Job.SessionDir, "update.log"); UpdaterSafety.NoLinks(path);
                File.AppendAllText(path, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
