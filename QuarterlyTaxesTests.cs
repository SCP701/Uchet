using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WorkLedger
{
    public static class QuarterlyTaxesTests
    {
        public static void Run(string root)
        {
            string folder = Path.Combine(Path.GetFullPath(root), "quarterly-taxes-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            int passed = 0;
            Action<string, Action> test = delegate(string name, Action action) { action(); passed++; Console.WriteLine("PASS Quarterly taxes " + name); };
            test("ten thousand receipts give twenty percent tax and eight thousand remaining", delegate {
                Store store = Fresh(folder, "ten-thousand"); Record party = Party(store, "Тестовый заказчик");
                Payment(store, party, "2026-07-15", 10000);
                QuarterlyTaxResult result = ReadonlyCalculate(store, 2026, 3, "BYN");
                Eq(result.Year, 2026, "year"); Eq(result.Quarter, 3, "quarter"); Eq(result.Currency, "BYN", "currency"); Eq(result.Rate, 0.20m, "rate");
                Eq(result.FromDate, new DateTime(2026, 7, 1), "quarter start"); Eq(result.ToDate, new DateTime(2026, 9, 30), "quarter end");
                Amounts(result, 10000m, 2000m, 8000m); Eq(result.Payments.Count, 1, "receipts count"); Eq(result.Payments[0].PartyName, party.Get("name"), "counterparty name");
            });
            test("quarter boundaries use actual payment date with both endpoints included", delegate {
                Store store = Fresh(folder, "boundaries"); Record party = Party(store, "Границы");
                Payment(store, party, "2026-06-30", 100); Record first = Payment(store, party, "2026-07-01", 10); Record last = Payment(store, party, "2026-09-30", 20);
                Payment(store, party, "2026-10-01", 200); Payment(store, party, "2025-07-01", 1000);
                QuarterlyTaxResult result = ReadonlyCalculate(store, 2026, 3, "BYN"); Amounts(result, 30m, 6m, 24m);
                Eq(result.Payments.Count, 2, "boundary payment count"); True(result.Payments.Any(x => x.PaymentId == first.Id) && result.Payments.Any(x => x.PaymentId == last.Id), "both endpoint payments included");
            });
            test("leap day and extreme supported years have safe inclusive dates", delegate {
                Store store = Fresh(folder, "calendar"); Record party = Party(store, "Календарь");
                Record leap = Payment(store, party, "2024-02-29", 1); Payment(store, party, "2024-03-31", 2); Payment(store, party, "2024-04-01", 3);
                QuarterlyTaxResult result = ReadonlyCalculate(store, 2024, 1, "BYN"); Amounts(result, 3m, 0.60m, 2.40m); True(result.Payments.Any(x => x.PaymentId == leap.Id), "leap day included"); Eq(result.ToDate, new DateTime(2024, 3, 31), "leap quarter end");
                Payment(store, party, "0001-01-01", 4); Payment(store, party, "0001-03-31", 5); Payment(store, party, "0001-04-01", 6);
                result = ReadonlyCalculate(store, 1, 1, "BYN"); Amounts(result, 9m, 1.80m, 7.20m); Eq(result.FromDate, DateTime.MinValue, "first supported day");
                Payment(store, party, "9999-10-01", 7); Payment(store, party, "9999-12-31", 8);
                result = ReadonlyCalculate(store, 9999, 4, "BYN"); Amounts(result, 15m, 3m, 12m); Eq(result.ToDate, new DateTime(9999, 12, 31), "last supported quarter avoids year 10000");
            });
            test("old draft cancelled and unpaid acts do not determine receipt period", delegate {
                Store store = Fresh(folder, "act-statuses"); Record party = Party(store, "Акты");
                Record old = Act(store, party, "OLD", "2019-01-10", "signed", 500);
                Record draft = Act(store, party, "DRAFT", "2026-08-01", "draft", 500);
                Record cancelled = Act(store, party, "CANCELLED", "2026-09-01", "cancelled", 500);
                Act(store, party, "UNPAID", "2026-07-10", "signed", 10000);
                Payment(store, party, "2026-07-10", 30, "BYN", "income", "bank", old.Id);
                Payment(store, party, "2026-08-10", 40, "BYN", "income", "cash", draft.Id);
                Payment(store, party, "2026-09-10", 50, "BYN", "income", "other", cancelled.Id);
                Payment(store, party, "2026-06-28", 200, "BYN", "income", "bank", draft.Id);
                QuarterlyTaxResult result = ReadonlyCalculate(store, 2026, 3, "BYN"); Amounts(result, 120m, 24m, 96m); Eq(result.Payments.Count, 3, "paid now despite act date or status");
            });
            test("bank cash other advances and several allocations count each payment once", delegate {
                Store store = Fresh(folder, "allocations"); Record party = Party(store, "Авансы");
                Record first = Act(store, party, "ALLOC-1", "2026-04-01", "signed", 100), second = Act(store, party, "ALLOC-2", "2026-04-02", "signed", 80);
                Record payment = Payment(store, party, "2026-07-02", 120); store.Allocate(payment.Id, first.Id, 70); store.Allocate(payment.Id, second.Id, 50);
                Payment(store, party, "2026-08-02", 20, "BYN", "income", "cash"); Payment(store, party, "2026-09-02", 10, "BYN", "income", "other");
                QuarterlyTaxResult result = ReadonlyCalculate(store, 2026, 3, "BYN"); Amounts(result, 150m, 30m, 120m); Eq(result.Payments.Count, 3, "allocation rows never counted");
                Eq(result.Payments.Select(x => x.Method).Distinct().Count(), 3, "all receipt methods");
                store.ReverseAllocation(store.Of("allocation").First().Id); result = ReadonlyCalculate(store, 2026, 3, "BYN"); Amounts(result, 150m, 30m, 120m); Eq(result.Payments.Count, 3, "reversed allocation does not undo receipt");
                Amounts(ReadonlyCalculate(store, 2026, 4, "BYN"), 0m, 0m, 0m);
            });
            test("archived receipts and archived counterparties remain in actual cash totals", delegate {
                Store store = Fresh(folder, "archive"); Record party = Party(store, "Архивный заказчик");
                Payment(store, party, "2026-07-01", 10); Record archived = Payment(store, party, "2026-07-02", 20); store.Archive(archived.Id); party.Set("archived", "true"); store.Upsert(party); store = new Store(store.DataDir);
                Eq(store.Of("party").Count(), 0, "counterparty hidden from active catalog"); Eq(store.Of("payment").Count(), 1, "payment hidden from active catalog");
                QuarterlyTaxResult result = ReadonlyCalculate(store, 2026, 3, "BYN"); Amounts(result, 30m, 6m, 24m); Eq(result.Payments.Count, 2, "archive counted"); Eq(result.Payments.Count(x => x.Archived), 1, "archive marker");
                True(result.Payments.All(x => x.PartyName == "Архивный заказчик"), "archived party name resolved from same snapshot");
            });
            test("currencies stay separate and outgoing payments never count as receipts", delegate {
                Store store = Fresh(folder, "currency"); Record party = Party(store, "Валюта"); string[] currencies = { "BYN", "RUB", "USD", "EUR" };
                for (int i = 0; i < currencies.Length; i++) { Payment(store, party, "2026-07-01", i + 1, currencies[i]); Payment(store, party, "2026-07-01", 500, currencies[i], "expense"); }
                for (int i = 0; i < currencies.Length; i++)
                {
                    QuarterlyTaxResult result = ReadonlyCalculate(store, 2026, 3, currencies[i]); Amounts(result, i + 1, (i + 1) * 0.20m, (i + 1) * 0.80m); Eq(result.Currency, currencies[i], "strict currency"); Eq(result.Payments.Count, 1, "one income receipt per currency");
                }
            });
            test("tax rounds after aggregate rather than per receipt", delegate {
                Store store = Fresh(folder, "aggregate-rounding"); Record party = Party(store, "Округление");
                Payment(store, party, "2026-07-01", 0.03m); Payment(store, party, "2026-07-02", 0.03m); Payment(store, party, "2026-07-03", 0.03m);
                QuarterlyTaxResult result = ReadonlyCalculate(store, 2026, 3, "BYN"); Amounts(result, 0.09m, 0.02m, 0.07m); True(result.Tax != 0.03m, "per-payment rounded tax is not used");
                store = Fresh(folder, "aggregate-small"); party = Party(store, "Копейки"); Payment(store, party, "2026-07-01", 0.02m); Payment(store, party, "2026-07-02", 0.02m);
                Amounts(ReadonlyCalculate(store, 2026, 3, "BYN"), 0.04m, 0.01m, 0.03m);
            });
            test("same-number distinct payments all count and have stable date number id order", delegate {
                Store store = Fresh(folder, "ordering"); Record party = Party(store, "Сортировка");
                Record z = NewPayment(party, "2026-08-01", 4); z.Id = "payment-z"; z.Set("number", "DUP"); store.Upsert(z);
                Record a = NewPayment(party, "2026-08-01", 3); a.Id = "payment-a"; a.Set("number", "DUP"); store.Upsert(a);
                Record earlier = NewPayment(party, "2026-07-01", 1); earlier.Id = "payment-earlier"; earlier.Set("number", "Z"); store.Upsert(earlier);
                Record number = NewPayment(party, "2026-08-01", 2); number.Id = "payment-number"; number.Set("number", "AAA"); store.Upsert(number);
                QuarterlyTaxResult result = ReadonlyCalculate(store, 2026, 3, "BYN"); Amounts(result, 10m, 2m, 8m);
                True(result.Payments.Select(x => x.PaymentId).SequenceEqual(new[] { earlier.Id, number.Id, a.Id, z.Id }), "stable date number id order"); Eq(result.Payments.Count(x => x.Number == "DUP"), 2, "payment numbers do not deduplicate");
                True(result.Payments.IsReadOnly, "receipt collection readonly"); Reject<NotSupportedException>(delegate { result.Payments.Add(result.Payments[0]); }); Reject<NotSupportedException>(delegate { result.Payments.RemoveAt(0); }); Reject<NotSupportedException>(delegate { result.Payments[0] = result.Payments[1]; });
                True(typeof(TaxReceipt).GetProperties().All(x => x.GetSetMethod() == null) && typeof(QuarterlyTaxResult).GetProperties().All(x => x.GetSetMethod() == null), "result data has no public setters");
                Record changed = store.Find(party.Id); changed.Set("name", "Новое название"); store.Upsert(changed); Payment(store, changed, "2026-09-01", 10);
                Eq(result.Receipts, 10m, "detached result amount"); Eq(result.Payments.Count, 4, "detached result count"); True(result.Payments.All(x => x.PartyName == "Сортировка"), "detached result name");
                QuarterlyTaxResult refreshed = ReadonlyCalculate(store, 2026, 3, "BYN"); Eq(refreshed.Receipts, 20m, "new calculation sees new receipt"); True(refreshed.Payments.All(x => x.PartyName == "Новое название"), "new calculation sees current same-snapshot party name");
            });
            test("empty state is zero and calculating never creates a ledger", delegate {
                Store store = Fresh(folder, "empty"); Eq(store.Revision, 0L, "initial revision"); True(!File.Exists(store.DataPath), "no initial ledger");
                QuarterlyTaxResult result = ReadonlyCalculate(store, 2026, 3, "BYN"); Amounts(result, 0m, 0m, 0m); Eq(result.Payments.Count, 0, "empty receipt list"); True(result.Payments.IsReadOnly, "empty list readonly"); True(!File.Exists(store.DataPath), "calculation does not persist empty state");
            });
            test("invalid inputs fail without changing records audit revision or ledger bytes", delegate {
                Store store = Fresh(folder, "invalid"); Record party = Party(store, "Параметры"); Payment(store, party, "2026-07-01", 10);
                byte[] before = File.ReadAllBytes(store.DataPath); long revision = store.Revision; int audit = store.Audit.Count, recordCount = store.Records.Count;
                foreach (int year in new[] { -1, 0, 10000 }) Reject<ArgumentException>(delegate { QuarterlyTaxes.Calculate(store, year, 3, "BYN"); });
                foreach (int quarter in new[] { -1, 0, 5 }) Reject<ArgumentException>(delegate { QuarterlyTaxes.Calculate(store, 2026, quarter, "BYN"); });
                foreach (string currency in new[] { "JPY", "byn", " BYN", "BYN ", "", null }) Reject<ArgumentException>(delegate { QuarterlyTaxes.Calculate(store, 2026, 3, currency); });
                Reject<ArgumentNullException>(delegate { QuarterlyTaxes.Calculate(null, 2026, 3, "BYN"); });
                True(File.ReadAllBytes(store.DataPath).SequenceEqual(before), "invalid calculation preserves ledger"); Eq(store.Revision, revision, "invalid calculation preserves revision"); Eq(store.Audit.Count, audit, "invalid calculation preserves audit"); Eq(store.Records.Count, recordCount, "invalid calculation preserves records");
                Amounts(ReadonlyCalculate(store, 2026, 3, "BYN"), 10m, 2m, 8m);
            });
            test("previous completed quarter defaults across calendar years", delegate {
                Previous(new DateTime(2026, 10, 12), 2026, 3); Previous(new DateTime(2026, 1, 1), 2025, 4); Previous(new DateTime(2026, 3, 31), 2025, 4); Previous(new DateTime(2026, 4, 1), 2026, 1); Previous(new DateTime(2026, 7, 1), 2026, 2); Previous(new DateTime(9999, 12, 31), 9999, 3);
                Reject<ArgumentOutOfRangeException>(delegate { QuarterlyTaxes.PreviousQuarter(DateTime.MinValue); });
            });
            File.WriteAllText(Path.Combine(folder, "PASS.txt"), "PASS: " + passed.ToString(CultureInfo.InvariantCulture) + " groups, actual receipts, quarter dates, advances, archives, allocation independence, currency, aggregate rounding, immutable snapshots and readonly storage.", new UTF8Encoding(false));
            Console.WriteLine("Quarterly taxes: " + passed.ToString(CultureInfo.InvariantCulture) + " groups passed. Synthetic verification: " + folder);
        }
        private static QuarterlyTaxResult ReadonlyCalculate(Store store, int year, int quarter, string currency)
        {
            bool existed = File.Exists(store.DataPath); byte[] before = existed ? File.ReadAllBytes(store.DataPath) : null; long revision = store.Revision; int audits = store.Audit.Count;
            QuarterlyTaxResult result = QuarterlyTaxes.Calculate(store, year, quarter, currency);
            Eq(store.Revision, revision, "calculate revision unchanged"); Eq(store.Audit.Count, audits, "calculate audit unchanged"); Eq(File.Exists(store.DataPath), existed, "calculate ledger existence unchanged");
            if (existed) True(File.ReadAllBytes(store.DataPath).SequenceEqual(before), "calculate ledger bytes unchanged");
            return result;
        }
        private static void Amounts(QuarterlyTaxResult result, decimal receipts, decimal tax, decimal remaining) { Eq(result.Receipts, receipts, "receipts"); Eq(result.Tax, tax, "tax"); Eq(result.Remaining, remaining, "remaining"); Eq(result.Receipts - result.Tax, result.Remaining, "remaining identity"); }
        private static void Previous(DateTime date, int year, int quarter) { Tuple<int, int> result = QuarterlyTaxes.PreviousQuarter(date); Eq(result.Item1, year, "previous year"); Eq(result.Item2, quarter, "previous quarter"); }
        private static Store Fresh(string root, string name) { return new Store(Path.Combine(root, name)); }
        private static Record Party(Store store, string name) { Record party = R("party", "name", name, "role", "both"); store.Upsert(party); return party; }
        private static Record NewPayment(Record party, string date, decimal amount, string currency = "BYN", string direction = "income", string method = "bank", string actId = "") { return R("payment", "date", date, "partyId", party.Id, "amount", Store.MoneyString(amount), "currency", currency, "direction", direction, "method", method, "actId", actId); }
        private static Record Payment(Store store, Record party, string date, decimal amount, string currency = "BYN", string direction = "income", string method = "bank", string actId = "") { Record payment = NewPayment(party, date, amount, currency, direction, method, actId); store.Upsert(payment); return payment; }
        private static Record Act(Store store, Record party, string number, string date, string status, decimal amount) { Record act = R("act", "number", number, "date", date, "partyId", party.Id, "amount", Store.MoneyString(amount), "currency", "BYN", "direction", "income", "status", status); store.Upsert(act); return act; }
        private static Record R(string kind, params string[] fields) { var result = new Record(kind); for (int i = 0; i < fields.Length; i += 2) result.Set(fields[i], fields[i + 1]); return result; }
        private static void True(bool condition, string message) { if (!condition) throw new Exception("Quarterly taxes assertion failed: " + message); }
        private static void Eq<T>(T actual, T expected, string message) { if (!Object.Equals(actual, expected)) throw new Exception("Quarterly taxes " + message + ": expected " + expected + ", got " + actual); }
        private static void Reject<TException>(Action action) where TException : Exception { try { action(); } catch (TException) { return; } throw new Exception("Quarterly taxes expected " + typeof(TException).Name); }
    }
}
