using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CSharp;

namespace WorkLedger
{
    internal static class UpdaterTests
    {
        private static int checks;
        private static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception("Updater test: " + label); }
        private static void Reject(Action action, string label) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, label); }

        internal static void Run(string directory)
        {
            checks = 0; string root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
            string fixtures = Path.Combine(root, "fixtures"); Directory.CreateDirectory(fixtures);
            string oldExe = Path.Combine(fixtures, "old.exe"), newExe = Path.Combine(fixtures, "new.exe");
            CompileFixture(oldExe, "1.0.0.0"); CompileFixture(newExe, "2.0.0.0");
            string package = Path.Combine(fixtures, "package.zip");
            using (var file = new FileStream(package, FileMode.Create, FileAccess.ReadWrite))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                foreach (string name in UpdaterTransaction.ProgramFiles) using (var input = File.OpenRead(newExe)) using (var output = archive.CreateEntry(name).Open()) input.CopyTo(output);
            var manifest = new UpdateManifest { Version = "2.0.0.0", PackageSize = new FileInfo(package).Length, PackageSha256 = UpdaterSafety.HashFile(package), Files = UpdaterTransaction.ProgramFiles.Select(n => new UpdateFile { Name = n, Size = new FileInfo(newExe).Length, Sha256 = UpdaterSafety.HashFile(newExe) }).ToList() };
            byte[] bytes = UpdateProtocol.Encode(manifest); byte[] signature; string publicKey;
            using (var key = new RSACryptoServiceProvider(2048, new CspParameters(24))) { key.PersistKeyInCsp = false; signature = key.SignData(bytes, CryptoConfig.MapNameToOID("SHA256")); publicKey = key.ToXmlString(false); }
            UpdateManifest verified = UpdateProtocol.VerifyManifest(bytes, signature, publicKey);
            Check(verified.Version == "2.0.0.0", "signed fixture manifest verified");
            Reject(delegate { UpdateProtocol.VerifyManifest(bytes, signature); }, "test signing key is rejected by production pinned verifier");
            string oldHash = UpdaterSafety.HashFile(oldExe), nextHash = UpdaterSafety.HashFile(newExe);

            UpdateJob first = PrepareCase(root, "swap-failure", oldExe);
            string firstStage = Extract(first, package, verified);
            bool didLaunch = false;
            FileStream lockedUpdater = null;
            Reject(delegate
            {
                try { UpdaterTransaction.Apply(first, firstStage, verified, delegate { didLaunch = true; return true; }, delegate { lockedUpdater = new FileStream(Path.Combine(first.TargetDir, "Updater.exe"), FileMode.Open, FileAccess.Read, FileShare.Read); }); }
                finally { if (lockedUpdater != null) lockedUpdater.Dispose(); }
            }, "real Windows second executable replacement failure reported");
            Check(!didLaunch, "failed swap never launches new application"); AssertPrograms(first, oldHash, "second swap rollback"); AssertData(first, "swap failure preserved data"); AssertPrevious(first, oldHash);
            Check(!Directory.GetFiles(first.TargetDir, ".update-*.tmp").Any(), "swap failure temporary files removed");

            UpdateJob startup = PrepareCase(root, "startup-failure", oldExe);
            string startupStage = Extract(startup, package, verified), updatedData = "{\"Version\":1,\"Revision\":1,\"Records\":[],\"Audit\":[]}";
            Reject(delegate
            {
                UpdaterTransaction.Apply(startup, startupStage, verified, delegate
                {
                    using (var child = LaunchFixture(Path.Combine(startup.TargetDir, "AktiOplaty.exe"), "touch-fail", Path.Combine(startup.DataDir, "ledger.json"), updatedData))
                    { Check(child.WaitForExit(5000), "failed fixture exits"); Check(child.ExitCode == 42, "startup fixture failed as intended"); return false; }
                }, null);
            }, "startup failure rolls back");
            AssertPrograms(startup, oldHash, "startup failure rollback");
            Check(File.ReadAllText(Path.Combine(startup.DataDir, "ledger.json")) == updatedData, "rollback preserves database writes from failed new program");
            Check(File.ReadAllText(Path.Combine(startup.DataDir, "attachments", "акт.txt")) == "вложение", "rollback preserves attachments"); AssertPrevious(startup, oldHash);

            UpdateJob success = PrepareCase(root, "success", oldExe);
            string successStage = Extract(success, package, verified);
            UpdaterTransaction.Apply(success, successStage, verified, delegate
            {
                string ack = new UTF8Encoding(false).GetString(UpdateProtocol.Encode(new StartupAck { Nonce = success.Nonce, Version = success.Version }));
                using (var child = LaunchFixture(Path.Combine(success.TargetDir, "AktiOplaty.exe"), "ack", Path.Combine(success.SessionDir, UpdateProtocol.AckName), ack))
                {
                    long ticks = child.StartTime.ToUniversalTime().Ticks; var deadline = Stopwatch.StartNew(); bool acknowledged = false;
                    while (deadline.ElapsedMilliseconds < 5000 && !child.HasExited)
                    {
                        if (UpdateProtocol.VerifyStartupAck(success.SessionDir, success.Nonce, success.Version)) { acknowledged = UpdaterSafety.ProcessMatches(child, ticks, Path.Combine(success.TargetDir, "AktiOplaty.exe")); break; }
                        Thread.Sleep(20);
                    }
                    Check(acknowledged, "real candidate process emits correct acknowledgement");
                    Check(child.WaitForExit(5000), "success fixture naturally exits"); return acknowledged;
                }
            }, null);
            AssertPrograms(success, nextHash, "success installs both executables"); AssertData(success, "success preserves database and attachments"); AssertPrevious(success, oldHash);
            Check(UpdateProtocol.VerifyStartupAck(success.SessionDir, success.Nonce, success.Version), "success acknowledgement remains accessible");
            Check(!UpdateProtocol.VerifyStartupAck(success.SessionDir, Guid.NewGuid().ToString("N"), success.Version), "wrong acknowledgement nonce rejected");

            UpdateJob thrown = PrepareCase(root, "launcher-throws", oldExe); string thrownStage = Extract(thrown, package, verified);
            Reject(delegate { UpdaterTransaction.Apply(thrown, thrownStage, verified, delegate { throw new InvalidOperationException("fixture launch exception"); }, null); }, "launch exception rolls back");
            AssertPrograms(thrown, oldHash, "launch exception rollback"); AssertData(thrown, "launch exception preserves data");

            UpdateJob changed = PrepareCase(root, "changed-stage", oldExe); string changedStage = Extract(changed, package, verified);
            using (var append = new FileStream(Path.Combine(changedStage, "AktiOplaty.exe"), FileMode.Append)) append.WriteByte(0x00);
            Reject(delegate { UpdaterTransaction.Apply(changed, changedStage, verified, delegate { return true; }, null); }, "staged executable hash mutation rejected");
            AssertPrograms(changed, oldHash, "changed stage never alters installation");

            UpdateJob preflight = PrepareCase(root, "second-preflight-failure", oldExe); string preflightStage = Extract(preflight, package, verified);
            using (var append = new FileStream(Path.Combine(preflightStage, "Updater.exe"), FileMode.Append)) append.WriteByte(0x00);
            Reject(delegate { UpdaterTransaction.Apply(preflight, preflightStage, verified, delegate { return true; }, null); }, "second preflight hash failure reported");
            AssertPrograms(preflight, oldHash, "second preflight leaves binaries unchanged"); AssertData(preflight, "second preflight leaves data unchanged");
            Check(!Directory.GetFiles(preflight.TargetDir, ".update-*.tmp").Any(), "second preflight cleans first prepared temporary executable");

            UpdateJob immediateCancel = PrepareCase(root, "immediate-cancel", oldExe); string cancelStage = Extract(immediateCancel, package, verified);
            File.WriteAllText(Path.Combine(immediateCancel.SessionDir, "cancelled"), immediateCancel.Nonce, new UTF8Encoding(false));
            bool immediateCancelled = false;
            using (var parent = Process.GetCurrentProcess())
            {
                try { UpdaterProgram.WaitForParentExit(parent, immediateCancel, 2000); } catch (UpdateCancelledException) { immediateCancelled = true; }
                Check(!parent.HasExited, "cancel never terminates parent process");
            }
            Check(immediateCancelled, "existing cancellation ends parent wait");
            bool cancelledLaunch = false;
            Reject(delegate { UpdaterTransaction.Apply(immediateCancel, cancelStage, verified, delegate { cancelledLaunch = true; return true; }, null); }, "cancelled job cannot apply later");
            Check(!cancelledLaunch, "cancelled job never launches candidate"); AssertPrograms(immediateCancel, oldHash, "cancelled wait preserves binaries"); AssertData(immediateCancel, "cancelled wait preserves database and attachments");
            Check(!Directory.GetFiles(immediateCancel.TargetDir, ".update-*.tmp").Any(), "cancelled apply creates no executable temporary files");

            UpdateJob delayedCancel = PrepareCase(root, "delayed-cancel", oldExe);
            var canceller = new Thread(delegate() { Thread.Sleep(150); File.WriteAllText(Path.Combine(delayedCancel.SessionDir, "cancelled"), delayedCancel.Nonce, new UTF8Encoding(false)); });
            canceller.IsBackground = true; canceller.Start(); bool stoppedDuringWait = false; Stopwatch cancelWatch = Stopwatch.StartNew();
            using (var parent = Process.GetCurrentProcess())
                try { UpdaterProgram.WaitForParentExit(parent, delayedCancel, 3000); } catch (UpdateCancelledException) { stoppedDuringWait = true; }
            Check(canceller.Join(5000), "cancellation marker writer completes"); Check(stoppedDuringWait && cancelWatch.ElapsedMilliseconds < 1500, "cancellation is polled during parent wait");
            AssertPrograms(delayedCancel, oldHash, "mid-wait cancellation preserves binaries"); AssertData(delayedCancel, "mid-wait cancellation preserves database and attachments");

            UpdateJob timeout = PrepareCase(root, "bounded-parent-wait", oldExe); Stopwatch timeoutWatch = Stopwatch.StartNew();
            using (var parent = Process.GetCurrentProcess()) Reject(delegate { UpdaterProgram.WaitForParentExit(parent, timeout, 50); }, "parent wait has bounded timeout");
            Check(timeoutWatch.ElapsedMilliseconds < 1500, "short parent timeout is honored"); AssertPrograms(timeout, oldHash, "parent timeout preserves binaries"); AssertData(timeout, "parent timeout preserves data");
            string echo = Path.Combine(root, "argument-echo.txt");
            foreach (string value in new[] { "Русский путь с пробелами", "C:\\tail\\", "a\"b\\", "--data-dir \"x\"", "" })
            {
                using (var child = LaunchFixture(newExe, "echo", echo, value)) Check(child.WaitForExit(5000), "argument fixture exits");
                Check(File.ReadAllText(echo) == value, "quoted Windows argument round trip: " + value);
            }
            UpdaterProgram.ValidateDataAndAttachments(first.DataDir); Check(true, "database and valid document attachments verified");
            File.Delete(Path.Combine(first.DataDir, "attachments", "акт.txt"));
            Reject(delegate { UpdaterProgram.ValidateDataAndAttachments(first.DataDir); }, "missing referenced attachment blocks update");
            Console.WriteLine("Updater checks passed: " + checks);
            File.WriteAllText(Path.Combine(root, "result.txt"), "Updater checks passed: " + checks, new UTF8Encoding(false));
        }

        private static UpdateJob PrepareCase(string root, string label, string oldExe)
        {
            string caseDir = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 8));
            string target = Path.Combine(caseDir, "program"), data = Path.Combine(caseDir, "data"); string nonce = Guid.NewGuid().ToString("N");
            string session = Path.Combine(caseDir, "WorkLedgerUpdates", "jobs", nonce);
            Directory.CreateDirectory(target); Directory.CreateDirectory(data); Directory.CreateDirectory(session); Directory.CreateDirectory(Path.Combine(data, "attachments"));
            foreach (string name in UpdaterTransaction.ProgramFiles) File.Copy(oldExe, Path.Combine(target, name));
            var attachment = new Record("document"); attachment.Set("title", "Акт"); attachment.Set("status", "signed"); attachment.Set("filePath", "attachments/акт.txt");
            File.WriteAllBytes(Path.Combine(data, "ledger.json"), UpdateProtocol.Encode(new LedgerDocument { Version = 1, Revision = 0, Records = new List<Record> { attachment }, Audit = new List<AuditEntry>() }));
            File.WriteAllText(Path.Combine(data, "attachments", "акт.txt"), "вложение", Encoding.UTF8);
            var job = new UpdateJob { ParentPid = Process.GetCurrentProcess().Id, ParentStartUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, TargetDir = target, DataDir = data, SessionDir = session, Version = "2.0.0.0", Nonce = nonce };
            UpdateProtocol.WriteJob(Path.Combine(session, UpdateProtocol.JobName), job);
            return job;
        }
        private static string Extract(UpdateJob job, string package, UpdateManifest manifest)
        {
            string path = Path.Combine(job.SessionDir, "verified"); UpdateProtocol.VerifyAndExtract(package, manifest, path); UpdateProtocol.CheckManifestDataVersion(manifest, Path.Combine(job.DataDir, "ledger.json")); return path;
        }
        private static void AssertPrograms(UpdateJob job, string hash, string label)
        {
            foreach (string name in UpdaterTransaction.ProgramFiles) Check(UpdaterSafety.HashFile(Path.Combine(job.TargetDir, name)) == hash, label + " " + name);
        }
        private static void AssertPrevious(UpdateJob job, string hash)
        {
            foreach (string name in UpdaterTransaction.ProgramFiles) Check(UpdaterSafety.HashFile(Path.Combine(job.SessionDir, "previous", name)) == hash, "previous executable retained " + name);
        }
        private static void AssertData(UpdateJob job, string label)
        {
            Check(new Store(job.DataDir).Records.Count == 1, label + " database"); Check(File.ReadAllText(Path.Combine(job.DataDir, "attachments", "акт.txt")) == "вложение", label + " attachment");
        }
        private static Process LaunchFixture(string exe, params string[] arguments)
        {
            return Process.Start(new ProcessStartInfo { FileName = exe, Arguments = String.Join(" ", arguments.Select(UpdaterSafety.QuoteArgument)), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(exe) });
        }
        private static void CompileFixture(string output, string version)
        {
            string source = "using System;using System.IO;using System.Reflection;using System.Text;using System.Threading;[assembly:AssemblyVersion(\"" + version + "\")]class Fixture{static int Main(string[] a){if(a.Length==0)return 0;if(a[0]==\"touch-fail\"){File.WriteAllText(a[1],a[2],new UTF8Encoding(false));return 42;}if(a[0]==\"echo\"||a[0]==\"ack\"){File.WriteAllText(a[1],a[2],new UTF8Encoding(false));if(a[0]==\"ack\")Thread.Sleep(1000);return 0;}return 42;}}";
            using (var compiler = new CSharpCodeProvider())
            {
                var parameters = new CompilerParameters(new[] { "System.dll" }, output) { GenerateExecutable = true, GenerateInMemory = false, CompilerOptions = "/optimize+" };
                CompilerResults result = compiler.CompileAssemblyFromSource(parameters, source);
                if (result.Errors.HasErrors) throw new Exception("Fixture compile failed: " + String.Join("; ", result.Errors.Cast<CompilerError>().Select(e => e.ToString())));
            }
            Check(AssemblyName.GetAssemblyName(output).Version.ToString() == version, "fixture assembly version " + version);
        }
    }
}
