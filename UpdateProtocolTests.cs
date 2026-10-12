using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WorkLedger
{
    public static class UpdateProtocolTests
    {
        public static void Run(string root)
        {
            string dir = Path.Combine(Path.GetFullPath(root), "update-protocol-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            int passed = 0;
            using (var key = new RSACryptoServiceProvider(2048, new CspParameters(24)))
            {
                key.PersistKeyInCsp = false; string publicKey = key.ToXmlString(false);
                byte[] assembly = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location); string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                Action<string, Action> test = delegate(string name, Action run) { run(); passed++; Console.WriteLine("PASS update " + name); };
                test("repository normalization and URI boundaries", delegate {
                    Eq(UpdateProtocol.NormalizeRepository("https://github.com/SCP701/Uchet.git"), "SCP701/Uchet"); Eq(UpdateProtocol.NormalizeRepository("SCP701/Uchet.git"), "SCP701/Uchet"); Eq(UpdateProtocol.NormalizeRepository("owner/ledger"), "owner/ledger"); Eq(UpdateProtocol.NormalizeRepository("https://github.com/owner/ledger.git/"), "owner/ledger");
                    foreach (string s in new[] { "owner/repo/extra", "https://evil.example/owner/repo", "https://github.com/owner/repo?token=x", "https://user:secret@github.com/owner/repo", "owner/../repo", "owner/repo%2Fextra", "https://github.com:444/owner/repo" }) Reject(delegate { UpdateProtocol.NormalizeRepository(s); });
                    GitHubUpdateClient.ValidateDownloadUri(new Uri("https://github.com/owner/ledger/releases/download/v1/package.zip"), "owner/ledger", true);
                    foreach (string uri in new[] { "http://github.com/owner/ledger/releases/download/v1/a", "https://github.com/other/ledger/releases/download/v1/a", "https://github.com.evil.example/owner/ledger/releases/download/v1/a" }) Reject(delegate { GitHubUpdateClient.ValidateDownloadUri(new Uri(uri), "owner/ledger", true); });
                    GitHubUpdateClient.ValidateDownloadUri(new Uri("https://release-assets.githubusercontent.com/file?signature=x"), "owner/ledger", false);
                    Reject(delegate { GitHubUpdateClient.ValidateDownloadUri(new Uri("https://attacker.githubusercontent.com/file"), "owner/ledger", false); });
                });
                test("network failures have plain Russian messages and keep diagnostics", delegate {
                    var error = new System.Net.WebException("technical internal", System.Net.WebExceptionStatus.Timeout); var friendly = GitHubUpdateClient.NetworkFailure(error, true);
                    True(friendly.Message.Contains("не ответил вовремя")); True(!friendly.Message.Contains("technical internal")); True(Object.ReferenceEquals(friendly.InnerException, error));
                    friendly = GitHubUpdateClient.NetworkFailure(new System.Net.WebException("technical", System.Net.WebExceptionStatus.NameResolutionFailure), false); True(friendly.Message.Contains("подключение"));
                });
                string goodZip = CreateZip(dir, "valid", new[] { UpdateProtocol.MainExeName, UpdateProtocol.UpdaterExeName }, assembly);
                UpdateManifest original = Manifest(goodZip, assembly, version);
                test("authentic detached signature and numeric version", delegate {
                    var m = Authenticate(original, key, publicKey); Eq(m.Version, version); True(UpdateProtocol.ParseVersion("1.10.0.0") > UpdateProtocol.ParseVersion("1.9.0.0"));
                    Reject(delegate { UpdateProtocol.ParseVersion("1.0"); }); Reject(delegate { UpdateProtocol.ParseVersion("v1.2.3.4"); });
                    byte[] bytes = UpdateProtocol.Encode(original), sig = key.SignData(bytes, CryptoConfig.MapNameToOID("SHA256"));
                    byte[] bad = (byte[])bytes.Clone(); bad[0] ^= 1; Reject(delegate { UpdateProtocol.VerifyManifest(bad, sig, publicKey); });
                    bad = (byte[])sig.Clone(); bad[0] ^= 1; Reject(delegate { UpdateProtocol.VerifyManifest(bytes, bad, publicKey); });
                    Reject(delegate { UpdateProtocol.VerifyManifest(bytes, new byte[5], publicKey); });
                    using (var wrong = new RSACryptoServiceProvider(2048, new CspParameters(24))) { wrong.PersistKeyInCsp = false; Reject(delegate { UpdateProtocol.VerifyManifest(bytes, sig, wrong.ToXmlString(false)); }); }
                });
                test("valid exact package extracted with matching assemblies", delegate {
                    string target = Path.Combine(dir, "valid-output"); UpdateProtocol.VerifyAndExtract(goodZip, Authenticate(original, key, publicKey), target);
                    True(File.Exists(Path.Combine(target, UpdateProtocol.MainExeName))); True(File.Exists(Path.Combine(target, UpdateProtocol.UpdaterExeName))); Eq(Directory.GetFiles(target).Length, 2);
                    Reject(delegate { UpdateProtocol.VerifyAndExtract(goodZip, Authenticate(original, key, publicKey), target); });
                });
                test("manifest changes and unsigned objects are rejected", delegate {
                    var m = Authenticate(original, key, publicKey); m.Files[0].Size++; Reject(delegate { UpdateProtocol.VerifyAndExtract(goodZip, m, Path.Combine(dir, "changed")); });
                    Reject(delegate { UpdateProtocol.VerifyAndExtract(goodZip, original, Path.Combine(dir, "unsigned")); });
                    var wrong = Manifest(goodZip, assembly, version); wrong.FormatVersion = 99; Reject(delegate { Authenticate(wrong, key, publicKey); });
                    wrong = Manifest(goodZip, assembly, version); wrong.PackageSize = UpdateProtocol.MaxPackageBytes + 1; Reject(delegate { Authenticate(wrong, key, publicKey); });
                });
                test("package hash and file hash are independently enforced", delegate {
                    var bad = Manifest(goodZip, assembly, version); bad.PackageSha256 = new string('0', 64); Reject(delegate { UpdateProtocol.VerifyAndExtract(goodZip, Authenticate(bad, key, publicKey), Path.Combine(dir, "bad-package-hash")); });
                    bad = Manifest(goodZip, assembly, version); bad.Files[0].Sha256 = new string('0', 64); Reject(delegate { UpdateProtocol.VerifyAndExtract(goodZip, Authenticate(bad, key, publicKey), Path.Combine(dir, "bad-file-hash")); });
                    True(!Directory.Exists(Path.Combine(dir, "bad-file-hash"))); True(!Directory.GetDirectories(dir, ".update-extract-*").Any());
                });
                test("traversal duplicate unexpected and directory entries rejected", delegate {
                    var cases = new[] { new[] { "../AktiOplaty.exe", "Updater.exe" }, new[] { "AktiOplaty.exe", "AktiOplaty.exe" }, new[] { "AktiOplaty.exe", "notes.txt" }, new[] { "AktiOplaty.exe", "Updater.exe", "extras.txt" }, new[] { "sub/AktiOplaty.exe", "Updater.exe" }, new[] { "AktiOplaty.exe/", "Updater.exe" } };
                    for (int i = 0; i < cases.Length; i++) { string zip = CreateZip(dir, "invalid-" + i, cases[i], assembly); var m = Manifest(zip, assembly, version); Reject(delegate { UpdateProtocol.VerifyAndExtract(zip, Authenticate(m, key, publicKey), Path.Combine(dir, "invalid-output-" + i)); }); }
                    True(!File.Exists(Path.Combine(root, "AktiOplaty.exe")));
                });
                test("assembly version and uncompressed signed size enforced", delegate {
                    var bad = Manifest(goodZip, assembly, "99.98.97.96"); Reject(delegate { UpdateProtocol.VerifyAndExtract(goodZip, Authenticate(bad, key, publicKey), Path.Combine(dir, "bad-version")); });
                    bad = Manifest(goodZip, assembly, version); bad.Files[0].Size++; Reject(delegate { UpdateProtocol.VerifyAndExtract(goodZip, Authenticate(bad, key, publicKey), Path.Combine(dir, "bad-size")); });
                    bad = Manifest(goodZip, assembly, version); bad.Files[0].Name = "../AktiOplaty.exe"; Reject(delegate { Authenticate(bad, key, publicKey); });
                });
                test("unsupported data schema prevents update", delegate {
                    string ledger = Path.Combine(dir, "ledger.json"); File.WriteAllText(ledger, "{\"Version\":1}"); var m = Authenticate(original, key, publicKey); UpdateProtocol.ValidateDataVersion(m, ledger);
                    var bad = Manifest(goodZip, assembly, version); bad.MinDataVersion = bad.MaxDataVersion = 2; Reject(delegate { UpdateProtocol.ValidateDataVersion(Authenticate(bad, key, publicKey), ledger); });
                    File.WriteAllText(ledger, "{\"Version\":2}"); Reject(delegate { UpdateProtocol.ValidateDataVersion(m, ledger); });
                });
                test("job marker and exact startup acknowledgement", delegate {
                    string session = Path.Combine(dir, "session"); Directory.CreateDirectory(session); string nonce = Guid.NewGuid().ToString("N");
                    var job = new UpdateJob { ParentPid = 42, ParentStartUtcTicks = 12345, TargetDir = Path.Combine(dir, "app"), DataDir = Path.Combine(dir, "data"), SessionDir = session, Version = version, Nonce = nonce };
                    UpdateProtocol.WriteJob(Path.Combine(session, UpdateProtocol.JobName), job); Eq(UpdateProtocol.ReadJob(Path.Combine(session, UpdateProtocol.JobName)).Nonce, nonce);
                    var marker = new UpdateMarker { HelperPid = 55, HelperStartUtcTicks = 12345, TargetDir = job.TargetDir, DataDir = job.DataDir, SessionDir = session, Version = version, Nonce = nonce };
                    UpdateProtocol.WriteMarker(Path.Combine(session, UpdateProtocol.MarkerName), marker); Eq(UpdateProtocol.ReadMarker(Path.Combine(session, UpdateProtocol.MarkerName)).HelperPid, 55);
                    UpdateProtocol.WriteStartupAck(session, nonce, version); True(UpdateProtocol.VerifyStartupAck(session, nonce, version)); True(!UpdateProtocol.VerifyStartupAck(session, Guid.NewGuid().ToString("N"), version));
                    File.WriteAllText(Path.Combine(session, UpdateProtocol.AckName), "{\"Nonce\":\"" + nonce + "\",\"Version\":\"" + version + "\",\"Extra\":\"x\"}"); True(!UpdateProtocol.VerifyStartupAck(session, nonce, version));
                    job.Nonce = "../../outside"; Reject(delegate { UpdateProtocol.WriteJob(Path.Combine(session, UpdateProtocol.JobName), job); });
                });
            }
            Console.WriteLine("Update protocol: " + passed.ToString(CultureInfo.InvariantCulture) + " groups passed. Artifacts: " + dir);
        }
        private static UpdateManifest Manifest(string zip, byte[] payload, string version)
        {
            return new UpdateManifest { Version = version, PackageSize = new FileInfo(zip).Length, PackageSha256 = UpdateProtocol.HashFile(zip), Files = new List<UpdateFile> { new UpdateFile { Name = UpdateProtocol.MainExeName, Size = payload.Length, Sha256 = UpdateProtocol.HashBytes(payload) }, new UpdateFile { Name = UpdateProtocol.UpdaterExeName, Size = payload.Length, Sha256 = UpdateProtocol.HashBytes(payload) } } };
        }
        private static UpdateManifest Authenticate(UpdateManifest m, RSACryptoServiceProvider key, string publicXml) { byte[] bytes = UpdateProtocol.Encode(m); return UpdateProtocol.VerifyManifest(bytes, key.SignData(bytes, CryptoConfig.MapNameToOID("SHA256")), publicXml); }
        private static string CreateZip(string dir, string name, string[] names, byte[] payload) { string path = Path.Combine(dir, name + ".zip"); using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) foreach (string n in names) { var entry = zip.CreateEntry(n, CompressionLevel.Optimal); using (var s = entry.Open()) s.Write(payload, 0, payload.Length); } return path; }
        private static void True(bool condition) { if (!condition) throw new Exception("Update protocol assertion failed."); }
        private static void Eq<T>(T a, T b) { if (!Object.Equals(a, b)) throw new Exception("Update protocol expected " + b + ", got " + a); }
        private static void Reject(Action operation) { try { operation(); } catch { return; } throw new Exception("Unsafe update was accepted."); }
    }
}


