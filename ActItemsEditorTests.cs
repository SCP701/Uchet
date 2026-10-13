using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class ActItemsEditorTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Act items editor test: " + message);
        }
        private static T Field<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        }
        private static void SaveEditor(object editor)
        {
            editor.GetType().GetMethod("SaveClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, new object[] { null, EventArgs.Empty });
        }
        private static Record Save(Store store, string kind, params string[] fields)
        {
            Record record = new Record(kind);
            for (int i = 0; i < fields.Length; i += 2) record.Set(fields[i], fields[i + 1]);
            store.Upsert(record); return record;
        }
        private static void AssertUnchanged(Store store, long revision, byte[] bytes, string scenario)
        {
            Assert(store.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), scenario + " changed the database");
        }

        public static void Run(string tempdir)
        {
            Store store = new Store(Path.Combine(tempdir, "items-" + Guid.NewGuid().ToString("N")));
            Record party = Save(store, "party", "name", "Заказчик позиций", "role", "both");
            Record project = Save(store, "project", "name", "Объект позиций", "partyId", party.Id, "status", "active");
            Record act = Save(store, "act", "number", "А-детали", "date", "2026-10-12", "partyId", party.Id, "projectId", project.Id,
                "direction", "income", "currency", "BYN", "amount", "100.00", "status", "signed", "description", "Старая общая услуга", "notes", "Сохранить примечание", "customField", "preserve");
            long checkpoint = store.Revision; byte[] checkpointBytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new ActItemsEditor(store, act))
            {
                DataGridView rows = Field<DataGridView>(editor, "items");
                Assert(rows.Rows.Count == 1 && Field<TextBox>(editor, "description").Text == act.Get("description"), "fallback amount/aggregate description was not loaded");
                rows.Rows[0].Cells["price"].Value = "120,50";
                Record preview = editor.PrepareRecord();
                Assert(preview.Get("amount") == "120.50" && ActDocument.ReadItems(preview)[0].Price == 120.50m, "preview did not calculate a consistent total");
                AssertUnchanged(store, checkpoint, checkpointBytes, "preview/cancel");
            }
            using (var editor = new ActItemsEditor(store, act))
            {
                DataGridView rows = Field<DataGridView>(editor, "items"); rows.Rows.Clear();
                rows.Rows.Add("Работы по монтажу", "ч", "2", "60,00", "");
                Field<TextBox>(editor, "description").Text = "Комплекс услуг по монтажу";
                SaveEditor(editor);
                Assert(editor.DialogResult == DialogResult.OK && editor.ResultRecord != null, "valid joint save failed");
            }
            act = store.Find(act.Id);
            Assert(act.Get("amount") == "120.00" && act.Get("printDescription") == "Комплекс услуг по монтажу" && act.Get("notes") == "Сохранить примечание" && act.Get("customField") == "preserve", "joint save lost unrelated fields or failed to update amount");
            Assert(ActDocument.ReadItems(act).Single().Quantity == 2m && ActDocument.ReadItems(act).Single().Price == 60m, "joint save lost structured detail");
            checkpoint = store.Revision; checkpointBytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), act))
            {
                Dictionary<string, Control> inputs = Field<Dictionary<string, Control>>(editor, "inputs");
                inputs["amount"].Text = "130,00";
                SaveEditor(editor);
                Assert(editor.ResultRecord == null && Field<Label>(editor, "hint").Text.Contains("Позиции акта"), "record editor allowed an inconsistent amount or gave no useful guidance");
            }
            AssertUnchanged(store, checkpoint, checkpointBytes, "separate amount edit");
            using (var editor = new RecordEditor(store, Catalog.Get("act"), act))
            {
                Field<Dictionary<string, Control>>(editor, "inputs")["notes"].Text = "Новое примечание";
                SaveEditor(editor);
                Assert(editor.ResultRecord != null && editor.ResultRecord.Get("printItemsJson") == act.Get("printItemsJson"), "unrelated edit failed or lost detail");
            }
            act = store.Find(act.Id);
            Record payment = Save(store, "payment", "date", "2026-10-12", "partyId", party.Id, "projectId", project.Id, "direction", "income", "currency", "BYN", "amount", "70.00", "method", "bank");
            store.Allocate(payment.Id, act.Id, 70m);
            Save(store, "hold", "actId", act.Id, "date", "2026-10-12", "amount", "20.00", "status", "active", "type", "warranty");
            checkpoint = store.Revision; checkpointBytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new ActItemsEditor(store, act))
            {
                DataGridView rows = Field<DataGridView>(editor, "items");
                rows.Rows[0].Cells["qty"].Value = "1"; rows.Rows[0].Cells["price"].Value = "50,00";
                SaveEditor(editor);
                Assert(editor.ResultRecord == null && Field<Label>(editor, "status").ForeColor.R > 100, "joint save bypassed payments/holds or gave no error");
                AssertUnchanged(store, checkpoint, checkpointBytes, "invalid lower amount");
                rows.Rows[0].Cells["price"].Value = "95,00";
                SaveEditor(editor);
                Assert(editor.ResultRecord != null && editor.ResultRecord.Get("amount") == "95.00", "valid corrected amount failed");
            }
            Assert(store.AllocatedToAct(act.Id) == 70m && store.ActiveHolds(act.Id) == 20m && store.ActDue(act.Id) == 5m, "editing details changed payments or holds");
            act = store.Find(act.Id);
            checkpoint = store.Revision; checkpointBytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new ActItemsEditor(store, act))
            {
                DataGridView rows = Field<DataGridView>(editor, "items"); rows.Rows.Clear();
                rows.Rows.Add("Работа", "ч", "1", "10,005", "");
                bool failed = false; try { editor.PrepareRecord(); } catch (InvalidOperationException e) { failed = e.Message.Contains("Строка 1"); }
                Assert(failed, "excess money precision was silently rounded");
                rows.Rows[0].Cells["price"].Value = "10,00"; rows.Rows[0].Cells["qty"].Value = "1,2345678";
                failed = false; try { editor.PrepareRecord(); } catch (InvalidOperationException e) { failed = e.Message.Contains("Строка 1"); }
                Assert(failed, "excess quantity precision was silently rounded");
                rows.Rows[0].Cells["qty"].Value = "1";
                rows.Rows.Add("Бесплатная сопутствующая услуга", "усл.", "1", "0,00", "");
                Record prepared = editor.PrepareRecord();
                Assert(prepared.Get("amount") == "10.00" && ActDocument.ReadItems(prepared).Count == 2, "zero-price position was not preserved");
            }
            AssertUnchanged(store, checkpoint, checkpointBytes, "precision/fallback checks");
            Record estimate = Save(store, "estimate", "number", "С-детали", "date", "2026-10-12", "partyId", party.Id, "projectId", project.Id, "direction", "income", "currency", "BYN", "status", "approved");
            Record work = Save(store, "work", "estimateId", estimate.Id, "description", "Точная работа", "unit", "м", "qty", "1.234567", "price", "10.00");
            Save(store, "work", "estimateId", estimate.Id, "description", "Бесплатная услуга", "unit", "усл.", "qty", "1", "price", "0.00");
            Record archivedWork = Save(store, "work", "estimateId", estimate.Id, "description", "Архивная строка", "qty", "1", "price", "999.00");
            store.Archive(archivedWork.Id);
            Record snapshot;
            using (var main = new MainForm(store))
            {
                checkpoint = store.Revision; checkpointBytes = File.ReadAllBytes(store.DataPath);
                snapshot = main.BuildActFromEstimate(estimate);
                Assert(snapshot.Get("amount") == "12.35" && ActDocument.ReadItems(snapshot).Count == 2 && ActDocument.ReadItems(snapshot)[0].Quantity == 1.234567m, "estimate snapshot lost precision, included archive, or changed total");
                AssertUnchanged(store, checkpoint, checkpointBytes, "estimate snapshot preparation");
                main.Navigate("act");
                var labels = CompactToolbarTests.CommandLabels(Field<FlowLayoutPanel>(main, "actions")).ToList();
                Assert(labels.Contains("Позиции акта") && labels.Contains("Акт Word") && labels.Contains("Печатная форма"), "Word/details actions missing or old print removed");
            }
            snapshot.Set("number", "А-снимок"); snapshot.Set("date", "2026-10-12"); store.Upsert(snapshot);
            work.Set("qty", "2"); work.Set("price", "20.00"); store.Upsert(work);
            Assert(ActDocument.ReadItems(store.Find(snapshot.Id))[0].Quantity == 1.234567m && ActDocument.ReadItems(store.Find(snapshot.Id))[0].Price == 10m, "changing estimate changed saved act detail");
            Record hourly = store.CreateHourlyActDraft(party.Id);
            hourly.Set("number", "П-часы"); hourly.Set("projectId", project.Id); hourly.Set("customField", "hour-preserve");
            Store.SetHourlyActLines(hourly, new[] { new HourlyActLine { Description = "Почасовой монтаж", Hours = 2m, Rate = 50m } });
            store.Upsert(hourly);
            string partySnapshot = store.Find(hourly.Id).Get("partySnapshot");
            using (var editor = new ActItemsEditor(store, store.Find(hourly.Id)))
            {
                DataGridView rows = Field<DataGridView>(editor, "items");
                Assert(Convert.ToString(rows.Rows[0].Cells["unit"].Value) == "ч" && Convert.ToString(rows.Rows[0].Cells["qty"].Value) == "2", "hourly fallback did not load real work lines");
                rows.Rows[0].Cells["qty"].Value = "3"; rows.Rows[0].Cells["price"].Value = "60,00";
                Field<TextBox>(editor, "description").Text = "Почасовые услуги по монтажу";
                SaveEditor(editor);
                Assert(editor.ResultRecord != null && editor.ResultRecord.Get("amount") == "180.00", "hourly detail save was reset by normalization");
            }
            hourly = store.Find(hourly.Id);
            Assert(hourly.Get("hours") == "3" && hourly.Get("hourlyRate") == "60.00" && Store.GetHourlyActLines(hourly).Single().Hours == 3m && ActDocument.ReadItems(hourly).Single().Quantity == 3m,
                "hourly and printed detail diverged after persistence");
            Assert(hourly.Get("partySnapshot") == partySnapshot && hourly.Get("customField") == "hour-preserve", "hourly detail save replaced snapshots or unrelated fields");
            // Simulate a subsequent save by the separate hourly composer retaining old print JSON.
            Store.SetHourlyActLines(hourly, new[] { new HourlyActLine { Description = "Новые почасовые работы", Hours = 4m, Rate = 25m } });
            store.Upsert(hourly); hourly = store.Find(hourly.Id);
            Assert(ActDocument.ReadItems(hourly).Single().Description == "Новые почасовые работы" && ActDocument.ReadItems(hourly).Single().Quantity == 4m
                && ActDocument.ReadItems(hourly).Single().Price == 25m, "old printed JSON overrode authoritative hourly work lines");
            ActDocument.ValidateItems(hourly, ActDocument.ReadItems(hourly));
            using (var editor = new ActItemsEditor(store, hourly))
            {
                DataGridView rows = Field<DataGridView>(editor, "items");
                rows.Rows[0].Cells["unit"].Value = "шт."; rows.Rows[0].Cells["qty"].Value = "2"; rows.Rows[0].Cells["price"].Value = "70,00";
                Assert(Field<Label>(editor, "mode").Text.Contains("обычные позиции"), "explicit unit conversion was not announced");
                SaveEditor(editor);
                Assert(editor.ResultRecord != null && editor.ResultRecord.Get("amount") == "140.00", "general conversion total was overwritten by old hourly data");
            }
            hourly = store.Find(hourly.Id);
            Assert(hourly.Get("workItems") == "" && hourly.Get("hours") == "" && hourly.Get("hourlyRate") == "" && hourly.Get("partySnapshot") == partySnapshot
                && ActDocument.ReadItems(hourly).Single().Unit == "шт." && ActDocument.ReadItems(hourly).Single().Quantity == 2m,
                "explicit unit conversion lost detail/snapshots or retained conflicting hourly fields");
            Console.WriteLine("PASS: act detail preview/cancel, joint amount save, payment/hold validation, separate-edit guard, precision, zero-price rows, estimate snapshot, Word/HTML actions, hourly synchronization/stale JSON/unit conversion.");
        }
    }
}

