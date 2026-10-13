using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class ActCardEditorTests
    {
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Act card test: " + message); }
        private static T Field<T>(object obj, string key) { return (T)obj.GetType().GetField(key, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj); }
        private static Dictionary<string, Control> Inputs(RecordEditor editor) { return Field<Dictionary<string, Control>>(editor, "inputs"); }
        private static ActWorkItemsControl Works(RecordEditor editor) { return Field<ActWorkItemsControl>(editor, "actWorks"); }
        private static string Selected(ComboBox combo)
        {
            object choice = combo.SelectedItem;
            return choice == null ? "" : Convert.ToString(choice.GetType().GetField("Value").GetValue(choice));
        }
        private static void Select(ComboBox combo, string value)
        {
            for (int i = 0; i < combo.Items.Count; ++i)
            {
                object choice = combo.Items[i];
                if (Convert.ToString(choice.GetType().GetField("Value").GetValue(choice)) == value) { combo.SelectedIndex = i; return; }
            }
            throw new InvalidOperationException("Missing act card option: " + value);
        }
        private static Record Save(Store store, string kind, params string[] fields)
        {
            Record record = new Record(kind); for (int i = 0; i < fields.Length; i += 2) record.Set(fields[i], fields[i + 1]);
            store.Upsert(record); return record;
        }
        private static Record Act(Store store, string party, string number, string amount, string description)
        {
            return Save(store, "act", "partyId", party, "number", number, "date", "2026-10-13", "direction", "income", "currency", "BYN", "status", "draft", "amount", amount, "description", description);
        }
        private static void Commit(RecordEditor editor)
        {
            typeof(RecordEditor).GetMethod("SaveClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, new object[] { null, EventArgs.Empty });
        }
        private static void Reject(Action action, string expected)
        {
            bool failed = false;
            try { action(); } catch (InvalidOperationException error) { failed = expected == null || error.Message.Contains(expected); }
            Assert(failed, "invalid candidate was accepted or wrong error: " + expected);
        }
        private static void Unchanged(Store store, long revision, byte[] bytes, string scenario)
        {
            Assert(store.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), scenario + " wrote the ledger");
        }
        public static void Run(string tempdir)
        {
            Store store = new Store(Path.Combine(tempdir, "card-" + Guid.NewGuid().ToString("N")));
            Record party = Save(store, "party", "name", "Первый заказчик", "role", "both", "unp", "111", "bankAccount", "BY111");
            Record secondParty = Save(store, "party", "name", "Второй заказчик", "role", "both", "unp", "222");
            Record project = Save(store, "project", "name", "Объект первого", "partyId", party.Id, "status", "active");
            Record secondProject = Save(store, "project", "name", "Объект второго", "partyId", secondParty.Id, "status", "active");
            Record contract = Save(store, "contract", "number", "Д1", "date", "2026-10-13", "partyId", party.Id, "projectId", project.Id, "currency", "BYN", "direction", "income", "amount", "10000.00", "status", "active");
            Record usdContract = Save(store, "contract", "number", "Д2", "date", "2026-10-13", "partyId", secondParty.Id, "projectId", secondProject.Id, "currency", "USD", "direction", "expense", "amount", "10000.00", "status", "active");
            Record settings = Save(store, "settings", "name", "Моя организация", "currency", "BYN", "hourlyRate", "50.00");
            Record longLegacy = Act(store, party.Id, "Старый-длинный", "120.00", new string('я', 2300));
            using (var editor = new RecordEditor(store, Catalog.Get("act"), longLegacy))
            {
                Inputs(editor)["notes"].Text = "Примечание к старому акту";
                var preview = editor.PrepareRecord();
                Assert(preview.Get("description") == longLegacy.Get("description") && preview.Get("amount") == "120.00", "notes-only long legacy description was rejected or rewritten");
            }
            string legacy = "Системное администрирование1. Перенести организацию 2. Настроить учёт 3. Заполнить политику 4. Заполнить справочники 5. Ввести остатки 6. Перенести документы 7. Заполнить договоры";
            Record act = Act(store, party.Id, "Семь-операций", "1050.00", legacy);
            act.Set("customField", "preserve"); store.CaptureActSnapshots(act); store.Upsert(act); act = store.Find(act.Id);
            long revision = store.Revision; byte[] bytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), act))
            {
                var works = Works(editor); var grid = works.WorkGrid;
                Assert(grid.Rows.Count == 7 && works.IsManualPricing, "legacy operations were collapsed or assigned invented prices");
                Assert(Store.ParseMoney(Inputs(editor)["amount"].Text) == 1050m && !((TextBox)Inputs(editor)["amount"]).ReadOnly, "manual legacy amount unavailable");
                var tabs = Field<TabControl>(editor, "sections");
                Assert(tabs.TabPages.Count == 4 && tabs.SelectedIndex == 0 && tabs.TabPages[0].Text == "Работы", "compact card pages missing");
                Assert(!((DateTimePicker)Inputs(editor)["dueDate"]).Checked && !((DateTimePicker)Inputs(editor)["signedDate"]).Checked, "optional dates were inferred");
                var mode = Field<CheckBox>(works, "manualPricing");
                mode.Checked = false; mode.Checked = true;
                Assert(Store.ParseMoney(Inputs(editor)["amount"].Text) == 1050m, "temporary pricing toggle lost confirmed total");
                Record unchanged = editor.PrepareRecord();
                Assert(unchanged.Get("description") == legacy && unchanged.Get("partySnapshot") == act.Get("partySnapshot"), "unchanged preview rewrote source descriptions or snapshots");
                grid.Rows[0].Cells["description"].Value = "Изменить организацию";
                Record preview = editor.PrepareRecord();
                string heading; var display = ActHtmlDocument.ReadDisplayRows(preview, out heading);
                Assert(display.Count == 7 && display[0].Description == "Изменить организацию" && display.All(row => !row.Price.HasValue) && heading == "Системное администрирование"
                    && preview.Get("amount") == "1050.00", "legacy row edit lost count, sum, or nullable pricing");
                Unchanged(store, revision, bytes, "legacy preview/cancel");
            }
            Unchanged(store, revision, bytes, "legacy disposal/cancel");
            using (var editor = new RecordEditor(store, Catalog.Get("act"), act))
            {
                var grid = Works(editor).WorkGrid;
                grid.Rows.Clear(); grid.Rows.Add(1, "Единственная подтверждённая операция", "", "", "", "");
                Inputs(editor)["notes"].Text = "Новое примечание";
                Commit(editor);
                Assert(editor.ResultRecord != null, "descriptive operation joint save failed");
            }
            act = store.Find(act.Id);
            string summary;
            Assert(ActHtmlDocument.ReadDisplayRows(act, out summary).Count == 1 && !ActHtmlDocument.ReadDisplayRows(act, out summary)[0].Price.HasValue &&
                act.Get("customField") == "preserve" && act.Get("notes") == "Новое примечание" && act.Get("amount") == "1050.00", "one unpriced operation or unrelated fields changed");
            revision = store.Revision; bytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), null))
            {
                var inputs = Inputs(editor);
                Select((ComboBox)inputs["partyId"], party.Id);
                Assert(Selected((ComboBox)inputs["contractId"]) == contract.Id && Selected((ComboBox)inputs["projectId"]) == project.Id, "unique compatible contract did not autofill");
                var grid = Works(editor).WorkGrid;
                grid.Rows.Clear(); grid.Rows.Add(1, "Монтаж", "шт", "2", "12,35", ""); grid.Rows.Add(2, "Настройка", "ч", "1,005", "2,00", "");
                Works(editor).RefreshTotals();
                Assert(((TextBox)inputs["amount"]).ReadOnly && Store.ParseMoney(inputs["amount"].Text) == 26.71m, "live per-line rounding or auto sum failed");
                var preview = editor.PrepareRecord();
                Assert(preview.Get("amount") == "26.71" && ActDocument.ReadItems(preview).Count == 2 && preview.Get("description").StartsWith("1. Монтаж"), "new card did not atomically prepare works and amount");
                Assert(preview.Get("partySnapshot") != "" && preview.Get("organizationSnapshot") != "", "new card requisites were not captured");
                var pricingMode = Field<CheckBox>(Works(editor), "manualPricing");
                pricingMode.Checked = true;
                Assert(Store.ParseMoney(inputs["amount"].Text) == 26.71m, "first manual-mode switch lost the current calculated total");
                pricingMode.Checked = false;
                grid.Rows[1].Cells["price"].Value = ""; Reject(delegate { editor.PrepareRecord(); }, "Работа 2");
                grid.Rows[1].Cells["price"].Value = "2,001"; Reject(delegate { editor.PrepareRecord(); }, "Работа 2");
                Unchanged(store, revision, bytes, "new totals/partial validation");
            }
            using (var editor = new RecordEditor(store, Catalog.Get("act"), act))
            {
                var inputs = Inputs(editor); string originalStatus = Selected((ComboBox)inputs["status"]);
                Select((ComboBox)inputs["partyId"], secondParty.Id);
                Assert(Selected((ComboBox)inputs["contractId"]) == "" && Selected((ComboBox)inputs["projectId"]) == "", "party change retained incompatible links");
                Select((ComboBox)inputs["contractId"], usdContract.Id);
                Assert(Selected((ComboBox)inputs["partyId"]) == secondParty.Id && Selected((ComboBox)inputs["projectId"]) == secondProject.Id &&
                    Selected((ComboBox)inputs["currency"]) == "USD" && Selected((ComboBox)inputs["direction"]) == "expense", "explicit contract selection did not autofill references/currency/direction");
                Assert(Selected((ComboBox)inputs["status"]) == originalStatus && !((DateTimePicker)inputs["dueDate"]).Checked &&
                    !((DateTimePicker)inputs["signedDate"]).Checked, "contract inferred payment date or signature/status");
                var preview = editor.PrepareRecord();
                Assert(Store.GetActSnapshot(preview, "partySnapshot").Id == secondParty.Id, "changed party retained old requisites");
                Select((ComboBox)inputs["currency"], "EUR");
                Assert(Selected((ComboBox)inputs["contractId"]) == "", "currency override kept incompatible contract");
            }
            Unchanged(store, revision, bytes, "reference changes/cancel");
            Record detailed = Act(store, party.Id, "Подробный", "100.00", "Старая строка");
            detailed.Set(ActDocument.ItemsField, ActDocument.SerializeItems(new[] { new ActPrintLine { Description = "Подробная работа", Unit = "ч", Quantity = 2m, Price = 50m } }));
            store.CaptureActSnapshots(detailed); store.Upsert(detailed); detailed = store.Find(detailed.Id);
            string savedItems = detailed.Get(ActDocument.ItemsField), savedSnapshot = detailed.Get("partySnapshot");
            party.Set("name", "Новое название справочника"); store.Upsert(party);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), detailed))
            {
                Inputs(editor)["notes"].Text = "Только примечание"; Commit(editor);
                Assert(editor.ResultRecord.Get(ActDocument.ItemsField) == savedItems && editor.ResultRecord.Get("partySnapshot") == savedSnapshot, "notes save rewrote print items or historical requisites");
            }
            detailed = store.Find(detailed.Id);
            revision = store.Revision; bytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), detailed))
            {
                Inputs(editor)["amount"].Text = "130,00"; Commit(editor);
                Assert(editor.ResultRecord == null && Field<Label>(editor, "hint").Text.Contains("Позиции акта"), "tampered auto total was accepted");
            }
            Unchanged(store, revision, bytes, "tampered calculated sum");
            Record hourly = Act(store, party.Id, "Часовой", "100.00", "");
            Store.SetHourlyActLines(hourly, new[] { new HourlyActLine { Description = "Почасовая настройка", Hours = 2, Rate = 50 } }); store.Upsert(hourly);
            revision = store.Revision; bytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), hourly))
            {
                var rows = Works(editor).WorkGrid; rows.Rows[0].Cells["qty"].Value = "3"; Works(editor).RefreshTotals();
                var preview = editor.PrepareRecord();
                Assert(preview.Get("amount") == "150.00" && preview.Get("hours") == "3" && Store.GetHourlyActLines(preview).Single().Hours == 3, "hourly edit desynchronized workItems/hours/total");
                rows.Rows[0].Cells["unit"].Value = "усл."; preview = editor.PrepareRecord();
                Assert(preview.Get("workItems") == "" && preview.Get("hours") == "" && preview.Get("hourlyRate") == "" && ActDocument.ReadItems(preview).Single().Unit == "усл.", "general-unit conversion retained hourly authority");
                Unchanged(store, revision, bytes, "hourly preview/cancel");
            }
            using (var editor = new RecordEditor(store, Catalog.Get(Catalog.ExternalActSection), null))
            {
                Select((ComboBox)Inputs(editor)["partyId"], party.Id);
                var rows = Works(editor).WorkGrid;
                rows.Rows[0].Cells["description"].Value = "Новый акт вне"; rows.Rows[0].Cells["price"].Value = "15";
                Works(editor).RefreshTotals();
                Assert(Store.IsExternalAct(editor.PrepareRecord()), "external section with null source created an ordinary act");
            }
            Record external = new Record("act"); external.Set("accountingScope", "external");
            using (var editor = new RecordEditor(store, Catalog.Get(Catalog.ExternalActSection), external))
            {
                Select((ComboBox)Inputs(editor)["partyId"], party.Id);
                var rows = Works(editor).WorkGrid; rows.Rows[0].Cells["description"].Value = "Работа вне"; rows.Rows[0].Cells["price"].Value = "10";
                Works(editor).RefreshTotals(); var preview = editor.PrepareRecord();
                Assert(Store.IsExternalAct(preview) && preview.Get("amount") == "10.00", "external card lost accounting scope");
            }
            Console.WriteLine("PASS: act card separate rows, legacy nullable prices, live totals/rounding, reference autofill/overrides, optional dates, snapshots, atomic preview/cancel, hourly conversion, external acts.");
        }
    }
}
