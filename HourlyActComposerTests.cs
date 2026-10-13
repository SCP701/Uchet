using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
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
                    && !composer.WorkGrid.Columns["rate"].ReadOnly && composer.WorkGrid.Columns["amount"].ReadOnly,
                    "work descriptions, hours and rates must be editable while amounts stay read-only");
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
                Assert(CompactToolbarTests.CommandLabels(PrivateField<FlowLayoutPanel>(form, "actions")).Contains("Акт в Excel"),
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
        private static TextBox BeginRateEdit(HourlyActComposer composer, DataGridViewRow row)
        {
            composer.WorkGrid.CurrentCell = row.Cells["rate"];
            composer.WorkGrid.Focus();
            Assert(composer.WorkGrid.BeginEdit(true), "the hourly price cell cannot enter edit mode");
            Application.DoEvents();
            TextBox input = composer.WorkGrid.EditingControl as TextBox;
            Assert(input != null, "the hourly price cell has no text editor");
            return input;
        }
        private static void TestManualRates(string path)
        {
            foreach (bool external in new[] { false, true })
            {
                var store = new Store(Path.Combine(path, external ? "external" : "standard"));
                Record party = Add(store, "party", "name", "Ручные ставки", "role", "client");
                string id;
                using (var composer = new HourlyActComposer(store, null, external))
                {
                    composer.SelectCounterparty(party.Id);
                    DataGridViewRow first = Work(composer, "Настройка системы", "1,5");
                    first.Cells["rate"].Value = "35,50";
                    DataGridViewRow second = Work(composer, "Проверка системы", "0.5");
                    second.Cells["rate"].Value = "40.25";
                    Record draft = composer.BuildDraft();
                    Assert(Store.ParseMoney(draft.Get("amount")) == 73.38m && draft.Get("hourlyRate") == "",
                        "manual mixed hourly prices do not calculate and preserve independently without settings");
                    Assert(store.NextActNumber == 500 && !store.Of("act").Any(), "manual preview consumed a number");
                    Record saved = composer.SaveDraft(); id = saved.Id;
                    var lines = Store.GetHourlyActLines(saved);
                    Assert(Store.IsExternalAct(saved) == external && saved.Get("number") == "500"
                        && lines[0].Rate == 35.50m && lines[1].Rate == 40.25m && lines[1].Amount == 20.13m,
                        "saving manual prices lost rates, rounding, numbering or external scope");
                }
                var reload = new Store(store.DataDir);
                Record settings = reload.Of("settings").Single().Clone();
                settings.Set("hourlyRate", "99.00"); reload.Upsert(settings);
                using (var existing = new HourlyActComposer(reload, reload.Find(id)))
                {
                    existing.RefreshDefaults();
                    Record restored = existing.BuildDraft();
                    var lines = Store.GetHourlyActLines(restored);
                    Assert(lines[0].Rate == 35.50m && lines[1].Rate == 40.25m && restored.Get("hourlyRate") == ""
                        && Store.ParseMoney(restored.Get("amount")) == 73.38m, "reopening mixed rates changed historical prices");
                }
            }
        }
        private static void TestRateValidation(string path)
        {
            var store = new Store(path);
            Add(store, "settings", "name", "Проверка цены", "currency", "BYN", "hourlyRate", "35.00");
            Record party = Add(store, "party", "name", "Заказчик", "role", "client");
            using (var composer = new HourlyActComposer(store))
            {
                composer.SelectCounterparty(party.Id);
                DataGridViewRow row = Work(composer, "Проверяемая работа", "1");
                foreach (object invalid in new object[] { null, "", " ", "0", "-1", "+1", "35,001", "35.001",
                    "ошибка", "NaN", "1e2", "1,000.00", "1.000,00", "1000000000000.01" })
                {
                    row.Cells["rate"].Value = invalid;
                    InvalidSave(composer, store, "invalid hourly price " + (invalid == null ? "(null)" : invalid));
                    Assert(row.Cells["amount"].Value == null, "invalid hourly price retained a calculated row amount");
                    Assert(store.NextActNumber == 500, "invalid hourly price consumed an act number");
                }
                CultureInfo original = System.Threading.Thread.CurrentThread.CurrentCulture;
                try
                {
                    foreach (string culture in new[] { "ru-RU", "en-US" })
                    {
                        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                        foreach (string text in new[] { "1\u00a0234,50", "1\u202f234.50", "1 234.50", "1234,50", "1234.50" })
                        {
                            row.Cells["rate"].Value = text;
                            Record valid = composer.BuildDraft();
                            Assert(Store.GetHourlyActLines(valid).Single().Rate == 1234.50m
                                && Store.ParseMoney(valid.Get("amount")) == 1234.50m,
                                "comma, dot or spaced hourly price depends on system culture");
                        }
                    }
                }
                finally { System.Threading.Thread.CurrentThread.CurrentCulture = original; }
                row.Cells["rate"].Value = "1000000000000";
                Assert(Store.ParseMoney(composer.BuildDraft().Get("amount")) == 1000000000000m, "maximum permitted hourly price is rejected");
                row.Cells["rate"].Value = "1000000000000";
                row.Cells["hours"].Value = "2";
                InvalidSave(composer, store, "hourly line above maximum amount");
                row.Cells["hours"].Value = "0.1"; row.Cells["rate"].Value = "0.01";
                InvalidSave(composer, store, "hourly line rounded to zero");
            }
        }
        private static void TestRefreshInheritedRates(string path)
        {
            var store = new Store(path);
            Record settings = Add(store, "settings", "name", "Обновление ставки", "currency", "BYN", "hourlyRate", "35.00");
            Record party = Add(store, "party", "name", "Заказчик", "role", "client");
            using (var host = new Form { ShowInTaskbar = false, Opacity = 0, Width = 1120, Height = 680 })
            using (var composer = new HourlyActComposer(store))
            {
                host.Controls.Add(composer); host.Show(); Application.DoEvents(); composer.SelectCounterparty(party.Id);
                DataGridViewRow inherited = Work(composer, "Автоматическая ставка", "1");
                DataGridViewRow custom = Work(composer, "Особая ставка", "1"); custom.Cells["rate"].Value = "45";
                DataGridViewRow sameAsDefault = Work(composer, "Ручная ставка равна прежней", "1");
                TextBox sameInput = BeginRateEdit(composer, sameAsDefault); sameInput.Text = "3"; sameInput.Text = "35"; composer.WorkGrid.EndEdit();
                DataGridViewRow blank = Work(composer, "Пустая ручная ставка", "1"); blank.Cells["rate"].Value = "";
                DataGridViewRow invalid = Work(composer, "Неправильная ручная ставка", "1"); invalid.Cells["rate"].Value = "ошибка";
                DataGridViewRow active = Work(composer, "Ставка в активном редакторе", "1");
                TextBox input = BeginRateEdit(composer, active); input.Text = "70,50"; Application.DoEvents();
                Assert(composer.WorkGrid.IsCurrentCellInEditMode, "active custom rate was prematurely committed");
                settings.Set("hourlyRate", "60.00"); store.Upsert(settings);
                long revision = store.Revision; composer.RefreshDefaults();
                Assert(Convert.ToDecimal(inherited.Cells["rate"].Value) == 60m, "untouched inherited price did not refresh");
                Assert(Convert.ToString(custom.Cells["rate"].Value) == "45" && Convert.ToString(sameAsDefault.Cells["rate"].Value) == "35",
                    "settings refresh overwrote custom prices, including a deliberate old default price");
                Assert(Convert.ToString(blank.Cells["rate"].Value) == "" && Convert.ToString(invalid.Cells["rate"].Value) == "ошибка",
                    "settings refresh silently repaired invalid or blank custom prices");
                Assert(Decimal.Parse(Convert.ToString(active.Cells["rate"].Value).Replace(',', '.'), CultureInfo.InvariantCulture) == 70.50m, "settings refresh lost active custom price");
                Assert(store.Revision == revision, "refreshing draft prices wrote an act");
                composer.WorkGrid.Rows.Remove(blank); composer.WorkGrid.Rows.Remove(invalid);
                DataGridViewRow fresh = Work(composer, "Строка после обновления настройки", "1");
                Assert(Convert.ToDecimal(fresh.Cells["rate"].Value) == 60m, "new row failed to inherit the updated default price");
                Record saved = composer.SaveDraft();
                Assert(Store.ParseMoney(saved.Get("amount")) == 270.50m && Store.GetHourlyActLines(saved).Select(line => line.Rate)
                    .SequenceEqual(new[] { 60m, 45m, 35m, 70.50m, 60m }), "saving refreshed prices lost inherited or custom rates");
                host.Close();
            }
        }
        private static void TestActiveManualRateSave(string path)
        {
            foreach (bool external in new[] { false, true })
            {
                var store = new Store(Path.Combine(path, external ? "external" : "standard"));
                Record party = Add(store, "party", "name", "Активная ячейка", "role", "client");
                using (var host = new Form { ShowInTaskbar = false, Opacity = 0, Width = 1120, Height = 680 })
                using (var composer = new HourlyActComposer(store, null, external))
                {
                    host.Controls.Add(composer); host.Show(); Application.DoEvents(); composer.SelectCounterparty(party.Id);
                    DataGridViewRow row = Work(composer, "Ввод цены последним действием", "2");
                    TextBox input = BeginRateEdit(composer, row); input.Text = "неверно"; Application.DoEvents();
                    InvalidSave(composer, store, "invalid active hourly rate");
                    Assert(store.NextActNumber == 500, "invalid active price consumed a number");
                    input = BeginRateEdit(composer, row); input.Text = "40,25"; Application.DoEvents();
                    Label total = (Label)typeof(HourlyActComposer).GetField("totals", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(composer);
                    Assert(Convert.ToDecimal(row.Cells["amount"].Value) == 80.50m && total.Text.Contains(80.50m.ToString("N2"))
                        && composer.WorkGrid.IsCurrentCellInEditMode, "typing an hourly price failed to recalculate the live row and total");
                    Record preview = composer.BuildDraft();
                    Assert(Store.GetHourlyActLines(preview).Single().Rate == 40.25m && store.NextActNumber == 500,
                        "preview did not commit the final hourly price or consumed a number");
                    input = BeginRateEdit(composer, row); input.Text = "50.75"; Application.DoEvents();
                    Record saved = composer.SaveDraft();
                    Assert(Store.GetHourlyActLines(saved).Single().Rate == 50.75m && Store.ParseMoney(saved.Get("amount")) == 101.50m
                        && Store.IsExternalAct(saved) == external && saved.Get("number") == "500" && store.NextActNumber == 501,
                        "save lost the final active hourly price, external scope or automatic number");
                    host.Close();
                }
            }
        }
        private static void ChooseFromButton(Button button, string query, bool cancel, bool clear)
        {
            using (var timer = new Timer { Interval = 10 })
            {
                timer.Tick += delegate
                {
                    var dialog = Application.OpenForms.OfType<ReferencePickerDialog>().FirstOrDefault();
                    if (dialog == null) return;
                    timer.Stop(); dialog.Opacity = 0;
                    dialog.SearchBox.Text = query;
                    ((Button)dialog.Controls.Find(cancel ? "picker-cancel" : clear ? "picker-clear" : "picker-select", true).Single()).PerformClick();
                };
                timer.Start(); button.PerformClick();
            }
        }
        private static void TestVisibleSelectionFields(string path)
        {
            var store = new Store(path);
            Add(store, "settings", "name", "Исполнитель", "currency", "BYN", "hourlyRate", "35");
            string fullName = "Общество с ограниченной ответственностью «Контрагент с длинным названием для проверки выбора»";
            Record party = Add(store, "party", "name", fullName, "role", "client", "unp", "123456789");
            Add(store, "party", "name", "Другой заказчик", "role", "client", "unp", "987654321");
            Record contract = Add(store, "contract", "number", "ДОГОВОР-2026-500", "partyId", party.Id,
                "status", "active", "currency", "BYN", "direction", "income", "date", "2026-10-13", "amount", "100");
            long revision = store.Revision; byte[] bytes = File.ReadAllBytes(store.DataPath);
            using (var host = new Form { Opacity = 0, ShowInTaskbar = false })
            using (var composer = new HourlyActComposer(store))
            {
                host.Controls.Add(composer); host.ClientSize = new System.Drawing.Size(850, 490);
                host.Show(); Application.DoEvents();
                var partyButton = (Button)composer.Controls.Find("composer-counterparty-choose", true).Single();
                var contractButton = (Button)composer.Controls.Find("composer-contract-choose", true).Single();
                var partyValue = (Button)composer.Controls.Find("composer-counterparty-value", true).Single();
                var contractValue = (Button)composer.Controls.Find("composer-contract-value", true).Single();
                foreach (var size in new[] { new System.Drawing.Size(850, 490), new System.Drawing.Size(1170, 710) })
                {
                    host.ClientSize = size; Application.DoEvents();
                    foreach (Button button in new[] { partyButton, contractButton, partyValue, contractValue })
                    {
                        Assert(button.Visible && button.Height >= 38, "reference selection button is hidden or too small");
                        Assert(button.Parent.ClientRectangle.Contains(button.Bounds), "reference selection button is clipped");
                    }
                    Assert(contractValue.Width > 300, "contract name lost its dedicated wide row");
                }
                ChooseFromButton(partyButton, "123456789", false, false);
                Assert(partyValue.Text == fullName && partyValue.AccessibleName.Contains(fullName), "counterparty name or accessible full value lost");
                Assert(contractValue.Text.Contains(contract.Get("number")), "automatic compatible contract is not shown in its button");
                DataGridViewRow row = Work(composer, "Несохранённая работа", "1,5");
                row.Cells["rate"].Value = "42,50";
                ChooseFromButton(contractButton, "", false, true);
                Assert(composer.BuildDraft().Get("contractId") == "" && contractValue.Text == "Без договора", "contract clear did not update the visible field");
                ChooseFromButton(partyValue, "987654321", true, false);
                Record draft = composer.BuildDraft();
                Assert(draft.Get("partyId") == party.Id && partyValue.Text == fullName, "cancel via visible value button changed the selection");
                Assert(Store.GetHourlyActLines(draft).Single().Rate == 42.50m
                    && row.Cells["description"].Value.ToString() == "Несохранённая работа", "reference selection lost draft work or custom price");
                Assert(store.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), "visible selection buttons saved an incomplete act");
                host.Close();
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
            TestVisibleSelectionFields(Path.Combine(path, "visible-selection-fields"));
            TestMissingRate(Path.Combine(path, "no-rate"));
            TestManualRates(Path.Combine(path, "manual-rates"));
            TestRateValidation(Path.Combine(path, "rate-validation"));
            TestRefreshInheritedRates(Path.Combine(path, "inherited-prices"));
            TestActiveManualRateSave(Path.Combine(path, "active-rate"));
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
            File.WriteAllText(Path.Combine(path, "PASS.txt"), "PASS: hourly act UI, editable manual per-row rates without required default, comma/dot and strict price validation, live active-cell totals and save, inherited-price refresh preserving custom edits, validation rollback, numbering, cancellation, navigation and historical requisites.", Encoding.UTF8);
        }
    }
}