using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class ListLayoutTests
    {
        private sealed class Fixture
        {
            public Store Store;
            public Record Customer, Other, Project, PendingAct, MatchedAct, CheckedAct, LegacyAct;
            public Record PendingPayment, MatchedPayment, CheckedPayment, LegacyPayment, DollarAct, DollarPayment;
            public Record ArchivedAct, OrdinaryDocument;
            public readonly List<Record> Unverified = new List<Record>(), Matched = new List<Record>();
        }
        private static void Assert(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("List layout test: " + reason); }
        private static T Field<T>(object owner, string name)
        {
            FieldInfo field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing integration field " + name); return (T)field.GetValue(owner);
        }
        private static Record Record(string kind, params string[] values)
        {
            var record = new Record(kind);
            for (int i = 0; i < values.Length; i += 2) record.Set(values[i], values[i + 1]);
            return record;
        }
        private static Record Add(Store store, string kind, params string[] values)
        { var record = Record(kind, values); store.Upsert(record); return store.Find(record.Id); }
        private static string Amount(decimal amount) { return amount.ToString("F2", CultureInfo.InvariantCulture); }
        private static Record Act(Fixture f, string number, string day, string amount, string signature, string review)
        {
            return Record("act", "number", number, "date", day, "partyId", f.Customer.Id, "projectId", f.Project.Id,
                "amount", amount, "currency", "BYN", "direction", "income", "status", signature,
                "signatureStatus", "unknown", "description", "Настройка и сопровождение информационной системы, полное описание сохраняется.",
                "notes", "Полное исходное примечание: не терять при скрытии колонок.", "reviewStatus", review,
                "reviewNotes", review == "" ? "" : "Сведения исходного документа для проверки.", "unknownFutureField", "Сохранить неизвестное поле");
        }
        private static Record Payment(Fixture f, string number, string day, string amount, string review)
        {
            return Record("payment", "number", number, "date", day, "partyId", f.Customer.Id, "projectId", f.Project.Id,
                "amount", amount, "currency", "BYN", "direction", "income", "method", "bank", "paymentScope", "standard",
                "notes", "Полное назначение платежа; цена и номер исходника должны сохраниться в выгрузке.",
                "reviewStatus", review, "reviewNotes", review == "" ? "" : "Связь по исходному номеру не подтверждает подпись.");
        }
        private static Fixture Seed(string dataDir)
        {
            var f = new Fixture { Store = new Store(dataDir) }; Store s = f.Store;
            Add(s, "settings", "currency", "BYN", "name", "Организация тестового списка", "hourlyRate", "35.00");
            f.Customer = Add(s, "party", "name", "Общество с ограниченной ответственностью «Заказчик с длинным названием для проверки списка»",
                "role", "client", "unp", "123456789", "phone", "+375 29 123 45 67", "email", "layout@example.test",
                "postalAddress", "Полный почтовый адрес из источника", "bankName", "Название банка для выгрузки");
            f.Other = Add(s, "party", "name", "Другой заказчик и поставщик", "role", "both", "unp", "987654321");
            f.Project = Add(s, "project", "name", "Объект с длинным полным названием, которое нельзя потерять в данных",
                "partyId", f.Customer.Id, "status", "active", "location", "Полный адрес расположения объекта");
            Record contract = Add(s, "contract", "number", "0000123", "date", "2026-09-01", "partyId", f.Customer.Id,
                "projectId", f.Project.Id, "direction", "income", "amount", "50000.50", "currency", "BYN", "status", "active", "dueDate", "2026-12-31");
            Add(s, "amendment", "number", "0000007", "date", "2026-09-03", "contractId", contract.Id, "amount", "100.25", "status", "signed");
            Record estimate = Add(s, "estimate", "number", "0000145", "date", "2026-09-04", "partyId", f.Customer.Id,
                "projectId", f.Project.Id, "contractId", contract.Id, "direction", "income", "currency", "BYN", "status", "approved");
            Add(s, "work", "estimateId", estimate.Id, "description", "Позиция с полным исходным описанием работ", "unit", "ч", "qty", "2.5", "price", "40.00");
            var entries = new List<ReconciliationEntry>();
            var acts = new List<Record>(); var payments = new List<Record>();
            string[] states = { "pending", "matched", "checked" };
            string[] days = { "2025-12-31", "2026-01-09", "2026-10-01" };
            string[] amounts = { "9.50", "100.01", "12880.25" };
            for (int index = 0; index < 40; index++)
            {
                string day = index < 3 ? days[index] : new DateTime(2026, 9, 1).AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string amount = index < 3 ? amounts[index] : Amount(100m + index + .25m);
                string state = index < 3 ? states[index] : "checked";
                var act = Act(f, index == 0 ? "BLR_2025_0140" : "А-" + index.ToString("D6"), day, amount, index < 3 ? "draft" : "signed", state);
                var payment = Payment(f, index.ToString("D7"), day, amount, state);
                acts.Add(act); payments.Add(payment);
                if (index < 3) { entries.Add(new ReconciliationEntry { Record = act }); entries.Add(new ReconciliationEntry { Record = payment }); }
                else { s.Upsert(act); s.Upsert(payment); }
            }
            var package = new ReconciliationPackage { Format = "WorkLedger.Reconciliation", Version = 1,
                PackageId = "list-layout-import", Title = "Тестовые записи всех состояний сверки",
                SourceHashes = new Dictionary<string, string> { { "synthetic", new string('b', 64) } }, Entries = entries };
            ReconciliationImport.Apply(s, package);
            f.PendingAct = s.Find(acts[0].Id); f.MatchedAct = s.Find(acts[1].Id); f.CheckedAct = s.Find(acts[2].Id);
            f.PendingPayment = s.Find(payments[0].Id); f.MatchedPayment = s.Find(payments[1].Id); f.CheckedPayment = s.Find(payments[2].Id);
            f.Unverified.AddRange(new[] { f.PendingAct, f.MatchedAct, f.PendingPayment, f.MatchedPayment });
            f.Matched.AddRange(new[] { f.MatchedAct, f.MatchedPayment });
            f.LegacyAct = Act(f, "LEGACY-ACT", "2026-09-14", "77.77", "draft", ""); s.Upsert(f.LegacyAct);
            f.LegacyPayment = Payment(f, "LEGACY-PAY", "2026-09-14", "77.77", ""); s.Upsert(f.LegacyPayment);
            f.Unverified.AddRange(new[] { f.LegacyAct, f.LegacyPayment });
            f.ArchivedAct = Act(f, "ARCHIVED-ACT", "2026-09-14", "10.25", "draft", "pending"); f.ArchivedAct.Set("archived", "true"); s.Upsert(f.ArchivedAct); f.Unverified.Add(f.ArchivedAct);
            var external = Act(f, "EXTERNAL-ACT", "2026-09-14", "55.50", "draft", "matched"); external.Set("accountingScope", "external"); s.Upsert(external); f.Unverified.Add(external); f.Matched.Add(external);
            var gray = Payment(f, "GRAY-PAY", "2026-09-14", "15.75", "matched"); gray.Set("paymentScope", "external"); gray.Set("actId", external.Id); s.Upsert(gray); f.Unverified.Add(gray); f.Matched.Add(gray);
            var outgoing = Payment(f, "OUT-PAY", "2026-09-14", "77.99", "pending"); outgoing.Set("direction", "expense"); outgoing.Set("partyId", f.Other.Id); outgoing.Set("projectId", ""); s.Upsert(outgoing); f.Unverified.Add(outgoing);
            f.DollarAct = Act(f, "USD-ACT", "2026-09-14", "76.25", "draft", "matched"); f.DollarAct.Set("currency", "USD"); f.DollarAct.Set("partyId", f.Other.Id); f.DollarAct.Set("projectId", ""); s.Upsert(f.DollarAct);
            f.DollarPayment = Payment(f, "USD-PAY", "2026-09-14", "76.25", "matched"); f.DollarPayment.Set("currency", "USD"); f.DollarPayment.Set("partyId", f.Other.Id); f.DollarPayment.Set("projectId", ""); s.Upsert(f.DollarPayment);
            f.Unverified.AddRange(new[] { f.DollarAct, f.DollarPayment }); f.Matched.AddRange(new[] { f.DollarAct, f.DollarPayment });
            foreach (string state in states)
            {
                var doc = Add(s, "document", "title", "Документ сверки " + state, "date", "2026-09-14", "partyId", f.Customer.Id,
                    "status", "draft", "reviewStatus", state, "reviewNotes", "Источник и причина проверки документа.");
                if (state != "checked") f.Unverified.Add(doc);
                if (state == "matched") f.Matched.Add(doc);
            }
            f.OrdinaryDocument = Add(s, "document", "title", "Обычный документ без отметок сверки", "date", "2026-09-14", "partyId", f.Customer.Id, "status", "draft");
            Add(s, "invoice", "number", "0000521", "date", "2026-09-14", "partyId", f.Customer.Id, "projectId", f.Project.Id,
                "contractId", contract.Id, "actId", acts[3].Id, "direction", "income", "amount", "103.25", "currency", "BYN", "status", "sent");
            Add(s, "expense", "date", "2026-09-14", "partyId", f.Other.Id, "projectId", f.Project.Id,
                "category", "materials", "amount", "9.50", "currency", "BYN", "description", "Полное описание расхода для экспорта.");
            Add(s, "hold", "actId", acts[3].Id, "date", "2026-09-14", "amount", "2.00", "status", "active", "type", "warranty");
            return f;
        }
        private static IEnumerable<Control> Descendants(Control root)
        { foreach (Control child in root.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; } }
        private static List<string> Ids(DataGridView grid)
        { return grid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).Select(row => Convert.ToString(row.Tag)).ToList(); }
        private static void IdsEqual(DataGridView grid, IEnumerable<Record> records, string reason)
        {
            var actual = Ids(grid); var expected = records.Select(record => record.Id).ToList();
            Assert(actual.Count == expected.Count && !actual.Except(expected).Any() && !expected.Except(actual).Any(), reason);
        }
        private static void Choose(MainForm form, string field, string id)
        {
            var combo = Field<ComboBox>(form, field);
            foreach (object item in combo.Items)
            {
                var choice = item as Choice;
                if (choice != null && choice.Id == id) { combo.SelectedItem = item; Application.DoEvents(); return; }
            }
            throw new InvalidOperationException("List layout test: missing explicit filter choice " + field + ":" + id);
        }
        private static void ShowHidden(Form form, Size size)
        { form.ShowInTaskbar = false; form.Opacity = 0; form.StartPosition = FormStartPosition.Manual; form.Location = Point.Empty; form.Size = size; form.Show(); form.PerformLayout(); Application.DoEvents(); }
        private static void Geometry(DataGridView grid, string context)
        {
            Assert(grid.Visible && grid.ClientSize.Width > 0, context + ": grid not rendered");
            var columns = grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).OrderBy(column => column.DisplayIndex).ToList();
            Assert(columns.Count > 0, context + ": all information columns were hidden");
            int right = grid.ClientSize.Width;
            VScrollBar vertical = grid.Controls.OfType<VScrollBar>().FirstOrDefault(bar => bar.Visible);
            if (vertical != null) right = Math.Min(right, vertical.Left);
            if (grid.BorderStyle != BorderStyle.None) right--;
            int left = grid.RowHeadersVisible ? grid.RowHeadersWidth : 0;
            int actualAvailable = right - left;
            int columnWidth = columns.Sum(column => column.Width);
            Assert(columnWidth <= actualAvailable, context + ": visible columns " + columnWidth + " exceed actual viewport " + actualAvailable);
            Assert(!grid.Controls.OfType<HScrollBar>().Any(bar => bar.Visible), context + ": actual bottom horizontal scrollbar is visible");
            Assert(grid.HorizontalScrollingOffset == 0, context + ": hidden horizontal displacement remains");
            foreach (DataGridViewColumn column in columns)
            {
                Rectangle rectangle = grid.GetCellDisplayRectangle(column.Index, -1, false);
                Assert(rectangle.Left >= left - 1 && rectangle.Right <= right + 1,
                    context + ": visible header outside viewport: " + column.HeaderText);
                Assert(grid.GetCellDisplayRectangle(column.Index, -1, true).Width >= column.Width - 1,
                    context + ": column is clipped despite disabled scrolling: " + column.HeaderText);
            }
        }
        private static void Important(DataGridView grid, string context, params string[] headers)
        {
            string[] moneyHeaders = { "Сумма", "К оплате", "Выручка по актам", "Всего затрат", "Прибыль", "Сальдо", "Изменение сальдо" };
            foreach (string header in headers)
            {
                DataGridViewColumn column = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.HeaderText == header);
                Assert(column != null && column.Visible, context + ": important column missing or hidden: " + header);
                bool fullValueRequired = header == "Дата" || header == "Валюта" || moneyHeaders.Contains(header);
                bool entityName = header == "Контрагент" || header == "Объект" || header == "Название";
                if (!fullValueRequired)
                    Assert(column.Width >= (entityName ? 90 : 60), context + ": text/identifier column has no readable minimum: " + header);
                foreach (DataGridViewRow row in grid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow))
                {
                    DataGridViewCell cell = row.Cells[column.Index]; string text = Convert.ToString(cell.FormattedValue);
                    if (text == "") continue;
                    Font font = cell.InheritedStyle.Font ?? grid.Font;
                    int required = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding).Width + cell.InheritedStyle.Padding.Horizontal;
                    if (fullValueRequired)
                        Assert(column.Width >= required, context + ": date/money/currency value cannot be read completely: " + header + " / " + text + " (" + column.Width + " < " + required + ")");
                    else if (column.Width < required)
                        Assert(cell.ToolTipText == Convert.ToString(cell.Value), context + ": shortened text/identifier lost full-value tooltip: " + header);
                }
            }
        }
        private static void ExportKeepsCells(MainForm form, string context)
        {
            DataGridView grid = Field<DataGridView>(form, "grid"); TableData exported = form.BuildExportTable();
            Assert(exported.Rows.Count == grid.Rows.Count, context + ": export drops rendered records");
            Assert(exported.Headers.Count >= grid.Columns.Count, context + ": export drops hidden headers");
            for (int column = 0; column < grid.Columns.Count; column++)
            {
                Assert(exported.Headers[column] == grid.Columns[column].HeaderText, context + ": column order no longer matches export");
                for (int row = 0; row < grid.Rows.Count; row++)
                {
                    string expected = Convert.ToString(grid.Rows[row].Cells[column].Value);
                    if (grid.Columns[column].HeaderText == "Проверка сверки")
                    {
                        Record record = Field<Store>(form, "store").Find(Convert.ToString(grid.Rows[row].Tag));
                        expected = record == null ? "" : record.Get("reviewStatus");
                    }
                    Assert(exported.Rows[row][column] == expected, context + ": export changed full/hidden value at " + row + ":" + column);
                }
            }
        }
        private static void FinancialColumns(MainForm form, string section, string context)
        {
            DataGridView grid = Field<DataGridView>(form, "grid");
            if (Catalog.IsActSection(section) || section == "invoice" || section == "contract")
                Important(grid, context, "Номер", "Дата", "Контрагент", "Сумма", "Валюта");
            else if (Catalog.IsPaymentSection(section))
                Important(grid, context, "Номер документа", "Дата", "Контрагент", "Сумма", "Валюта");
            else if (section == "document") Important(grid, context, "Название", "Дата", "Контрагент");
        }
        private static void MainLists(Fixture f, string root)
        {
            Store store = f.Store; byte[] original = File.ReadAllBytes(store.DataPath); long revision = store.Revision;
            bool verticalObserved = false, hiddenColumnsObserved = false;
            var exports = new Dictionary<string, string>();
            using (var form = new MainForm(store, false))
            {
                ShowHidden(form, new Size(1120, 680));
                form.ApplyInterfacePreferences(new UiPreferences { AnimationsEnabled = false }); Application.DoEvents();
                foreach (Size size in new[] { new Size(1120, 680), new Size(1440, 900), new Size(1920, 1080) })
                {
                    form.Size = size; form.PerformLayout(); Application.DoEvents();
                    foreach (UiSection section in UiPreferences.Sections.Where(section => section.Key != "updates"))
                    {
                        form.Navigate(section.Key); form.ResetFilters(); Application.DoEvents();
                        string context = section.Key + " at " + size.Width + "x" + size.Height;
                        foreach (DataGridView visible in Descendants(form).OfType<DataGridView>().Where(g => g.Visible && g.Columns.Count > 0))
                        {
                            Geometry(visible, context);
                            verticalObserved |= visible.Controls.OfType<VScrollBar>().Any(bar => bar.Visible);
                        }
                        DataGridView main = Field<DataGridView>(form, "grid");
                        if (!main.Visible) continue;
                        Assert(main.Rows.Count > 0, context + ": seeded list/report unexpectedly empty");
                        FinancialColumns(form, section.Key, context); ExportKeepsCells(form, context);
                        hiddenColumnsObserved |= main.Columns.Cast<DataGridViewColumn>().Any(column => !column.Visible);
                        TableData export = form.BuildExportTable();
                        string serialized = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 32000000 }.Serialize(export);
                        string exportKey = section.Key == "reports" ? "report-" + Field<ComboBox>(form, "report").SelectedIndex : section.Key;
                        if (!exports.ContainsKey(exportKey)) exports[exportKey] = serialized;
                        else Assert(exports[exportKey] == serialized, context + ": resizing changes export data, numbers, dates or amounts");
                    }
                    for (int report = 0; report < 7; report++)
                    {
                        form.SelectReport(report); form.ResetFilters(); Application.DoEvents();
                        string context = "report " + report + " at " + size.Width + "x" + size.Height;
                        var grid = Field<DataGridView>(form, "grid"); Geometry(grid, context); ExportKeepsCells(form, context);
                        string exportKey = "report-" + report;
                        string serialized = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 32000000 }.Serialize(form.BuildExportTable());
                        if (!exports.ContainsKey(exportKey)) exports[exportKey] = serialized;
                        else Assert(exports[exportKey] == serialized, context + ": resizing changed this report's export data");
                        if (report == 0) Important(grid, context, "Акт", "Дата", "Контрагент", "К оплате");
                        if (report == 1) Important(grid, context, "Объект", "Выручка по актам", "Всего затрат", "Прибыль");
                        if (report == 2) Important(grid, context, "Дата", "Документ", "Контрагент", "Сальдо", "Изменение сальдо");
                        if (report == 4) Important(grid, context, "Дата", "Платёж", "Контрагент", "Сумма");
                    }
                }
                Assert(verticalObserved, "fixtures did not exercise an actual visible vertical scrollbar");
                Assert(hiddenColumnsObserved, "narrow lists were not exercised with hidden secondary data");
                MainReviewStatesAndFilters(form, f);
                SortAndRoundTrip(form, f, root);
                form.Close();
            }
            Assert(store.Revision == revision && original.SequenceEqual(File.ReadAllBytes(store.DataPath)), "navigation, layout, export or filter checks changed accounting records");
        }
        private static void MainReviewStatesAndFilters(MainForm form, Fixture f)
        {
            form.Navigate("act"); form.ResetFilters(); Application.DoEvents();
            var grid = Field<DataGridView>(form, "grid");
            foreach (Record record in new[] { f.PendingAct, f.MatchedAct, f.CheckedAct, f.LegacyAct })
                Assert(Ids(grid).Contains(record.Id), "main act list drops " + record.Get("reviewStatus") + " draft");
            Choose(form, "status", "draft");
            IdsEqual(grid, new[] { f.PendingAct, f.MatchedAct, f.CheckedAct, f.LegacyAct }, "explicit draft filter lost a review state");
            Field<CheckBox>(form, "archived").Checked = true; Application.DoEvents();
            Assert(Ids(grid).Contains(f.ArchivedAct.Id), "explicit archive toggle did not add archived pending draft");
            form.ResetFilters(); Field<ComboBox>(form, "currency").SelectedItem = "USD"; Application.DoEvents();
            IdsEqual(grid, new[] { f.DollarAct }, "explicit currency filter ignored while keeping unverified acts");
            form.ResetFilters();
            DateTimePicker from = Field<DateTimePicker>(form, "from"), to = Field<DateTimePicker>(form, "to");
            from.Value = new DateTime(2026, 1, 1); to.Value = new DateTime(2026, 1, 31); from.Checked = to.Checked = true; form.RefreshView(); Application.DoEvents();
            IdsEqual(grid, new[] { f.MatchedAct }, "explicit dates did not select the matched unverified act");
            form.Navigate("payment"); form.ResetFilters(); Application.DoEvents();
            foreach (Record record in new[] { f.PendingPayment, f.MatchedPayment, f.CheckedPayment, f.LegacyPayment })
                Assert(Ids(grid).Contains(record.Id), "main payments list drops " + record.Get("reviewStatus") + " payment");
            Choose(form, "party", f.Other.Id); Field<ComboBox>(form, "currency").SelectedItem = "USD"; Application.DoEvents();
            IdsEqual(grid, new[] { f.DollarPayment }, "combined explicit counterparty/currency filters not respected");
            form.ResetFilters(); Choose(form, "party", f.Customer.Id); Field<ComboBox>(form, "direction").SelectedIndex = 2; Application.DoEvents();
            Assert(Ids(grid).Count == 0, "explicit outgoing direction was bypassed by unverified-state visibility");
        }
        private static void SortAndRoundTrip(MainForm form, Fixture f, string root)
        {
            form.Navigate("payment"); form.ResetFilters(); Application.DoEvents();
            var grid = Field<DataGridView>(form, "grid");
            DataGridViewColumn money = grid.Columns.Cast<DataGridViewColumn>().Single(column => column.HeaderText == "Сумма");
            grid.Sort(money, ListSortDirection.Ascending); Application.DoEvents();
            var values = Ids(grid).Select(id => Store.ParseMoney(f.Store.Find(id).Get("amount"))).ToList();
            Assert(values.SequenceEqual(values.OrderBy(v => v)), "money sorting is lexical rather than numeric");
            Assert(values.First() == 9.50m && values.Contains(100.01m) && values.Last() == 12880.25m, "numeric sorting did not exercise the required monetary range");
            foreach (Size size in new[] { new Size(1120, 680), new Size(1920, 1080) })
            {
                form.Size = size; form.RefreshView(); Application.DoEvents();
                Assert(Ids(grid).Select(id => Store.ParseMoney(f.Store.Find(id).Get("amount"))).SequenceEqual(values), "refresh/resize lost active numeric sorting");
                ExportKeepsCells(form, "sorted payment export");
            }
            TableData exported = form.BuildExportTable(); int reviewColumn = exported.Headers.IndexOf("Проверка сверки");
            Assert(reviewColumn >= 0, "review status missing from export");
            var ids = Ids(grid);
            for (int row = 0; row < ids.Count; row++)
                Assert(exported.Rows[row][reviewColumn] == f.Store.Find(ids[row]).Get("reviewStatus"), "export uses review display alias instead of raw enum/blank");
            foreach (string extension in new[] { ".xlsx", ".csv" })
            {
                string path = Path.Combine(root, "resized-payments" + extension);
                if (extension == ".xlsx") TableIO.WriteXlsx(path, exported.Headers, exported.Rows);
                else TableIO.WriteCsv(path, exported.Headers, exported.Rows);
                var read = TableIO.Read(path);
                Assert(read.Count == ids.Count, "roundtrip dropped exported rows " + extension);
                using (var import = new ImportDialog(f.Store, Catalog.Get("payment"), read, "BYN"))
                {
                    List<Record> prepared = import.PrepareImport();
                    Assert(prepared.Count == ids.Count, "PrepareImport dropped exported review states");
                    for (int row = 0; row < ids.Count; row++)
                    {
                        Record source = f.Store.Find(ids[row]), actual = prepared[row];
                        foreach (string field in new[] { "number", "date", "currency", "partyId", "projectId", "direction", "reviewStatus", "notes", "reviewNotes" })
                            Assert(actual.Get(field) == source.Get(field), "export/import roundtrip changed " + field + " " + extension);
                        Assert(Store.ParseMoney(actual.Get("amount")) == Store.ParseMoney(source.Get("amount")), "export/import roundtrip changed numeric amount");
                    }
                }
            }
            var date = grid.Columns.Cast<DataGridViewColumn>().Single(column => column.HeaderText == "Дата");
            grid.Sort(date, ListSortDirection.Ascending); Application.DoEvents();
            var dates = Ids(grid).Select(id => f.Store.Find(id).Get("date")).ToList();
            Assert(dates.SequenceEqual(dates.OrderBy(day => day, StringComparer.Ordinal)) && dates.First() == "2025-12-31", "date sorting did not preserve chronological order across years");
            form.Size = new Size(1120, 680); form.RefreshView(); Application.DoEvents();
            Assert(Ids(grid).Select(id => f.Store.Find(id).Get("date")).SequenceEqual(dates), "date order changed after narrow resize/refresh");
        }
        private static void ReviewWorkflow(Fixture f)
        {
            byte[] before = File.ReadAllBytes(f.Store.DataPath);
            using (var dialog = new ReconciliationReviewDialog(f.Store, null))
            {
                ShowHidden(dialog, new Size(960, 600));
                var grid = Field<DataGridView>(dialog, "grid"); var filter = Field<ComboBox>(dialog, "filter");
                Assert(filter.SelectedIndex == 1, "review dialog does not start with unverified records");
                IdsEqual(grid, f.Unverified, "unverified view excludes matched/legacy/external/outgoing/archived records or includes unrelated documents");
                Assert(!Ids(grid).Contains(f.CheckedAct.Id) && !Ids(grid).Contains(f.OrdinaryDocument.Id), "manual checked or unannotated document appears as unverified");
                foreach (Size size in new[] { new Size(960, 600), new Size(1440, 900), new Size(1920, 1080) })
                {
                    dialog.Size = size; dialog.PerformLayout(); Application.DoEvents();
                    Geometry(grid, "review unverified " + size);
                    Important(grid, "review important columns", "Номер", "Дата", "Контрагент", "Сумма", "Валюта");
                    filter.SelectedIndex = 0; Application.DoEvents(); Geometry(grid, "review all " + size);
                    Assert(grid.Controls.OfType<VScrollBar>().Any(bar => bar.Visible), "review fixture did not exercise vertical scrolling");
                    filter.SelectedIndex = 2; Application.DoEvents(); IdsEqual(grid, f.Matched, "explicit matched-only review filter is ignored");
                    filter.SelectedIndex = 3; Application.DoEvents();
                    Assert(Ids(grid).Contains(f.CheckedAct.Id) && !Ids(grid).Contains(f.MatchedAct.Id), "explicit checked filter is ignored");
                    filter.SelectedIndex = 1; Application.DoEvents();
                }
                Assert(before.SequenceEqual(File.ReadAllBytes(f.Store.DataPath)), "reading or resizing review list changed database");
                Record original = f.Store.Find(f.MatchedAct.Id);
                DataGridViewRow selected = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Tag) == original.Id);
                grid.ClearSelection(); selected.Selected = true; grid.CurrentCell = selected.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                Button mark = Descendants(dialog).OfType<Button>().Single(button => button.Text == "Отметить проверено");
                Assert(mark.Visible && mark.Enabled, "checking a displayed record is inaccessible"); mark.PerformClick(); Application.DoEvents();
                Record checkedRecord = f.Store.Find(original.Id);
                Assert(checkedRecord.Get("reviewStatus") == "checked" && !Ids(grid).Contains(original.Id), "manual check did not leave unverified view");
                foreach (var field in original.Fields.Where(field => field.Key != "reviewStatus"))
                    Assert(checkedRecord.Get(field.Key) == field.Value, "manual review changed protected/source field " + field.Key);
                Assert(f.Store.Of("allocation").Count() == 0 && checkedRecord.Get("status") == "draft" && checkedRecord.Get("signatureStatus") == "unknown",
                    "manual check signed the act or allocated a payment");
                filter.SelectedIndex = 3; Application.DoEvents();
                Assert(Ids(grid).Contains(original.Id), "checked record cannot be found later in the checked list");
                dialog.Close();
            }
        }
        public static void Run(string tempdir)
        {
            string root = Path.Combine(tempdir, "list-layout-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Fixture f = Seed(Path.Combine(root, "data"));
            MainLists(f, root); ReviewWorkflow(f);
            File.WriteAllText(Path.Combine(tempdir, "list-layout-fixture-path.txt"), f.Store.DataDir, Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "PASS.txt"), "PASS: rendered column bounds, actual horizontal/vertical scrollbars, all grid sections and reports at three sizes, full exports with raw review status, CSV/XLSX re-import, numeric/date sorting, all imported/legacy review states and manual checking.", Encoding.UTF8);
            Console.WriteLine("PASS: responsive list geometry, preserved exports, sorting, imported review states and later manual checking");
            Console.WriteLine("List layout fixture: " + f.Store.DataDir);
        }
    }
}
