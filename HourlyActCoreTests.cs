using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WorkLedger
{
    public static class HourlyActCoreTests
    {
        private static int passes, failures;
        public static int Run(string tempdir)
        {
            passes = 0; failures = 0;
            string root = Path.Combine(Path.GetFullPath(tempdir), "hourly-act-core-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Test("Numbers begin at 500; preview and cancelled draft reserve nothing", delegate {
                Store store = Fresh(root, "initial"); Record party = Party(store, "Клиент");
                long revision = store.Revision; Eq(store.NextActNumber, 500L); Eq(store.NextActNumber, 500L);
                Record draft = store.CreateHourlyActDraft(party.Id);
                Eq(draft.Get("number"), ""); Eq(store.Revision, revision); Eq(store.Of("act").Count(), 0);
                Store.SetHourlyActLines(draft, new[] { Line("Монтаж", 2m, 35m) }); store.Upsert(draft);
                Eq(draft.Get("number"), "500"); Eq(draft.Get("autoNumber"), "true"); Eq(store.NextActNumber, 501L);
                Eq(store.Of("settings").Single().Get("nextActNumber"), "501");
                Store reloaded = new Store(store.DataDir); Record next = PlainAct(party.Id); reloaded.Upsert(next);
                Eq(next.Get("number"), "501"); Eq(reloaded.NextActNumber, 502L);
            });
            Test("Invalid save leaves the number, counter, audit and bytes unchanged", delegate {
                Store store = Fresh(root, "invalid-save"); Record party = Party(store, "Клиент");
                Record act = PlainAct(party.Id); act.Set("date", "2026-02-30");
                long revision = store.Revision; int audits = store.Audit.Count; string bytes = File.ReadAllText(store.DataPath);
                Reject(delegate { store.Upsert(act); });
                Eq(act.Get("number"), ""); Eq(store.NextActNumber, 500L); Eq(store.Revision, revision);
                Eq(store.Audit.Count, audits); Eq(File.ReadAllText(store.DataPath), bytes); Eq(store.Of("settings").Count(), 0);
                act.Set("date", "2026-10-12"); store.Upsert(act); Eq(act.Get("number"), "500");
            });
            Test("Existing manual and archived numeric numbers seed the counter", delegate {
                Store store = Fresh(root, "legacy"); Record party = Party(store, "Клиент");
                Record legacy = PlainAct(party.Id); legacy.Set("number", "900"); store.Upsert(legacy);
                Record archived = PlainAct(party.Id); archived.Set("number", "00901"); store.Upsert(archived); store.Archive(archived.Id);
                Record letter = PlainAct(party.Id); letter.Set("number", "А-10000"); store.Upsert(letter);
                Eq(store.NextActNumber, 902L); Record act = PlainAct(party.Id); store.Upsert(act); Eq(act.Get("number"), "902");
                Eq(store.Find(legacy.Id).Get("number"), "900"); Eq(store.Find(archived.Id).Get("number"), "00901"); Eq(store.Find(letter.Id).Get("number"), "А-10000");
            });
            Test("Settings can increase the number but cannot cause reuse", delegate {
                Store store = Fresh(root, "counter-setting"); Record party = Party(store, "Клиент");
                Record settings = R("settings", "currency", "BYN", "hourlyRate", "35", "nextActNumber", "650"); store.Upsert(settings);
                Eq(store.DefaultHourlyRate, 35m); Eq(store.NextActNumber, 650L);
                Record act = PlainAct(party.Id); store.Upsert(act); Eq(act.Get("number"), "650");
                Record changed = store.Find(settings.Id); changed.Set("nextActNumber", "500"); store.Upsert(changed);
                Eq(changed.Get("nextActNumber"), "651"); Eq(store.NextActNumber, 651L);
                changed.Set("nextActNumber", "675"); store.Upsert(changed); Eq(store.NextActNumber, 675L);
                changed.Set("nextActNumber", "674.5"); Reject(delegate { store.Upsert(changed); });
                changed = store.Find(settings.Id); changed.Set("hourlyRate", "35.001"); Reject(delegate { store.Upsert(changed); });
                changed.Set("hourlyRate", "0"); Reject(delegate { store.Upsert(changed); });
                Eq(store.DefaultHourlyRate, 35m);
            });
            Test("Automatic numbers remain stable on edit and unique across counterparties", delegate {
                Store store = Fresh(root, "stable"); Record party = Party(store, "Первый"), other = Party(store, "Второй");
                Record act = PlainAct(party.Id); store.Upsert(act);
                Record edit = store.Find(act.Id); edit.Set("notes", "Изменено"); store.Upsert(edit); Eq(edit.Get("number"), "500"); Eq(store.NextActNumber, 501L);
                edit.Set("number", "800"); Reject(delegate { store.Upsert(edit); }); Eq(store.Find(act.Id).Get("number"), "500");
                Record duplicate = PlainAct(other.Id); duplicate.Set("number", "00500"); Reject(delegate { store.Upsert(duplicate); });
                store.Archive(act.Id); Record next = PlainAct(other.Id); store.Upsert(next); Eq(next.Get("number"), "501");
            });
            Test("Hourly totals round each line to cents and survive reloading", delegate {
                Store store = Fresh(root, "hourly"); Record party = Party(store, "Клиент");
                Record act = store.CreateHourlyActDraft(party.Id);
                Store.SetHourlyActLines(act, new[] { Line(" Монтаж ", 1.005m, 2m), Line("Наладка", 0.5m, 3m) });
                Eq(act.Get("amount"), "3.51"); Eq(act.Get("hours"), "1.505"); store.Upsert(act);
                Record loaded = new Store(store.DataDir).Find(act.Id);
                var lines = Store.GetHourlyActLines(loaded); Eq(lines.Count, 2); Eq(lines[0].Description, "Монтаж");
                Eq(lines[0].Hours, 1.005m); Eq(lines[0].Rate, 2m); Eq(lines[0].Amount, 2.01m);
                Eq(lines[1].Amount, 1.50m); Eq(loaded.Get("amount"), "3.51"); Eq(loaded.Get("hours"), "1.505");
                loaded.Set("amount", "999.00"); store.Upsert(loaded); Eq(store.Find(act.Id).Get("amount"), "3.51");
            });
            Test("Work lines and historical requisites retain their original values", delegate {
                Store store = Fresh(root, "snapshots"); Record party = Party(store, "Первое название");
                party.Set("bankAccount", "BY001"); store.Upsert(party);
                Record settings = R("settings", "name", "ИП Первый", "currency", "BYN", "hourlyRate", "35.00"); store.Upsert(settings);
                Record act = store.CreateHourlyActDraft(party.Id);
                Eq(act.Get("hourlyRate"), "35.00");
                Store.SetHourlyActLines(act, new[] { Line("Работа", 2m, store.DefaultHourlyRate) }); store.Upsert(act);
                party.Set("name", "Новое название"); party.Set("bankAccount", "BY002"); store.Upsert(party);
                settings = store.Find(settings.Id); settings.Set("name", "ИП Новый"); settings.Set("hourlyRate", "50"); store.Upsert(settings);
                Record saved = store.Find(act.Id); store.CaptureActSnapshots(saved); store.Upsert(saved);
                Eq(Store.GetActSnapshot(saved, "partySnapshot").Get("name"), "Первое название");
                Eq(Store.GetActSnapshot(saved, "partySnapshot").Get("bankAccount"), "BY001");
                Eq(Store.GetActSnapshot(saved, "organizationSnapshot").Get("name"), "ИП Первый");
                Eq(Store.GetHourlyActLines(saved).Single().Rate, 35m); Eq(saved.Get("amount"), "70.00");
                Eq(store.DefaultHourlyRate, 50m);
                var returned = Store.GetHourlyActLines(saved); returned[0].Description = "Чужое изменение";
                Eq(Store.GetHourlyActLines(saved).Single().Description, "Работа");
            });
            Test("Changing the selected counterparty captures their requisites", delegate {
                Store store = Fresh(root, "changed-party"); Record first = Party(store, "Первый"), second = Party(store, "Второй");
                Record act = store.CreateHourlyActDraft(first.Id); act.Set("partyId", second.Id); store.CaptureActSnapshots(act);
                Eq(Store.GetActSnapshot(act, "partySnapshot").Id, second.Id); Eq(Store.GetActSnapshot(act, "partySnapshot").Get("name"), "Второй");
            });
            Test("Blank names and invalid hours or rates cannot become saved lines", delegate {
                Record act = new Record("act");
                foreach (HourlyActLine invalid in new[] { Line("", 1m, 1m), Line("Работа", 0m, 1m), Line("Работа", -1m, 1m), Line("Работа", 0.0000001m, 1m), Line("Работа", 1m, 0m), Line("Работа", 1m, 1.001m), Line("Работа", 1000000000000m, 1000000000000m) })
                    Reject(delegate { Store.SetHourlyActLines(act, new[] { invalid }); });
                Reject(delegate { Store.SetHourlyActLines(act, new HourlyActLine[0]); });
                Eq(act.Get("workItems"), ""); Eq(act.Get("amount"), "");
            });
            Test("Corrupt saved totals are rejected without replacing the ledger", delegate {
                Store store = Fresh(root, "corrupt"); Record party = Party(store, "Клиент"); Record act = store.CreateHourlyActDraft(party.Id);
                Store.SetHourlyActLines(act, new[] { Line("Работа", 2m, 35m) }); store.Upsert(act);
                string bytes = File.ReadAllText(store.DataPath).Replace("\"amount\":\"70.00\"", "\"amount\":\"69.00\"");
                File.WriteAllText(store.DataPath, bytes); Reject(delegate { new Store(store.DataDir); }); Eq(File.ReadAllText(store.DataPath), bytes);
            });
            Test("Stale concurrent writers cannot duplicate or consume numbers", delegate {
                Store store = Fresh(root, "stale"); Record party = Party(store, "Клиент"); Store stale = new Store(store.DataDir);
                Record first = PlainAct(party.Id); store.Upsert(first); Record second = PlainAct(party.Id);
                Reject(delegate { stale.Upsert(second); }); Eq(second.Get("number"), ""); Eq(new Store(store.DataDir).NextActNumber, 501L);
                stale.Reload(); stale.Upsert(second); Eq(second.Get("number"), "501"); Eq(stale.NextActNumber, 502L);
            });
            Test("Failed disk writes leave the counter and caller untouched", delegate {
                Store store = Fresh(root, "io-failure"); Record party = Party(store, "Клиент"); Record act = PlainAct(party.Id);
                long revision = store.Revision; string bytes = File.ReadAllText(store.DataPath);
                using (FileStream held = new FileStream(store.DataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Reject(delegate { store.Upsert(act); });
                Eq(act.Get("number"), ""); Eq(store.NextActNumber, 500L); Eq(store.Revision, revision);
                Eq(File.ReadAllText(store.DataPath), bytes); Eq(store.Of("settings").Count(), 0);
            });
            Test("Blank imported numbers use the same atomic counter", delegate {
                Store store = Fresh(root, "import"); Record party = Party(store, "Клиент");
                Record first = PlainAct(party.Id), second = PlainAct(party.Id); second.Set("date", "2026-02-30");
                long revision = store.Revision; Reject(delegate { store.ImportBatch(new[] { first, second }); });
                Eq(store.NextActNumber, 500L); Eq(store.Revision, revision); Eq(first.Get("number"), "");
                second.Set("date", "2026-10-12"); store.ImportBatch(new[] { first, second });
                Eq(first.Get("number"), "500"); Eq(second.Get("number"), "501"); Eq(store.NextActNumber, 502L);
                Eq(store.Revision, revision + 1); Eq(store.Audit.Count(a => a.Action == "import"), 2);
            });
            Console.WriteLine("Hourly act core tests: " + passes + " passed, " + failures + " failed. Artifacts: " + root);
            return failures;
        }
        private static void Test(string name, Action test)
        {
            try { test(); passes++; Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; Console.WriteLine("FAIL " + name + ": " + exception); }
        }
        private static void Eq<T>(T actual, T expected) { if (!Object.Equals(actual, expected)) throw new Exception("Expected " + expected + ", got " + actual); }
        private static void Reject(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection."); }
        private static Store Fresh(string root, string name) { return new Store(Path.Combine(root, name)); }
        private static Record R(string kind, params string[] fields) { Record r = new Record(kind); for (int i = 0; i < fields.Length; i += 2) r.Set(fields[i], fields[i + 1]); return r; }
        private static Record Party(Store store, string name) { Record party = R("party", "name", name, "role", "client"); store.Upsert(party); return party; }
        private static Record PlainAct(string partyId) { return R("act", "partyId", partyId, "date", "2026-10-12", "amount", "10.00", "currency", "BYN", "direction", "income", "status", "draft"); }
        private static HourlyActLine Line(string description, decimal hours, decimal rate) { return new HourlyActLine { Description = description, Hours = hours, Rate = rate }; }
    }
}