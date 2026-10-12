using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class HourlyActComposerTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Hourly act UI test: " + message);
        }
        private static Record Add(Store store, string kind, params string[] values)
        {
            var record = new Record(kind);
            for (int i = 0; i < values.Length; i += 2) record.Set(values[i], values[i + 1]);
            store.Upsert(record); return record;
        }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control descendant in Descendants(child)) yield return descendant;
            }
        }
        private static T PrivateField<T>(MainForm form, string name)
        {
            return (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        }
        private static void InvalidSave(HourlyActComposer composer, Store store, string reason)
        {
            long revision = store.Revision;
            byte[] before = File.ReadAllBytes(store.DataPath);
            bool rejected = false;
            try { composer.SaveDraft(); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, reason + " was accepted");
            Assert(store.Revision == revision && before.SequenceEqual(File.ReadAllBytes(store.DataPath)), reason + " changed the database");
            Assert(composer.ResultRecord == null, reason + " returned a saved result");
        }
        private static DataGridViewRow Work(HourlyActComposer composer, string description, string hours)
        {
            int index = composer.WorkGrid.Rows.Add(description, hours, null, null);
            return composer.WorkGrid.Rows[index];
        }
        private static void TestMissingRate(string path)
        {
            var store = new Store(path);
            Record party = Add(store, "party", "name", "Заказчик без ставки", "role", "client");
            using (var composer = new HourlyActComposer(store))
            {
                composer.SelectCounterparty(party.Id);
                Work(composer, "Выполненная работа", "1");
                InvalidSave(composer, store, "missing hourly rate");
                Assert(store.NextActNumber == 500 && !store.Of("act").Any(), "failed unconfigured draft consumed a number");
            }
        }
        private static Record TestSaveAndValidation(Store store, Record party)
        {
            long revision = store.Revision;
            using (var composer = new HourlyActComposer(store))
            {
                int events = 0;
                composer.ActSaved += delegate { events++; };
                Assert(!composer.WorkGrid.Columns["description"].ReadOnly && !composer.WorkGrid.Columns["hours"].ReadOnly
                    && composer.WorkGrid.Columns["rate"].ReadOnly && composer.WorkGrid.Columns["amount"].ReadOnly,
                    "work descriptions and hours must be editable while calculated fields stay read-only");
                InvalidSave(composer, store, "missing counterparty");
                composer.SelectCounterparty(party.Id);
                Assert(store.Revision == revision, "selecting counterparty saved an incomplete draft");
                InvalidSave(composer, store, "empty work list");
                DataGridViewRow row = Work(composer, " ", "1");
                InvalidSave(composer, store, "blank work description");
                row.Cells["description"].Value = "Монтаж оборудования";
                row.Cells["hours"].Value = "0";
                InvalidSave(composer, store, "zero hours");
                row.Cells["hours"].Value = "-1";
                InvalidSave(composer, store, "negative hours");
                row.Cells["hours"].Value = "ошибка";
                InvalidSave(composer, store, "nonnumeric hours");
                row.Cells["hours"].Value = "1,5";
                Work(composer, "Проверка и настройка", "0,25");
                Record draft = composer.BuildDraft();
                Assert(draft.Get("partyId") == party.Id && draft.Get("currency") == "BYN" && Store.ParseMoney(draft.Get("amount")) == 61.25m,
                    "counterparty or amount was not formed from configured rate and comma-separated hours");
                Assert(draft.Get("number") == "" && store.Revision == revision && store.NextActNumber == 500 && events == 0,
                    "building or validating a draft consumed a number or fired the saved event");
                Record saved = composer.SaveDraft();
                Assert(saved != null && composer.ResultRecord.Id == saved.Id && saved.Get("number") == "500"
                    && store.NextActNumber == 501 && store.Of("act").Count() == 1 && events == 1,
                    "first saved act did not receive number 500 exactly once");
                List<HourlyActLine> lines = Store.GetHourlyActLines(saved);
                Assert(lines.Count == 2 && lines[0].Hours == 1.5m && lines[1].Hours == 0.25m
                    && lines.All(line => line.Rate == 35m) && lines.Sum(line => line.Amount) == 61.25m,
                    "saved act lost its work lines, decimal hours or configured rate");
                Assert(Store.GetActSnapshot(saved, "organizationSnapshot").Get("name") == "Первый исполнитель"
                    && Store.GetActSnapshot(saved, "partySnapshot").Get("name") == "Первый заказчик",
                    "saved act lacks its automatically populated requisites");
                Record repeated = composer.SaveDraft();
                Assert(repeated.Id == saved.Id && repeated.Get("number") == "500" && store.NextActNumber == 501 && store.Of("act").Count() == 1,
                    "repeated save created a second act or consumed another number");
                return repeated;
            }
        }
        private static Record TestAllActsNavigation(Store store, Record party)
        {
            using (var form = new MainForm(store))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
                form.Navigate("act");
                PrivateField<TextBox>(form, "search").Text = "Этот поиск не найдёт новый акт";
                PrivateField<ComboBox>(form, "direction").SelectedIndex = 2;
                PrivateField<ComboBox>(form, "party").SelectedIndex = 2;
                DateTimePicker from = PrivateField<DateTimePicker>(form, "from");
                from.Value = DateTime.Today.AddYears(1); from.Checked = true;
                PrivateField<ComboBox>(form, "currency").SelectedItem = "EUR";
                form.Navigate("create-act"); Application.DoEvents();
                HourlyActComposer composer = Descendants(form).OfType<HourlyActComposer>().Single();
                composer.SelectCounterparty(party.Id);
                Work(composer, "Работы из вкладки создания", "2");
                Record saved = composer.SaveDraft(); Application.DoEvents();
                Assert(saved.Get("number") == "501" && PrivateField<string>(form, "current") == "act",
                    "successful creation did not open the Acts tab");
                TableData table = PrivateField<TableData>(form, "table");
                DataGridView grid = PrivateField<DataGridView>(form, "grid");
                Assert(table.Ids.Contains(saved.Id) && grid.Rows.Cast<DataGridViewRow>().Any(row => Convert.ToString(row.Tag) == saved.Id),
                    "new act is hidden by stale filters in the Acts tab");
                Assert(grid.SelectedRows.Count == 1 && Convert.ToString(grid.SelectedRows[0].Tag) == saved.Id,
                    "newly saved act is not selected in the Acts tab");
                Assert(PrivateField<FlowLayoutPanel>(form, "actions").Controls.OfType<Button>().Any(button => button.Text == "Акт в Excel"),
                    "Acts tab does not expose individual act export to Excel");
                form.Navigate("create-act"); Application.DoEvents();
                HourlyActComposer fresh = Descendants(form).OfType<HourlyActComposer>().Single();
                Assert(!Object.ReferenceEquals(fresh, composer) && fresh.ResultRecord == null,
                    "opening Create Act after saving reused the previous saved composer");
                fresh.SelectCounterparty(party.Id); Work(fresh, "Новая несохранённая работа", "1");
                Record freshDraft = fresh.BuildDraft();
                Assert(freshDraft.Id != saved.Id && freshDraft.Get("number") == "" && store.NextActNumber == 502 && store.Of("act").Count() == 2,
                    "fresh Create Act draft reused the saved record or consumed a number");
                form.Close();
                return saved;
            }
        }
        private static void TestSavedEdit(Store store, Record saved, Record organization, Record party)
        {
            string organizationSnapshot = saved.Get("organizationSnapshot"), partySnapshot = saved.Get("partySnapshot");
            saved.Set("customField", "preserve-existing-data"); saved.Set("notes", "Примечание сохранённого акта");
            saved.Set("status", "signed"); store.Upsert(saved);
            organization.Set("name", "Новый исполнитель"); organization.Set("hourlyRate", "60.00"); organization.Set("currency", "RUB");
            store.Upsert(organization);
            party.Set("name", "Новое имя заказчика"); party.Set("unp", "999999999"); store.Upsert(party);
            long revision = store.Revision;
            using (var editor = new HourlyActComposer(store, store.Find(saved.Id)))
            {
                var rows = editor.WorkGrid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).ToList();
                Assert(rows.Count == 2 && Convert.ToString(rows[0].Cells["description"].Value) == "Монтаж оборудования",
                    "opening a saved act did not restore its work lines");
                rows[0].Cells["description"].Value = "Монтаж и проверка оборудования";
                rows[0].Cells["hours"].Value = "2,5";
                Assert(store.Revision == revision, "editing a saved act wrote unsaved changes");
                Record edited = editor.SaveDraft();
                Assert(edited.Id == saved.Id && edited.Get("number") == "500" && store.Of("act").Count() == 2 && store.NextActNumber == 502,
                    "editing a saved act changed its number or created a duplicate");
                Assert(Store.GetHourlyActLines(edited).All(line => line.Rate == 35m) && Store.ParseMoney(edited.Get("amount")) == 96.25m
                    && edited.Get("currency") == "BYN", "current settings changed historical rates or currency");
                Assert(edited.Get("partySnapshot") == partySnapshot && edited.Get("organizationSnapshot") == organizationSnapshot,
                    "editing work lines rewrote historical organization or counterparty details");
                Assert(edited.Get("status") == "signed" && edited.Get("customField") == "preserve-existing-data"
                    && edited.Get("notes") == "Примечание сохранённого акта", "editing work lines lost existing act data");
            }
        }
        public static void Run(string tempdir)
        {
            string path = Path.Combine(tempdir, "hourly-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            var store = new Store(Path.Combine(path, "data"));
            Record organization = Add(store, "settings", "name", "Первый исполнитель", "currency", "BYN", "hourlyRate", "35.00", "unp", "123456789");
            Record party = Add(store, "party", "name", "Первый заказчик", "role", "client", "unp", "987654321");
            Record otherParty = Add(store, "party", "name", "Другой заказчик", "role", "client");
            TestMissingRate(Path.Combine(path, "no-rate"));
            Record saved = TestSaveAndValidation(store, party);
            TestAllActsNavigation(store, party);
            TestSavedEdit(store, saved, organization, party);
            long revision = store.Revision;
            using (var canceled = new HourlyActComposer(store))
            {
                canceled.SelectCounterparty(otherParty.Id);
                Work(canceled, "Несохранённая работа", "3");
            }
            Assert(store.Revision == revision && store.Of("act").Count() == 2 && store.NextActNumber == 502,
                "discarding an unfinished draft saved work or consumed a number");
            using (var fresh = new HourlyActComposer(store))
            {
                fresh.SelectCounterparty(otherParty.Id); Work(fresh, "Новая работа по новой ставке", "1,25");
                Record next = fresh.SaveDraft();
                Assert(next.Get("number") == "502" && next.Get("currency") == "RUB" && Store.ParseMoney(next.Get("amount")) == 75m
                    && Store.GetHourlyActLines(next).Single().Rate == 60m, "new drafts do not use updated organization settings");
            }
            var reloaded = new Store(store.DataDir);
            Assert(reloaded.Of("act").Count() == 3 && reloaded.NextActNumber == 503
                && Store.GetHourlyActLines(reloaded.Find(saved.Id)).Count == 2
                && Store.GetActSnapshot(reloaded.Find(saved.Id), "partySnapshot").Get("name") == "Первый заказчик",
                "reloading the app lost acts, work lines, numbering or historical requisites");
            File.WriteAllText(Path.Combine(path, "PASS.txt"), "PASS: hourly act UI, validation rollback, automatic numbering, draft cancellation, all Acts navigation, saved edits and historical requisites.", Encoding.UTF8);
        }
    }
}