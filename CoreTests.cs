using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WorkLedger
{
    public static class CoreTests
    {
        private static int failures;
        private static int passes;
        public static int Run(string tempdir)
        {
            failures = 0; passes = 0;
            string root = Path.Combine(Path.GetFullPath(tempdir), "core-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Test("Money uses exact decimal cents", delegate {
                Eq(Store.ParseMoney("12,345"), 12.35m); Eq(Store.ParseMoney("-0.005"), -0.01m); Eq(Store.MoneyString(0.005m), "0.01");
                Reject(delegate { Store.ParseMoney("1e4"); }); Reject(delegate { Store.ParseMoney("1.2.3"); }); Reject(delegate { Store.Quantity("0"); });
            });
            Test("Persistent records, audit and copy isolation", delegate {
                Store s = Fresh(root, "persist"); Record p = Party(s, "Customer");
                long rev = s.Revision; Record copy = s.Find(p.Id); copy.Set("name", "Changed only in copy"); Eq(s.Find(p.Id).Get("name"), "Customer");
                s.Upsert(copy); Eq(s.Revision, rev + 1); Store loaded = new Store(s.DataDir); Eq(loaded.Find(p.Id).Get("name"), "Changed only in copy");
                Eq(loaded.Audit.Count, 2); True(File.Exists(s.DataPath + ".bak")); True(loaded.Audit.Last().Before.Contains("Customer"));
            });
            Test("One payment pays several signed acts, one act receives several payments", delegate {
                Store s = Fresh(root, "multi"); Record party = Party(s, "Multi"); Record a = Act(s, party, "A1", 100m, "signed"); Record b = Act(s, party, "A2", 80m, "signed"); Record p = Payment(s, party, 120m);
                s.Allocate(p.Id, a.Id, 70m); s.Allocate(p.Id, b.Id, 50m); Eq(s.PaymentUnallocated(p.Id), 0m); Eq(s.ActDue(a.Id), 30m); Eq(s.ActDue(b.Id), 30m);
                Record p2 = Payment(s, party, 40m); s.Allocate(p2.Id, a.Id, 30m); Eq(s.ActDue(a.Id), 0m); Eq(s.PaymentUnallocated(p2.Id), 10m); Eq(s.PaymentStatus(a.Id), "Оплачен");
            });
            Test("Over-allocation and reducing amounts roll back completely", delegate {
                Store s = Fresh(root, "caps"); Record party = Party(s, "Caps"); Record a = Act(s, party, "CAP", 100m, "signed"); Record p = Payment(s, party, 100m); s.Allocate(p.Id, a.Id, 60m);
                long rev = s.Revision; int count = s.Records.Count; string bytes = File.ReadAllText(s.DataPath);
                Reject(delegate { s.Allocate(p.Id, a.Id, 40.01m); }); Eq(s.Revision, rev); Eq(s.Records.Count, count); Eq(File.ReadAllText(s.DataPath), bytes);
                Record edit = s.Find(a.Id); edit.Set("amount", "59.99"); Reject(delegate { s.Upsert(edit); }); Eq(s.Find(a.Id).Get("amount"), "100.00");
                edit = s.Find(p.Id); edit.Set("amount", "59.99"); Reject(delegate { s.Upsert(edit); }); Eq(s.Revision, rev);
            });
            Test("Unsigned and cancelled acts produce no debt", delegate {
                Store s = Fresh(root, "status"); Record party = Party(s, "Status"); Record draft = Act(s, party, "D", 100m, "draft"); Record cancelled = Act(s, party, "C", 100m, "cancelled"); Record p = Payment(s, party, 100m);
                Eq(s.ActDue(draft.Id), 0m); Eq(s.ActDue(cancelled.Id), 0m); Reject(delegate { s.Allocate(p.Id, draft.Id, 1m); });
                Record signed = Act(s, party, "S", 100m, "signed"); s.Allocate(p.Id, signed.Id, 1m); Record edit = s.Find(signed.Id); edit.Set("status", "cancelled"); Reject(delegate { s.Upsert(edit); });
            });
            Test("Currency, party, direction and contract allocation mismatches are rejected", delegate {
                Store s = Fresh(root, "match"); Record p1 = Party(s, "Match1"), p2 = Party(s, "Match2"); Record a = Act(s, p1, "M", 100m, "signed");
                Record payment = Payment(s, p1, 100m, "USD", "income"); Reject(delegate { s.Allocate(payment.Id, a.Id, 1m); });
                payment = Payment(s, p2, 100m); Reject(delegate { s.Allocate(payment.Id, a.Id, 1m); });
                payment = Payment(s, p1, 100m, "BYN", "expense"); Reject(delegate { s.Allocate(payment.Id, a.Id, 1m); });
                Record c1 = Contract(s, p1, "C1", 100m), c2 = Contract(s, p1, "C2", 100m); Record linkedAct = Act(s, p1, "L", 100m, "signed", c1.Id);
                Record pay = R("payment", "date", "2026-10-11", "partyId", p1.Id, "contractId", c2.Id, "amount", "100", "currency", "BYN", "direction", "income", "method", "bank"); s.Upsert(pay); Reject(delegate { s.Allocate(pay.Id, linkedAct.Id, 1m); });
            });
            Test("Hold reduces payable debt and releasing it permits final payment", delegate {
                Store s = Fresh(root, "holds"); Record party = Party(s, "Hold"); Record a = Act(s, party, "H", 100m, "signed"); Record p = Payment(s, party, 100m);
                Record h = R("hold", "actId", a.Id, "date", "2026-10-11", "amount", "20", "status", "active", "type", "warranty"); s.Upsert(h); Eq(s.ActDue(a.Id), 80m); Eq(s.ActGrossDue(a.Id), 100m);
                s.Allocate(p.Id, a.Id, 80m); Eq(s.PaymentStatus(a.Id), "Остаток удержан"); Reject(delegate { s.Allocate(p.Id, a.Id, 0.01m); });
                Record edit = s.Find(h.Id); edit.Set("amount", "20.01"); Reject(delegate { s.Upsert(edit); });
                edit = s.Find(h.Id); edit.Set("status", "released"); s.Upsert(edit); Eq(s.ActDue(a.Id), 20m); s.Allocate(p.Id, a.Id, 20m); Eq(s.ActGrossDue(a.Id), 0m);
            });
            Test("Work line totals and signed contract amendments are derived", delegate {
                Store s = Fresh(root, "derive"); Record party = Party(s, "Derive"); Record c = Contract(s, party, "D-C", 100m);
                Record am = R("amendment", "number", "D-AM", "date", "2026-10-11", "contractId", c.Id, "amount", "-20", "status", "signed"); s.Upsert(am); Eq(s.EffectiveContractAmount(c.Id), 80m);
                Record tooMuch = R("amendment", "number", "D-AM2", "date", "2026-10-11", "contractId", c.Id, "amount", "-81", "status", "signed"); Reject(delegate { s.Upsert(tooMuch); });
                Record e = R("estimate", "number", "E", "date", "2026-10-11", "partyId", party.Id, "currency", "BYN", "direction", "income", "status", "draft"); s.Upsert(e);
                Record line = R("work", "estimateId", e.Id, "description", "Line", "qty", "1.005", "price", "2", "amount", "999"); s.Upsert(line); Eq(s.EstimateTotal(e.Id), 2.01m); Eq(s.Find(line.Id).Get("amount"), "2.01");
                Eq(new Store(s.DataDir).EstimateTotal(e.Id), 2.01m);
            });
            Test("Archiving retains financial amounts; dependencies cannot be archived", delegate {
                Store s = Fresh(root, "archive"); Record party = Party(s, "Archive"); Record a = Act(s, party, "AR", 100m, "signed"); Reject(delegate { s.Archive(party.Id); });
                s.Archive(a.Id); Eq(s.Of("act").Count(), 0); Eq(s.ActDue(a.Id), 100m); Record payment = Payment(s, party, 100m); Reject(delegate { s.Allocate(payment.Id, a.Id, 1m); });
                Record restore = s.Find(a.Id); restore.Set("archived", "false"); s.Upsert(restore); Eq(s.Of("act").Count(), 1); Eq(s.Audit.Last().Action, "restore"); s.Allocate(payment.Id, a.Id, 1m); Reject(delegate { s.Archive(a.Id); });
            });
            Test("Reversal preserves history and returns payment advance", delegate {
                Store s = Fresh(root, "reverse"); Record party = Party(s, "Reverse"); Record a = Act(s, party, "RV", 100m, "signed"); Record p = Payment(s, party, 100m); s.Allocate(p.Id, a.Id, 100m);
                Record allocation = s.Of("allocation").Single(); s.ReverseAllocation(allocation.Id); Eq(s.ActDue(a.Id), 100m); Eq(s.PaymentUnallocated(p.Id), 100m); Eq(s.Records.Count(r => r.Kind == "allocation"), 1); Eq(s.Audit.Last().Action, "reverse");
            });
            Test("Write failure leaves in-memory state and ledger bytes unchanged", delegate {
                Store s = Fresh(root, "failure"); Record party = Party(s, "Failure"); long rev = s.Revision; int audits = s.Audit.Count; string data = File.ReadAllText(s.DataPath);
                using (FileStream held = new FileStream(s.DataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Record edit = s.Find(party.Id); edit.Set("name", "Must not persist"); Reject(delegate { s.Upsert(edit); });
                }
                Eq(s.Revision, rev); Eq(s.Audit.Count, audits); Eq(s.Find(party.Id).Get("name"), "Failure"); Eq(File.ReadAllText(s.DataPath), data);
            });
            Test("Stale concurrent store cannot overwrite a newer ledger", delegate {
                Store s = Fresh(root, "stale"); Record party = Party(s, "Original"); Store stale = new Store(s.DataDir);
                Record edit = s.Find(party.Id); edit.Set("name", "Current"); s.Upsert(edit);
                Record staleEdit = stale.Find(party.Id); staleEdit.Set("name", "Stale"); Reject(delegate { stale.Upsert(staleEdit); }); Eq(new Store(s.DataDir).Find(party.Id).Get("name"), "Current"); Eq(stale.Find(party.Id).Get("name"), "Original");
            });
            Test("Malformed database is preserved and cannot open as empty", delegate {
                string dir = Path.Combine(root, "corrupt"); Directory.CreateDirectory(dir); string path = Path.Combine(dir, "ledger.json"); File.WriteAllText(path, "{malformed");
                Reject(delegate { new Store(dir); }); Eq(File.ReadAllText(path), "{malformed");
            });
            Test("Snapshot backup reloads and document path stays in attachments", delegate {
                Store s = Fresh(root, "backup"); Record party = Party(s, "Backup"); string dir = Path.Combine(root, "restore"); Directory.CreateDirectory(dir); s.SaveBackup(Path.Combine(dir, "ledger.json"));
                Eq(new Store(dir).Find(party.Id).Get("name"), "Backup"); Reject(delegate { s.ResolveAttachment("attachments/../../ledger.json"); }); Reject(delegate { s.ResolveAttachment(s.DataPath); });
                string source = Path.Combine(root, "source.txt"); File.WriteAllText(source, "attachment"); string relative = s.ImportAttachment(source); Eq(File.ReadAllText(s.ResolveAttachment(relative)), "attachment");
            });
            Test("Invalid dates, missing links and duplicate numbers are rejected", delegate {
                Store s = Fresh(root, "invalid"); Record party = Party(s, "Invalid"); Record a = Act(s, party, "UNIQ", 100m, "signed");
                Record dup = R("act", "number", "UNIQ", "date", "2026-10-11", "partyId", party.Id, "amount", "10", "currency", "BYN", "direction", "income", "status", "signed"); Reject(delegate { s.Upsert(dup); });
                Record edit = s.Find(a.Id); edit.Set("date", "2026-02-30"); Reject(delegate { s.Upsert(edit); }); edit = s.Find(a.Id); edit.Set("partyId", "missing"); Reject(delegate { s.Upsert(edit); });
            });
            Test("Batch import is atomic and creates audit for all rows", delegate {
                Store s = Fresh(root, "batch"); Record party = Party(s, "Batch");
                Record a = R("act", "number", "B1", "date", "2026-10-11", "partyId", party.Id, "amount", "100", "currency", "BYN", "direction", "income", "status", "signed");
                Record b = R("act", "number", "B2", "date", "2026-02-30", "partyId", party.Id, "amount", "100", "currency", "BYN", "direction", "income", "status", "signed");
                long rev = s.Revision; string bytes = File.ReadAllText(s.DataPath); Reject(delegate { s.ImportBatch(new[] { a, b }); }); Eq(s.Revision, rev); Eq(s.Of("act").Count(), 0); Eq(File.ReadAllText(s.DataPath), bytes);
                b.Set("date", "2026-10-11"); s.ImportBatch(new[] { a, b }); Eq(s.Revision, rev + 1); Eq(s.Of("act").Count(), 2); Eq(s.Audit.Count(x => x.Action == "import"), 2);
                Reject(delegate { s.ImportBatch(new[] { a }); }); Eq(s.Of("act").Count(), 2);
            });
            Test("Supplier expense and outgoing act may use customer project", delegate {
                Store s = Fresh(root, "supplier"); Record customer = Party(s, "Customer"), vendor = Party(s, "Vendor");
                Record project = R("project", "name", "Customer site", "partyId", customer.Id, "status", "active"); s.Upsert(project);
                Record expense = R("expense", "date", "2026-10-11", "projectId", project.Id, "partyId", vendor.Id, "amount", "10", "currency", "BYN", "category", "materials"); s.Upsert(expense);
                Record c = R("contract", "number", "OUT", "date", "2026-10-11", "partyId", vendor.Id, "projectId", project.Id, "amount", "100", "currency", "BYN", "direction", "expense", "status", "active"); s.Upsert(c);
                Record a = R("act", "number", "OUT-A", "date", "2026-10-11", "partyId", vendor.Id, "projectId", project.Id, "contractId", c.Id, "amount", "50", "currency", "BYN", "direction", "expense", "status", "signed"); s.Upsert(a); Eq(s.ActDue(a.Id), 50m);
            });
            Test("Parent edits validate existing financial descendants", delegate {
                Store s = Fresh(root, "parents"); Record party = Party(s, "Parent"), other = Party(s, "Other"); Record c = Contract(s, party, "PAR", 100m); Act(s, party, "PAR-A", 50m, "signed", c.Id);
                Record edit = s.Find(c.Id); edit.Set("currency", "USD"); Reject(delegate { s.Upsert(edit); }); edit = s.Find(c.Id); edit.Set("partyId", other.Id); Reject(delegate { s.Upsert(edit); });
                Eq(s.Find(c.Id).Get("currency"), "BYN"); Eq(s.Find(c.Id).Get("partyId"), party.Id);
            });
            Test("Stored fractions of a cent are rejected without replacing the file", delegate {
                Store s = Fresh(root, "cent-corrupt"); Record party = Party(s, "Cents"); Act(s, party, "CENT", 100m, "signed");
                string json = File.ReadAllText(s.DataPath); True(json.Contains("\"amount\":\"100.00\"")); string changed = json.Replace("\"amount\":\"100.00\"", "\"amount\":\"100.001\""); File.WriteAllText(s.DataPath, changed);
                Reject(delegate { new Store(s.DataDir); }); Eq(File.ReadAllText(s.DataPath), changed);
            });
            Test("Legacy organization remains optional and preserves additional fields", delegate {
                Store s = Fresh(root, "organization-legacy");
                Record settings = R("settings", "currency", "BYN", "requisites", "Прежние реквизиты\r\nСчёт 000123", "contact", "Прежний контакт", "customSetting", "Сохранить неизвестное поле");
                s.Upsert(settings);
                Store loaded = new Store(s.DataDir); Record edit = loaded.Find(settings.Id);
                Eq(edit.Get("name"), ""); Eq(edit.Get("unp"), ""); Eq(edit.Get("registrationDate"), "");
                edit.Set("name", "ИП Тестовый"); edit.Set("unp", "001234567"); edit.Set("registrationDate", ""); loaded.Upsert(edit);
                Record saved = new Store(s.DataDir).Find(settings.Id);
                Eq(saved.Get("requisites"), settings.Get("requisites")); Eq(saved.Get("contact"), settings.Get("contact"));
                Eq(saved.Get("customSetting"), "Сохранить неизвестное поле"); Eq(saved.Get("unp"), "001234567");
                Record party = R("party", "name", "Другой тип записи", "role", "client", "registrationDate", "Прежняя произвольная дата"); loaded.Upsert(party);
                Eq(new Store(s.DataDir).Find(party.Id).Get("registrationDate"), "Прежняя произвольная дата");
            });
            Test("Organization requisites survive reload and snapshot backup without changing text", delegate {
                Store s = Fresh(root, "organization-requisites");
                Record settings = R("settings", "name", "ИП Иванов Иван Иванович", "currency", "BYN", "unp", "001234567",
                    "registrationNumber", "Свидетельство № 001-A", "registrationDate", "2000-02-29", "registrationAuthority", "Минский горисполком",
                    "registrationAddress", "220001, г. Минск, ул. Регистрации, д. 1", "postalAddress", "220002, г. Минск, а/я 12",
                    "actualAddress", "г. Минск, ул. Рабочая, д. 2\r\nПомещение 003", "phone", "+375 29 000 00 00", "email", "ip@example.com",
                    "bankName", "Банк «Тест»", "bankAccount", "BY13 NBRB 3600 9000 0000 2Z00 AB00", "bankCode", "nBrBbY2x",
                    "signatoryName", "Иванов Иван Иванович", "signatoryPosition", "Индивидуальный предприниматель",
                    "signatoryBasis", "Свидетельство о государственной регистрации", "vatNote", "Условие НДС по договору",
                    "website", "https://ip.example.com", "chiefAccountantName", "Петрова Анна Ивановна", "chiefAccountantPhone", "+375 17 000 00 00",
                    "requisites", "Дополнительные реквизиты", "contact", "Дополнительный контакт", "customSetting", "Legacy value");
                s.Upsert(settings); Store loaded = new Store(s.DataDir);
                foreach (var field in settings.Fields) Eq(loaded.Find(settings.Id).Get(field.Key), field.Value);
                string restoredDir = Path.Combine(root, "organization-restored"); Directory.CreateDirectory(restoredDir);
                loaded.SaveBackup(Path.Combine(restoredDir, "ledger.json")); Store restored = new Store(restoredDir);
                foreach (var field in settings.Fields) Eq(restored.Find(settings.Id).Get(field.Key), field.Value);
                Eq(restored.Revision, loaded.Revision); Eq(restored.Audit.Count, loaded.Audit.Count);
                True(restored.Audit.Last().After.Contains("УНП: 001234567"));
                True(restored.Audit.Last().After.Contains("Расчётный счёт (IBAN): " + settings.Get("bankAccount")));
                True(restored.Audit.Last().After.Contains("Фактический адрес: " + settings.Get("actualAddress")));
            });
            Test("Invalid organization registration date rolls back completely", delegate {
                Store s = Fresh(root, "organization-date"); Record settings = R("settings", "currency", "BYN", "registrationDate", "2015-12-31"); s.Upsert(settings);
                long revision = s.Revision; int audits = s.Audit.Count; string bytes = File.ReadAllText(s.DataPath);
                foreach (string invalid in new[] { "2026-02-30", "31.12.2015", "2026-2-3" })
                {
                    Record edit = s.Find(settings.Id); edit.Set("registrationDate", invalid); edit.Set("name", "Must not persist"); Reject(delegate { s.Upsert(edit); });
                    Eq(s.Revision, revision); Eq(s.Audit.Count, audits); Eq(File.ReadAllText(s.DataPath), bytes);
                    Eq(s.Find(settings.Id).Get("registrationDate"), "2015-12-31"); Eq(s.Find(settings.Id).Get("name"), "");
                }
                Record cleared = s.Find(settings.Id); cleared.Set("registrationDate", ""); s.Upsert(cleared);
                Eq(new Store(s.DataDir).Find(settings.Id).Get("registrationDate"), "");
                cleared.Set("registrationDate", "2015-12-31"); s.Upsert(cleared);
                string corrupted = File.ReadAllText(s.DataPath).Replace("\"registrationDate\":\"2015-12-31\"", "\"registrationDate\":\"2015-02-30\"");
                True(corrupted.Contains("\"registrationDate\":\"2015-02-30\"")); File.WriteAllText(s.DataPath, corrupted);
                Reject(delegate { new Store(s.DataDir); }); Eq(File.ReadAllText(s.DataPath), corrupted);
            });
            Test("Legacy counterparties remain valid and shared requisites survive reload and backup", delegate {
                Store s = Fresh(root, "party-requisites"); Record party = R("party", "name", "Поставщик «Тест»", "role", "vendor"); s.Upsert(party);
                Store legacy = new Store(s.DataDir); Record edit = legacy.Find(party.Id);
                Eq(edit.Get("name"), "Поставщик «Тест»"); Eq(edit.Get("role"), "vendor");
                Eq(edit.Get("unp"), ""); Eq(edit.Get("postalAddress"), ""); Eq(edit.Get("chiefAccountantName"), "");
                string[] details = new[] {
                    "unp", "000123456", "registrationNumber", "Свидетельство № 00012", "registrationAuthority", "Минский райисполком",
                    "registrationAddress", "220001, г. Минск, ул. Регистрации, д. 1", "postalAddress", "220002, г. Минск, а/я 12",
                    "actualAddress", "г. Минск, ул. Рабочая, д. 2\r\nПомещение 003", "phone", "+375 29 000 00 00", "email", "vendor@example.com",
                    "website", "https://vendor.example.com", "contact", "Старый контакт поставщика",
                    "chiefAccountantName", "Петрова Анна Ивановна", "chiefAccountantPhone", "+375 17 000 00 00",
                    "bankName", "Банк «Тест»", "bankAccount", "BY13 NBRB 3600 9000 0000 2Z00 AB00", "bankCode", "nBrBbY2x",
                    "signatoryName", "Сидоров Иван Петрович", "signatoryPosition", "Директор", "signatoryBasis", "Устав",
                    "vatNote", "НДС по условиям документа", "requisites", "Прежние реквизиты\r\nСчёт 000123", "customPartySetting", "Сохранить неизвестное поле"
                };
                for (int i = 0; i < details.Length; i += 2) edit.Set(details[i], details[i + 1]);
                legacy.Upsert(edit); Store loaded = new Store(s.DataDir);
                foreach (var field in edit.Fields) Eq(loaded.Find(party.Id).Get(field.Key), field.Value);
                Record changed = loaded.Find(party.Id); changed.Set("role", "both"); loaded.Upsert(changed);
                string restoredDir = Path.Combine(root, "party-restored"); Directory.CreateDirectory(restoredDir);
                loaded.SaveBackup(Path.Combine(restoredDir, "ledger.json")); Store restored = new Store(restoredDir);
                foreach (var field in changed.Fields) Eq(restored.Find(party.Id).Get(field.Key), field.Value);
                Eq(restored.Revision, loaded.Revision); Eq(restored.Audit.Count, loaded.Audit.Count);
                True(restored.Audit.Last().After.Contains("Сайт: https://vendor.example.com"));
                True(restored.Audit.Last().After.Contains("ФИО главного бухгалтера: Петрова Анна Ивановна"));
                True(restored.Audit.Last().After.Contains("Телефон главного бухгалтера: +375 17 000 00 00"));
            });
            Console.WriteLine("Core tests: " + passes.ToString(CultureInfo.InvariantCulture) + " passed, " + failures.ToString(CultureInfo.InvariantCulture) + " failed. Artifacts: " + root);
            return failures;
        }
        private static void Test(string name, Action test)
        {
            try { test(); passes++; Console.WriteLine("PASS " + name); }
            catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e); }
        }
        private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
        private static void Eq<T>(T actual, T expected) { if (!Object.Equals(actual, expected)) throw new Exception("Expected " + expected + ", got " + actual); }
        private static void Reject(Action operation) { try { operation(); } catch { return; } throw new Exception("Expected rejection, operation succeeded."); }
        private static Store Fresh(string root, string name) { return new Store(Path.Combine(root, name)); }
        private static Record R(string kind, params string[] fields) { Record r = new Record(kind); for (int i = 0; i < fields.Length; i += 2) r.Set(fields[i], fields[i + 1]); return r; }
        private static Record Party(Store s, string name) { Record r = R("party", "name", name, "role", "both"); s.Upsert(r); return r; }
        private static Record Contract(Store s, Record party, string number, decimal amount) { Record r = R("contract", "number", number, "date", "2026-10-11", "partyId", party.Id, "amount", Store.MoneyString(amount), "currency", "BYN", "direction", "income", "status", "active"); s.Upsert(r); return r; }
        private static Record Act(Store s, Record party, string number, decimal amount, string status, string contractId = "") { Record r = R("act", "number", number, "date", "2026-10-11", "partyId", party.Id, "contractId", contractId, "amount", Store.MoneyString(amount), "currency", "BYN", "direction", "income", "status", status); s.Upsert(r); return r; }
        private static Record Payment(Store s, Record party, decimal amount, string currency = "BYN", string direction = "income") { Record r = R("payment", "date", "2026-10-11", "partyId", party.Id, "amount", Store.MoneyString(amount), "currency", currency, "direction", direction, "method", "bank"); s.Upsert(r); return r; }
    }
}


