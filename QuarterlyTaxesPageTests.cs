using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class QuarterlyTaxesPageTests
    {
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Quarterly tax page test: " + message);
        }
        private static T Named<T>(Control root, string name) where T : Control
        {
            Control[] found = root.Controls.Find(name, true);
            Assert(found.Length == 1 && found[0] is T, "missing page control " + name);
            return (T)found[0];
        }
        private static Record Add(Store store, string kind, params string[] fields)
        {
            var record = new Record(kind);
            for (int i = 0; i < fields.Length; i += 2) record.Set(fields[i], fields[i + 1]);
            store.Upsert(record); return record;
        }
        private static Record Payment(Store store, Record party, string number, string date, string currency, string direction, string amount, string method, bool archived)
        {
            return Add(store, "payment", "partyId", party.Id, "number", number, "date", date, "currency", currency,
                "direction", direction, "amount", amount, "method", method, "archived", archived ? "true" : "false");
        }
        private static void Unchanged(Store store, byte[] before, long revision, string operation)
        {
            Assert(store.Revision == revision && File.ReadAllBytes(store.DataPath).SequenceEqual(before), operation + " wrote to the ledger");
        }
        private static void Card(QuarterlyTaxesPage page, string controlName, decimal value, string currency)
        {
            Assert(Named<Label>(page, controlName).Text == value.ToString("N2", Russian) + " " + currency, "wrong card value in " + controlName);
        }
        private static void Layout(QuarterlyTaxesPage page, Form host, Size size)
        {
            host.ClientSize = size; host.PerformLayout(); page.PerformLayout(); Application.DoEvents();
            var grid = Named<DataGridView>(page, "taxPaymentsGrid");
            Assert(grid.ClientSize.Width >= grid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible), "payment columns require horizontal scrolling at " + size);
            Assert(grid.ClientSize.Height >= grid.ColumnHeadersHeight + grid.RowTemplate.Height, "payment table has no usable row at " + size);
            foreach (string name in new[] { "taxYear", "taxQuarter", "taxCurrency", "taxPeriod", "taxReceipts", "taxAmount", "taxRemaining", "taxPaymentCount" })
            {
                Control control = Named<Control>(page, name);
                Rectangle relative = page.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                Assert(relative.Left >= 0 && relative.Top >= 0 && relative.Right <= page.ClientSize.Width + 1 && relative.Bottom <= page.ClientSize.Height + 1,
                    name + " is clipped at " + size);
            }
            foreach (string name in new[] { "taxReceipts", "taxAmount", "taxRemaining" })
            {
                Label label = Named<Label>(page, name);
                Size text = TextRenderer.MeasureText(label.Text, label.Font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                Assert(text.Width <= label.ClientSize.Width && text.Height <= label.ClientSize.Height, name + " summary amount is clipped at " + size + ": measured " + text + ", available " + label.ClientSize + ", font " + label.Font.Size);
            }
        }
        public static void Run(string root)
        {
            Directory.CreateDirectory(root);
            var store = new Store(Path.Combine(root, "tax-page-" + Guid.NewGuid().ToString("N")));
            Add(store, "settings", "currency", "EUR");
            Record party = Add(store, "party", "name", "Тестовый заказчик", "role", "both");
            Record bank = Payment(store, party, "П-001", "2026-07-20", "EUR", "income", "1250.50", "bank", false);
            Record archived = Payment(store, party, "П-002", "2026-08-15", "EUR", "income", "250.00", "cash", true);
            Payment(store, party, "П-003", "2026-09-01", "USD", "income", "600.00", "bank", false);
            Payment(store, party, "П-004", "2026-08-01", "EUR", "expense", "99.00", "bank", false);
            Payment(store, party, "П-005", "2026-10-01", "EUR", "income", "300.00", "other", false);
            byte[] original = File.ReadAllBytes(store.DataPath); long revision = store.Revision;
            using (var form = new Form { ShowInTaskbar = false, Opacity = 0, Font = new Font("Segoe UI", 9.5F), AutoScaleMode = AutoScaleMode.None })
            using (var page = new QuarterlyTaxesPage(store))
            {
                form.Controls.Add(page); form.ClientSize = new Size(1180, 620); form.Show(); Application.DoEvents();
                Tuple<int, int> previous = QuarterlyTaxes.PreviousQuarter(DateTime.Today);
                Assert(page.SelectedYear == previous.Item1 && page.SelectedQuarter == previous.Item2 && page.SelectedCurrency == "EUR", "default period/currency did not use completed quarter and organization currency");
                page.SetPeriod(2026, 3, "EUR"); Application.DoEvents();
                Assert(page.CurrentResult.Receipts == 1500.50m && page.CurrentResult.Tax == 300.10m && page.CurrentResult.Remaining == 1200.40m, "period selection did not refresh calculation");
                Card(page, "taxReceipts", 1500.50m, "EUR"); Card(page, "taxAmount", 300.10m, "EUR"); Card(page, "taxRemaining", 1200.40m, "EUR");
                string period = Named<Label>(page, "taxPeriod").Text;
                Assert(period.Contains("1 июля") && period.Contains("30 сентября 2026"), "selected date range is not shown explicitly");
                var grid = Named<DataGridView>(page, "taxPaymentsGrid");
                Assert(grid.ReadOnly && !grid.AllowUserToAddRows && !grid.AllowUserToDeleteRows && grid.Rows.Count == 2, "detail table is editable or has wrong receipts");
                Assert(grid.Rows[0].Cells["date"].Value is DateTime && grid.Rows[0].Cells["amount"].Value is decimal, "date/money sorting lost typed values");
                Assert(Convert.ToString(grid.Rows[0].Cells["method"].Value) == "Банк" && Convert.ToString(grid.Rows[1].Cells["method"].Value) == "Наличные", "payment method labels show internal values");
                Assert(Convert.ToString(grid.Rows[1].Tag) == archived.Id && Convert.ToString(grid.Rows[1].Cells["number"].Value).Contains("архив"), "archive inclusion is hidden or payment identity lost");
                Assert(Named<Label>(page, "taxPaymentCount").Text.Contains("В архиве: 1"), "included archive count is missing");
                Layout(page, form, new Size(1180, 620)); Layout(page, form, new Size(820, 450));
                grid.Sort(grid.Columns["amount"], ListSortDirection.Ascending);
                Assert(Convert.ToString(grid.Rows[0].Tag) == archived.Id, "amount sort is lexical or detached payment IDs");
                grid.Sort(grid.Columns["amount"], ListSortDirection.Descending);
                Assert(Convert.ToString(grid.Rows[0].Tag) == bank.Id, "descending numeric sort is incorrect");
                string requested = null; page.PaymentRequested += delegate(string id) { requested = id; };
                typeof(DataGridView).GetMethod("OnCellDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(grid, new object[] { new DataGridViewCellEventArgs(0, 0) });
                Assert(requested == bank.Id, "open-payment action uses a row index instead of sorted payment ID");
                Named<ComboBox>(page, "taxQuarter").SelectedIndex = 3; Application.DoEvents();
                Assert(page.CurrentResult.Receipts == 300m && grid.Rows.Count == 1 && Convert.ToString(grid.Rows[0].Cells["method"].Value) == "Другое", "changing the quarter did not refresh receipts");
                Named<ComboBox>(page, "taxCurrency").SelectedItem = "RUB"; Application.DoEvents();
                Assert(page.CurrentResult.Receipts == 0 && page.CurrentResult.Tax == 0 && page.CurrentResult.Remaining == 0 && grid.Rows.Count == 0, "empty quarter did not clear old data");
                Assert(Named<Label>(page, "taxEmptyState").Visible && !grid.Visible, "empty quarter did not show a friendly state");
                Card(page, "taxReceipts", 0, "RUB"); Card(page, "taxAmount", 0, "RUB"); Card(page, "taxRemaining", 0, "RUB");
                page.SetPeriod(2026, 3, "EUR"); page.RefreshData();
                Unchanged(store, original, revision, "constructing/selecting/sorting/refreshing the tax page");
                Payment(store, party, "П-006", "2026-09-30", "EUR", "income", "1000.00", "bank", false);
                original = File.ReadAllBytes(store.DataPath); revision = store.Revision;
                page.RefreshData();
                Assert(page.SelectedYear == 2026 && page.SelectedQuarter == 3 && page.SelectedCurrency == "EUR" && page.CurrentResult.Receipts == 2500.50m && grid.Rows.Count == 3,
                    "refresh missed new payments or reset own selectors");
                Card(page, "taxAmount", 500.10m, "EUR");
                Unchanged(store, original, revision, "refreshing after a new receipt");
                bool rejected = false; try { page.SetPeriod(2026, 5, "EUR"); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Assert(rejected && page.SelectedQuarter == 3 && page.CurrentResult.Receipts == 2500.50m, "invalid period partly changed the page");
                form.Close();
            }
            File.WriteAllText(Path.Combine(root, "quarterly-taxes-page-result.txt"), "PASS: default period/currency, selector refresh, cards, typed numeric sorting and payment identity, archive receipts, empty state, responsive layout, read-only ledger and refresh after new receipt");
        }
    }
}