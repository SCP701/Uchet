using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WorkLedger
{
    public static class MonthlyActCoreTests
    {
        private static int passes, failures;
        public static int Run(string tempdir)
        {
            passes = 0; failures = 0;
            string root = Path.Combine(Path.GetFullPath(tempdir), "monthly-act-core-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Test("Start month defaults to today on day 1 and next month otherwise", delegate {
                Eq(Store.DefaultMonthlyServiceStart(new DateTime(2026, 10, 1)), new DateTime(2026, 10, 1));
                Eq(Store.DefaultMonthlyServiceStart(new DateTime(2026, 10, 12)), new DateTime(2026, 11, 1));
                Eq(Store.DefaultMonthlyServiceStart(new DateTime(2026, 12, 31)), new DateTime(2027, 1, 1));
                Eq(Store.DefaultMonthlyServiceStart(new DateTime(2028, 2, 29)), new DateTime(2028, 3, 1));
                Record party = R("party", "name", "Клиент", "role", "client", "monthlyServiceEnabled", "true", "monthlyServiceAmount", "120,50");
                Store.PrepareMonthlyServiceSettings(party, null, new DateTime(2026, 10, 12));
                Eq(party.Get("monthlyServiceStartMonth"), "2026-11-01"); Eq(party.Get("monthlyServiceCurrency"), "BYN");
                Eq(party.Get("monthlyServiceDescription"), "Ежемесячное обслуживание");
            });
            Test("A future start or disabled party creates nothing and writes nothing", delegate {
                Store store = Fresh(root, "future");
                MonthlyParty(store, "Будущий", "2026-11-01", "120.00", "BYN");
                Record disabled = R("party", "name", "Отключён", "role", "client", "monthlyServiceEnabled", "false", "monthlyServiceAmount", "не сумма", "monthlyServiceCurrency", "invalid", "monthlyServiceStartMonth", "invalid");
                store.Upsert(disabled); long revision = store.Revision; int audits = store.Audit.Count; string bytes = File.ReadAllText(store.DataPath);
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2026, 10, 31)).Count, 0);
                Eq(store.Revision, revision); Eq(store.Audit.Count, audits); Eq(File.ReadAllText(store.DataPath), bytes); Eq(store.NextActNumber, 500L);
            });
            Test("First day creates one fixed-sum draft with no invented hours", delegate {
                Store store = Fresh(root, "first-day"); Record party = MonthlyParty(store, "Клиент", "2026-11-01", "123.45", "USD");
                List<Record> generated = store.GenerateMonthlyServiceActs(new DateTime(2026, 11, 1)); Eq(generated.Count, 1);
                Record act = generated[0]; Eq(act.Get("number"), "500"); Eq(act.Get("autoNumber"), "true");
                Eq(act.Get("date"), "2026-11-01"); Eq(act.Get("status"), "draft"); Eq(act.Get("direction"), "income");
                Eq(act.Get("amount"), "123.45"); Eq(act.Get("currency"), "USD"); Eq(act.Get("partyId"), party.Id);
                Eq(act.Get("hours"), ""); Eq(act.Get("hourlyRate"), ""); Eq(act.Get("workItems"), "");
                Eq(act.Get("serviceGenerated"), "true"); Eq(act.Get("recurringPartyId"), party.Id); Eq(act.Get("monthlyServiceMonth"), "2026-11");
                Eq(store.ActDue(act.Id), 0m); Eq(store.ActGrossDue(act.Id), 0m); Eq(store.PaymentStatus(act.Id), "Не подписан");
                Eq(store.NextActNumber, 501L); Eq(new Store(store.DataDir).Find(act.Id).Get("amount"), "123.45");
            });
            Test("Missed months including year rollover are committed together", delegate {
                Store store = Fresh(root, "rollover"); MonthlyParty(store, "Клиент", "2026-12-01", "100.00", "BYN");
                long revision = store.Revision; List<Record> generated = store.GenerateMonthlyServiceActs(new DateTime(2027, 2, 28));
                Eq(generated.Count, 3); Eq(String.Join(",", generated.Select(a => a.Get("date"))), "2026-12-01,2027-01-01,2027-02-01");
                Eq(String.Join(",", generated.Select(a => a.Get("number"))), "500,501,502"); Eq(store.Revision, revision + 1); Eq(store.NextActNumber, 503L);
                Eq(store.Audit.Count(a => a.Kind == "act" && a.Action == "create"), 3);
                long after = store.Revision; Eq(store.GenerateMonthlyServiceActs(new DateTime(2027, 2, 28)).Count, 0); Eq(store.Revision, after);
            });
            Test("Multiple counterparties and leap February receive unique consecutive numbers", delegate {
                Store store = Fresh(root, "several"); Record one = MonthlyParty(store, "Один", "2028-02-01", "50.00", "EUR"), two = MonthlyParty(store, "Два", "2028-02-01", "70.00", "RUB");
                List<Record> generated = store.GenerateMonthlyServiceActs(new DateTime(2028, 3, 1)); Eq(generated.Count, 4);
                Eq(String.Join(",", generated.Select(a => a.Get("date"))), "2028-02-01,2028-02-01,2028-03-01,2028-03-01");
                Eq(generated.Select(a => a.Get("number")).Distinct().Count(), 4); Eq(store.NextActNumber, 504L);
                Eq(generated.Count(a => a.Get("partyId") == one.Id && a.Get("amount") == "50.00" && a.Get("currency") == "EUR"), 2);
                Eq(generated.Count(a => a.Get("partyId") == two.Id && a.Get("amount") == "70.00" && a.Get("currency") == "RUB"), 2);
            });
            Test("Archive and cancellation keep the month occupied", delegate {
                Store store = Fresh(root, "archive-cancel"); MonthlyParty(store, "Клиент", "2026-10-01", "100.00", "BYN");
                var generated = store.GenerateMonthlyServiceActs(new DateTime(2026, 11, 1)); store.Archive(generated[0].Id);
                Record cancelled = store.Find(generated[1].Id); cancelled.Set("status", "cancelled"); store.Upsert(cancelled);
                Store reloaded = new Store(store.DataDir); long revision = reloaded.Revision;
                Eq(reloaded.GenerateMonthlyServiceActs(new DateTime(2026, 11, 20)).Count, 0); Eq(reloaded.Revision, revision);
                Eq(reloaded.Records.Count(a => a.Kind == "act"), 2); Eq(reloaded.GenerateMonthlyServiceActs(new DateTime(2026, 12, 1)).Single().Get("number"), "502");
            });
            Test("Changing terms only affects future documents and preserves requisites", delegate {
                Store store = Fresh(root, "terms"); Record party = MonthlyParty(store, "Старое название", "2026-10-01", "100.00", "BYN");
                party.Set("bankAccount", "BY001"); store.Upsert(party);
                Record organization = R("settings", "name", "ИП Старый", "currency", "BYN"); store.Upsert(organization);
                Record oldAct = store.GenerateMonthlyServiceActs(new DateTime(2026, 10, 1)).Single();
                party = store.Find(party.Id); party.Set("name", "Новое название"); party.Set("bankAccount", "BY002");
                party.Set("monthlyServiceAmount", "200.00"); party.Set("monthlyServiceCurrency", "EUR"); party.Set("monthlyServiceDescription", "Поддержка"); store.Upsert(party);
                organization = store.Find(organization.Id); organization.Set("name", "ИП Новый"); store.Upsert(organization);
                Record nextAct = store.GenerateMonthlyServiceActs(new DateTime(2026, 11, 1)).Single();
                Eq(store.Find(oldAct.Id).Get("amount"), "100.00"); Eq(store.Find(oldAct.Id).Get("currency"), "BYN");
                Eq(Store.GetActSnapshot(oldAct, "partySnapshot").Get("name"), "Старое название"); Eq(Store.GetActSnapshot(oldAct, "partySnapshot").Get("bankAccount"), "BY001");
                Eq(Store.GetActSnapshot(oldAct, "organizationSnapshot").Get("name"), "ИП Старый");
                Eq(nextAct.Get("amount"), "200.00"); Eq(nextAct.Get("currency"), "EUR");
                Eq(Store.GetActSnapshot(nextAct, "partySnapshot").Get("name"), "Новое название"); Eq(Store.GetActSnapshot(nextAct, "organizationSnapshot").Get("name"), "ИП Новый");
                if (!nextAct.Get("description").StartsWith("Поддержка за ноябрь 2026")) throw new Exception("Missing month and service name.");
                nextAct.Set("amount", "999.00"); Eq(store.Find(nextAct.Id).Get("amount"), "200.00");
            });
            Test("Disabling stops creation and reenable starts from the next month", delegate {
                Store store = Fresh(root, "toggle"); Record party = MonthlyParty(store, "Клиент", "2026-10-01", "100.00", "BYN");
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2026, 10, 1)).Count, 1);
                party = store.Find(party.Id); party.Set("monthlyServiceEnabled", "false"); store.Upsert(party);
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2026, 12, 1)).Count, 0);
                Record previous = store.Find(party.Id); Record enabled = previous.Clone(); enabled.Set("monthlyServiceEnabled", "true");
                Store.PrepareMonthlyServiceSettings(enabled, previous, new DateTime(2026, 12, 12));
                Eq(enabled.Get("monthlyServiceStartMonth"), "2027-01-01"); store.Upsert(enabled);
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2026, 12, 31)).Count, 0);
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2027, 1, 1)).Single().Get("monthlyServiceMonth"), "2027-01");
                Eq(store.Records.Count(a => a.Kind == "act"), 2);
            });
            Test("Invalid active amounts and currencies fail before changing the ledger", delegate {
                Store store = Fresh(root, "invalid"); Record party = MonthlyParty(store, "Клиент", "2026-10-01", "100.00", "BYN");
                string bytes = File.ReadAllText(store.DataPath); long revision = store.Revision;
                foreach (string invalid in new[] { "0", "-1", "1.001", "1000000000001", "not money" })
                {
                    Record changed = store.Find(party.Id); changed.Set("monthlyServiceAmount", invalid); Reject(delegate { store.Upsert(changed); });
                }
                Record currency = store.Find(party.Id); currency.Set("monthlyServiceCurrency", "JPY"); Reject(delegate { store.Upsert(currency); });
                Record date = store.Find(party.Id); date.Set("monthlyServiceStartMonth", "2026-02-30"); Reject(delegate { store.Upsert(date); });
                Eq(store.Revision, revision); Eq(File.ReadAllText(store.DataPath), bytes);
                Record month = store.Find(party.Id); month.Set("monthlyServiceStartMonth", "2026-10-22"); store.Upsert(month); Eq(month.Get("monthlyServiceStartMonth"), "2026-10-01");
            });
            Test("Stale concurrent generation cannot consume numbers or duplicate documents", delegate {
                Store store = Fresh(root, "stale"); MonthlyParty(store, "Клиент", "2026-10-01", "100.00", "BYN"); Store stale = new Store(store.DataDir);
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2026, 11, 1)).Count, 2);
                long staleRevision = stale.Revision; int staleAudit = stale.Audit.Count;
                Reject(delegate { stale.GenerateMonthlyServiceActs(new DateTime(2026, 11, 1)); });
                Eq(stale.Revision, staleRevision); Eq(stale.Audit.Count, staleAudit); Eq(stale.Of("act").Count(), 0); Eq(stale.NextActNumber, 500L);
                stale.Reload(); Eq(stale.GenerateMonthlyServiceActs(new DateTime(2026, 11, 1)).Count, 0); Eq(stale.NextActNumber, 502L);
                Eq(stale.GenerateMonthlyServiceActs(new DateTime(2026, 12, 1)).Single().Get("number"), "502");
            });
            Test("Disk failure rolls back a whole batch including audit and the counter", delegate {
                Store store = Fresh(root, "io-failure"); MonthlyParty(store, "Клиент", "2026-10-01", "100.00", "BYN");
                string bytes = File.ReadAllText(store.DataPath); long revision = store.Revision; int audits = store.Audit.Count;
                using (FileStream held = new FileStream(store.DataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Reject(delegate { store.GenerateMonthlyServiceActs(new DateTime(2026, 12, 1)); });
                Eq(store.Revision, revision); Eq(store.Audit.Count, audits); Eq(store.NextActNumber, 500L); Eq(store.Of("act").Count(), 0); Eq(File.ReadAllText(store.DataPath), bytes);
                Eq(String.Join(",", store.GenerateMonthlyServiceActs(new DateTime(2026, 12, 1)).Select(a => a.Get("number"))), "500,501,502");
            });
            Test("Recurring identity and historical snapshots survive ordinary act editing", delegate {
                Store store = Fresh(root, "identity"); Record party = MonthlyParty(store, "Клиент", "2026-10-01", "100.00", "BYN");
                Record act = store.GenerateMonthlyServiceActs(new DateTime(2026, 10, 1)).Single(); string snapshot = act.Get("partySnapshot");
                act.Set("serviceGenerated", "false"); act.Set("monthlyServiceMonth", "2026-09"); act.Set("recurringPartyId", ""); act.Set("partySnapshot", ""); act.Set("notes", "Правка");
                store.Upsert(act); Eq(act.Get("serviceGenerated"), "true"); Eq(act.Get("monthlyServiceMonth"), "2026-10"); Eq(act.Get("recurringPartyId"), party.Id); Eq(act.Get("partySnapshot"), snapshot);
                Record duplicate = act.Clone(); duplicate.Id = Guid.NewGuid().ToString("N"); duplicate.Set("number", ""); duplicate.Set("autoNumber", ""); Reject(delegate { store.Upsert(duplicate); }); Eq(store.NextActNumber, 501L);
                Record other = R("party", "name", "Другой", "role", "client"); store.Upsert(other); act.Set("partyId", other.Id); Reject(delegate { store.Upsert(act); });
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2026, 10, 31)).Count, 0);
            });
            Test("A long existing history does not block the next monthly act", delegate {
                Store store = Fresh(root, "long-history"); MonthlyParty(store, "Клиент", "2020-01-01", "100.00", "BYN");
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2039, 12, 1)).Count, 240);
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2040, 1, 1)).Single().Get("monthlyServiceMonth"), "2040-01");
                Eq(store.NextActNumber, 741L);
            });
            Test("An erroneous old configuration leaves all pending parties untouched", delegate {
                Store store = Fresh(root, "old-start"); MonthlyParty(store, "Актуальный", "2026-10-01", "100.00", "BYN"); MonthlyParty(store, "Старый", "1980-01-01", "100.00", "BYN");
                string bytes = File.ReadAllText(store.DataPath); long revision = store.Revision;
                Reject(delegate { store.GenerateMonthlyServiceActs(new DateTime(2026, 10, 1)); });
                Eq(store.Revision, revision); Eq(store.Of("act").Count(), 0); Eq(store.NextActNumber, 500L); Eq(File.ReadAllText(store.DataPath), bytes);
            });
            Console.WriteLine("Monthly act core tests: " + passes + " passed, " + failures + " failed. Artifacts: " + root);
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
        private static Record MonthlyParty(Store store, string name, string start, string amount, string currency)
        {
            Record party = R("party", "name", name, "role", "client", "monthlyServiceEnabled", "true", "monthlyServiceAmount", amount, "monthlyServiceCurrency", currency, "monthlyServiceStartMonth", start);
            store.Upsert(party); return party;
        }
    }
}