using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace WorkLedger
{
    public sealed class UpdateFile { public string Name { get; set; } public long Size { get; set; } public string Sha256 { get; set; } }
    public sealed class UpdateManifest
    {
        public int FormatVersion { get; set; }
        public string Version { get; set; }
        public int MinDataVersion { get; set; }
        public int MaxDataVersion { get; set; }
        public string PackageFile { get; set; }
        public long PackageSize { get; set; }
        public string PackageSha256 { get; set; }
        public List<UpdateFile> Files { get; set; }
        internal string AuthenticationDigest;
        public UpdateManifest() { FormatVersion = 1; MinDataVersion = MaxDataVersion = 1; PackageFile = "AktiOplaty-update.zip"; Files = new List<UpdateFile>(); }
    }
    public sealed class UpdateJob
    {
        public int ParentPid { get; set; } public long ParentStartUtcTicks { get; set; }
        public string TargetDir { get; set; } public string DataDir { get; set; } public string SessionDir { get; set; }
        public string Version { get; set; } public string Nonce { get; set; }
    }
    public sealed class UpdateMarker
    {
        public int HelperPid { get; set; } public long HelperStartUtcTicks { get; set; }
        public string Nonce { get; set; } public string Version { get; set; }
        public string TargetDir { get; set; } public string DataDir { get; set; } public string SessionDir { get; set; }
    }
    public sealed class StartupAck { public string Nonce { get; set; } public string Version { get; set; } }
    public sealed class ReleaseUpdate
    {
        public UpdateManifest Manifest { get; internal set; }
        public byte[] ManifestBytes { get; internal set; } public byte[] SignatureBytes { get; internal set; }
        public string PackageUrl { get; internal set; } public string Repository { get; internal set; } public string Notes { get; internal set; }
    }
    public static class UpdateProtocol
    {
        public const string MainExeName = "AktiOplaty.exe", UpdaterExeName = "Updater.exe";
        public const string ManifestName = "manifest.json", SignatureName = "manifest.sig", PackageName = "package.zip", JobName = "job.json", AckName = "startup-ok", MarkerName = ".update-in-progress.json";
        public const long MaxPackageBytes = 25L * 1024 * 1024, MaxUncompressedBytes = 100L * 1024 * 1024;
        public static string NormalizeRepository(string repository)
        {
            if (String.IsNullOrWhiteSpace(repository)) throw new InvalidOperationException("Укажите репозиторий GitHub: владелец/название.");
            string s = repository.Trim();
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri; if (!Uri.TryCreate(s, UriKind.Absolute, out uri) || uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "") throw new InvalidOperationException("Допускается только адрес репозитория https://github.com/владелец/название.");
                s = uri.AbsolutePath.Trim('/');
            }
            s = s.TrimEnd('/'); if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            string[] parts = s.Split('/');
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$") || !Regex.IsMatch(parts[1], "^[A-Za-z0-9_.-]{1,100}$") || parts[1] == "." || parts[1] == "..") throw new InvalidOperationException("Укажите репозиторий в виде владелец/название.");
            return parts[0] + "/" + parts[1];
        }
        public static Version ReadApplicationVersion(string path) { return AssemblyName.GetAssemblyName(path).Version; }
        public static string HashFile(string path) { using (var file = File.OpenRead(path)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(file)); }
        internal static string HashBytes(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        private static string Hex(byte[] hash) { return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); }
        public static UpdateManifest VerifyManifest(byte[] bytes, byte[] signature) { return VerifyManifest(bytes, signature, TrustedUpdateKey.PublicXml); }
        internal static UpdateManifest VerifyManifest(byte[] bytes, byte[] signature, string publicXml)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 65536 || signature == null || signature.Length == 0 || signature.Length > 1024) throw new InvalidOperationException("Некорректный размер подписанных данных обновления.");
            try
            {
                using (var rsa = new RSACryptoServiceProvider(new CspParameters(24)))
                {
                    rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicXml);
                    if (rsa.KeySize < 2048 || signature.Length != rsa.KeySize / 8 || !rsa.VerifyData(bytes, CryptoConfig.MapNameToOID("SHA256"), signature)) throw new InvalidOperationException("Подпись обновления не подтверждена. Установка остановлена.");
                }
            }
            catch (CryptographicException e) { throw new InvalidOperationException("Не удалось проверить подпись обновления.", e); }
            UpdateManifest manifest = Decode<UpdateManifest>(bytes, 65536);
            ValidateManifest(manifest); manifest.AuthenticationDigest = HashBytes(Encode(manifest)); return manifest;
        }
        internal static Version ParseVersion(string text)
        {
            Version v;
            if (text == null || !Regex.IsMatch(text, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$") || !Version.TryParse(text, out v)) throw new InvalidOperationException("Версия обновления должна содержать четыре числа.");
            return v;
        }
        private static void ValidateManifest(UpdateManifest m)
        {
            if (m == null || m.FormatVersion != 1) throw new InvalidOperationException("Неподдерживаемый формат обновления.");
            ParseVersion(m.Version);
            if (m.MinDataVersion < 1 || m.MaxDataVersion < m.MinDataVersion) throw new InvalidOperationException("Некорректная совместимость базы обновления.");
            if (m.PackageFile != "AktiOplaty-update.zip" || m.PackageSize <= 0 || m.PackageSize > MaxPackageBytes || !IsHash(m.PackageSha256)) throw new InvalidOperationException("Некорректные сведения о пакете обновления.");
            if (m.Files == null || m.Files.Count != 2 || m.Files.Any(f => f == null) || !m.Files.Select(f => f.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { MainExeName, UpdaterExeName }.OrderBy(x => x, StringComparer.Ordinal))) throw new InvalidOperationException("Пакет должен содержать только AktiOplaty.exe и Updater.exe.");
            long total = 0;
            foreach (var f in m.Files) { if (f.Size <= 0 || f.Size > MaxUncompressedBytes || !IsHash(f.Sha256)) throw new InvalidOperationException("Некорректный размер или контрольная сумма файла обновления."); total += f.Size; }
            if (total > MaxUncompressedBytes) throw new InvalidOperationException("Обновление превышает допустимый размер.");
        }
        private static bool IsHash(string h) { return h != null && Regex.IsMatch(h, "^[0-9a-fA-F]{64}$"); }
        private static void Authenticated(UpdateManifest m)
        {
            ValidateManifest(m);
            if (String.IsNullOrEmpty(m.AuthenticationDigest) || HashBytes(Encode(m)) != m.AuthenticationDigest) throw new InvalidOperationException("Манифест обновления не проверен или был изменён после проверки.");
        }
        public static void ValidateDataVersion(UpdateManifest manifest, string ledgerPath)
        {
            Authenticated(manifest);
            byte[] bytes = ReadBounded(ledgerPath, 32 * 1024 * 1024);
            var obj = Decode<Dictionary<string, object>>(bytes, 32 * 1024 * 1024); object value; int version;
            if (obj == null || !obj.TryGetValue("Version", out value) || !Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < manifest.MinDataVersion || version > manifest.MaxDataVersion) throw new InvalidOperationException("Эта версия обновления несовместима с вашей базой. Данные сохранены.");
        }
        public static void CheckManifestDataVersion(UpdateManifest manifest, string ledgerPath) { ValidateDataVersion(manifest, ledgerPath); }
        public static void VerifyAndExtract(string zipPath, UpdateManifest manifest, string destinationDir)
        {
            Authenticated(manifest);
            var info = new FileInfo(zipPath);
            if (!info.Exists || info.Length != manifest.PackageSize || info.Length > MaxPackageBytes || !HashFile(zipPath).Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Размер или контрольная сумма пакета обновления не совпадают.");
            string destination = Path.GetFullPath(destinationDir).TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new InvalidOperationException("Папка подготовки обновления должна быть пустой.");
            string parent = Path.GetDirectoryName(destination); Directory.CreateDirectory(parent);
            string temp = Path.Combine(parent, ".update-extract-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    if (zip.Entries.Count != 2) throw new InvalidOperationException("Обновление содержит лишние или недостающие файлы.");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in zip.Entries)
                    {
                        var f = manifest.Files.FirstOrDefault(x => x.Name == e.FullName);
                        if (f == null || e.Name != e.FullName || !seen.Add(e.FullName) || e.Length != f.Size || ((e.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (e.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Недопустимый файл в пакете обновления.");
                        string target = Path.Combine(temp, f.Name);
                        using (var input = e.Open()) using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[65536]; long written = 0; int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { if (written + count > f.Size) throw new InvalidOperationException("Распакованный файл превышает подписанный размер."); output.Write(buffer, 0, count); written += count; }
                            if (written != f.Size) throw new InvalidOperationException("Распакованный файл неполон."); output.Flush(true);
                        }
                        if (!HashFile(target).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase) || ReadApplicationVersion(target) != ParseVersion(manifest.Version)) throw new InvalidOperationException("Контрольная сумма или версия файла обновления не совпадают.");
                    }
                }
                if (Directory.Exists(destination)) Directory.Delete(destination, false);
                Directory.Move(temp, destination);
            }
            finally
            {
                if (Directory.Exists(temp)) { foreach (string name in new[] { MainExeName, UpdaterExeName }) { string path = Path.Combine(temp, name); if (File.Exists(path)) File.Delete(path); } Directory.Delete(temp, false); }
            }
        }
        public static void WriteJob(string jobPath, UpdateJob job) { ValidateJob(job); WriteJson(jobPath, job); }
        public static UpdateJob ReadJob(string jobPath) { var job = Decode<UpdateJob>(ReadBounded(jobPath, 32768), 32768); ValidateJob(job); return job; }
        private static void ValidateJob(UpdateJob j)
        {
            if (j == null || j.ParentPid <= 0 || j.ParentStartUtcTicks <= 0 || !ValidNonce(j.Nonce)) throw new InvalidOperationException("Некорректное задание обновления.");
            ParseVersion(j.Version);
            ValidatePath(j.TargetDir); ValidatePath(j.DataDir); ValidatePath(j.SessionDir);
            if (Path.GetFullPath(j.TargetDir).Equals(Path.GetFullPath(j.DataDir), StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(j.TargetDir).Equals(Path.GetFullPath(j.SessionDir), StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(j.DataDir).Equals(Path.GetFullPath(j.SessionDir), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Папки программы, базы и подготовки должны различаться.");
        }
        public static void WriteMarker(string path, UpdateMarker marker) { ValidateMarker(marker); WriteJson(path, marker); }
        public static UpdateMarker ReadMarker(string path) { var m = Decode<UpdateMarker>(ReadBounded(path, 32768), 32768); ValidateMarker(m); return m; }
        private static void ValidateMarker(UpdateMarker m) { if (m == null || m.HelperPid <= 0 || m.HelperStartUtcTicks <= 0 || !ValidNonce(m.Nonce)) throw new InvalidOperationException("Некорректный маркер обновления."); ParseVersion(m.Version); ValidatePath(m.TargetDir); ValidatePath(m.DataDir); ValidatePath(m.SessionDir); }
        private static bool ValidNonce(string value) { return value != null && Regex.IsMatch(value, "^[0-9a-fA-F]{32}$"); }
        private static void ValidatePath(string path) { if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || Path.GetFullPath(path) != path.TrimEnd(Path.DirectorySeparatorChar) && Path.GetFullPath(path) != path) throw new InvalidOperationException("Путь обновления должен быть абсолютным и нормализованным."); }
        public static void WriteStartupAck(string sessionDir, string nonce, string version)
        {
            if (!ValidNonce(nonce)) throw new InvalidOperationException("Некорректное подтверждение запуска."); ParseVersion(version); ValidatePath(sessionDir);
            WriteJson(Path.Combine(sessionDir, AckName), new StartupAck { Nonce = nonce, Version = version });
        }
        public static bool VerifyStartupAck(string sessionDir, string nonce, string version)
        {
            try
            {
                string path = Path.Combine(sessionDir, AckName); if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
                var ack = Decode<Dictionary<string, string>>(ReadBounded(path, 4096), 4096); string n, v;
                return ack != null && ack.Count == 2 && ack.TryGetValue("Nonce", out n) && ack.TryGetValue("Version", out v) && n == nonce && v == version;
            }
            catch { return false; }
        }
        internal static byte[] Encode(object value) { return new UTF8Encoding(false, true).GetBytes(new JavaScriptSerializer { MaxJsonLength = 65536 }.Serialize(value)); }
        internal static T Decode<T>(byte[] bytes, int max) { return new JavaScriptSerializer { MaxJsonLength = max, RecursionLimit = 64 }.Deserialize<T>(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF')); }
        internal static byte[] ReadBounded(string path, int max)
        {
            using (var stream = File.OpenRead(path)) { if (stream.Length > max) throw new InvalidOperationException("Файл обновления слишком велик."); using (var mem = new MemoryStream()) { byte[] b = new byte[65536]; int n; while ((n = stream.Read(b, 0, b.Length)) > 0) { if (mem.Length + n > max) throw new InvalidOperationException("Файл обновления слишком велик."); mem.Write(b, 0, n); } return mem.ToArray(); } }
        }
        private static void WriteJson(string path, object value)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            try { byte[] bytes = Encode(value); using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); } if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    internal sealed class GitHubRelease { public bool draft { get; set; } public bool prerelease { get; set; } public string body { get; set; } public List<GitHubAsset> assets { get; set; } }
    internal sealed class GitHubAsset { public string name { get; set; } public string browser_download_url { get; set; } public long size { get; set; } }
    public sealed class GitHubUpdateClient
    {
        private readonly Action<string> progress;
        public GitHubUpdateClient(Action<string> status = null) { progress = status; }
        public ReleaseUpdate Check(string repository, string currentVersion) { return Check(repository, UpdateProtocol.ParseVersion(currentVersion)); }
        public ReleaseUpdate Check(string repository, Version currentVersion)
        {
            string repo = UpdateProtocol.NormalizeRepository(repository);
            if (currentVersion == null) throw new ArgumentNullException("currentVersion");
            Notify("Проверка новых версий...");
            byte[] releaseBytes = FetchBytes(new Uri("https://api.github.com/repos/" + repo + "/releases/latest"), 1024 * 1024, null);
            var release = UpdateProtocol.Decode<GitHubRelease>(releaseBytes, 1024 * 1024);
            if (release == null || release.draft || release.prerelease || release.assets == null) return null;
            GitHubAsset mf = Asset(release, "update-manifest.json"), sf = Asset(release, "update-manifest.sig"), pf = Asset(release, "AktiOplaty-update.zip");
            byte[] manifest = FetchBytes(AssetUri(mf, repo), 65536, repo), signature = FetchBytes(AssetUri(sf, repo), 1024, repo);
            var verified = UpdateProtocol.VerifyManifest(manifest, signature);
            if (UpdateProtocol.ParseVersion(verified.Version) <= currentVersion) return null;
            if (pf.size != verified.PackageSize) throw new InvalidOperationException("Размер выпуска GitHub отличается от подписанного пакета.");
            return new ReleaseUpdate { Manifest = verified, ManifestBytes = manifest, SignatureBytes = signature, PackageUrl = AssetUri(pf, repo).AbsoluteUri, Repository = repo, Notes = String.IsNullOrEmpty(release.body) ? "" : release.body.Substring(0, Math.Min(10000, release.body.Length)) };
        }
        public string Download(ReleaseUpdate update, string sessionDir)
        {
            if (update == null) throw new ArgumentNullException("update");
            var manifest = UpdateProtocol.VerifyManifest(update.ManifestBytes, update.SignatureBytes);
            if (manifest.Version != update.Manifest.Version) throw new InvalidOperationException("Сведения о выпуске изменились.");
            Directory.CreateDirectory(sessionDir);
            SaveExact(Path.Combine(sessionDir, UpdateProtocol.ManifestName), update.ManifestBytes); SaveExact(Path.Combine(sessionDir, UpdateProtocol.SignatureName), update.SignatureBytes);
            string path = Path.Combine(sessionDir, UpdateProtocol.PackageName), temp = path + ".download-" + Guid.NewGuid().ToString("N");
            try
            {
                Notify("Загрузка обновления...");
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Fetch(new Uri(update.PackageUrl), manifest.PackageSize, update.Repository, file);
                if (new FileInfo(temp).Length != manifest.PackageSize || !UpdateProtocol.HashFile(temp).Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Загруженное обновление не прошло проверку.");
                if (File.Exists(path)) throw new InvalidOperationException("Папка подготовки уже содержит пакет."); File.Move(temp, path); return path;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private void Notify(string text) { if (progress != null) progress(text); }
        private static void SaveExact(string path, byte[] bytes)
        {
            if (File.Exists(path)) { if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new InvalidOperationException("Папка подготовки содержит другое обновление."); return; }
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
        }
        private static GitHubAsset Asset(GitHubRelease r, string name)
        {
            var items = r.assets.Where(a => a != null && a.name == name).ToList();
            if (items.Count != 1 || items[0].size <= 0) throw new InvalidOperationException("В выпуске отсутствует необходимый файл: " + name); return items[0];
        }
        private static Uri AssetUri(GitHubAsset asset, string repo)
        {
            Uri uri; if (!Uri.TryCreate(asset.browser_download_url, UriKind.Absolute, out uri)) throw new InvalidOperationException("Некорректный адрес файла GitHub.");
            ValidateDownloadUri(uri, repo, true);
            string name = Uri.UnescapeDataString(uri.AbsolutePath.Substring(uri.AbsolutePath.LastIndexOf('/') + 1));
            if (name != asset.name || uri.Query != "") throw new InvalidOperationException("Адрес файла GitHub не соответствует выпуску."); return uri;
        }
        internal static void ValidateDownloadUri(Uri uri, string repo, bool initial)
        {
            if (uri == null || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo != "" || uri.Fragment != "" || uri.AbsoluteUri.Length > 8192) throw new InvalidOperationException("Обновления загружаются только по безопасным адресам GitHub.");
            if (initial)
            {
                if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.StartsWith("/" + repo + "/releases/download/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Адрес обновления не относится к выбранному репозиторию GitHub.");
            }
            else if (!new[] { "github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "github-releases.githubusercontent.com" }.Contains(uri.Host.ToLowerInvariant())) throw new InvalidOperationException("Перенаправление обновления ведёт за пределы GitHub.");
        }
        private static byte[] FetchBytes(Uri uri, long limit, string repo)
        {
            using (var mem = new MemoryStream()) { Fetch(uri, limit, repo, mem); return mem.ToArray(); }
        }
        private static void Fetch(Uri uri, long limit, string repo, Stream output)
        {
            try { FetchCore(uri, limit, repo, output); }
            catch (WebException error) { throw NetworkFailure(error, repo == null); }
        }
        internal static Exception NetworkFailure(WebException error, bool latest)
        {
            string message = "Не удалось связаться с GitHub. Проверьте подключение к интернету и повторите попытку.";
            var response = error.Response as HttpWebResponse;
            if (response != null)
            {
                int code = (int)response.StatusCode;
                if (code == 404) message = latest ? "В репозитории пока нет опубликованных выпусков, либо он недоступен. Проверьте ссылку и наличие Releases." : "Файл выпуска GitHub недоступен. Повторите проверку обновлений.";
                else if (code == 403 || code == 429) message = "GitHub временно ограничил доступ. Повторите проверку позже; репозиторий обновлений должен быть публичным.";
                else if (code >= 500) message = "GitHub временно недоступен. Повторите попытку позже.";
                response.Close();
            }
            else if (error.Status == WebExceptionStatus.Timeout) message = "GitHub не ответил вовремя. Проверьте подключение к интернету и повторите попытку.";
            else if (error.Status == WebExceptionStatus.TrustFailure || error.Status == WebExceptionStatus.SecureChannelFailure) message = "Не удалось установить защищённое соединение с GitHub. Проверьте дату компьютера и подключение к интернету.";
            return new InvalidOperationException(message, error);
        }
        private static void FetchCore(Uri uri, long limit, string repo, Stream output)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            if (repo != null) ValidateDownloadUri(uri, repo, true);
            else if (uri.Scheme != "https" || uri.Host != "api.github.com" || !uri.IsDefaultPort || uri.UserInfo != "") throw new InvalidOperationException("Некорректный адрес API GitHub.");
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                var request = (HttpWebRequest)WebRequest.Create(uri); request.AllowAutoRedirect = false; request.Timeout = request.ReadWriteTimeout = 15000;
                request.UserAgent = "AktiOplaty-Updater/1.1"; request.Accept = repo == null ? "application/vnd.github+json" : "application/octet-stream"; request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    if (new[] { 301, 302, 303, 307, 308 }.Contains(code))
                    {
                        string location = response.Headers["Location"]; Uri next; if (redirects == 5 || String.IsNullOrEmpty(location) || !Uri.TryCreate(uri, location, out next)) throw new InvalidOperationException("Слишком много перенаправлений GitHub.");
                        ValidateDownloadUri(next, repo, false); uri = next; continue;
                    }
                    if (code != 200 || response.ContentLength > limit) throw new InvalidOperationException("Ответ GitHub некорректен или превышает допустимый размер.");
                    using (var input = response.GetResponseStream())
                    {
                        byte[] buffer = new byte[65536]; long total = 0; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { if (total + count > limit) throw new InvalidOperationException("Ответ GitHub превышает допустимый размер."); output.Write(buffer, 0, count); total += count; }
                    }
                    return;
                }
            }
            throw new InvalidOperationException("Обновление GitHub не загружено.");
        }
    }
}


