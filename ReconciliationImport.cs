using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace WorkLedger
{
    public sealed class ReconciliationEntry
    {
        public Record Before { get; set; }
        public Record Record { get; set; }
        public string[] UpdateFields { get; set; }
    }
    public sealed class ReconciliationPackage
    {
        public string Format { get; set; }
        public int Version { get; set; }
        public string PackageId { get; set; }
        public string Title { get; set; }
        public Dictionary<string, string> SourceHashes { get; set; }
        public List<ReconciliationEntry> Entries { get; set; }
    }
    public sealed class ReconciliationPlan
    {
        public string PackageId { get; internal set; }
        public string Title { get; internal set; }
        public List<Record> Records { get; internal set; }
        public int NewCount { get; internal set; }
        public int ExistingCount { get; internal set; }
        public int UpdatedCount { get { return ExistingCount; } }
        public int ChangedCount { get { return Records.Count; } }
        public int UnchangedCount { get; internal set; }
        public int ConflictCount { get { return Conflicts.Count; } }
        public List<string> Conflicts { get; internal set; }
        internal string BaselineFingerprint;
    }
    public static class ReconciliationImport
    {
        private const int MaxBytes = 32000000, MaxRecords = 10000, MaxField = 100000;
        private static readonly string[] AllowedKinds = { "party", "act", "payment", "document" };
        private static readonly string[] Metadata = { "reviewStatus", "reviewNotes", "candidateActIds", "sourceActCandidates", "sourcePositionsJson", "reviewPackageId" };
        private static readonly string[] Positions = { "workItems", "printItemsJson", "descriptiveItemsJson", "printDescription", "description", "hours", "hourlyRate" };
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 100 }; }

        public static ReconciliationPackage Read(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new InvalidDataException("Файл сверки не найден.");
            if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Пакет сверки превышает 32 МБ.");
            Dictionary<string, object> root;
            try { root = Serializer().DeserializeObject(File.ReadAllText(path, new UTF8Encoding(false, true)).TrimStart('\uFEFF')) as Dictionary<string, object>; }
            catch (Exception exception) { throw new InvalidDataException("Не удалось прочитать JSON пакета сверки: " + exception.Message, exception); }
            if (root == null) throw new InvalidDataException("Пакет сверки должен быть объектом JSON.");
            object version;
            if (!root.TryGetValue("Version", out version) || !(version is int)) throw new InvalidDataException("Укажите числовую версию пакета сверки.");
            var package = new ReconciliationPackage { Format = StringValue(root, "Format"), Version = (int)version,
                PackageId = StringValue(root, "PackageId"), Title = StringValue(root, "Title"), SourceHashes = new Dictionary<string, string>(StringComparer.Ordinal),
                Entries = new List<ReconciliationEntry>() };
            object hashes; Dictionary<string, object> hashObject;
            if (!root.TryGetValue("SourceHashes", out hashes) || (hashObject = hashes as Dictionary<string, object>) == null)
                throw new InvalidDataException("SourceHashes должен содержать объект с контрольными суммами исходников.");
            foreach (var hash in hashObject)
            {
                if (!(hash.Value is string)) throw new InvalidDataException("Контрольные суммы исходников должны быть строками.");
                package.SourceHashes.Add(hash.Key, (string)hash.Value);
            }
            object entries; object[] entryArray;
            if (!root.TryGetValue("Entries", out entries) || (entryArray = entries as object[]) == null)
                throw new InvalidDataException("Entries должен содержать массив записей сверки.");
            foreach (object item in entryArray)
            {
                var data = item as Dictionary<string, object>;
                if (data == null) throw new InvalidDataException("Повреждена запись сверки.");
                object before, record, updates;
                if (!data.TryGetValue("Record", out record)) throw new InvalidDataException("Запись сверки не содержит Record.");
                var entry = new ReconciliationEntry { Record = ReadRecord(record), Before = data.TryGetValue("Before", out before) && before != null ? ReadRecord(before) : null };
                if (data.TryGetValue("UpdateFields", out updates) && updates != null)
                {
                    object[] names = updates as object[];
                    if (names == null || names.Any(name => !(name is string))) throw new InvalidDataException("UpdateFields должен содержать массив названий полей.");
                    entry.UpdateFields = names.Cast<string>().ToArray();
                }
                package.Entries.Add(entry);
            }
            ValidatePackage(package);
            return package;
        }
        private static string StringValue(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || !(value is string)) throw new InvalidDataException("Поле «" + key + "» должно быть строкой.");
            return (string)value;
        }
        private static Record ReadRecord(object data)
        {
            var source = data as Dictionary<string, object>;
            if (source == null) throw new InvalidDataException("Record и Before должны быть объектами записей.");
            object fields; Dictionary<string, object> fieldObject;
            if (!source.TryGetValue("Fields", out fields) || (fieldObject = fields as Dictionary<string, object>) == null)
                throw new InvalidDataException("Запись должна содержать объект Fields.");
            var record = new Record(StringValue(source, "Kind")) { Id = StringValue(source, "Id") };
            foreach (var field in fieldObject)
            {
                if (!(field.Value is string)) throw new InvalidDataException("Значения Fields должны быть строками: " + record.Id + "/" + field.Key + ".");
                record.Set(field.Key, (string)field.Value);
            }
            return record;
        }
        private static void ValidateRecord(Record record)
        {
            if (record == null || String.IsNullOrWhiteSpace(record.Id) || record.Id != record.Id.Trim() || record.Id.Length > 100 || record.Id.Any(Char.IsControl)
                || !AllowedKinds.Contains(record.Kind) || record.Fields == null || record.Fields.Count > 100)
                throw new InvalidDataException("Некорректная запись сверки. Допустимы только контрагенты, акты, оплаты и документы.");
            if (record.Fields.Any(field => String.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 100 || field.Value == null || field.Value.Length > MaxField))
                throw new InvalidDataException("Некорректное или слишком большое поле записи сверки.");
        }
        private static void ValidatePackage(ReconciliationPackage package)
        {
            if (package == null || package.Format != "WorkLedger.Reconciliation" || package.Version != 1)
                throw new InvalidDataException("Неизвестный формат пакета сверки. Требуется WorkLedger.Reconciliation версии 1.");
            if (String.IsNullOrWhiteSpace(package.PackageId) || package.PackageId.Length > 100 || package.PackageId != package.PackageId.Trim()
                || package.PackageId.Any(Char.IsControl) || String.IsNullOrWhiteSpace(package.Title) || package.Title.Length > 500)
                throw new InvalidDataException("Укажите идентификатор и название пакета сверки.");
            if (package.SourceHashes == null || package.SourceHashes.Count > 1000 || package.SourceHashes.Any(hash => String.IsNullOrWhiteSpace(hash.Key)
                || hash.Key.Length > 500 || hash.Value == null || hash.Value.Length != 64 || hash.Value.Any(c => !Uri.IsHexDigit(c))))
                throw new InvalidDataException("Некорректные контрольные суммы исходников пакета сверки.");
            if (package.Entries == null || package.Entries.Count == 0 || package.Entries.Count > MaxRecords)
                throw new InvalidDataException("В пакете сверки должно быть от 1 до 10 000 записей.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ReconciliationEntry entry in package.Entries)
            {
                if (entry == null) throw new InvalidDataException("Пустая запись сверки.");
                ValidateRecord(entry.Record);
                if (!new[] { "", "pending", "matched", "checked" }.Contains(entry.Record.Get("reviewStatus")))
                    throw new InvalidDataException("Неизвестная отметка сверки. Допустимы pending, matched или checked.");
                if (!ids.Add(entry.Record.Id)) throw new InvalidDataException("Повторяющийся идентификатор в пакете сверки: " + entry.Record.Id + ".");
                if (entry.Before != null)
                {
                    ValidateRecord(entry.Before);
                    if (entry.Before.Id != entry.Record.Id || entry.Before.Kind != entry.Record.Kind)
                        throw new InvalidDataException("Before должен относиться к той же записи, что и Record.");
                }
                if (entry.UpdateFields != null && (entry.UpdateFields.Distinct(StringComparer.Ordinal).Count() != entry.UpdateFields.Length
                    || entry.UpdateFields.Any(field => !Metadata.Contains(field) && !Positions.Contains(field))))
                    throw new InvalidDataException("Пакет пытается изменить защищённое поле. Разрешены только отметки сверки и позиции акта.");
                if (entry.Record.Kind != "act" && entry.UpdateFields != null && entry.UpdateFields.Any(Positions.Contains))
                    throw new InvalidDataException("Позиции разрешены только для актов.");
            }
        }
        public static ReconciliationPlan PlanReadOnly(Store store, ReconciliationPackage package)
        {
            if (store == null) throw new ArgumentNullException("store");
            return PlanReadOnly(store.Records, package);
        }
        public static ReconciliationPlan PlanReadOnly(IEnumerable<Record> snapshot, ReconciliationPackage package)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            ValidatePackage(package);
            List<Record> baseline = snapshot.Select(record => record.Clone()).ToList();
            var byId = baseline.ToDictionary(record => record.Id, StringComparer.Ordinal);
            var plan = new ReconciliationPlan { PackageId = package.PackageId, Title = package.Title, Records = new List<Record>(),
                Conflicts = new List<string>(), BaselineFingerprint = Fingerprint(baseline) };
            foreach (ReconciliationEntry entry in package.Entries)
            {
                Record current;
                bool existing = byId.TryGetValue(entry.Record.Id, out current);
                if (existing && current.Kind != entry.Record.Kind) throw new InvalidDataException("Тип существующей записи не совпадает: " + entry.Record.Id + ".");
                Record source = entry.Record.Clone(), desired = source.Clone(), result = existing ? current.Clone() : desired.Clone();
                string[] updates = entry.UpdateFields ?? desired.Fields.Keys.Where(key => Metadata.Contains(key) || desired.Kind == "act" && Positions.Contains(key)).ToArray();
                if (existing && desired.Kind == "party" && updates.Length == 0) { plan.UnchangedCount++; continue; }
                var reasons = new List<string>();
                bool checkedByUser = existing && current.Get("reviewStatus") == "checked";
                if (desired.Kind == "act" && updates.Any(Positions.Contains) && HasPositions(desired))
                {
                    try
                    {
                        string summary; ActHtmlDocument.ReadDisplayRows(desired, out summary);
                        if (desired.Get("workItems") != "")
                        {
                            Record normalized = desired.Clone(); Store.SetHourlyActLines(normalized, Store.GetHourlyActLines(desired));
                            foreach (string key in new[] { "workItems", "hours", "hourlyRate", "description" }) desired.Set(key, normalized.Get(key));
                        }
                    }
                    catch (Exception exception)
                    {
                        reasons.Add("Позиции не прошли проверку: " + exception.Message);
                        foreach (string key in new[] { "workItems", "printItemsJson", "descriptiveItemsJson", "hours", "hourlyRate" }) desired.Fields.Remove(key);
                        TryDescriptiveFallback(desired, source);
                        updates = updates.Concat(new[] { "workItems", "printItemsJson", "descriptiveItemsJson", "hours", "hourlyRate" }).Distinct(StringComparer.Ordinal).ToArray();
                    }
                }
                foreach (string key in Metadata.Where(updates.Contains))
                {
                    if (key == "reviewStatus" && checkedByUser) continue;
                    if (key == "reviewNotes") result.Set(key, MergeNote(result.Get(key), desired.Get(key)));
                    else if (desired.Fields.ContainsKey(key)) result.Set(key, desired.Get(key));
                }
                if (desired.Kind != "party")
                {
                    result.Set("reviewPackageId", package.PackageId);
                    if (result.Get("reviewStatus") == "") result.Set("reviewStatus", "pending");
                    if (result.Get("reviewNotes") == "") result.Set("reviewNotes", "Материалы загружены для сверки. Проверьте исходные документы.");
                }
                bool financialConflict = existing && !SameFinancial(current, entry.Before ?? desired);
                if (financialConflict) reasons.Add("Основные реквизиты или сумма изменены в программе; данные и позиции сохранены без замены.");
                if (existing && entry.Before == null && !SameDesiredSource(current, desired))
                    reasons.Add("Идентификатор уже существует, но исходное состояние не задано; существующая запись сохранена.");
                string[] positionUpdates = updates.Where(Positions.Contains).ToArray();
                bool positionsConflict = existing && positionUpdates.Length > 0 && positionUpdates.Concat(new[] { "workItems", "printItemsJson", "descriptiveItemsJson" })
                    .Distinct(StringComparer.Ordinal).Any(key => !CanReplace(current, entry.Before, desired, key));
                if (!positionsConflict && existing && positionUpdates.Length > 0)
                {
                    foreach (string key in new[] { "workItems", "printItemsJson", "descriptiveItemsJson" })
                        if (current.Get(key) != "" && desired.Get(key) == "" && !positionUpdates.Contains(key)) positionsConflict = true;
                }
                if (positionsConflict) reasons.Add("Позиции или описание изменены пользователем; текущие работы сохранены.");
                if (desired.Kind == "act" && (positionUpdates.Length > 0 || !existing && HasPositions(desired)))
                {
                    if (!financialConflict && !positionsConflict && !(existing && entry.Before == null && !SameDesiredSource(current, desired)))
                    {
                        if (existing) foreach (string key in positionUpdates) { if (desired.Fields.ContainsKey(key)) result.Set(key, desired.Get(key)); else result.Fields.Remove(key); }
                        try
                        {
                            string summary; ActHtmlDocument.ReadDisplayRows(result, out summary);
                            if (result.Get("workItems") != "")
                            {
                                Record normalized = result.Clone(); Store.SetHourlyActLines(normalized, Store.GetHourlyActLines(result));
                                if (Store.ParseMoney(normalized.Get("amount")) != Store.ParseMoney(result.Get("amount"))) throw new InvalidOperationException("Итог почасовых работ не совпадает с суммой акта.");
                                foreach (string key in new[] { "workItems", "hours", "hourlyRate" }) result.Set(key, normalized.Get(key));
                            }
                        }
                        catch (Exception exception)
                        {
                            reasons.Add("Позиции не прошли проверку: " + exception.Message);
                            RestorePositions(result, existing ? current : null);
                            if (!HasPositions(result)) TryDescriptiveFallback(result, desired);
                        }
                    }
                    if (reasons.Count > 0) PreserveSourcePositions(result, source);
                }
                if (reasons.Count > 0)
                {
                    string reason = String.Join(" ", reasons.Distinct().ToArray());
                    bool knownConflict = existing && current.Get("reviewNotes").IndexOf("Сверка: " + reason, StringComparison.Ordinal) >= 0
                        && (source.Kind != "act" || SourcePositions(source) == current.Get("sourcePositionsJson"));
                    result.Set("reviewStatus", checkedByUser && knownConflict ? "checked" : "pending");
                    result.Set("reviewNotes", MergeNote(result.Get("reviewNotes"), "Сверка: " + reason));
                    plan.Conflicts.Add(Caption(result) + ": " + reason);
                }
                ValidateRecord(result);
                if (existing && EqualRecord(current, result)) plan.UnchangedCount++;
                else
                {
                    plan.Records.Add(result);
                    if (existing) plan.ExistingCount++; else plan.NewCount++;
                }
            }
            return plan;
        }
        private static bool CanReplace(Record current, Record before, Record desired, string key)
        {
            string value = current.Get(key);
            return value == "" || PositionEqual(key, value, desired.Get(key)) || before != null && PositionEqual(key, value, before.Get(key));
        }
        private static bool SameFinancial(Record current, Record before)
        {
            if (current.Kind != "act" && current.Kind != "payment") return true;
            string[] keys = current.Kind == "payment" ? new[] { "number", "date", "partyId", "amount", "currency", "direction", "method" }
                : new[] { "number", "date", "partyId", "amount", "currency", "direction" };
            return keys.All(key => FinancialEqual(key, current.Get(key), before.Get(key)));
        }
        private static bool FinancialEqual(string key, string a, string b)
        {
            if (key != "amount") return a == b;
            decimal x, y;
            return Decimal.TryParse(a.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out x)
                && Decimal.TryParse(b.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out y) && x == y;
        }
        private static bool HasPositions(Record record) { return new[] { "workItems", "printItemsJson", "descriptiveItemsJson" }.Any(key => record.Get(key) != ""); }
        private static void RestorePositions(Record target, Record previous)
        {
            foreach (string key in Positions)
                if (previous != null && previous.Fields.ContainsKey(key)) target.Set(key, previous.Get(key)); else target.Fields.Remove(key);
        }
        private static bool PositionEqual(string key, string a, string b)
        {
            if (a == b) return true;
            if (a == "" || b == "") return false;
            try
            {
                if (key == "workItems")
                {
                    var ra = new Record("act"); ra.Set(key, a); var rb = new Record("act"); rb.Set(key, b);
                    var la = Store.GetHourlyActLines(ra); var lb = Store.GetHourlyActLines(rb);
                    return la.Count == lb.Count && la.Select((line, i) => line.Description == lb[i].Description && line.Hours == lb[i].Hours && line.Rate == lb[i].Rate).All(value => value);
                }
                if (key == "printItemsJson")
                {
                    var ra = new Record("act"); ra.Set(key, a); var rb = new Record("act"); rb.Set(key, b);
                    var la = ActDocument.ReadItems(ra); var lb = ActDocument.ReadItems(rb);
                    return la.Count == lb.Count && la.Select((line, i) => line.Description == lb[i].Description && line.Unit == lb[i].Unit && line.Quantity == lb[i].Quantity && line.Price == lb[i].Price).All(value => value);
                }
                if (key == "descriptiveItemsJson") return Serializer().Serialize(Serializer().DeserializeObject(a)) == Serializer().Serialize(Serializer().DeserializeObject(b));
                if (key == "hours" || key == "hourlyRate") return FinancialEqual("amount", a, b);
            }
            catch { return false; }
            return a.Trim() == b.Trim();
        }
        private static string SourcePositions(Record source)
        {
            if (source.Get("sourcePositionsJson") != "") return source.Get("sourcePositionsJson");
            var values = Positions.Where(key => source.Fields.ContainsKey(key)).ToDictionary(key => key, key => source.Get(key), StringComparer.Ordinal);
            if (values.Count == 0) return "";
            string json = Serializer().Serialize(values);
            if (json.Length > MaxField) throw new InvalidDataException("Исходные позиции превышают допустимый размер поля.");
            return json;
        }
        private static void PreserveSourcePositions(Record target, Record source)
        {
            string json = SourcePositions(source);
            if (json != "") target.Set("sourcePositionsJson", json);
        }
        private static void TryDescriptiveFallback(Record target, Record source)
        {
            var descriptions = new List<string>();
            foreach (string key in new[] { "descriptiveItemsJson", "printItemsJson", "workItems" })
            {
                if (source.Get(key) == "") continue;
                try
                {
                    object[] values = Serializer().DeserializeObject(source.Get(key)) as object[];
                    if (values == null) continue;
                    descriptions = values.Select(value => value is string ? (string)value : value is Dictionary<string, object>
                        ? (value as Dictionary<string, object>).ContainsKey("Description") ? (value as Dictionary<string, object>)["Description"] as string : null : null).ToList();
                    if (descriptions.Count > 0 && descriptions.All(value => !String.IsNullOrWhiteSpace(value))) break;
                }
                catch { descriptions.Clear(); }
            }
            if (descriptions.Count == 0)
            {
                List<ActHtmlLine> numbered; string heading;
                if (ActHtmlDocument.TryReadNumberedOperations(source.Get("description"), out numbered, out heading)) descriptions = numbered.Select(row => row.Description).ToList();
                else if (source.Get("description") != "") descriptions.Add(source.Get("description"));
            }
            try
            {
                target.Set("descriptiveItemsJson", ActHtmlDocument.SerializeDescriptions(descriptions));
                if (source.Get("printDescription").Length <= 2000 && source.Get("printDescription") != "") target.Set("printDescription", source.Get("printDescription"));
                string summary; ActHtmlDocument.ReadDisplayRows(target, out summary);
            }
            catch { target.Fields.Remove("descriptiveItemsJson"); }
        }
        private static string MergeNote(string current, string addition)
        {
            current = (current ?? "").Trim(); addition = (addition ?? "").Trim();
            if (addition == "" || current.IndexOf(addition, StringComparison.Ordinal) >= 0) return current;
            if (current == "") return addition;
            string result = current + "\r\n" + addition;
            if (result.Length > MaxField) throw new InvalidDataException("Примечания сверки превышают допустимый размер поля.");
            return result;
        }
        private static string Caption(Record record)
        {
            string name = record.Get("number"); if (name == "") name = record.Get("name"); if (name == "") name = record.Get("title");
            return Store.KindLabel(record.Kind) + (name == "" ? "" : " «" + name + "»");
        }
        private static bool SameDesiredSource(Record current, Record desired)
        {
            return current.Id == desired.Id && current.Kind == desired.Kind && desired.Fields.Where(field => !Metadata.Contains(field.Key)).All(field =>
                field.Key == "amount" ? FinancialEqual(field.Key, current.Get(field.Key), field.Value)
                : PositionEqual(field.Key, current.Get(field.Key), field.Value));
        }
        private static bool EqualRecord(Record a, Record b)
        {
            return a.Id == b.Id && a.Kind == b.Kind && a.Fields.Count == b.Fields.Count
                && a.Fields.All(field => b.Fields.ContainsKey(field.Key) && b.Get(field.Key) == field.Value);
        }
        private static string Fingerprint(IEnumerable<Record> records)
        {
            var text = new StringBuilder();
            foreach (Record record in records.OrderBy(record => record.Id, StringComparer.Ordinal))
            {
                Append(text, record.Id); Append(text, record.Kind); Append(text, record.Fields.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var field in record.Fields.OrderBy(field => field.Key, StringComparer.Ordinal)) { Append(text, field.Key); Append(text, field.Value ?? ""); }
            }
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
        }
        private static void Append(StringBuilder builder, string value) { builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value); }
        public static ReconciliationPlan Apply(Store store, ReconciliationPackage package) { return Apply(store, PlanReadOnly(store, package)); }
        public static ReconciliationPlan Apply(Store store, ReconciliationPlan plan)
        {
            if (store == null || plan == null) throw new ArgumentNullException(store == null ? "store" : "plan");
            if (plan.Records.Count == 0) return plan;
            List<Record> current = store.Records;
            var byId = current.ToDictionary(record => record.Id, StringComparer.Ordinal);
            if (plan.Records.All(record => byId.ContainsKey(record.Id) && EqualRecord(byId[record.Id], record))) return plan;
            if (Fingerprint(current) != plan.BaselineFingerprint) throw new InvalidOperationException("База изменилась после предпросмотра сверки. Откройте пакет заново; изменения не применены.");
            store.ApplyReconciliation(plan.Records.Select(record => record.Clone()).ToList());
            return plan;
        }
    }
}