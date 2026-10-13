using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class GrayPaymentIntegrationTests
    {
        private static void Assert(bool ok, string why) { if (!ok) throw new Exception("Grey payments: " + why); }
        private static T Field<T>(object instance, string key) { return (T)instance.GetType().GetField(key, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
        private static Record Add(Store store, string kind, params string[] values)
        {
            var r = new Record(kind);
            for (int i = 0; i < values.Length; i += 2) r.Set(values[i], values[i+1]);
            store.Upsert(r); return r;
        }
        private static Record Act(Store store, Record party, string number, bool external, string direction, string currency)
        {
            return Add(store, "act", "number", number, "date", "2026-07-01", "partyId", party.Id,
                "direction", direction, "currency", currency, "amount", "6000", "status", "signed",
                "accountingScope", external ? "external" : "standard");
        }
        private static Record Pay(Store store, Record party, string number, string date, string direction, string amount, string currency, string scope, Record act)
        {
            return Add(store, "payment", "number", number, "date", date, "partyId", party.Id, "direction", direction,
                "amount", amount, "currency", currency, "method", "bank", "paymentScope", scope, "actId", act == null ? "" : act.Id);
        }
        private static IEnumerable<Control> All(Control control)
        {
            foreach (Control child in control.Controls) { yield return child; foreach (Control nested in All(child)) yield return nested; }
        }
        private static string Money(decimal value) { return value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")); }
        private static void OverviewTotal(MainForm form, decimal expected, string currency)
        {
            form.Navigate("overview");
            Field<ComboBox>(form, "currency").SelectedItem = currency; form.RefreshView(); Application.DoEvents();
            Label heading = All(Field<Panel>(form, "overview")).OfType<Label>().Single(l => l.Text == "С/Р");
            Assert(heading.Parent.Controls.OfType<Label>().Any(l => l.Text == Money(expected) + " " + currency),
                "SR totals each incoming source payment once in the selected currency");
            Assert(heading.Parent.Controls.OfType<Label>().Any(l => l.Text.Contains("Белые + серые")), "SR explains both receipt categories");
        }

        public static void Run(string root)
        {
            string folder = Path.Combine(root, "grey-payment-" + Guid.NewGuid().ToString("N"));
            var store = new Store(Path.Combine(folder, "data"));
            Add(store, "settings", "currency", "BYN");
            Record party = Add(store, "party", "name", "Проверочный заказчик", "role", "both");
            Record ordinary = Act(store, party, "WHITE-A", false, "income", "BYN");
            Record external = Act(store, party, "GREY-A", true, "income", "BYN");
            Record expenseAct = Act(store, party, "GREY-EXP-A", true, "expense", "BYN");
            Record white = Pay(store, party, "WHITE-P", "2026-08-01", "income", "1000", "BYN", "standard", null);
            Record grey = Pay(store, party, "GREY-P", "2026-08-02", "income", "500", "BYN", "external", external);
            Record mixed = Pay(store, party, "MIXED-P", "2026-09-01", "income", "120", "BYN", "", null);
            store.Allocate(mixed.Id, ordinary.Id, 40m); store.Allocate(mixed.Id, external.Id, 60m);
            Record archivedGrey = Pay(store, party, "ARCHIVED-GREY", "2026-09-30", "income", "250", "BYN", "external", external);
            store.Archive(archivedGrey.Id);
            Record nextQuarter = Pay(store, party, "NEXT-Q", "2026-10-01", "income", "200", "BYN", "standard", null);
            Pay(store, party, "WHITE-EXP", "2026-08-01", "expense", "300", "BYN", "standard", null);
            Pay(store, party, "GREY-EXP", "2026-08-01", "expense", "100", "BYN", "external", expenseAct);
            Record dollar = Pay(store, party, "USD-P", "2026-08-01", "income", "100", "USD", "standard", null);
            byte[] bytes = File.ReadAllBytes(store.DataPath); long revision = store.Revision; int audits = store.Audit.Count;
            QuarterlyTaxResult result = QuarterlyTaxes.Calculate(store, 2026, 3, "BYN");
            Assert(result.Receipts == 1060m && result.Tax == 212m && result.Remaining == 848m, "tax uses white parts by payment date, not grey, expenses or a later quarter");
            Assert(result.Payments.Select(p => p.PaymentId).OrderBy(x => x).SequenceEqual(new[] { white.Id, mixed.Id }.OrderBy(x => x)), "only taxable payment IDs enter tax detail");
            TaxReceipt mixedReceipt = result.Payments.Single(p => p.PaymentId == mixed.Id);
            Assert(mixedReceipt.Amount == 60m && mixedReceipt.TotalAmount == 120m && mixedReceipt.GreyAmount == 60m, "mixed tax detail retains exact original, white and excluded grey amounts");
            Assert(QuarterlyTaxes.Calculate(store, 2026, 3, "USD").Receipts == 100m, "currency remains independent");
            Assert(QuarterlyTaxes.Calculate(store, 2026, 4, "BYN").Receipts == 200m, "payment date chooses quarter");

            using (var form = new MainForm(store))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.ApplyInterfacePreferences(new UiPreferences { AnimationsEnabled = false });
                form.Show(); Application.DoEvents(); OverviewTotal(form, 2070m, "BYN"); OverviewTotal(form, 100m, "USD");
                form.Navigate(Catalog.GrayPaymentSection);
                Field<ComboBox>(form, "currency").SelectedItem = "BYN"; form.RefreshView(); Application.DoEvents();
                TableData table = Field<TableData>(form, "table");
                Assert(table.Title == "Серые оплаты" && table.Ids.Contains(grey.Id) && table.Ids.Contains(mixed.Id) && !table.Ids.Contains(white.Id) && !table.Ids.Contains(archivedGrey.Id), "grey section shows grey portions and respects archive filter");
                int mixedIndex = table.Ids.IndexOf(mixed.Id);
                Assert(table.Rows[mixedIndex][table.Headers.IndexOf("Белая часть")] == Money(60m) && table.Rows[mixedIndex][table.Headers.IndexOf("Серая часть")] == Money(60m), "mixed row exposes parts without rewriting its full payment");
                Assert(form.NewRecordForCurrentSection().Kind == "payment" && form.NewRecordForCurrentSection().Get("paymentScope") == "external", "grey creation seeds a payment with an explicit grey scope");
                Assert(form.SectionForRecord(grey) == Catalog.GrayPaymentSection && form.SectionForRecord(mixed) == "payment", "record navigation routes whole grey payments and mixed receipts consistently");
                Assert(Field<FlowLayoutPanel>(form, "actions").Controls.OfType<Button>().Any(b => b.Text == "Зачесть оплату"), "grey view retains allocation action");
                Field<CheckBox>(form, "archived").Checked = true; form.RefreshView();
                Assert(Field<TableData>(form, "table").Ids.Contains(archivedGrey.Id), "archived grey receipts remain visible on request");
                using (var allocationDialog = new AllocationDialog(store, external))
                {
                    var paymentChoices = Field<ComboBox>(allocationDialog, "payments");
                    var actChoices = Field<ComboBox>(allocationDialog, "acts");
                    object paymentChoice = paymentChoices.SelectedItem, actChoice = actChoices.SelectedItem;
                    string paymentId = (string)paymentChoice.GetType().GetField("Id").GetValue(paymentChoice);
                    string actId = (string)actChoice.GetType().GetField("Id").GetValue(actChoice);
                    Assert(store.Find(paymentId).Get("paymentScope") != "standard" && actId == external.Id,
                        "allocating an external act selects a compatible payment instead of redirecting to an ordinary act");
                }
                form.Navigate("payment"); Field<ComboBox>(form, "currency").SelectedItem = "BYN"; form.RefreshView();
                table = Field<TableData>(form, "table");
                Assert(table.Ids.Contains(white.Id) && table.Ids.Contains(mixed.Id) && table.Ids.Contains(nextQuarter.Id) && !table.Ids.Contains(grey.Id) && !table.Ids.Contains(archivedGrey.Id), "ordinary section shows white portions only");
                Assert(form.NewRecordForCurrentSection().Get("paymentScope") == "standard", "ordinary creation explicitly remains white");
                form.Navigate("taxes"); var page = Field<QuarterlyTaxesPage>(form, "taxesPage"); page.SetPeriod(2026, 3, "BYN"); Application.DoEvents();
                Assert(page.CurrentResult.Receipts == 1060m, "tax page uses the same payment classification as payment lists");
                form.SelectReport(4); table = Field<TableData>(form, "table");
                Assert(table.Ids.Count(id => id == grey.Id) == 1 && table.Ids.Count(id => id == mixed.Id) == 1, "cash flow retains one complete record for each grey or mixed payment");
                var preferences = new UiPreferences { AnimationsEnabled = false };
                foreach (UiSection section in UiPreferences.Sections) preferences.SetVisible(section.Key, section.Key == Catalog.GrayPaymentSection);
                form.ApplyInterfacePreferences(preferences); Application.DoEvents();
                Assert(Field<string>(form, "current") == Catalog.GrayPaymentSection, "grey payments can be the sole visible main section");
                form.Close();
            }
            Assert(store.Revision == revision && store.Audit.Count == audits && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), "views, routing, tax recalculation and SR leave ledger and audit unchanged");

            // A direct legacy external receipt remains fully grey, including its unallocated advance.
            var legacy = new Store(Path.Combine(folder, "legacy-direct"));
            Record cp = Add(legacy, "party", "name", "Старый заказчик", "role", "both");
            Record ext = Act(legacy, cp, "OLD-A", true, "income", "BYN");
            Record legacyPayment = Pay(legacy, cp, "OLD-P", "2026-07-01", "income", "100", "BYN", "", ext);
            legacy.Allocate(legacyPayment.Id, ext.Id, 30m);
            Assert(QuarterlyTaxes.Calculate(legacy, 2026, 3, "BYN").Receipts == 0m, "linked legacy grey advance is excluded fully");
            Assert(PaymentAccounting.Build(legacy.Records).For(legacyPayment).GreyAmount == 100m, "legacy direct link and allocation are never counted twice");
            Console.WriteLine("PASS: grey and white navigation, mixed tax portions, currency/quarter, archive, SR, common cash report and unchanged ledger");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "PASS.txt"), "PASS: white+grey SR, grey tax exclusion, mixed receipt white parts, dates/currencies, navigation/visibility, legacy grey advance and read-only views.");
        }
    }
}
