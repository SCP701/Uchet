using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class FilterTests
    {
        private sealed class Fixture
        {
            public Store Store;
            public Record Customer, Other, Vendor, ArchivedParty, Project, OtherProject;
            public Record Due, Overdue, Paid, Held, Draft, Dollar, OtherAct, NoProject, ArchivedAct;
            public Record Advance, Allocated, DollarPayment, VendorPayment, ArchivedPartyPayment;
            public Record Expense, AnonymousExpense, Estimate, OldEstimate, Work, OldWork;
            public Record Contract, Amendment, Invoice, Hold, Document;
        }
        private static void Assert(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Filter test: " + reason);
        }
        private static T Field<T>(MainForm form, string name)
        {
            FieldInfo field = typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing integration field " + name);
            return (T)field.GetValue(form);
        }
        private static Record Add(Store store, string kind, params string[] fields)
        {
            Record record = new Record(kind);
            for (int index = 0; index < fields.Length; index += 2) record.Set(fields[index], fields[index + 1]);
            store.Upsert(record); return record;
        }
        private static string Date(DateTime value) { return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        private static Record Act(Store store, Record party, Record project, string number, DateTime date, string currency, string direction, string status, string amount)
        {
            return Add(store, "act", "number", number, "date", Date(date), "partyId", party.Id,
                "projectId", project == null ? "" : project.Id, "currency", currency, "direction", direction,
                "status", status, "amount", amount);
        }
        private static Record Payment(Store store, Record party, Record project, string number, DateTime date, string currency, string direction, string amount)
        {
            return Add(store, "payment", "number", number, "date", Date(date), "partyId", party.Id,
                "projectId", project == null ? "" : project.Id, "currency", currency, "direction", direction,
                "method", "bank", "amount", amount);
        }
        private static Fixture BuildFixture(string path)
        {
            Fixture f = new Fixture(); f.Store = new Store(path); Store s = f.Store; DateTime today = DateTime.Today;
            Add(s, "settings", "name", "Проверка фильтров", "currency", "BYN");
            f.Customer = Add(s, "party", "name", "Ёлка Север", "role", "client", "contact", "Мария Секретная", "notes", "Скрытый якорь");
            f.Other = Add(s, "party", "name", "Другой берег", "role", "both");
            f.Vendor = Add(s, "party", "name", "Подрядчик Монтаж", "role", "vendor");
            f.ArchivedParty = Add(s, "party", "name", "Архивный заказчик", "role", "client");
            f.Project = Add(s, "project", "name", "Северная площадка", "partyId", f.Customer.Id, "status", "active", "notes", "Проектный якорь");
            f.OtherProject = Add(s, "project", "name", "Южная площадка", "partyId", f.Other.Id, "status", "complete");
            f.Due = Act(s, f.Customer, f.Project, "F-DUE", today, "BYN", "income", "signed", "100");
            Record edit = s.Find(f.Due.Id); edit.Set("dueDate", Date(today.AddDays(2))); edit.Set("notes", "Уникальная скрытая заметка"); s.Upsert(edit);
            f.Overdue = Act(s, f.Customer, f.Project, "F-OVERDUE", today.AddDays(-1), "BYN", "income", "signed", "80");
            edit = s.Find(f.Overdue.Id); edit.Set("dueDate", Date(today.AddDays(-1))); s.Upsert(edit);
            f.Paid = Act(s, f.Customer, f.Project, "F-PAID", today, "BYN", "income", "signed", "50");
            f.Held = Act(s, f.Customer, f.Project, "F-HELD", today, "BYN", "income", "signed", "30");
            f.Hold = Add(s, "hold", "actId", f.Held.Id, "date", Date(today), "amount", "30", "status", "active", "type", "warranty");
            f.Draft = Act(s, f.Customer, f.Project, "F-DRAFT", today.AddDays(1), "BYN", "income", "draft", "60");
            f.Dollar = Act(s, f.Customer, f.Project, "F-USD", today, "USD", "income", "signed", "70");
            f.OtherAct = Act(s, f.Other, f.OtherProject, "F-OTHER", today, "BYN", "income", "signed", "90");
            f.NoProject = Act(s, f.Customer, null, "F-NONE", today, "BYN", "income", "signed", "20");
            f.ArchivedAct = Act(s, f.Customer, f.Project, "F-ARCHIVE", today, "BYN", "income", "signed", "40"); s.Archive(f.ArchivedAct.Id);
            f.Advance = Payment(s, f.Customer, f.Project, "F-ADVANCE", today, "BYN", "income", "25");
            f.Allocated = Payment(s, f.Customer, f.Project, "F-ALLOCATED", today, "BYN", "income", "50"); s.Allocate(f.Allocated.Id, f.Paid.Id, 50m);
            f.DollarPayment = Payment(s, f.Customer, f.Project, "F-USD-PAY", today, "USD", "income", "15");
            f.VendorPayment = Payment(s, f.Vendor, f.Project, "F-VENDOR", today, "BYN", "expense", "12");
            f.ArchivedPartyPayment = Payment(s, f.ArchivedParty, null, "F-ARCHIVED-PARTY", today, "BYN", "income", "7");
            edit = s.Find(f.ArchivedParty.Id); edit.Set("archived", "true"); s.Upsert(edit);
            f.Expense = Add(s, "expense", "date", Date(today), "partyId", f.Vendor.Id, "projectId", f.Project.Id, "amount", "11", "currency", "BYN", "category", "materials");
            f.AnonymousExpense = Add(s, "expense", "date", Date(today), "projectId", f.Project.Id, "amount", "9", "currency", "BYN", "category", "transport");
            f.Estimate = Add(s, "estimate", "number", "F-ESTIMATE", "date", Date(today), "partyId", f.Customer.Id, "projectId", f.Project.Id, "currency", "BYN", "direction", "income", "status", "approved");
            f.OldEstimate = Add(s, "estimate", "number", "F-ESTIMATE-OLD", "date", Date(today.AddDays(-10)), "partyId", f.Customer.Id, "projectId", f.Project.Id, "currency", "BYN", "direction", "income", "status", "draft");
            f.Work = Add(s, "work", "estimateId", f.Estimate.Id, "description", "Монтаж оборудования", "unit", "шт", "qty", "2", "price", "15");
            f.OldWork = Add(s, "work", "estimateId", f.OldEstimate.Id, "description", "Предыдущая работа", "unit", "шт", "qty", "1", "price", "10");
            f.Contract = Add(s, "contract", "number", "F-CONTRACT", "date", Date(today), "partyId", f.Customer.Id, "projectId", f.Project.Id, "currency", "USD", "direction", "income", "amount", "200", "status", "active");
            f.Amendment = Add(s, "amendment", "number", "F-AMENDMENT", "date", Date(today), "contractId", f.Contract.Id, "amount", "10", "status", "signed");
            f.Invoice = Add(s, "invoice", "number", "F-INVOICE", "date", Date(today), "partyId", f.Customer.Id, "actId", f.Dollar.Id, "amount", "70", "currency", "USD", "direction", "income", "status", "sent");
            f.Document = Add(s, "document", "title", "Паспорт оборудования", "date", Date(today), "actId", f.Dollar.Id, "status", "missing", "notes", "Документный якорь");
            return f;
        }
        private static void ShowHidden(MainForm form)
        {
            form.ShowInTaskbar = false; form.Opacity = 0; form.Show();
            form.ApplyInterfacePreferences(new UiPreferences { AnimationsEnabled = false }); Application.DoEvents();
        }
        private static void Navigate(MainForm form, string section) { form.Navigate(section); Application.DoEvents(); }
        private static string ChoiceId(object item)
        {
            Assert(item != null, "a filter has no selected choice");
            FieldInfo id = item.GetType().GetField("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert(id != null, "filter choices do not expose identity"); return Convert.ToString(id.GetValue(item));
        }
        private static void Choose(MainForm form, string field, string id)
        {
            ComboBox combo = Field<ComboBox>(form, field);
            for (int i = 0; i < combo.Items.Count; i++)
                if (ChoiceId(combo.Items[i]) == id) { combo.SelectedIndex = i; Application.DoEvents(); return; }
            throw new InvalidOperationException("Filter test: missing " + field + " choice " + id);
        }
        private static void Currency(MainForm form, string value)
        {
            ComboBox combo = Field<ComboBox>(form, "currency"); combo.SelectedItem = value;
            Application.DoEvents(); Assert(Convert.ToString(combo.SelectedItem) == value, "currency choice was silently replaced in " + Field<string>(form, "current"));
        }
        private static void Query(MainForm form, string text) { Field<TextBox>(form, "search").Text = text; Application.DoEvents(); }
        private static void Reset(MainForm form)
        {
            Button button = Field<Button>(form, "resetFiltersButton"); Assert(button.Visible && button.Enabled, "reset button is inaccessible");
            button.PerformClick(); Application.DoEvents();
        }
        private static List<string> VisibleIds(MainForm form)
        {
            return Field<DataGridView>(form, "grid").Rows.Cast<DataGridViewRow>().Select(row => Convert.ToString(row.Tag)).Where(id => id != "").ToList();
        }
        private static void Ids(MainForm form, string reason, params Record[] expected)
        {
            List<string> actual = VisibleIds(form); List<string> wanted = expected.Select(r => r.Id).ToList();
            Assert(actual.Count == wanted.Count && !actual.Except(wanted).Any() && !wanted.Except(actual).Any(),
                reason + " (expected " + wanted.Count + " rows, rendered " + actual.Count + ")");
        }
        private static void Range(MainForm form, DateTime? start, DateTime? end)
        {
            DateTimePicker from = Field<DateTimePicker>(form, "from"), to = Field<DateTimePicker>(form, "to");
            from.Checked = to.Checked = false;
            if (start.HasValue) from.Value = start.Value; if (end.HasValue) to.Value = end.Value;
            from.Checked = start.HasValue; to.Checked = end.HasValue; form.RefreshView(); Application.DoEvents();
        }
        private static bool Available(MainForm form, string name) { Control control = Field<Control>(form, name); return control.Visible && control.Enabled; }
        private static void TestCombinedAndFinancialStatus(MainForm form, Fixture f)
        {
            Navigate(form, "act"); Reset(form); Currency(form, "Все валюты");
            Choose(form, "party", f.Customer.Id); Choose(form, "project", f.Project.Id); Choose(form, "status", "signed");
            Ids(form, "combined counterparty, object and signature filters", f.Due, f.Overdue, f.Paid, f.Held, f.Dollar);
            Currency(form, "USD"); Ids(form, "currency combines with object and signature", f.Dollar);
            Currency(form, "BYN"); Choose(form, "status", "due"); Ids(form, "payable balance excludes paid, held and draft acts", f.Due, f.Overdue);
            Choose(form, "status", "overdue"); Ids(form, "overdue checks due date and positive payable balance", f.Overdue);
            Choose(form, "status", "paid"); Ids(form, "paid status follows actual allocation", f.Paid);
            Choose(form, "status", "held"); Ids(form, "held status follows active holds", f.Held);
            Choose(form, "status", "draft"); Ids(form, "draft signature status", f.Draft);
            Reset(form); Choose(form, "project", "__none__"); Ids(form, "documents without objects", f.NoProject);
            Reset(form); Choose(form, "project", f.Project.Id); Choose(form, "status", "signed");
            Field<CheckBox>(form, "archived").Checked = true; Application.DoEvents();
            Ids(form, "archive toggle adds archived matching records", f.Due, f.Overdue, f.Paid, f.Held, f.ArchivedAct);
            Navigate(form, "payment"); Reset(form); Choose(form, "party", f.Customer.Id); Choose(form, "project", f.Project.Id);
            Choose(form, "status", "advance"); Ids(form, "advance filter uses unallocated amount", f.Advance);
            Choose(form, "status", "allocated"); Ids(form, "allocated payment filter", f.Allocated);
            Reset(form); Choose(form, "party", f.ArchivedParty.Id); Ids(form, "archived counterparties remain selectable for historical payments", f.ArchivedPartyPayment);
            ComboBox parties = Field<ComboBox>(form, "party");
            Assert(parties.Items.Cast<object>().Single(item => ChoiceId(item) == f.ArchivedParty.Id).ToString().IndexOf("архив", StringComparison.OrdinalIgnoreCase) >= 0,
                "archived counterparty is not marked in filter choices");
        }
        private static void TestSearch(MainForm form, Fixture f)
        {
            Navigate(form, "act"); Reset(form);
            Query(form, "  УНИКАЛЬНАЯ\tскрытая\nЗАМЕТКА  "); Ids(form, "token search reads hidden notes and all whitespace", f.Due);
            Query(form, "ЕЛКА   f-due"); Ids(form, "token search combines a related counterparty and document number, normalizing е/ё", f.Due);
            Query(form, "уникальная отсутствующее"); Ids(form, "every search token must match");
            Navigate(form, "party"); Reset(form); Query(form, "МАРИЯ Секретная"); Ids(form, "search reads hidden counterparty contact", f.Customer);
            Query(form, "елка якорь"); Ids(form, "search combines name and hidden notes", f.Customer);
            Navigate(form, "act"); Reset(form); Query(form, "оплачен F-PAID"); Ids(form, "search includes derived payment state", f.Paid);
            Navigate(form, "document"); Reset(form); Currency(form, "Все валюты"); Query(form, "Документный якорь елка");
            Ids(form, "search follows linked act to its counterparty", f.Document);
        }
        private static void TestDateLimits(MainForm form, Fixture f)
        {
            Navigate(form, "act"); Reset(form); Choose(form, "party", f.Customer.Id); Choose(form, "project", f.Project.Id);
            DateTime today = DateTime.Today;
            Range(form, today.AddHours(18), today.AddHours(2)); Ids(form, "both boundaries include the entire same calendar day", f.Due, f.Paid, f.Held);
            Range(form, null, today.AddDays(-1)); Ids(form, "end-only date range", f.Overdue);
            Range(form, today.AddDays(1), null); Ids(form, "start-only date range", f.Draft);
            Range(form, today.AddDays(2), today.AddDays(-2)); Ids(form, "invalid range clears stale rows");
            Assert(Field<Label>(form, "count").Text.IndexOf("дата", StringComparison.OrdinalIgnoreCase) >= 0, "invalid range does not explain how to correct it");
            Range(form, today, today); Ids(form, "correcting an invalid range restores matching rows", f.Due, f.Paid, f.Held);
            Navigate(form, "work"); Reset(form); Range(form, today, today); Ids(form, "work inherits the estimate date", f.Work);
            Range(form, null, today.AddDays(-1)); Ids(form, "inherited work dates support end-only range", f.OldWork);
        }
        private static void TestPresets(MainForm form)
        {
            Navigate(form, "act"); Reset(form); DateTime today = DateTime.Today;
            ComboBox period = Field<ComboBox>(form, "period");
            DateTimePicker from = Field<DateTimePicker>(form, "from"), to = Field<DateTimePicker>(form, "to");
            DateTime month = new DateTime(today.Year, today.Month, 1);
            int[] choices = { 1, 2, 3, 4 };
            DateTime[] starts = { today, month, month.AddMonths(-1), new DateTime(today.Year, 1, 1) };
            DateTime[] ends = { today, month.AddMonths(1).AddDays(-1), month.AddDays(-1), new DateTime(today.Year, 12, 31) };
            for (int index = 0; index < choices.Length; index++)
            {
                period.SelectedIndex = choices[index]; Application.DoEvents();
                Assert(from.Checked && to.Checked && from.Value.Date == starts[index] && to.Value.Date == ends[index], "wrong calendar period preset " + choices[index]);
            }
            Range(form, today.AddDays(-3), today); Assert(period.SelectedIndex == 5, "manual boundary changes leave an inaccurate preset label");
            Reset(form); Assert(period.SelectedIndex == 0 && !from.Checked && !to.Checked, "reset did not clear preset and both boundaries");
        }
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private static void ToggleBoundary(DateTimePicker picker)
        {
            IntPtr point = new IntPtr(8 | ((picker.Height / 2) << 16));
            SendMessage(picker.Handle, 0x201, new IntPtr(1), point);
            SendMessage(picker.Handle, 0x202, IntPtr.Zero, point);
            Application.DoEvents();
        }
        private static void TestDateInteraction(MainForm form, Fixture f)
        {
            Navigate(form, "act"); Reset(form);
            ComboBox period = Field<ComboBox>(form, "period");
            DateTimePicker from = Field<DateTimePicker>(form, "from"), to = Field<DateTimePicker>(form, "to");
            Assert(from.CustomFormat == "'Без ограничения'" && to.CustomFormat == "'Без ограничения'"
                && Field<Label>(form, "count").Text.Contains("За всё время"), "unchecked date controls display an apparently active date");
            from.Value = DateTime.Today.AddDays(1); Application.DoEvents();
            Assert(from.Checked && !to.Checked && period.SelectedIndex == 5 && from.CustomFormat == "dd.MM.yyyy",
                "selecting a date does not activate just its boundary or show the manual period");
            Ids(form, "editing the lower date immediately filters the list", f.Draft);
            Assert(Field<Label>(form, "count").Text.Contains("С " + from.Value.ToString("dd.MM.yyyy")), "start-only period is not shown below the grid");
            Choose(form, "party", f.Customer.Id);
            period.SelectedIndex = 0; Application.DoEvents();
            Assert(!from.Checked && !to.Checked && ChoiceId(Field<ComboBox>(form, "party").SelectedItem) == f.Customer.Id,
                "all-time preset clears other filters or leaves a date active");
            Ids(form, "all-time preset restores records within the selected counterparty", f.Due, f.Overdue, f.Paid, f.Held, f.Draft, f.NoProject);
            to.Value = DateTime.Today.AddDays(-1); Application.DoEvents();
            Assert(!from.Checked && to.Checked && period.SelectedIndex == 5, "editing the upper date accidentally enables the lower boundary");
            Ids(form, "editing the upper date immediately filters the list", f.Overdue);
            ToggleBoundary(to);
            Assert(!to.Checked && period.SelectedIndex == 0 && to.CustomFormat == "'Без ограничения'", "native checkbox removal does not update the date filter state (checked=" + to.Checked + ", preset=" + period.SelectedIndex + ", format=" + to.CustomFormat + ")");
            Ids(form, "unchecking the final boundary immediately restores the list", f.Due, f.Overdue, f.Paid, f.Held, f.Draft, f.NoProject);
            ToggleBoundary(from);
            Assert(from.Checked && !to.Checked && period.SelectedIndex == 5, "native checkbox enabling does not update the manual period");
            Ids(form, "checking a stored boundary immediately applies its date", f.Draft);
            ToggleBoundary(from);
            from.Focus();
            SendMessage(from.Handle, 0x100, new IntPtr((int)Keys.Space), new IntPtr(1));
            SendMessage(from.Handle, 0x102, new IntPtr((int)Keys.Space), new IntPtr(1));
            SendMessage(from.Handle, 0x101, new IntPtr((int)Keys.Space), new IntPtr(1)); Application.DoEvents();
            Assert(from.Checked && period.SelectedIndex == 5, "keyboard checkbox toggle does not update the boundary state (checked=" + from.Checked + ", preset=" + period.SelectedIndex + ")");
            Ids(form, "keyboard checkbox applies the date immediately", f.Draft);
            Reset(form);
        }
        private static void SortDates(MainForm form, string header, ListSortDirection direction, params Record[] expected)
        {
            DataGridView grid = Field<DataGridView>(form, "grid");
            DataGridViewColumn column = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.HeaderText == header);
            grid.Sort(column, direction); Application.DoEvents();
            Assert(VisibleIds(form).SequenceEqual(expected.Select(r => r.Id)), "incorrect chronological order for " + header + " / " + direction);
            foreach (DataGridViewRow row in grid.Rows)
            {
                Record record = expected.Single(r => r.Id == Convert.ToString(row.Tag));
                if (header == "Дата") Assert(Convert.ToString(row.Cells[column.Index].Value) == DateTime.ParseExact(record.Get("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy"),
                    "date sorting detached row values from their record identity");
            }
        }
        private static void TestDateSorting(string path)
        {
            Store store = new Store(path); Add(store, "settings", "currency", "BYN");
            Record party = Add(store, "party", "name", "Проверка разных лет", "role", "client");
            Record first = Act(store, party, null, "DATE-A", new DateTime(2025, 7, 1), "BYN", "income", "signed", "100");
            Record second = Act(store, party, null, "DATE-B", new DateTime(2025, 12, 31), "BYN", "income", "signed", "200");
            Record third = Act(store, party, null, "DATE-C", new DateTime(2026, 1, 1), "BYN", "income", "signed", "300");
            Record fourth = Act(store, party, null, "DATE-D", new DateTime(2026, 4, 1), "BYN", "income", "signed", "400");
            Record fifth = Act(store, party, null, "DATE-E", new DateTime(2026, 7, 14), "BYN", "income", "signed", "500");
            Record[] acts = { first, second, third, fourth, fifth };
            string[] deadlines = { "2026-01-01", "2025-12-31", "2026-04-01", "2026-07-14", "2025-07-01" };
            for (int i = 0; i < acts.Length; i++) { Record edit = store.Find(acts[i].Id); edit.Set("dueDate", deadlines[i]); store.Upsert(edit); }
            byte[] bytes = File.ReadAllBytes(store.DataPath); long revision = store.Revision;
            using (MainForm form = new MainForm(store))
            {
                ShowHidden(form); Navigate(form, "act"); Reset(form);
                SortDates(form, "Дата", ListSortDirection.Ascending, acts);
                Range(form, new DateTime(2025, 12, 31), new DateTime(2026, 4, 1));
                Assert(VisibleIds(form).SequenceEqual(new[] { second.Id, third.Id, fourth.Id }), "filtering drops the chosen date ordering or excludes range edges");
                SortDates(form, "Дата", ListSortDirection.Descending, fourth, third, second);
                Field<ComboBox>(form, "period").SelectedIndex = 0; Application.DoEvents();
                Assert(VisibleIds(form).SequenceEqual(acts.Reverse().Select(r => r.Id)), "clearing dates drops descending date ordering");
                SortDates(form, "Срок оплаты", ListSortDirection.Ascending, fifth, second, first, third, fourth);
                SortDates(form, "Срок оплаты", ListSortDirection.Descending, fourth, third, first, second, fifth);
                form.SelectReport(0); Reset(form);
                SortDates(form, "Дата", ListSortDirection.Ascending, acts);
                SortDates(form, "Срок", ListSortDirection.Ascending, fifth, second, first, third, fourth);
                Assert(store.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), "date filtering or sorting changed the saved ledger");
                form.Close();
            }
        }
        private static void TestInheritedFiltersAndExpense(MainForm form, Fixture f)
        {
            foreach (string section in new[] { "contract", "amendment", "invoice", "document", "payment" })
            {
                Navigate(form, section); Reset(form); Currency(form, "Все валюты");
                Record expected = section == "contract" ? f.Contract : section == "amendment" ? f.Amendment : section == "invoice" ? f.Invoice : section == "document" ? f.Document : f.DollarPayment;
                Choose(form, "project", f.Project.Id); Choose(form, "party", f.Customer.Id); Currency(form, "USD");
                Ids(form, "related object/counterparty/currency filtering in " + section, expected);
            }
            Navigate(form, "hold"); Reset(form); Choose(form, "party", f.Customer.Id); Choose(form, "project", f.Project.Id); Choose(form, "status", "active");
            Ids(form, "hold inherits linked act dimensions", f.Hold);
            Navigate(form, "expense"); Reset(form); Field<ComboBox>(form, "direction").SelectedIndex = 1; Application.DoEvents();
            Ids(form, "expense records do not leak into income direction");
            Field<ComboBox>(form, "direction").SelectedIndex = 2; Application.DoEvents(); Ids(form, "expense direction includes costs", f.Expense, f.AnonymousExpense);
            Choose(form, "party", f.Customer.Id); Ids(form, "expense without party does not inherit the project's customer");
            Choose(form, "party", f.Vendor.Id); Ids(form, "expense supplier filter", f.Expense);
        }
        private static void TestInactiveAndReset(MainForm form, Fixture f)
        {
            Navigate(form, "act"); Reset(form); Choose(form, "party", f.Other.Id); Choose(form, "project", f.OtherProject.Id); Choose(form, "status", "draft");
            Currency(form, "USD"); Field<ComboBox>(form, "direction").SelectedIndex = 2; Range(form, DateTime.Today.AddDays(5), DateTime.Today.AddDays(-5));
            Navigate(form, "party");
            Assert(!Available(form, "currency") && !Available(form, "project") && !Available(form, "party") && !Available(form, "direction") && !Available(form, "from") && !Available(form, "to"),
                "counterparty page exposes inapplicable transaction filters");
            Ids(form, "inactive filters cannot hide counterparties", f.Customer, f.Other, f.Vendor);
            Range(form, null, null); Navigate(form, "audit");
            Assert(!Available(form, "party") && !Available(form, "project") && !Available(form, "currency") && !Available(form, "status") && !Available(form, "direction") && Available(form, "from"),
                "audit exposes transaction-only filters or hides date controls");
            Assert(Field<DataGridView>(form, "grid").Rows.Count == f.Store.Audit.Count, "hidden transaction filters hide audit entries");
            Navigate(form, "project");
            Assert(!Available(form, "direction") && !Available(form, "from") && !Available(form, "to"), "project page exposes unsupported direction or date filters");
            Navigate(form, "overview");
            Assert(Available(form, "currency") && !Available(form, "search") && !Available(form, "party") && !Available(form, "project") && !Available(form, "status") && !Available(form, "from"),
                "overview exposes a filter it does not honor");
            Navigate(form, "act"); Field<CheckBox>(form, "archived").Checked = true; Query(form, "missing record"); Reset(form);
            Assert(Field<TextBox>(form, "search").Text == "" && Convert.ToString(Field<ComboBox>(form, "currency").SelectedItem) == "BYN"
                && ChoiceId(Field<ComboBox>(form, "party").SelectedItem) == "" && ChoiceId(Field<ComboBox>(form, "project").SelectedItem) == ""
                && ChoiceId(Field<ComboBox>(form, "status").SelectedItem) == "" && Field<ComboBox>(form, "direction").SelectedIndex == 0
                && !Field<DateTimePicker>(form, "from").Checked && !Field<DateTimePicker>(form, "to").Checked && !Field<CheckBox>(form, "archived").Checked,
                "one-click reset leaves an active constraint");
            Ids(form, "reset restores the default currency and complete live act list", f.Due, f.Overdue, f.Paid, f.Held, f.Draft, f.OtherAct, f.NoProject);
        }
        private static string MoneyText(decimal value) { return value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")); }
        private static TableData Table(MainForm form) { return Field<TableData>(form, "table"); }
        private static decimal Amount(string value) { return Store.ParseMoney(value); }
        private static void TestReports(string path)
        {
            Store s = new Store(path); DateTime today = DateTime.Today;
            Add(s, "settings", "currency", "BYN");
            Record customer = Add(s, "party", "name", "Ёлка Сверка", "role", "client");
            Record other = Add(s, "party", "name", "Другой клиент", "role", "client");
            Record vendor = Add(s, "party", "name", "Поставщик отчётов", "role", "vendor");
            Record project = Add(s, "project", "name", "Контрольный объект", "partyId", customer.Id, "status", "active");
            Record second = Add(s, "project", "name", "Другой объект", "partyId", other.Id, "status", "active");
            Act(s, customer, project, "ОПОРНЫЙ-1", today.AddDays(-10), "BYN", "income", "signed", "100");
            Record recent = Act(s, customer, project, "ОПОРНЫЙ-2", today, "BYN", "income", "signed", "40");
            Record archived = Act(s, customer, project, "АРХИВ-3", today, "BYN", "income", "signed", "60"); s.Archive(archived.Id);
            Act(s, vendor, project, "ПОДРЯДЧИК", today, "BYN", "expense", "signed", "20");
            Act(s, other, second, "ПОСТОРОННИЙ", today.AddDays(-20), "BYN", "income", "signed", "900");
            Record payment = Payment(s, customer, project, "ОПОРНЫЙ-П", today, "BYN", "income", "15");
            Payment(s, other, second, "НЕПОДХОДЯЩИЙ-П", today, "BYN", "income", "700");
            Record outgoing = Payment(s, vendor, project, "ОПОРНЫЙ-РАСХОД", today, "BYN", "expense", "8");
            Add(s, "expense", "date", Date(today), "projectId", project.Id, "partyId", vendor.Id, "currency", "BYN", "amount", "10", "category", "other");
            byte[] bytes = File.ReadAllBytes(s.DataPath); long revision = s.Revision;
            using (MainForm form = new MainForm(s))
            {
                ShowHidden(form); form.SelectReport(4); Reset(form); Query(form, "опорный");
                TableData cash = Table(form);
                Assert(cash.Ids.Count(id => id != "") == 2 && cash.Ids.Contains(payment.Id) && cash.Ids.Contains(outgoing.Id), "cash movement query does not scope source payments");
                Assert(cash.Rows.Last()[1] == "Всего поступило: " + MoneyText(15m)
                    && cash.Rows.Last()[4] == "Всего выплачено: " + MoneyText(8m)
                    && cash.Rows.Last()[5] == "Остаток потока: " + MoneyText(7m), "cash movement totals include unmatched payments");
                Assert(Field<DataGridView>(form, "grid").Rows.Count == cash.Rows.Count, "cash movement search hides its totals row after calculating it");
                form.SelectReport(2); Reset(form); Range(form, today, today); Query(form, "опорный");
                TableData reconciliation = Table(form);
                Assert(reconciliation.Ids.Contains(recent.Id) && reconciliation.Ids.Contains(payment.Id) && reconciliation.Ids.Contains(outgoing.Id) && !reconciliation.Ids.Contains(archived.Id),
                    "reconciliation query does not scope financial documents");
                Assert(Amount(reconciliation.Rows.First()[6]) == 100m && Amount(reconciliation.Rows.Last()[6]) == 133m,
                    "reconciliation opening or final balance contains documents outside the query");
                Assert(Field<DataGridView>(form, "grid").Rows.Count == reconciliation.Rows.Count, "search hides reconciliation balance rows");
                form.SelectReport(1); Reset(form); Choose(form, "party", customer.Id); Choose(form, "project", project.Id); Query(form, "ЕЛКА контрольный");
                TableData profit = Table(form);
                Assert(profit.Ids.Count == 1 && profit.Ids[0] == project.Id, "profit query does not search selected object and related customer labels");
                Assert(Amount(profit.Rows[0][1]) == 200m && Amount(profit.Rows[0][2]) == 20m && Amount(profit.Rows[0][3]) == 10m && Amount(profit.Rows[0][5]) == 170m,
                    "profit filters drop archived revenue or supplier costs belonging to the selected object");
                Assert(Field<DataGridView>(form, "grid").Rows.Count == 1, "related-party profit query hides the computed object row");
                string[] amountQueries = { "20,00", "10,00", "15,00", "8,00" };
                int[] amountColumns = { 2, 3, 6, 7 };
                for (int index = 0; index < amountQueries.Length; index++)
                {
                    Query(form, amountQueries[index]);
                    Ids(form, "profit search includes visible subcontractor, expense and payment amount " + amountQueries[index], project);
                    TableData matching = Table(form);
                    Assert(matching.Rows.Count == 1 && matching.Rows[0][amountColumns[index]] == MoneyText(Amount(amountQueries[index]))
                        && Amount(matching.Rows[0][5]) == 170m,
                        "searching a visible profit column changes object totals for " + amountQueries[index]);
                }
                form.SelectReport(0); Reset(form); Choose(form, "project", project.Id);
                Assert(Table(form).Ids.Contains(archived.Id), "financial debt report drops an archived signed act");
                Assert(s.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(s.DataPath)), "report filters altered ledger contents");
                form.Close();
            }
        }
        private static void TestCreationDefaultsAndComputedSearch(string path)
        {
            Store store = new Store(path); DateTime today = DateTime.Today;
            Add(store, "settings", "currency", "BYN");
            Record customer = Add(store, "party", "name", "Клиент черновиков", "role", "client");
            Record project = Add(store, "project", "name", "Объект черновиков", "partyId", customer.Id, "status", "active");
            Record estimate = Add(store, "estimate", "number", "RUB-ESTIMATE", "date", Date(today), "partyId", customer.Id, "projectId", project.Id,
                "currency", "RUB", "direction", "income", "status", "approved");
            Record act = Act(store, customer, project, "COMPUTED", today, "BYN", "income", "signed", "1200");
            Record payment = Payment(store, customer, project, "COMPUTED-PAYMENT", today, "BYN", "income", "900");
            store.Allocate(payment.Id, act.Id, 900m);
            Add(store, "hold", "actId", act.Id, "date", Date(today), "amount", "100", "status", "active", "type", "warranty");
            byte[] bytes = File.ReadAllBytes(store.DataPath); long revision = store.Revision;
            using (MainForm form = new MainForm(store))
            {
                ShowHidden(form); Navigate(form, "payment"); Reset(form); Currency(form, "Все валюты");
                Choose(form, "party", customer.Id); Choose(form, "project", project.Id); Field<ComboBox>(form, "direction").SelectedIndex = 1;
                Record draft = form.NewRecordForCurrentSection();
                Assert(draft.Get("currency") == "BYN" && draft.Get("partyId") == customer.Id && draft.Get("projectId") == project.Id && draft.Get("direction") == "income",
                    "new payment seeds an invalid all-currency value or loses applicable filter defaults");
                Navigate(form, "work"); Reset(form); Currency(form, "USD");
                Choose(form, "party", customer.Id); Choose(form, "project", project.Id); Field<ComboBox>(form, "direction").SelectedIndex = 2;
                draft = form.NewRecordForCurrentSection();
                Assert(draft.Get("currency") == "" && draft.Get("partyId") == "" && draft.Get("projectId") == "" && draft.Get("direction") == "",
                    "new work seeds hidden financial fields that should come from its estimate");
                draft.Set("estimateId", estimate.Id);
                MethodInfo effectiveMethod = typeof(MainForm).GetMethod("Effective", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(effectiveMethod != null, "missing related-record resolver");
                Record effective = (Record)effectiveMethod.Invoke(form, new object[] { draft });
                Assert(effective.Get("currency") == "RUB" && effective.Get("partyId") == customer.Id && effective.Get("projectId") == project.Id && effective.Get("direction") == "income",
                    "work inherits stale selected currency or direction instead of linked estimate values");
                foreach (string section in new[] { "hold", "amendment" })
                {
                    Navigate(form, section); Reset(form); Currency(form, "USD"); Choose(form, "party", customer.Id); Choose(form, "project", project.Id);
                    draft = form.NewRecordForCurrentSection();
                    Assert(draft.Get("currency") == "" && draft.Get("partyId") == "" && draft.Get("projectId") == "" && draft.Get("direction") == "",
                        "new linked record seeds hidden derived defaults in " + section);
                }
                Navigate(form, "act"); Reset(form); Query(form, "200,00");
                Ids(form, "search includes the displayed payable amount after allocation and hold", act);
                Assert(store.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), "filter-derived draft defaults persisted a record");
                form.Close();
            }
        }
        public static void Run(string path)
        {
            string root = Path.Combine(path, "filters-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Fixture fixture = BuildFixture(Path.Combine(root, "lists")); Store store = fixture.Store;
            byte[] bytes = File.ReadAllBytes(store.DataPath); long revision = store.Revision; int audits = store.Audit.Count;
            using (MainForm form = new MainForm(store))
            {
                ShowHidden(form);
                TestCombinedAndFinancialStatus(form, fixture); TestSearch(form, fixture); TestDateLimits(form, fixture);
                TestPresets(form); TestDateInteraction(form, fixture); TestInheritedFiltersAndExpense(form, fixture); TestInactiveAndReset(form, fixture);
                Assert(store.Revision == revision && store.Audit.Count == audits && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)),
                    "changing filters, navigation or reset mutated the ledger or audit history");
                form.Close();
            }
            TestDateSorting(Path.Combine(root, "date-sorting"));
            TestReports(Path.Combine(root, "reports"));
            TestCreationDefaultsAndComputedSearch(Path.Combine(root, "draft-defaults"));
            File.WriteAllText(Path.Combine(root, "PASS.txt"),
                "PASS: combined counterparty/object/currency/status filters; archive references; AND search including hidden fields and е/ё; inclusive/one-sided/recoverable date ranges; native date checkbox interaction and clear inactive state; all-time/calendar presets; chronological date/deadline sorting retained after filtering; inherited work dates and linked dimensions; expense direction and supplier identity; inactive filters; reset; filtered cash and reconciliation totals; archived financial contributions; ledger isolation.", Encoding.UTF8);
        }
    }
}



