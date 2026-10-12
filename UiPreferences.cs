using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace WorkLedger
{
    public sealed class UiSection
    {
        public string Key { get; private set; }
        public string Text { get; private set; }
        public string Group { get; private set; }
        public bool IsMain { get { return Key != "updates"; } }
        public UiSection(string key, string text, string group) { Key = key; Text = text; Group = group; }
        public override string ToString() { return Text; }
    }

    public sealed class UiPreferences
    {
        private sealed class SavedPreferences
        {
            public SavedPreferences() { }
            public int Version { get; set; }
            public bool? AnimationsEnabled { get; set; }
            public string[] HiddenSections { get; set; }
        }
        public static readonly IList<UiSection> Sections = Array.AsReadOnly(new[]
        {
            new UiSection("overview", "Обзор", "Главное"),
            new UiSection("create-act", "Создать акт", "Главное"),
            new UiSection("party", "Контрагенты", "Работы и документы"),
            new UiSection("project", "Объекты", "Работы и документы"),
            new UiSection("contract", "Договоры", "Работы и документы"),
            new UiSection("amendment", "Допсоглашения", "Работы и документы"),
            new UiSection("estimate", "Сметы", "Работы и документы"),
            new UiSection("work", "Работы в сметах", "Работы и документы"),
            new UiSection("act", "Акты", "Работы и документы"),
            new UiSection("invoice", "Счета", "Финансы"),
            new UiSection("payment", "Оплаты и авансы", "Финансы"),
            new UiSection("expense", "Расходы", "Финансы"),
            new UiSection("hold", "Удержания", "Финансы"),
            new UiSection("document", "Документы", "Работы и документы"),
            new UiSection("reports", "Отчёты и сверка", "Контроль"),
            new UiSection("audit", "История изменений", "Контроль"),
            new UiSection("updates", "Обновления", "Контроль")
        });
        public HashSet<string> HiddenSections { get; private set; }
        public bool AnimationsEnabled { get; set; }
        public bool HasVisibleMainSection { get { return Sections.Any(section => section.IsMain && IsVisible(section.Key)); } }

        public UiPreferences()
        {
            HiddenSections = new HashSet<string>(StringComparer.Ordinal);
            AnimationsEnabled = true;
        }
        public bool IsVisible(string key) { return !HiddenSections.Contains(key); }
        public void SetVisible(string key, bool visible)
        {
            if (!Sections.Any(section => section.Key == key)) throw new ArgumentException("Неизвестный раздел интерфейса.", "key");
            if (visible) HiddenSections.Remove(key); else HiddenSections.Add(key);
        }
        public UiPreferences Clone()
        {
            var copy = new UiPreferences { AnimationsEnabled = AnimationsEnabled };
            foreach (string key in HiddenSections) copy.HiddenSections.Add(key);
            return copy;
        }
        public void Validate()
        {
            if (!HasVisibleMainSection) throw new InvalidOperationException("Оставьте хотя бы один основной раздел. Можно выбрать «Акты» или «Создать акт».");
        }
        private static string CurrentUserIdentity()
        {
            try
            {
                using (WindowsIdentity user = WindowsIdentity.GetCurrent())
                    if (user.User != null) return user.User.Value;
            }
            catch (SystemException) { }
            return Environment.UserDomainName + "\\" + Environment.UserName;
        }
        public static string GetProfilePath(string dataDir, string userIdentity = null)
        {
            if (String.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("Не указана папка данных.", "dataDir");
            string identity = userIdentity ?? CurrentUserIdentity();
            if (String.IsNullOrWhiteSpace(identity)) throw new ArgumentException("Не указан пользователь.", "userIdentity");
            string profile;
            using (SHA256 hash = SHA256.Create())
                profile = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
            return Path.Combine(Path.GetFullPath(dataDir), "ui-preferences", profile + ".json");
        }
        public static UiPreferences Load(string dataDir, string userIdentity = null)
        {
            var defaults = new UiPreferences();
            try
            {
                string path = GetProfilePath(dataDir, userIdentity);
                if (!File.Exists(path) || new FileInfo(path).Length > 32768) return defaults;
                var serializer = new JavaScriptSerializer { MaxJsonLength = 32768 };
                SavedPreferences saved = serializer.Deserialize<SavedPreferences>(File.ReadAllText(path, Encoding.UTF8));
                if (saved == null || saved.Version != 1 || !saved.AnimationsEnabled.HasValue || saved.HiddenSections == null) return defaults;
                var result = new UiPreferences { AnimationsEnabled = saved.AnimationsEnabled.Value };
                foreach (string key in saved.HiddenSections)
                    if (key != null && Sections.Any(section => section.Key == key)) result.HiddenSections.Add(key);
                result.Validate();
                return result;
            }
            catch (IOException) { return defaults; }
            catch (UnauthorizedAccessException) { return defaults; }
            catch (ArgumentException) { return defaults; }
            catch (InvalidOperationException) { return defaults; }
            catch (FormatException) { return defaults; }
            catch (OverflowException) { return defaults; }
            catch (System.Security.SecurityException) { return defaults; }
        }
        public void Save(string dataDir, string userIdentity = null)
        {
            Validate();
            string path = GetProfilePath(dataDir, userIdentity);
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            var saved = new SavedPreferences
            {
                Version = 1,
                AnimationsEnabled = AnimationsEnabled,
                HiddenSections = Sections.Where(section => !IsVisible(section.Key)).Select(section => section.Key).ToArray()
            };
            string temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(saved), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}