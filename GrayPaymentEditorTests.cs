using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class GrayPaymentEditorTests
    {
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Gray payment UI test: " + message); }
        private static Record Add(Store store, string kind, params string[] pairs)
        { var r = new Record(kind); for (int i = 0; i < pairs.Length; i += 2) r.Set(pairs[i], pairs[i + 1]); store.Upsert(r); return r; }
        private static Dictionary<string, Control> Inputs(RecordEditor editor)
        { return (Dictionary<string, Control>)typeof(RecordEditor).GetField("inputs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor); }
        private static string Value(Control control)
        { var combo = control as ComboBox; return combo == null ? control.Text : combo.SelectedItem == null ? "" : Convert.ToString(combo.SelectedItem.GetType().GetField("Value").GetValue(combo.SelectedItem)); }
        private static List<string> Values(Control control)
        { return ((ComboBox)control).Items.Cast<object>().Select(x => Convert.ToString(x.GetType().GetField("Value").GetValue(x))).ToList(); }
        private static void Choose(Control control, string value)
        {
            var combo = (ComboBox)control;
            for (int i = 0; i < combo.Items.Count; i++) if (Convert.ToString(combo.Items[i].GetType().GetField("Value").GetValue(combo.Items[i])) == value)
            { combo.SelectedIndex = i; return; }
            throw new InvalidOperationException("UI choice not found: " + value);
        }
        private static void ShowHidden(Form form)
        { form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents(); }
        private static void Save(RecordEditor editor)
        {
            try { typeof(RecordEditor).GetMethod("SaveClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, new object[] { null, EventArgs.Empty }); }
            catch (TargetInvocationException e) { throw new InvalidOperationException("Editor save failed", e.InnerException); }
        }
        private static void Unchanged(Store store, byte[] bytes, long revision, string reason)
        { Assert(store.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), reason + " wrote to the ledger"); }
        private static Record Act(Store store, Record party, string number, bool external, string currency, Record project, Record contract)
        {
            return Add(store, "act", "partyId", party.Id, "number", number, "date", "2026-07-01", "direction", "income", "currency", currency,
                "amount", "2000.00", "status", "signed", "accountingScope", external ? "external" : "standard",
                "projectId", project == null ? "" : project.Id, "contractId", contract == null ? "" : contract.Id);
        }
        private static Record Payment(Store store, Record party, string number, string scope, string actId, string amount, string currency)
        {
            var r = new Record("payment"); r.Set("partyId", party.Id); r.Set("number", number); r.Set("date", "2026-08-01");
            r.Set("direction", "income"); r.Set("amount", amount); r.Set("currency", currency); r.Set("method", "bank");
            if (scope != null) r.Set("paymentScope", scope); if (actId != null) r.Set("actId", actId); store.Upsert(r); return r;
        }
        private static Dictionary<string, string> GrayRow(string actor, string amount)
        {
            return new Dictionary<string, string> { { "Акт (ID)", actor }, { "Дата", "2026-08-11" }, { "Сумма", amount }, { "Способ оплаты", "Банк" } };
        }
        private static void RejectImport(Store store, Schema schema, List<Dictionary<string, string>> rows, bool gray, string reason, string fragment)
        {
            byte[] bytes = File.ReadAllBytes(store.DataPath); long revision = store.Revision; bool rejected = false;
            using (var dialog = new ImportDialog(store, schema, rows, "BYN", false, gray))
            {
                try { var prepared = dialog.PrepareImport(); store.ImportBatch(prepared); }
                catch (InvalidOperationException e) { rejected = fragment == null || e.Message.Contains(fragment); }
            }
            Assert(rejected, reason + " was accepted or lost its useful error"); Unchanged(store, bytes, revision, reason);
        }
        private static List<Dictionary<string, string>> ExportRows(string path, Schema schema, Record record, string extension)
        {
            var headers = schema.Fields.Select(f => f.Type == "ref" ? f.Label + " (ID)" : f.Label).ToList();
            var row = schema.Fields.Select(f => Catalog.ValueLabel(f, record.Get(f.Key))).ToList();
            if (extension == ".csv") TableIO.WriteCsv(path, headers, new List<List<string>> { row });
            else TableIO.WriteXlsx(path, headers, new List<List<string>> { row });
            return TableIO.Read(path);
        }
        public static void Run(string root)
        {
            Directory.CreateDirectory(root); string folder = Path.Combine(root, "gray-ui-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var store = new Store(folder); Add(store, "settings", "currency", "BYN");
            Record party = Add(store, "party", "name", "Тестовый заказчик", "role", "both");
            Record other = Add(store, "party", "name", "Другой заказчик", "role", "client");
            Record project = Add(store, "project", "name", "Тестовый объект EUR", "partyId", party.Id, "status", "active");
            Record contract = Add(store, "contract", "number", "Д-EUR", "date", "2026-01-01", "partyId", party.Id, "projectId", project.Id,
                "direction", "income", "currency", "EUR", "amount", "5000.00", "status", "active");
            Record ordinary = Act(store, other, "А-Б", false, "BYN", null, null);
            Record external = Act(store, party, "А-С", true, "BYN", null, null);
            Record eur = Act(store, party, "А-EUR", true, "EUR", project, contract);
            Schema graySchema = Catalog.Get(Catalog.GrayPaymentSection), whiteSchema = Catalog.Get("payment");
            Assert(graySchema.Kind == "payment" && graySchema.SectionKey == Catalog.GrayPaymentSection && graySchema.Title == "Серые оплаты"
                && Catalog.IsPaymentSection("payment") && Catalog.IsPaymentSection(Catalog.GrayPaymentSection), "virtual payment schema changes stored kind");
            Assert(graySchema.Fields.Count(f => f.Key == "actId") == 1 && !graySchema.Fields.Single(f => f.Key == "actId").Required
                && graySchema.Fields.Single(f => f.Key == "paymentScope").Options[""] == "По актам и распределениям", "optional base act and legacy scope are missing");
            byte[] bytes = File.ReadAllBytes(store.DataPath); long revision = store.Revision;
            Record gray;
            using (var editor = new RecordEditor(store, graySchema, null))
            {
                ShowHidden(editor); var input = Inputs(editor);
                Assert(Value(input["paymentScope"]) == "external" && Values(input["actId"]).Contains(eur.Id) && !Values(input["actId"]).Contains(ordinary.Id), "new gray editor has wrong scope or act list");
                input["amount"].Text = "250,50"; Save(editor);
                Assert(editor.ResultRecord == null && editor.DialogResult != DialogResult.OK, "gray payment without act was saved");
                Unchanged(store, bytes, revision, "missing gray act");
                Choose(input["actId"], eur.Id);
                Assert(Value(input["partyId"]) == party.Id && Value(input["currency"]) == "EUR" && Value(input["direction"]) == "income"
                    && Value(input["projectId"]) == project.Id && Value(input["contractId"]) == contract.Id, "act selection did not populate related fields");
                Save(editor); gray = editor.ResultRecord;
                Assert(gray != null && gray.Kind == "payment" && gray.Get("paymentScope") == "external" && gray.Get("actId") == eur.Id && gray.Get("amount") == "250.50", "gray CRUD lost record identity, act, scope or money");
            }
            Assert(store.AllocatedFromPayment(gray.Id) == 0 && store.ActGrossDue(eur.Id) == 2000, "saving a payment secretly allocated it or closed the act debt");
            Record white;
            using (var editor = new RecordEditor(store, whiteSchema, null))
            {
                ShowHidden(editor); var input = Inputs(editor);
                Assert(Value(input["paymentScope"]) == "standard" && !Values(input["actId"]).Contains(external.Id), "ordinary editor did not default to white");
                Choose(input["partyId"], other.Id); input["amount"].Text = "30.00"; Save(editor); white = editor.ResultRecord;
                Assert(white != null && white.Get("paymentScope") == "standard" && white.Get("actId") == "", "white advance incorrectly requires an act");
            }
            using (var editor = new RecordEditor(store, graySchema, store.Find(gray.Id)))
            {
                ShowHidden(editor); var input = Inputs(editor); Choose(input["paymentScope"], "standard");
                Assert(Value(input["actId"]) == "" && Values(input["actId"]).Contains(ordinary.Id) && !Values(input["actId"]).Contains(eur.Id), "scope switch retained an incompatible act");
                Choose(input["actId"], ordinary.Id);
                Assert(Value(input["partyId"]) == other.Id && Value(input["currency"]) == "BYN" && Value(input["projectId"]) == "" && Value(input["contractId"]) == "", "existing payment did not clear stale related fields after act change");
                Save(editor); gray = editor.ResultRecord;
                Assert(gray != null && gray.Get("paymentScope") == "standard" && gray.Get("actId") == ordinary.Id, "gray-to-white transfer failed");
            }
            using (var editor = new RecordEditor(store, whiteSchema, store.Find(gray.Id)))
            {
                ShowHidden(editor); var input = Inputs(editor); Choose(input["paymentScope"], "external"); Choose(input["actId"], eur.Id); Save(editor); gray = editor.ResultRecord;
                Assert(gray != null && gray.Get("paymentScope") == "external" && gray.Get("currency") == "EUR", "white-to-gray transfer failed");
            }
            Record legacy = Payment(store, party, "П-смешанная", null, null, "1000.00", "BYN");
            Record samePartyWhite = Act(store, party, "А-Б-2", false, "BYN", null, null);
            store.Allocate(legacy.Id, samePartyWhite.Id, 100); store.Allocate(legacy.Id, external.Id, 200);
            PaymentSplit before = PaymentAccounting.Build(store.Records).For(legacy);
            bytes = File.ReadAllBytes(store.DataPath); revision = store.Revision;
            using (var editor = new RecordEditor(store, whiteSchema, store.Find(legacy.Id)))
            {
                ShowHidden(editor); var input = Inputs(editor);
                Assert(Value(input["paymentScope"]) == "" && Values(input["actId"]).Contains(external.Id) && Values(input["actId"]).Contains(samePartyWhite.Id), "legacy mixed editor converted scope or hid one act category");
                Unchanged(store, bytes, revision, "opening legacy mixed editor"); input["notes"].Text = "Примечание смешанной оплаты"; Save(editor);
                Assert(editor.ResultRecord != null && editor.ResultRecord.Get("paymentScope") == "", "editing legacy notes retagged entire payment");
            }
            PaymentSplit after = PaymentAccounting.Build(store.Records).ForId(legacy.Id);
            Assert(before.WhiteAmount == after.WhiteAmount && before.GreyAmount == after.GreyAmount && after.IsMixed, "legacy mixed split changed after editing");
            Record archivedActor = Act(store, party, "А-архив", true, "BYN", null, null);
            Record linkedArchive = Payment(store, party, "П-архив-связь", "external", archivedActor.Id, "20.00", "BYN");
            archivedActor.Set("archived", "true"); store.Upsert(archivedActor); // Historical bound-archive fixture, rather than a new archive action.
            using (var editor = new RecordEditor(store, graySchema, store.Find(linkedArchive.Id)))
            {
                ShowHidden(editor); var input = Inputs(editor);
                Assert(Value(input["actId"]) == archivedActor.Id && Values(input["actId"]).Contains(archivedActor.Id), "existing archived bound act disappeared");
                input["notes"].Text = "Сохранена архивная связь"; Save(editor); Assert(editor.ResultRecord != null && editor.ResultRecord.Get("actId") == archivedActor.Id, "editing broke archived existing link");
            }
            using (var editor = new RecordEditor(store, graySchema, null))
            { Assert(!Values(Inputs(editor)["actId"]).Contains(archivedActor.Id), "new gray payment offers an archived act"); }
            var row = GrayRow(eur.Id, "40.00");
            using (var dialog = new ImportDialog(store, graySchema, new List<Dictionary<string, string>> { row }, "BYN", false, true))
            {
                bytes = File.ReadAllBytes(store.DataPath); revision = store.Revision; var prepared = dialog.PrepareImport();
                Assert(prepared.Count == 1 && prepared[0].Kind == "payment" && prepared[0].Get("paymentScope") == "external"
                    && prepared[0].Get("partyId") == party.Id && prepared[0].Get("currency") == "EUR" && prepared[0].Get("direction") == "income"
                    && prepared[0].Get("projectId") == project.Id && prepared[0].Get("contractId") == contract.Id && prepared[0].Get("date") == "2026-08-11", "act-first import failed to infer fields or changed payment date");
                Unchanged(store, bytes, revision, "preparing gray import"); store.ImportBatch(prepared);
                Assert(new Store(folder).Find(prepared[0].Id).Get("paymentScope") == "external", "gray import marker did not survive restart");
            }
            Record automatic = Payment(store, other, "П-авто", null, ordinary.Id, "15.00", "BYN");
            foreach (string extension in new[] { ".csv", ".xlsx" })
            {
                foreach (Record original in new[] { store.Find(gray.Id), store.Find(white.Id), store.Find(automatic.Id) })
                {
                    bool isGray = original.Get("paymentScope") == "external"; Schema selectedSchema = isGray ? graySchema : whiteSchema;
                    string file = Path.Combine(folder, "roundtrip-" + original.Id.Substring(0, 6) + extension);
                    var exported = ExportRows(file, selectedSchema, original, extension);
                    using (var dialog = new ImportDialog(store, selectedSchema, exported, "BYN", false, isGray))
                    {
                        var prepared = dialog.PrepareImport();
                        Assert(prepared.Count == 1 && prepared[0].Get("paymentScope") == original.Get("paymentScope") && prepared[0].Get("actId") == original.Get("actId")
                            && prepared[0].Get("amount") == original.Get("amount") && prepared[0].Get("currency") == original.Get("currency"), extension + " roundtrip changed payment type, act or full amount");
                        store.ImportBatch(prepared);
                    }
                }
            }
            var legacyRow = GrayRow(ordinary.Id, "10.00"); legacyRow["paymentScope"] = "";
            using (var dialog = new ImportDialog(store, whiteSchema, new List<Dictionary<string, string>> { legacyRow }, "BYN"))
            { Assert(dialog.PrepareImport().Single().Get("paymentScope") == "", "explicit empty exported marker was replaced with standard"); }
            var conflicting = GrayRow(eur.Id, "10.00"); conflicting["Вид оплаты"] = "Серая (Акты вне)"; conflicting["paymentScope"] = "standard";
            RejectImport(store, graySchema, new List<Dictionary<string, string>> { row, conflicting }, true, "conflicting scope aliases", "разные значения");
            var badCurrency = GrayRow(eur.Id, "10.00"); badCurrency["Валюта"] = "BYN";
            RejectImport(store, graySchema, new List<Dictionary<string, string>> { row, badCurrency }, true, "conflicting currency", "противоречит");
            RejectImport(store, graySchema, new List<Dictionary<string, string>> { GrayRow(ordinary.Id, "10.00") }, true, "ordinary act in gray import", null);
            RejectImport(store, graySchema, new List<Dictionary<string, string>> { GrayRow(archivedActor.Id, "10.00") }, true, "archived new import link", null);
            var mixed = GrayRow(external.Id, "1000.00"); mixed["Вид оплаты"] = "По актам и распределениям"; mixed["Серая часть"] = "200.00";
            RejectImport(store, graySchema, new List<Dictionary<string, string>> { mixed }, true, "partial mixed gray export", "Смешанная оплата");
            var whiteMixed = GrayRow(samePartyWhite.Id, "1000.00"); whiteMixed["Вид оплаты"] = "По актам и распределениям"; whiteMixed["Белая часть"] = "800.00";
            RejectImport(store, whiteSchema, new List<Dictionary<string, string>> { whiteMixed }, false, "partial mixed white export", "Смешанная оплата");
            var allGrayLegacy = GrayRow("", "1000.00"); allGrayLegacy["paymentScope"] = ""; allGrayLegacy["Контрагент (ID)"] = party.Id; allGrayLegacy["Серая часть"] = "1000.00";
            RejectImport(store, whiteSchema, new List<Dictionary<string, string>> { allGrayLegacy }, false, "fully allocated legacy gray export must not become a white advance", "другому разделу");
            var allWhiteLegacy = GrayRow(external.Id, "1000.00"); allWhiteLegacy["paymentScope"] = ""; allWhiteLegacy["Белая часть"] = "1000.00";
            RejectImport(store, graySchema, new List<Dictionary<string, string>> { allWhiteLegacy }, true, "fully white legacy export must not become gray by direct act", "другому разделу");
            var oldExternal = new Dictionary<string, string> { { "Номер", "ИМП-вне" }, { "Дата", "2026-08-11" }, { "Контрагент (ID)", party.Id }, { "Сумма", "25.00" }, { "Валюта", "BYN" }, { "Направление", "income" }, { "Статус", "signed" } };
            using (var dialog = new ImportDialog(store, Catalog.Get("act"), new List<Dictionary<string, string>> { oldExternal }, "BYN", true))
            { Assert(Store.IsExternalAct(dialog.PrepareImport().Single()), "existing fifth externalActs parameter changed meaning"); }
            bool rejectedFlag = false; try { using (var dialog = new ImportDialog(store, whiteSchema, new List<Dictionary<string, string>> { row }, "BYN", true)) { } }
            catch (ArgumentException) { rejectedFlag = true; } Assert(rejectedFlag, "externalActs flag silently became a payment flag");
            store.Archive(gray.Id); Assert(new Store(folder).Find(gray.Id).Get("paymentScope") == "external", "archiving or restart lost gray scope");
            File.WriteAllText(Path.Combine(root, "gray-payment-editor-result.txt"), "PASS: gray/white creation, required external act, related fields, scope transfers, legacy mixed preservation, archived links, act-first import, CSV/XLSX type roundtrip and atomic rejection of conflicts/partial mixed exports");
            Console.WriteLine("Gray payment editor/import checks passed");
        }
    }
}