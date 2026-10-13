using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WorkLedger
{
    public static class ExternalActCoreTests
    {
        private static int passes, failures;
        public static int Run(string tempdir)
        {
            passes = 0; failures = 0;
            string root = Path.Combine(Path.GetFullPath(tempdir), "external-act-core-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Test("Legacy and explicit ordinary acts remain in the tax selection; external acts do not", delegate {
                Store store = Fresh(root, "classification"); Record party = Party(store);
                Record legacy = Act(party.Id, "", "signed", "100"), ordinary = Act(party.Id, "standard", "draft", "20"), external = Act(party.Id, "external", "signed", "30"), archived = Act(party.Id, "", "signed", "40");
                store.Upsert(legacy); store.Upsert(ordinary); store.Upsert(external); store.Upsert(archived); store.Archive(archived.Id);
                Eq(Store.IsTaxAct(legacy), true); Eq(Store.IsTaxAct(ordinary), true); Eq(Store.IsExternalAct(external), true); Eq(Store.IsTaxAct(external), false);
                Eq(Store.IsTaxAct(null), false); Eq(Store.IsExternalAct(null), false); Eq(Store.IsTaxAct(party), false);
                Record unrelated = R("payment", "accountingScope", "external"); Eq(Store.IsExternalAct(unrelated), false);
                Record invalid = R("act", "accountingScope", "typo"); Eq(Store.IsTaxAct(invalid), false); Eq(Store.IsExternalAct(invalid), false);
                Eq(store.Of("act").Count(), 3); Eq(store.OfTaxActs().Count(), 2);
                Eq(store.OfTaxActs().Any(a => a.Id == external.Id || a.Id == archived.Id), false);
                Record returned = store.OfTaxActs().First(); returned.Set("accountingScope", "external"); Eq(store.OfTaxActs().Count(), 2);
                Eq(new Store(store.DataDir).OfTaxActs().Count(), 2);
            });
            Test("External hourly drafts use the same defaults, rounded lines and historical requisites", delegate {
                Store store = Fresh(root, "hourly"); Record party = Party(store); party.Set("bankAccount", "BY001"); store.Upsert(party);
                Record settings = R("settings", "name", "ИП Первый", "currency", "USD", "hourlyRate", "35.00"); store.Upsert(settings);
                long revision = store.Revision; Record act = store.CreateHourlyActDraft(party.Id, true);
                Eq(act.Kind, "act"); Eq(act.Get("accountingScope"), "external"); Eq(act.Get("currency"), "USD"); Eq(act.Get("hourlyRate"), "35.00"); Eq(act.Get("number"), ""); Eq(store.Revision, revision);
                Store.SetHourlyActLines(act, new[] { Line("Монтаж", 1.005m, 2m), Line("Наладка", 0.5m, 3m) }); store.Upsert(act);
                Eq(act.Get("amount"), "3.51"); Eq(act.Get("hours"), "1.505"); Eq(store.ActDue(act.Id), 0m); Eq(store.PaymentStatus(act.Id), "Не подписан");
                party.Set("name", "Новое имя"); party.Set("bankAccount", "BY002"); store.Upsert(party);
                settings = store.Find(settings.Id); settings.Set("name", "ИП Новый"); store.Upsert(settings);
                Record edited = store.Find(act.Id); store.CaptureActSnapshots(edited); edited.Set("status", "signed"); edited.Set("signedDate", "2026-10-12"); store.Upsert(edited);
                Record loaded = new Store(store.DataDir).Find(act.Id); Eq(Store.IsExternalAct(loaded), true); Eq(Store.GetHourlyActLines(loaded).Count, 2);
                Eq(Store.GetHourlyActLines(loaded)[0].Amount, 2.01m); Eq(Store.GetActSnapshot(loaded, "partySnapshot").Get("bankAccount"), "BY001");
                Eq(Store.GetActSnapshot(loaded, "organizationSnapshot").Get("name"), "ИП Первый"); Eq(store.ActDue(act.Id), 3.51m); Eq(store.OfTaxActs().Count(), 0);
                Record ordinary = store.CreateHourlyActDraft(party.Id); Eq(Store.IsTaxAct(ordinary), true); Eq(ordinary.Get("accountingScope"), "");
            });
            Test("Invalid accounting flags reject atomically and cannot be attached to other record kinds", delegate {
                Store store = Fresh(root, "invalid-flags"); Record party = Party(store); string bytes = File.ReadAllText(store.DataPath); long revision = store.Revision; int audits = store.Audit.Count;
                foreach (string scope in new[] { "typo", "EXTERNAL", "false", "tax-free" })
                {
                    Record invalid = Act(party.Id, scope, "draft", "10"); Reject(delegate { store.Upsert(invalid); }); Eq(invalid.Get("number"), "");
                }
                foreach (string scope in new[] { "standard", "external" })
                {
                    Record changed = store.Find(party.Id); changed.Set("accountingScope", scope); Reject(delegate { store.Upsert(changed); });
                    Record payment = R("payment", "partyId", party.Id, "date", "2026-10-12", "amount", "10", "currency", "BYN", "direction", "income", "method", "cash", "accountingScope", scope);
                    Reject(delegate { store.Upsert(payment); });
                }
                Eq(store.Revision, revision); Eq(store.Audit.Count, audits); Eq(store.NextActNumber, 500L); Eq(File.ReadAllText(store.DataPath), bytes);
            });
            Test("External signed acts share allocations, holds, debt and reversal with ordinary acts", delegate {
                Store store = Fresh(root, "debt"); Record party = Party(store); Record ordinary = Act(party.Id, "", "signed", "40"), external = Act(party.Id, "external", "signed", "100");
                store.Upsert(ordinary); store.Upsert(external);
                Record payment = R("payment", "partyId", party.Id, "date", "2026-10-12", "amount", "120", "currency", "BYN", "direction", "income", "method", "cash"); store.Upsert(payment);
                store.Allocate(payment.Id, ordinary.Id, 40m); store.Allocate(payment.Id, external.Id, 60m);
                Record hold = R("hold", "actId", external.Id, "date", "2026-10-12", "amount", "10", "status", "active", "type", "warranty"); store.Upsert(hold);
                Eq(store.ActGrossDue(external.Id), 40m); Eq(store.ActDue(external.Id), 30m); Eq(store.AllocatedToAct(external.Id), 60m); Eq(store.AllocatedFromPayment(payment.Id), 100m); Eq(store.PaymentUnallocated(payment.Id), 20m);
                Eq(store.Of("act").Where(a => a.Get("status") == "signed").Sum(a => store.ActDue(a.Id)), 30m);
                Reject(delegate { store.Allocate(payment.Id, external.Id, 21m); });
                Record unsigned = store.Find(external.Id); unsigned.Set("status", "draft"); Reject(delegate { store.Upsert(unsigned); });
                Record allocated = store.Of("allocation").Single(a => a.Get("actId") == external.Id); store.ReverseAllocation(allocated.Id);
                Eq(store.ActDue(external.Id), 90m); Eq(store.PaymentUnallocated(payment.Id), 80m); Eq(new Store(store.DataDir).ActDue(external.Id), 90m);
            });
            Test("Unsigned external acts cannot accept payments; currency and party checks still apply", delegate {
                Store store = Fresh(root, "allocation-validation"); Record party = Party(store); Record act = Act(party.Id, "external", "draft", "100"); store.Upsert(act);
                Record payment = R("payment", "partyId", party.Id, "date", "2026-10-12", "amount", "100", "currency", "BYN", "direction", "income", "method", "bank"); store.Upsert(payment);
                Reject(delegate { store.Allocate(payment.Id, act.Id, 10m); }); Eq(store.AllocatedToAct(act.Id), 0m);
                act.Set("status", "signed"); act.Set("currency", "USD"); store.Upsert(act); Reject(delegate { store.Allocate(payment.Id, act.Id, 10m); });
                act.Set("currency", "BYN"); store.Upsert(act); Record other = R("party", "name", "Другой", "role", "client"); store.Upsert(other);
                payment.Set("partyId", other.Id); store.Upsert(payment); Reject(delegate { store.Allocate(payment.Id, act.Id, 10m); }); Eq(store.AllocatedToAct(act.Id), 0m);
            });
            Test("Contract totals include both signed scopes and exclude drafts and cancellations", delegate {
                Store store = Fresh(root, "contract"); Record party = Party(store);
                Record contract = R("contract", "partyId", party.Id, "number", "Д-1", "date", "2026-10-12", "amount", "500", "currency", "BYN", "direction", "income", "status", "active"); store.Upsert(contract);
                foreach (Record act in new[] { Act(party.Id, "", "signed", "100"), Act(party.Id, "external", "signed", "200"), Act(party.Id, "external", "draft", "40"), Act(party.Id, "external", "cancelled", "30") })
                { act.Set("contractId", contract.Id); store.Upsert(act); }
                Eq(store.ContractActTotal(contract.Id), 300m); Eq(store.EffectiveContractAmount(contract.Id), 500m);
                Eq(store.Of("act").Where(a => a.Get("status") == "signed").Sum(a => store.ActDue(a.Id)), 300m);
                Eq(store.OfTaxActs().Where(a => a.Get("status") == "signed").Sum(a => Store.ParseMoney(a.Get("amount"))), 100m);
            });
            Test("Both scopes use a single sequence and cannot reuse automatic or manual numbers", delegate {
                Store store = Fresh(root, "numbering"); Record party = Party(store), other = R("party", "name", "Второй", "role", "client"); store.Upsert(other);
                Record ordinary = Act(party.Id, "", "draft", "10"), external = Act(party.Id, "external", "draft", "10"), next = Act(other.Id, "", "draft", "10");
                store.Upsert(ordinary); store.Upsert(external); store.Archive(external.Id); store.Upsert(next);
                Eq(ordinary.Get("number"), "500"); Eq(external.Get("number"), "501"); Eq(next.Get("number"), "502"); Eq(store.NextActNumber, 503L);
                Record reused = Act(other.Id, "external", "draft", "10"); reused.Set("number", "00500"); Reject(delegate { store.Upsert(reused); });
                Record manual = Act(party.Id, "", "draft", "10"); manual.Set("number", "А-1"); store.Upsert(manual);
                Record manualOutside = Act(party.Id, "external", "draft", "10"); manualOutside.Set("number", "А-1"); Reject(delegate { store.Upsert(manualOutside); });
                Eq(new Store(store.DataDir).NextActNumber, 503L);
            });
            Test("Reclassifying a signed act keeps its identity, number, allocations and balance", delegate {
                Store store = Fresh(root, "reclassify"); Record party = Party(store); Record act = Act(party.Id, "", "signed", "100"); store.Upsert(act);
                Record payment = R("payment", "partyId", party.Id, "date", "2026-10-12", "amount", "30", "currency", "BYN", "direction", "income", "method", "cash"); store.Upsert(payment); store.Allocate(payment.Id, act.Id, 30m);
                Eq(store.OfTaxActs().Count(), 1); act = store.Find(act.Id); act.Set("accountingScope", "external"); store.Upsert(act);
                Eq(act.Get("number"), "500"); Eq(store.OfTaxActs().Count(), 0); Eq(store.Of("act").Count(), 1); Eq(store.ActDue(act.Id), 70m); Eq(store.AllocatedToAct(act.Id), 30m);
                act.Set("accountingScope", "standard"); store.Upsert(act); Eq(store.OfTaxActs().Count(), 1); Eq(store.ActDue(act.Id), 70m); Eq(store.NextActNumber, 501L);
            });
            Test("Monthly automation creates only ordinary acts and retains one global number counter", delegate {
                Store store = Fresh(root, "monthly"); Record party = Party(store);
                party.Set("monthlyServiceEnabled", "true"); party.Set("monthlyServiceAmount", "100"); party.Set("monthlyServiceCurrency", "BYN"); party.Set("monthlyServiceStartMonth", "2026-10-01"); store.Upsert(party);
                Record external = Act(party.Id, "external", "draft", "25"); store.Upsert(external);
                Record generated = store.GenerateMonthlyServiceActs(new DateTime(2026, 10, 12)).Single(); Eq(Store.IsTaxAct(generated), true); Eq(Store.IsExternalAct(generated), false); Eq(generated.Get("number"), "501");
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2026, 10, 31)).Count, 0); Eq(store.OfTaxActs().Count(), 1); Eq(store.Of("act").Count(), 2);
                generated.Set("accountingScope", "external"); string bytes = File.ReadAllText(store.DataPath); Reject(delegate { store.Upsert(generated); }); Eq(File.ReadAllText(store.DataPath), bytes);
                Eq(store.GenerateMonthlyServiceActs(new DateTime(2026, 11, 1)).Single().Get("number"), "502");
            });
            Test("Unknown scope in a saved ledger is rejected without replacing original bytes", delegate {
                Store store = Fresh(root, "corrupt-scope"); Record party = Party(store); Record external = Act(party.Id, "external", "draft", "10"); store.Upsert(external);
                string json = File.ReadAllText(store.DataPath).Replace("\"accountingScope\":\"external\"", "\"accountingScope\":\"extenal\"");
                File.WriteAllText(store.DataPath, json); Reject(delegate { new Store(store.DataDir); }); Eq(File.ReadAllText(store.DataPath), json);
            });
            Console.WriteLine("External act core tests: " + passes + " passed, " + failures + " failed. Artifacts: " + root);
            return failures;
        }
        private static void Test(string name, Action test) { try { test(); passes++; Console.WriteLine("PASS " + name); } catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e); } }
        private static void Eq<T>(T actual, T expected) { if (!Object.Equals(actual, expected)) throw new Exception("Expected " + expected + ", got " + actual); }
        private static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected validation rejection."); }
        private static Store Fresh(string root, string name) { return new Store(Path.Combine(root, name)); }
        private static Record R(string kind, params string[] fields) { Record r = new Record(kind); for (int i = 0; i < fields.Length; i += 2) r.Set(fields[i], fields[i + 1]); return r; }
        private static Record Party(Store store) { Record party = R("party", "name", "Клиент", "role", "client"); store.Upsert(party); return party; }
        private static Record Act(string partyId, string scope, string status, string amount) { return R("act", "partyId", partyId, "accountingScope", scope, "date", "2026-10-12", "amount", amount, "currency", "BYN", "direction", "income", "status", status); }
        private static HourlyActLine Line(string description, decimal hours, decimal rate) { return new HourlyActLine { Description = description, Hours = hours, Rate = rate }; }
    }
}
