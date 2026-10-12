using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace WorkLedger
{
    public sealed class HourlyActLine
    {
        public string Description { get; set; }
        public decimal Hours { get; set; }
        public decimal Rate { get; set; }
        public decimal Amount { get { return Decimal.Round(Hours * Rate, 2, MidpointRounding.AwayFromZero); } }
    }

    public sealed class Record
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public Dictionary<string, string> Fields { get; set; }
        public Record() { Id = Guid.NewGuid().ToString("N"); Fields = new Dictionary<string, string>(StringComparer.Ordinal); }
        public Record(string kind) : this() { Kind = kind; }
        public string Get(string key) { string v; return Fields != null && Fields.TryGetValue(key, out v) ? v ?? "" : ""; }
        public void Set(string key, string value) { Fields[key] = value ?? ""; }
        public Record Clone() { return new Record { Id = Id, Kind = Kind, Fields = Fields == null ? new Dictionary<string, string>() : new Dictionary<string, string>(Fields, StringComparer.Ordinal) }; }
    }

    public sealed class AuditEntry
    {
        public string Id { get; set; }
        public string Date { get; set; }
        public string Action { get; set; }
        public string User { get; set; }
        public string Kind { get; set; }
        public string RecordId { get; set; }
        public string Summary { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public AuditEntry Clone() { return (AuditEntry)MemberwiseClone(); }
    }

    public sealed class LedgerDocument
    {
        public int Version { get; set; }
        public long Revision { get; set; }
        public List<Record> Records { get; set; }
        public List<AuditEntry> Audit { get; set; }
    }

    public sealed class Store
    {
        private static readonly string[] Kinds = { "party", "project", "contract", "amendment", "estimate", "work", "act", "invoice", "payment", "allocation", "expense", "hold", "document", "settings" };
        private static readonly string[] LinkKeys = { "partyId", "projectId", "contractId", "estimateId", "actId", "paymentId" };
        private readonly object gate = new object();
        private List<Record> records = new List<Record>();
        private List<AuditEntry> audit = new List<AuditEntry>();
        private string fingerprint;
        public string DataDir { get; private set; }
        public string DataPath { get { return Path.Combine(DataDir, "ledger.json"); } }
        public long Revision { get; private set; }
        public List<Record> Records { get { lock (gate) return records.Select(r => r.Clone()).ToList(); } }
        public List<AuditEntry> Audit { get { lock (gate) return audit.Select(a => a.Clone()).ToList(); } }
        public Store(string dataDir)
        {
            if (String.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("Не указана папка базы.");
            DataDir = Path.GetFullPath(dataDir);
            Directory.CreateDirectory(DataDir);
            Reload();
        }
        public IEnumerable<Record> Of(string kind) { lock (gate) return records.Where(r => r.Kind == kind && r.Get("archived") != "true").Select(r => r.Clone()).ToList(); }
        public Record Find(string id) { lock (gate) { Record r = records.FirstOrDefault(x => x.Id == id); return r == null ? null : r.Clone(); } }
        public long NextActNumber { get { lock (gate) return NextActNumberFor(records); } }
        public decimal DefaultHourlyRate
        {
            get { lock (gate) { Record settings = records.FirstOrDefault(r => r.Kind == "settings" && r.Get("archived") != "true"); return settings == null || settings.Get("hourlyRate") == "" ? 0m : ParseMoney(settings.Get("hourlyRate")); } }
        }
        private static long ReadActCounter(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return 500;
            long number;
            if (!Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 500 || number == Int64.MaxValue)
                throw new InvalidOperationException("Следующий номер акта должен быть целым числом не меньше 500.");
            return number;
        }
        private static long NextActNumberFor(List<Record> source)
        {
            long next = 500;
            Record settings = source.FirstOrDefault(r => r.Kind == "settings" && r.Get("archived") != "true");
            if (settings != null) next = ReadActCounter(settings.Get("nextActNumber"));
            foreach (Record act in source.Where(r => r.Kind == "act"))
            {
                long number;
                if (!Int64.TryParse(act.Get("number"), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < next) continue;
                if (number >= Int64.MaxValue - 1) throw new InvalidOperationException("Достигнут предел автоматической нумерации актов.");
                next = number + 1;
            }
            return next;
        }
        public Record CreateHourlyActDraft(string partyId)
        {
            lock (gate)
            {
                Record party = records.FirstOrDefault(r => r.Id == partyId && r.Kind == "party" && r.Get("archived") != "true");
                if (party == null) throw new InvalidOperationException("Выберите действующего контрагента.");
                Record settings = records.FirstOrDefault(r => r.Kind == "settings" && r.Get("archived") != "true");
                Record draft = new Record("act");
                draft.Set("partyId", partyId); draft.Set("date", DateString(DateTime.Today));
                draft.Set("currency", settings == null || settings.Get("currency") == "" ? "BYN" : settings.Get("currency"));
                draft.Set("direction", "income"); draft.Set("status", "draft");
                if (DefaultHourlyRate > 0) draft.Set("hourlyRate", MoneyString(DefaultHourlyRate));
                CaptureActSnapshots(draft);
                return draft;
            }
        }
        public void CaptureActSnapshots(Record act)
        {
            if (act == null || act.Kind != "act") throw new ArgumentException("Не указан акт.");
            lock (gate)
            {
                Record saved = records.FirstOrDefault(r => r.Id == act.Id && r.Kind == "act");
                if (saved != null && saved.Get("partyId") == act.Get("partyId") && saved.Get("partySnapshot") != "")
                {
                    act.Set("partySnapshot", saved.Get("partySnapshot")); act.Set("organizationSnapshot", saved.Get("organizationSnapshot")); return;
                }
                Record party = records.FirstOrDefault(r => r.Id == act.Get("partyId") && r.Kind == "party" && r.Get("archived") != "true");
                if (party == null) throw new InvalidOperationException("Выберите действующего контрагента.");
                Record settings = records.FirstOrDefault(r => r.Kind == "settings" && r.Get("archived") != "true");
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 100000, RecursionLimit = 20 };
                act.Set("partySnapshot", serializer.Serialize(party.Clone()));
                act.Set("organizationSnapshot", settings == null ? "" : serializer.Serialize(settings.Clone()));
            }
        }
        public static Record GetActSnapshot(Record act, string field)
        {
            if (field != "partySnapshot" && field != "organizationSnapshot") throw new ArgumentException("Неизвестный снимок реквизитов.");
            if (act == null || act.Get(field) == "") return null;
            try
            {
                Record snapshot = new JavaScriptSerializer { MaxJsonLength = 100000, RecursionLimit = 20 }.Deserialize<Record>(act.Get(field));
                if (snapshot == null || snapshot.Fields == null || snapshot.Kind != (field == "partySnapshot" ? "party" : "settings")) throw new InvalidOperationException();
                return snapshot.Clone();
            }
            catch (Exception e) { throw new InvalidOperationException("Некорректный снимок реквизитов акта.", e); }
        }
        public static List<HourlyActLine> GetHourlyActLines(Record act)
        {
            if (act == null || act.Get("workItems") == "") return new List<HourlyActLine>();
            try
            {
                List<HourlyActLine> lines = new JavaScriptSerializer { MaxJsonLength = 100000, RecursionLimit = 20 }.Deserialize<List<HourlyActLine>>(act.Get("workItems"));
                return CheckedHourlyActLines(lines);
            }
            catch (Exception e) { throw new InvalidOperationException("Некорректные строки работ акта. " + e.Message, e); }
        }
        private static List<HourlyActLine> CheckedHourlyActLines(IEnumerable<HourlyActLine> source)
        {
            if (source == null) throw new InvalidOperationException("Добавьте выполненные работы и часы.");
            List<HourlyActLine> lines = source.Take(101).ToList();
            if (lines.Count == 0 || lines.Count > 100) throw new InvalidOperationException("В акте должно быть от 1 до 100 строк работ.");
            var result = new List<HourlyActLine>();
            foreach (HourlyActLine line in lines)
            {
                if (line == null || String.IsNullOrWhiteSpace(line.Description) || line.Description.Trim().Length > 2000) throw new InvalidOperationException("Укажите наименование работы длиной до 2000 символов.");
                if (line.Hours <= 0 || line.Hours > 1000000000000m || Decimal.Round(line.Hours, 6) != line.Hours) throw new InvalidOperationException("Часы должны быть больше нуля и содержать не более 6 знаков после запятой.");
                if (line.Rate <= 0 || line.Rate > 1000000000000m || Decimal.Round(line.Rate, 2) != line.Rate) throw new InvalidOperationException("Ставка за час должна быть больше нуля и содержать не более 2 знаков после запятой.");
                if (line.Amount <= 0 || line.Amount > 1000000000000m) throw new InvalidOperationException("Сумма строки работ должна быть больше нуля и не превышать допустимый предел.");
                result.Add(new HourlyActLine { Description = line.Description.Trim(), Hours = line.Hours, Rate = line.Rate });
            }
            return result;
        }
        public static void SetHourlyActLines(Record act, IEnumerable<HourlyActLine> source)
        {
            if (act == null || act.Kind != "act") throw new ArgumentException("Не указан акт.");
            List<HourlyActLine> lines = CheckedHourlyActLines(source);
            decimal amount = lines.Sum(line => line.Amount), hours = lines.Sum(line => line.Hours);
            if (amount > 1000000000000m || hours > 1000000000000m) throw new InvalidOperationException("Итог акта превышает допустимый предел.");
            string json = new JavaScriptSerializer { MaxJsonLength = 100000, RecursionLimit = 20 }.Serialize(lines);
            if (json.Length > 100000) throw new InvalidOperationException("Слишком большой объём строк работ.");
            act.Set("workItems", json); act.Set("amount", MoneyString(amount));
            act.Set("hours", hours.ToString("0.######", CultureInfo.InvariantCulture));
            act.Set("hourlyRate", lines.All(line => line.Rate == lines[0].Rate) ? MoneyString(lines[0].Rate) : "");
            act.Set("description", String.Join("\r\n", lines.Select(line => line.Description + " — " + line.Hours.ToString("0.######", CultureInfo.GetCultureInfo("ru-RU")) + " ч")));
        }
        private static void StageActCounter(List<Record> staged, List<AuditEntry> entries)
        {
            long next = NextActNumberFor(staged);
            Record existing = staged.FirstOrDefault(r => r.Kind == "settings" && r.Get("archived") != "true");
            if (existing != null && existing.Get("nextActNumber") == next.ToString(CultureInfo.InvariantCulture)) return;
            Record updated = existing == null ? new Record("settings") : existing.Clone();
            if (existing == null) updated.Set("currency", "BYN");
            updated.Set("nextActNumber", next.ToString(CultureInfo.InvariantCulture));
            if (existing == null) staged.Add(updated); else staged[staged.IndexOf(existing)] = updated;
            entries.Add(MakeAudit(existing == null ? "create" : "update", existing, updated));
        }
        private static long NumericActNumber(string value)
        {
            long number; return Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : -1;
        }
        public void Reload()
        {
            lock (gate)
            {
                if (!File.Exists(DataPath))
                {
                    if (fingerprint != null) throw new InvalidOperationException("Файл базы исчез. Восстановите резервную копию.");
                    records = new List<Record>(); audit = new List<AuditEntry>(); Revision = 0; return;
                }
                byte[] bytes = File.ReadAllBytes(DataPath);
                LedgerDocument d = ReadDocument(bytes);
                records = d.Records.Select(r => r.Clone()).ToList();
                audit = d.Audit.Select(a => a.Clone()).ToList();
                Revision = d.Revision; fingerprint = Hash(bytes);
            }
        }
        public static decimal ParseMoney(string value)
        {
            decimal d = ParseDecimal(value);
            return Decimal.Round(d, 2, MidpointRounding.AwayFromZero);
        }
        public static string MoneyString(decimal value) { return Decimal.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture); }
        public static decimal Quantity(string value)
        {
            decimal d = ParseDecimal(value);
            if (d <= 0 || Decimal.Round(d, 6) != d) throw new InvalidOperationException("Количество должно быть больше нуля и содержать не более 6 знаков после запятой.");
            return d;
        }
        private static decimal ParseDecimal(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Не указана сумма или количество.");
            decimal d;
            string s = value.Trim().Replace(',', '.');
            if (!Decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out d)) throw new InvalidOperationException("Некорректное число: " + value);
            if (d > 1000000000000m || d < -1000000000000m) throw new InvalidOperationException("Число превышает допустимый предел.");
            return d;
        }
        public static string DateString(DateTime value) { return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public void Upsert(Record record)
        {
            if (record == null) throw new ArgumentNullException("record");
            lock (gate)
            {
                List<Record> staged = records.Select(r => r.Clone()).ToList();
                Record candidate = record.Clone();
                if (String.IsNullOrWhiteSpace(candidate.Id)) candidate.Id = Guid.NewGuid().ToString("N");
                Record old = staged.FirstOrDefault(r => r.Id == candidate.Id);
                if (old != null && old.Kind != candidate.Kind) throw new InvalidOperationException("Тип записи нельзя менять.");

                if (old != null && old.Kind == "allocation") throw new InvalidOperationException("Распределение оплаты нельзя редактировать. Используйте сторнирование распределения.");
                foreach (string key in LinkKeys)
                {
                    string id = candidate.Get(key);
                    Record target = staged.FirstOrDefault(r => r.Id == id);
                    if (target != null && target.Get("archived") == "true" && (old == null || old.Get(key) != id)) throw new InvalidOperationException("Нельзя привязать запись к архивному документу.");
                }
                bool automaticNumber = old == null && candidate.Kind == "act" && candidate.Get("number") == "";
                if (automaticNumber)
                {
                    candidate.Set("number", NextActNumberFor(staged).ToString(CultureInfo.InvariantCulture)); candidate.Set("autoNumber", "true");
                }
                if (old != null && old.Kind == "act" && old.Get("autoNumber") == "true")
                {
                    if (candidate.Get("number") != old.Get("number")) throw new InvalidOperationException("Автоматический номер сохранённого акта менять нельзя.");
                    candidate.Set("autoNumber", "true");
                }
                if (candidate.Kind == "settings") candidate.Set("nextActNumber", Math.Max(ReadActCounter(candidate.Get("nextActNumber")), NextActNumberFor(staged)).ToString(CultureInfo.InvariantCulture));
                Normalize(candidate);
                if (old == null) staged.Add(candidate); else staged[staged.IndexOf(old)] = candidate;
                Validate(staged);
                List<AuditEntry> entries = new List<AuditEntry>();
                if (automaticNumber) StageActCounter(staged, entries);
                Validate(staged);
                entries.Add(MakeAudit(old == null ? "create" : old.Get("archived") == "true" && candidate.Get("archived") != "true" ? "restore" : "update", old, candidate));
                Commit(staged, entries);
                record.Id = candidate.Id;
                record.Fields = new Dictionary<string, string>(candidate.Fields);
            }
        }
        public void ImportBatch(IEnumerable<Record> incoming)
        {
            if (incoming == null) throw new ArgumentNullException("incoming");
            lock (gate)
            {
                List<Record> source = incoming.ToList();
                if (source.Count == 0) return;
                if (source.Count > 10000) throw new InvalidOperationException("За один импорт допускается не более 10 000 строк.");
                List<Record> staged = records.Select(r => r.Clone()).ToList();
                List<Record> added = new List<Record>();
                HashSet<string> ids = new HashSet<string>(staged.Select(r => r.Id), StringComparer.Ordinal);
                for (int i = 0; i < source.Count; i++)
                {
                    try
                    {
                        if (source[i] == null) throw new InvalidOperationException("Пустая запись.");
                        Record r = source[i].Clone();
                        if (String.IsNullOrWhiteSpace(r.Id)) r.Id = Guid.NewGuid().ToString("N");
                        if (!ids.Add(r.Id)) throw new InvalidOperationException("Повторяющийся идентификатор. Импорт создаёт только новые записи.");
                        if (r.Get("archived") == "true") throw new InvalidOperationException("Нельзя импортировать архивную запись.");
                        if (r.Kind == "act" && r.Get("number") == "")
                        {
                            r.Set("number", NextActNumberFor(staged).ToString(CultureInfo.InvariantCulture)); r.Set("autoNumber", "true");
                        }
                        Normalize(r); added.Add(r); staged.Add(r);
                    }
                    catch (Exception e) { throw new InvalidOperationException("Строка импорта " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + e.Message, e); }
                }
                foreach (Record r in added)
                {
                    foreach (string key in LinkKeys)
                    {
                        Record target = staged.FirstOrDefault(x => x.Id == r.Get(key));
                        if (target != null && target.Get("archived") == "true") throw new InvalidOperationException("Строка импорта " + (added.IndexOf(r) + 1).ToString(CultureInfo.InvariantCulture) + ": нельзя привязать запись к архивной карточке.");
                    }
                }
                Validate(staged);
                List<AuditEntry> entries = new List<AuditEntry>();
                if (added.Any(r => r.Kind == "act" && r.Get("autoNumber") == "true")) StageActCounter(staged, entries);
                Validate(staged);
                entries.AddRange(added.Select(r => MakeAudit("import", null, r)));
                Commit(staged, entries);
                for (int i = 0; i < source.Count; i++) { source[i].Id = added[i].Id; source[i].Fields = new Dictionary<string, string>(added[i].Fields); }
            }
        }
        public void Archive(string id)
        {
            lock (gate)
            {
                Record old = records.FirstOrDefault(r => r.Id == id);
                if (old == null) throw new InvalidOperationException("Запись не найдена.");
                if (old.Get("archived") == "true") return;
                if (records.Any(r => r.Id != id && LinkKeys.Any(k => r.Get(k) == id))) throw new InvalidOperationException("Запись используется другими документами. Сначала разберите связанные записи.");
                if (old.Kind == "allocation") throw new InvalidOperationException("Распределение оплаты архивировать нельзя.");
                List<Record> staged = records.Select(r => r.Clone()).ToList();
                Record changed = staged.First(r => r.Id == id); changed.Set("archived", "true");
                Validate(staged); Commit(staged, MakeAudit("archive", old, changed));
            }
        }
        public void Allocate(string paymentId, string actId, decimal amount)
        {
            Record r = new Record("allocation");
            r.Set("paymentId", paymentId); r.Set("actId", actId); r.Set("amount", MoneyString(amount)); r.Set("date", DateString(DateTime.Today)); Upsert(r);
        }
        public void ReverseAllocation(string allocationId)
        {
            lock (gate)
            {
                Record old = records.FirstOrDefault(r => r.Id == allocationId && r.Kind == "allocation");
                if (old == null) throw new InvalidOperationException("Распределение не найдено.");
                if (old.Get("reversed") == "true") return;
                List<Record> staged = records.Select(r => r.Clone()).ToList();
                Record changed = staged.First(r => r.Id == allocationId); changed.Set("reversed", "true"); changed.Set("reversedDate", DateString(DateTime.Today));
                Validate(staged); Commit(staged, MakeAudit("reverse", old, changed));
            }
        }
        public decimal AllocatedToAct(string id) { lock (gate) return AllocatedTo(records, id); }
        public decimal AllocatedFromPayment(string id) { lock (gate) return AllocatedFrom(records, id); }
        public decimal ActiveHolds(string id) { lock (gate) return Holds(records, id); }
        public decimal ActGrossDue(string id)
        {
            lock (gate)
            {
                Record a = records.FirstOrDefault(r => r.Id == id && r.Kind == "act");
                return a == null || a.Get("status") != "signed" ? 0 : Math.Max(0, Amount(a) - AllocatedTo(records, id));
            }
        }
        public decimal ActDue(string id) { lock (gate) return Math.Max(0, ActGrossDue(id) - Holds(records, id)); }
        public decimal PaymentUnallocated(string id)
        {
            lock (gate) { Record p = records.FirstOrDefault(r => r.Id == id && r.Kind == "payment"); return p == null ? 0 : Math.Max(0, Amount(p) - AllocatedFrom(records, id)); }
        }
        public string PaymentStatus(string actId)
        {
            lock (gate)
            {
                Record a = records.FirstOrDefault(r => r.Id == actId && r.Kind == "act");
                if (a == null) return "Не найден";
                if (a.Get("status") == "cancelled") return "Отменён";
                if (a.Get("status") != "signed") return "Не подписан";
                decimal allocated = AllocatedTo(records, actId), gross = Amount(a) - allocated, held = Holds(records, actId);
                if (gross == 0) return "Оплачен";
                if (gross == held) return "Остаток удержан";
                return allocated > 0 ? "Частично оплачен" : "Не оплачен";
            }
        }
        public decimal EffectiveContractAmount(string id)
        {
            lock (gate)
            {
                Record c = records.FirstOrDefault(r => r.Id == id && r.Kind == "contract");
                return c == null ? 0 : Amount(c) + records.Where(r => r.Kind == "amendment" && r.Get("contractId") == id && r.Get("status") == "signed").Sum(r => Amount(r));
            }
        }
        public decimal ContractActTotal(string id) { lock (gate) return records.Where(r => r.Kind == "act" && r.Get("contractId") == id && r.Get("status") == "signed").Sum(r => Amount(r)); }
        public decimal EstimateTotal(string id) { lock (gate) return records.Where(r => r.Kind == "work" && r.Get("estimateId") == id && r.Get("archived") != "true").Sum(r => Amount(r)); }
        public void ExportSnapshot(string path)
        {
            lock (gate)
            {
                if (Path.GetFullPath(path).Equals(DataPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Выберите отдельный файл резервной копии.");
                AtomicWrite(Path.GetFullPath(path), Serialize(new LedgerDocument { Version = 1, Revision = Revision, Records = records, Audit = audit }));
            }
        }
        public void SaveBackup(string path) { ExportSnapshot(path); }
        public string ImportAttachment(string source)
        {
            string folder = Path.Combine(DataDir, "attachments"); Directory.CreateDirectory(folder);
            string name = Guid.NewGuid().ToString("N") + Path.GetExtension(source);
            File.Copy(source, Path.Combine(folder, name), false);
            return "attachments/" + name;
        }
        public string ResolveAttachment(string relativePath)
        {
            if (String.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) throw new InvalidOperationException("Некорректный путь вложения.");
            string full = Path.GetFullPath(Path.Combine(DataDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = Path.GetFullPath(Path.Combine(DataDir, "attachments")) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Вложение должно находиться в папке attachments базы.");
            return full;
        }
        private static decimal Amount(Record r) { return ParseMoney(r.Get("amount")); }
        private static decimal AllocatedTo(List<Record> rs, string id) { return rs.Where(r => r.Kind == "allocation" && r.Get("actId") == id && r.Get("reversed") != "true").Sum(r => Amount(r)); }
        private static decimal AllocatedFrom(List<Record> rs, string id) { return rs.Where(r => r.Kind == "allocation" && r.Get("paymentId") == id && r.Get("reversed") != "true").Sum(r => Amount(r)); }
        private static decimal Holds(List<Record> rs, string id) { return rs.Where(r => r.Kind == "hold" && r.Get("actId") == id && r.Get("status") == "active").Sum(r => Amount(r)); }
        private static void Normalize(Record r)
        {
            if (r.Fields == null) r.Fields = new Dictionary<string, string>();
            foreach (string key in r.Fields.Keys.ToList()) r.Fields[key] = (r.Fields[key] ?? "").Trim();
            if (!String.IsNullOrWhiteSpace(r.Get("amount"))) r.Set("amount", MoneyString(ParseMoney(r.Get("amount"))));
            if (r.Get("hourlyRate") != "")
            {
                decimal rate = ParseDecimal(r.Get("hourlyRate"));
                if (rate <= 0 || rate != ParseMoney(r.Get("hourlyRate"))) throw new InvalidOperationException("Ставка за час должна быть больше нуля и содержать не более 2 знаков после запятой.");
                r.Set("hourlyRate", MoneyString(rate));
            }
            if (r.Kind == "act" && r.Get("workItems") != "") SetHourlyActLines(r, GetHourlyActLines(r));
            if (r.Kind == "work")
            {
                decimal qty = Quantity(r.Get("qty")), price = ParseMoney(r.Get("price"));
                r.Set("qty", qty.ToString("0.######", CultureInfo.InvariantCulture)); r.Set("price", MoneyString(price)); r.Set("amount", MoneyString(qty * price));
            }
        }
        public static string FieldLabel(string key)
        {
            switch (key)
            {
                case "notes": return "Примечания"; case "contact": return "Контактное лицо"; case "phone": return "Телефон"; case "email": return "Электронная почта"; case "requisites": return "Реквизиты"; case "location": return "Адрес объекта"; case "unit": return "Единица измерения"; case "filePath": return "Вложение"; case "archived": return "Архив"; case "reversed": return "Сторнировано"; case "name": return "Наименование"; case "number": return "Номер"; case "date": return "Дата";
                case "hourlyRate": return "Ставка за час"; case "nextActNumber": return "Следующий номер акта"; case "hours": return "Всего часов"; case "workItems": return "Строки работ"; case "partySnapshot": return "Реквизиты контрагента в акте"; case "organizationSnapshot": return "Реквизиты организации в акте";
                case "dueDate": return "Срок оплаты"; case "signedDate": return "Дата подписания"; case "reversedDate": return "Дата сторнирования";
                case "website": return "Сайт"; case "chiefAccountantName": return "ФИО главного бухгалтера"; case "chiefAccountantPhone": return "Телефон главного бухгалтера";
                case "unp": return "УНП"; case "registrationNumber": return "Номер свидетельства / регистрации";
                case "registrationDate": return "Дата регистрации"; case "registrationAuthority": return "Регистрирующий орган";
                case "registrationAddress": return "Адрес регистрации"; case "postalAddress": return "Почтовый адрес"; case "actualAddress": return "Фактический адрес";
                case "bankName": return "Наименование банка"; case "bankAccount": return "Расчётный счёт (IBAN)"; case "bankCode": return "БИК / SWIFT";
                case "signatoryName": return "ФИО подписанта"; case "signatoryPosition": return "Должность / статус подписанта";
                case "signatoryBasis": return "Основание подписи"; case "vatNote": return "Налоги / НДС (для документов)";
                case "partyId": return "Контрагент"; case "projectId": return "Объект"; case "contractId": return "Договор";
                case "estimateId": return "Смета"; case "actId": return "Акт"; case "paymentId": return "Платёж";
                case "amount": return "Сумма"; case "currency": return "Валюта"; case "direction": return "Направление оплаты";
                case "status": return "Статус"; case "role": return "Роль контрагента"; case "method": return "Способ оплаты";
                case "category": return "Категория расхода"; case "type": return "Вид удержания"; case "qty": return "Количество";
                case "price": return "Цена"; case "description": return "Описание"; case "title": return "Название";
                default: return key;
            }
        }
        public static string KindLabel(string kind)
        {
            switch (kind)
            {
                case "party": return "Контрагенты"; case "project": return "Объекты"; case "contract": return "Договоры";
                case "amendment": return "Допсоглашения"; case "estimate": return "Сметы"; case "work": return "Работы сметы";
                case "act": return "Акты"; case "invoice": return "Счета"; case "payment": return "Оплаты";
                case "allocation": return "Распределение оплат"; case "expense": return "Расходы"; case "hold": return "Удержания";
                case "document": return "Документы"; case "settings": return "Организация"; default: return kind;
            }
        }
        private static void Required(Record r, params string[] keys)
        {
            foreach (string k in keys) if (String.IsNullOrWhiteSpace(r.Get(k))) throw new InvalidOperationException("Не заполнено обязательное поле «" + FieldLabel(k) + "» в разделе «" + KindLabel(r.Kind) + "».");
        }
        private static void Choice(Record r, string key, params string[] choices)
        {
            if (!choices.Contains(r.Get(key))) throw new InvalidOperationException("Недопустимое значение поля «" + FieldLabel(key) + "» в разделе «" + KindLabel(r.Kind) + "».");
        }
        private static void DateField(Record r, string key, bool required)
        {
            string s = r.Get(key); if (s == "" && !required) return;
            DateTime d; if (!DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) throw new InvalidOperationException("Дата «" + FieldLabel(key) + "» должна иметь формат ГГГГ-ММ-ДД.");
        }
        private static Record Link(Record r, string key, string kind, Dictionary<string, Record> byId, bool required)
        {
            string id = r.Get(key); if (id == "" && !required) return null;
            Record target; if (id == "" || !byId.TryGetValue(id, out target) || target.Kind != kind) throw new InvalidOperationException("Не найдена связанная запись «" + FieldLabel(key) + "» в разделе «" + KindLabel(r.Kind) + "».");
            return target;
        }
        private static void Same(Record a, Record b, params string[] keys)
        {
            foreach (string key in keys)
            {
                string av = a.Get(key), bv = b.Get(key);
                if (av != "" && bv != "" && av != bv) throw new InvalidOperationException("Связанные документы должны иметь одинаковое поле «" + FieldLabel(key) + "».");
            }
        }
        private static void Validate(List<Record> rs)
        {
            if (rs == null || rs.Count > 100000) throw new InvalidOperationException("Некорректный список записей базы.");
            Dictionary<string, Record> byId = new Dictionary<string, Record>(StringComparer.Ordinal);
            foreach (Record r in rs)
            {
                if (r == null || String.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 100 || !Kinds.Contains(r.Kind) || r.Fields == null) throw new InvalidOperationException("База содержит некорректную запись.");
                if (byId.ContainsKey(r.Id)) throw new InvalidOperationException("База содержит повторяющийся идентификатор.");
                byId.Add(r.Id, r);
                if (r.Fields.Count > 100 || r.Fields.Any(p => p.Key == null || p.Key.Length > 100 || (p.Value != null && p.Value.Length > 100000))) throw new InvalidOperationException("Слишком большой размер поля.");
                if (r.Get("archived") != "" && r.Get("archived") != "true" && r.Get("archived") != "false") throw new InvalidOperationException("Некорректный признак архива.");
                foreach (string moneyKey in new[] { "amount", "price", "hourlyRate" }) if (r.Get(moneyKey) != "" && ParseDecimal(r.Get(moneyKey)) != ParseMoney(r.Get(moneyKey))) throw new InvalidOperationException("В базе сумма «" + FieldLabel(moneyKey) + "» содержит доли копейки.");
                foreach (string key in new[] { "date", "dueDate", "signedDate", "reversedDate" }) if (r.Get(key) != "") DateField(r, key, false);
                if (r.Get("currency") != "" && !new[] { "BYN", "RUB", "USD", "EUR" }.Contains(r.Get("currency"))) throw new InvalidOperationException("Выберите валюту BYN, RUB, USD или EUR.");
            }
            foreach (Record r in rs)
            {
                Record party = Link(r, "partyId", "party", byId, r.Kind == "contract" || r.Kind == "estimate" || r.Kind == "act" || r.Kind == "invoice" || r.Kind == "payment");
                Record project = Link(r, "projectId", "project", byId, r.Kind == "expense");
                Record contract = Link(r, "contractId", "contract", byId, r.Kind == "amendment");
                Record act = Link(r, "actId", "act", byId, r.Kind == "allocation" || r.Kind == "hold");
                if (project != null && r.Get("direction") == "income") Same(r, project, "partyId");
                if (contract != null) Same(r, contract, "partyId", "projectId", "currency", "direction");
                if (act != null && r.Kind != "allocation" && r.Kind != "hold") Same(r, act, "partyId", "projectId", "contractId", "currency", "direction");
                if (r.Kind == "party") { Required(r, "name"); Choice(r, "role", "client", "vendor", "both"); }
                else if (r.Kind == "project") { Required(r, "name"); Choice(r, "status", "active", "complete"); }
                else if (r.Kind == "contract") { Required(r, "number", "amount", "currency", "direction"); Choice(r, "status", "active", "complete", "cancelled"); }
                else if (r.Kind == "amendment") { Required(r, "number", "amount"); Choice(r, "status", "draft", "signed", "cancelled"); }
                else if (r.Kind == "estimate") { Required(r, "number", "currency", "direction"); Choice(r, "status", "draft", "approved", "cancelled"); }
                else if (r.Kind == "work")
                {
                    Link(r, "estimateId", "estimate", byId, true); Required(r, "description", "qty", "price", "amount");
                    decimal q = Quantity(r.Get("qty")), p = ParseMoney(r.Get("price"));
                    if (p < 0 || Amount(r) != Decimal.Round(q * p, 2, MidpointRounding.AwayFromZero)) throw new InvalidOperationException("Сумма работы должна равняться количеству, умноженному на цену.");
                }
                else if (r.Kind == "act")
                {
                    Required(r, "number", "amount", "currency", "direction"); Choice(r, "status", "draft", "sent", "signed", "cancelled");
                    if (r.Get("workItems") != "")
                    {
                        List<HourlyActLine> lines = GetHourlyActLines(r);
                        if (Amount(r) != lines.Sum(line => line.Amount) || r.Get("hours") == "" || ParseDecimal(r.Get("hours")) != lines.Sum(line => line.Hours))
                            throw new InvalidOperationException("Итог акта должен равняться сумме работ и часов.");
                    }
                    if (r.Get("partySnapshot") != "") GetActSnapshot(r, "partySnapshot");
                    if (r.Get("organizationSnapshot") != "") GetActSnapshot(r, "organizationSnapshot");
                    if (r.Get("autoNumber") == "true")
                    {
                        long automatic;
                        if (!Int64.TryParse(r.Get("number"), NumberStyles.None, CultureInfo.InvariantCulture, out automatic) || automatic < 500) throw new InvalidOperationException("Некорректный автоматический номер акта.");
                        if (rs.Any(other => other.Kind == "act" && other.Id != r.Id && NumericActNumber(other.Get("number")) == automatic)) throw new InvalidOperationException("Такой номер акта уже использован.");
                    }
                }
                else if (r.Kind == "invoice") { Required(r, "number", "amount", "currency", "direction"); Choice(r, "status", "draft", "sent", "cancelled"); }
                else if (r.Kind == "payment") { Required(r, "amount", "currency", "direction"); Choice(r, "method", "bank", "cash", "other"); }
                else if (r.Kind == "expense") { Required(r, "amount", "currency"); Choice(r, "category", "materials", "labor", "transport", "rent", "other"); }
                else if (r.Kind == "hold") { Required(r, "amount"); Choice(r, "status", "active", "released"); Choice(r, "type", "warranty", "penalty", "other"); if (r.Get("status") == "active" && act.Get("status") != "signed") throw new InvalidOperationException("Удержание возможно только по подписанному акту."); }
                else if (r.Kind == "document") { Required(r, "title"); Choice(r, "status", "missing", "draft", "sent", "signed"); string f = r.Get("filePath"); if (f != "" && (Path.IsPathRooted(f) || !f.Replace('\\', '/').StartsWith("attachments/", StringComparison.Ordinal) || f.Split('/', '\\').Contains(".."))) throw new InvalidOperationException("Недопустимый путь вложения."); }
                else if (r.Kind == "settings")
                {
                    DateField(r, "registrationDate", false);
                    if (r.Get("nextActNumber") != "") ReadActCounter(r.Get("nextActNumber"));
                    if (r.Get("hourlyRate") != "" && ParseMoney(r.Get("hourlyRate")) <= 0) throw new InvalidOperationException("Ставка за час должна быть больше нуля.");
                }
                else if (r.Kind == "allocation")
                {
                    Record payment = Link(r, "paymentId", "payment", byId, true); Required(r, "amount");
                    if (r.Get("reversed") != "" && r.Get("reversed") != "true" && r.Get("reversed") != "false") throw new InvalidOperationException("Некорректное сторнирование.");
                    if (r.Get("reversed") != "true")
                    {
                        if (act.Get("status") != "signed") throw new InvalidOperationException("Оплату можно распределить только на подписанный акт.");
                        Same(payment, act, "partyId", "projectId", "contractId", "currency", "direction");
                    }
                }
                if (new[] { "contract", "amendment", "estimate", "act", "invoice", "payment", "allocation", "expense", "hold" }.Contains(r.Kind)) DateField(r, "date", true);
                if (new[] { "contract", "act", "invoice", "payment", "allocation", "expense", "hold" }.Contains(r.Kind) && Amount(r) <= 0) throw new InvalidOperationException("Сумма должна быть больше нуля.");
                if (r.Kind == "amendment" && Amount(r) == 0) throw new InvalidOperationException("Сумма допсоглашения не может быть нулевой.");
                if (new[] { "contract", "estimate", "act", "invoice", "payment" }.Contains(r.Kind)) Choice(r, "direction", "income", "expense");
                if (new[] { "contract", "estimate", "act", "invoice", "payment", "expense" }.Contains(r.Kind)) Required(r, "currency");
            }
            foreach (Record a in rs.Where(r => r.Kind == "act"))
            {
                decimal allocated = AllocatedTo(rs, a.Id), holds = Holds(rs, a.Id);
                if (allocated + holds > Amount(a)) throw new InvalidOperationException("Распределённые оплаты и удержания превышают сумму акта.");
                if (a.Get("status") != "signed" && (allocated > 0 || holds > 0)) throw new InvalidOperationException("Нельзя отменить подпись акта с оплатами или удержаниями.");
            }
            foreach (Record p in rs.Where(r => r.Kind == "payment")) if (AllocatedFrom(rs, p.Id) > Amount(p)) throw new InvalidOperationException("Распределение превышает сумму платежа.");
            foreach (Record c in rs.Where(r => r.Kind == "contract")) if (Amount(c) + rs.Where(r => r.Kind == "amendment" && r.Get("contractId") == c.Id && r.Get("status") == "signed").Sum(r => Amount(r)) < 0) throw new InvalidOperationException("Допсоглашения уменьшают договор ниже нуля.");
            var duplicates = rs.Where(r => r.Get("number") != "" && r.Get("archived") != "true" && new[] { "contract", "amendment", "estimate", "act", "invoice" }.Contains(r.Kind)).GroupBy(r => r.Kind + "|" + r.Get("partyId") + "|" + r.Get("contractId") + "|" + r.Get("number").ToUpperInvariant());
            if (duplicates.Any(g => g.Count() > 1)) throw new InvalidOperationException("Документ с таким номером уже есть для этого контрагента и договора.");
            if (rs.Count(r => r.Kind == "settings" && r.Get("archived") != "true") > 1) throw new InvalidOperationException("Допускается одна карточка организации.");
        }
        public static string ActionLabel(string action)
        {
            switch (action)
            {
                case "create": return "Создано"; case "update": return "Изменено"; case "archive": return "В архив";
                case "restore": return "Восстановлено"; case "import": return "Импортировано"; case "reverse": return "Сторнировано"; default: return action;
            }
        }
        private static AuditEntry MakeAudit(string action, Record before, Record after)
        {
            string title = after.Get("number"); if (title == "") title = after.Get("name"); if (title == "") title = after.Get("title");
            return new AuditEntry { Id = Guid.NewGuid().ToString("N"), Date = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Action = action, User = Environment.UserName, Kind = after.Kind, RecordId = after.Id, Summary = ActionLabel(action) + ": " + KindLabel(after.Kind) + (title == "" ? "" : " — " + title), Before = before == null ? "" : FieldsText(before), After = FieldsText(after) };
        }
        private static string FieldsText(Record r) { return String.Join("\r\n", r.Fields.OrderBy(p => p.Key).Select(p => FieldLabel(p.Key) + ": " + p.Value)); }
        private void Commit(List<Record> staged, AuditEntry entry) { Commit(staged, new List<AuditEntry> { entry }); }
        private void Commit(List<Record> staged, List<AuditEntry> entries)
        {
            List<AuditEntry> nextAudit = audit.Select(a => a.Clone()).ToList(); nextAudit.AddRange(entries);
            if (Revision == Int64.MaxValue) throw new InvalidOperationException("Достигнут предел версии базы.");
            LedgerDocument d = new LedgerDocument { Version = 1, Revision = Revision + 1, Records = staged, Audit = nextAudit };
            byte[] bytes = Serialize(d);
            try
            {
                using (FileStream fileLock = new FileStream(Path.Combine(DataDir, "ledger.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    if (File.Exists(DataPath))
                    {
                        byte[] current = File.ReadAllBytes(DataPath);
                        if (fingerprint == null || Hash(current) != fingerprint) throw new InvalidOperationException("База изменена другим процессом. Перезагрузите данные перед сохранением.");
                    }
                    else if (fingerprint != null) throw new InvalidOperationException("Файл базы исчез. Сохранение остановлено.");
                    AtomicWrite(DataPath, bytes);
                }
            }
            catch (IOException e) { throw new IOException("Не удалось сохранить базу. Изменения не применены. " + e.Message, e); }
            records = staged; audit = nextAudit; Revision = d.Revision; fingerprint = Hash(bytes);
        }
        private static void AtomicWrite(string target, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(target); Directory.CreateDirectory(directory);
            string temp = Path.Combine(directory, ".ledger-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { fs.Write(bytes, 0, bytes.Length); fs.Flush(true); }
                if (File.Exists(target)) File.Replace(temp, target, target + ".bak", true); else File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static byte[] Serialize(LedgerDocument d)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 32000000, RecursionLimit = 100 };
            return new UTF8Encoding(false, true).GetBytes(serializer.Serialize(d));
        }
        private static LedgerDocument ReadDocument(byte[] bytes)
        {
            try
            {
                if (bytes.Length > 32000000) throw new InvalidOperationException("База превышает допустимый размер.");
                string json = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 32000000, RecursionLimit = 100 };
                LedgerDocument d = serializer.Deserialize<LedgerDocument>(json);
                if (d == null || d.Version != 1 || d.Revision < 0 || d.Records == null || d.Audit == null || d.Audit.Any(a => a == null)) throw new InvalidOperationException("Неизвестный или повреждённый формат базы.");
                Validate(d.Records); return d;
            }
            catch (Exception e) { throw new InvalidOperationException("База повреждена или несовместима. Исходный файл сохранён; используйте проверенную резервную копию. " + e.Message, e); }
        }
        private static string Hash(byte[] bytes) { using (SHA256 h = SHA256.Create()) return Convert.ToBase64String(h.ComputeHash(bytes)); }
    }
}




