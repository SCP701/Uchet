using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace WorkLedger
{
    internal static class ReleaseTool
    {
        private const string PackageName = "AktiOplaty-update.zip";
        private const string ManifestName = "update-manifest.json";
        private const string SignatureName = "update-manifest.sig";

        private static int Main(string[] args)
        {
            try
            {
                Dictionary<string, string> options = ParseArguments(args);
                if (options.ContainsKey("help"))
                {
                    Console.WriteLine("ReleaseTool --input <dist> --output <release-folder> --key <private-key.xml>");
                    Console.WriteLine("Подготавливает и проверяет подписанное обновление. Публикация не выполняется.");
                    return 0;
                }
                string input = RequiredPath(options, "input");
                string output = RequiredPath(options, "output");
                string keyPath = RequiredPath(options, "key");
                Prepare(input, output, keyPath);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Не удалось подготовить выпуск: " + exception.Message);
                return 1;
            }
        }

        private static Dictionary<string, string> ParseArguments(string[] args)
        {
            Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i] == "--help" || args[i] == "-h") { options["help"] = "true"; continue; }
                if (args[i] != "--input" && args[i] != "--output" && args[i] != "--key")
                    throw new ArgumentException("Неизвестный параметр. Используйте --help.");
                string name = args[i].Substring(2);
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Не указано значение параметра --" + name + ".");
                if (options.ContainsKey(name)) throw new ArgumentException("Параметр --" + name + " указан дважды.");
                options.Add(name, args[++i]);
            }
            return options;
        }

        private static string RequiredPath(Dictionary<string, string> options, string name)
        {
            string value;
            if (!options.TryGetValue(name, out value) || String.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Укажите --" + name + ". Справка: --help.");
            return Path.GetFullPath(value);
        }

        private static void Prepare(string input, string output, string keyPath)
        {
            if (!Directory.Exists(input)) throw new InvalidOperationException("Папка готовой сборки не найдена.");
            if (!File.Exists(keyPath)) throw new InvalidOperationException("Не найден приватный ключ подписи. Восстановите первоначальный ключ выпуска. Создание нового ключа сделает обновления несовместимыми с установленным приложением.");
            string mainPath = Path.Combine(input, UpdateProtocol.MainExeName);
            string updaterPath = Path.Combine(input, UpdateProtocol.UpdaterExeName);
            if (!File.Exists(mainPath) || !File.Exists(updaterPath))
                throw new InvalidOperationException("В папке сборки должны быть AktiOplaty.exe и Updater.exe.");
            Version version = UpdateProtocol.ReadApplicationVersion(mainPath);
            Version updaterVersion = UpdateProtocol.ReadApplicationVersion(updaterPath);
            if (version != updaterVersion) throw new InvalidOperationException("Версии AktiOplaty.exe и Updater.exe различаются. Соберите оба файла заново.");
            if (version.Build < 0 || version.Revision < 0) throw new InvalidOperationException("Версия сборки должна содержать четыре части.");
            if (Directory.Exists(output))
                foreach (string name in new[] { PackageName, ManifestName, SignatureName })
                    if (Directory.Exists(Path.Combine(output, name))) throw new InvalidOperationException("Имя файла выпуска занято папкой: " + name + ".");
            Directory.CreateDirectory(output);
            string stage = Path.Combine(output, ".release-staging-" + Guid.NewGuid().ToString("N"));
            AssertChildPath(output, stage);
            Directory.CreateDirectory(stage);
            try
            {
                string packagePath = Path.Combine(stage, PackageName);
                using (FileStream file = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    AddFile(archive, mainPath, UpdateProtocol.MainExeName);
                    AddFile(archive, updaterPath, UpdateProtocol.UpdaterExeName);
                }
                UpdateManifest manifest = new UpdateManifest
                {
                    FormatVersion = 1,
                    Version = version.ToString(4),
                    MinDataVersion = 1,
                    MaxDataVersion = 1,
                    PackageFile = PackageName,
                    PackageSize = new FileInfo(packagePath).Length,
                    PackageSha256 = UpdateProtocol.HashFile(packagePath),
                    Files = new List<UpdateFile>
                    {
                        DescribeFile(mainPath, UpdateProtocol.MainExeName),
                        DescribeFile(updaterPath, UpdateProtocol.UpdaterExeName)
                    }
                };
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 65536 };
                byte[] manifestBytes = new UTF8Encoding(false, true).GetBytes(serializer.Serialize(manifest));
                byte[] signature = Sign(manifestBytes, keyPath);
                string manifestPath = Path.Combine(stage, ManifestName);
                string signaturePath = Path.Combine(stage, SignatureName);
                File.WriteAllBytes(manifestPath, manifestBytes);
                File.WriteAllBytes(signaturePath, signature);
                UpdateManifest verified;
                try { verified = UpdateProtocol.VerifyManifest(File.ReadAllBytes(manifestPath), File.ReadAllBytes(signaturePath)); }
                catch (Exception) { throw new InvalidOperationException("Подпись не прошла проверку встроенным публичным ключом. Проверьте ключ подписи и сборку выпуска."); }
                string verificationPath = Path.Combine(stage, "verified");
                UpdateProtocol.VerifyAndExtract(packagePath, verified, verificationPath);
                if (UpdateProtocol.HashFile(Path.Combine(verificationPath, UpdateProtocol.MainExeName)) != manifest.Files[0].Sha256
                    || UpdateProtocol.HashFile(Path.Combine(verificationPath, UpdateProtocol.UpdaterExeName)) != manifest.Files[1].Sha256)
                    throw new InvalidOperationException("Проверка распакованных файлов не пройдена.");
                foreach (string name in new[] { PackageName, SignatureName, ManifestName })
                    WriteAtomically(Path.Combine(stage, name), Path.Combine(output, name));
                UpdateManifest final = UpdateProtocol.VerifyManifest(File.ReadAllBytes(Path.Combine(output, ManifestName)), File.ReadAllBytes(Path.Combine(output, SignatureName)));
                if (UpdateProtocol.HashFile(Path.Combine(output, PackageName)) != final.PackageSha256
                    || new FileInfo(Path.Combine(output, PackageName)).Length != final.PackageSize)
                    throw new InvalidOperationException("Записанные файлы выпуска не прошли проверку.");
                Console.WriteLine("Готов подписанный выпуск " + version.ToString(4) + ".");
                Console.WriteLine("Проверены подпись, контрольные суммы, состав архива и одинаковые версии приложения и обновлятора.");
                Console.WriteLine("Файлы: " + PackageName + ", " + ManifestName + ", " + SignatureName + ".");
                Console.WriteLine("Публикация не выполнялась.");
            }
            finally
            {
                AssertChildPath(output, stage);
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
            }
        }

        private static byte[] Sign(byte[] bytes, string keyPath)
        {
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(new CspParameters(24)))
            {
                rsa.PersistKeyInCsp = false;
                try { rsa.FromXmlString(File.ReadAllText(keyPath)); }
                catch (Exception) { throw new InvalidOperationException("Не удалось прочитать приватный ключ подписи RSA."); }
                if (rsa.PublicOnly || rsa.KeySize < 2048) throw new InvalidOperationException("Для выпуска нужен приватный ключ RSA размером не менее 2048 бит.");
                return rsa.SignData(bytes, CryptoConfig.MapNameToOID("SHA256"));
            }
        }

        private static UpdateFile DescribeFile(string path, string name)
        {
            return new UpdateFile { Name = name, Size = new FileInfo(path).Length, Sha256 = UpdateProtocol.HashFile(path) };
        }

        private static void AddFile(ZipArchive archive, string path, string name)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using (Stream target = entry.Open())
            using (FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) source.CopyTo(target);
        }

        private static void WriteAtomically(string source, string target)
        {
            string parent = Path.GetDirectoryName(target);
            string temporary = Path.Combine(parent, ".release-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            AssertChildPath(parent, temporary);
            try
            {
                File.Copy(source, temporary, false);
                if (File.Exists(target)) File.Replace(temporary, target, null, true);
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void AssertChildPath(string parent, string child)
        {
            string parentPath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string childPath = Path.GetFullPath(child);
            if (!childPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Служебный путь выходит за пределы папки выпуска.");
        }
    }
}
