using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;

namespace WorkLedger
{
    public static class ExternalActIntegrationTests
    {
        private const string ExternalSection = "external-act";
        private static void Assert(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("External act integration: " + reason);
        }
        private static Record Add(Store store, string kind, params string[] fields)
        {
            var record = new Record(kind);
            for (int index = 0; index < fields.Length; index += 2) record.Set(fields[index], fields[index + 1]);
            store.Upsert(record); return store.Find(record.Id);
        }
        private static T Field<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing integration field " + name);
            return (T)field.GetValue(target);
        }
        private static object Invoke(object target, string method, params object[] arguments)
        {
            try { return target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments); }
            catch (TargetInvocationException exception) { throw new InvalidOperationException(method + " failed", exception.InnerException); }
        }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control descendant in Descendants(child)) yield return descendant;
            }
        }
        private static void Show(Form form)
        {
            form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
        }
        private static void Navigate(MainForm form, string section)
        {
            form.Navigate(section); Application.DoEvents();
        }
        private static TableData Table(MainForm form) { return Field<TableData>(form, "table"); }
        private static decimal Amount(string value) { return Store.ParseMoney(value); }
        private static string PackageText(string path, string entry)
        {
            using (var archive = ZipFile.OpenRead(path))
            using (var stream = archive.GetEntry(entry).Open())
            {
                var document = XDocument.Load(stream);
                return String.Concat(document.Descendants().Where(element => element.Name.LocalName == "t" || element.Name.LocalName == "v").Select(element => element.Value));
            }
        }
        private static Record SignedAct(Store store, Record party, Record project, Record contract, string number, string amount, string scope, string direction)
        {
            return Add(store, "act", "number", number, "date", "2026-10-12", "partyId", party.Id,
                "projectId", project.Id, "contractId", contract.Id, "direction", direction, "currency", "BYN",
                "amount", amount, "status", "signed", "signedDate", "2026-10-12", "accountingScope", scope);
        }
        private static void TestFieldsAndVisibility(string path)
        {
            Schema ordinary = Catalog.Get("act"), external = Catalog.Get(ExternalSection);
            Assert(external != null && external.Kind == "act" && external.Title == "Акты вне", "external section is missing or uses an incompatible record kind");
            Assert(ordinary.Fields.Count == external.Fields.Count, "external act is missing ordinary act fields");
            for (int index = 0; index < ordinary.Fields.Count; index++)
            {
                FieldDef first = ordinary.Fields[index], second = external.Fields[index];
                Assert(first.Key == second.Key && first.Label == second.Label && first.Type == second.Type
                    && first.Required == second.Required && first.ReferenceKind == second.ReferenceKind && first.Section == second.Section
                    && first.Options.OrderBy(pair => pair.Key).SequenceEqual(second.Options.OrderBy(pair => pair.Key)),
                    "ordinary act field differs in the external section: " + first.Key);
            }
            Assert(Catalog.All.Count(schema => schema.Kind == "act") == 1, "external section duplicated the persisted act schema");
            Assert(UiPreferences.Sections.Count(section => section.Key == ExternalSection) == 1, "external navigation entry is missing or repeated");
            var store = new Store(Path.Combine(path, "data"));
            Add(store, "settings", "currency", "BYN");
            byte[] original = File.ReadAllBytes(store.DataPath); long revision = store.Revision;
            var hidden = new UiPreferences { AnimationsEnabled = false }; hidden.SetVisible(ExternalSection, false); hidden.Save(store.DataDir);
            hidden.Save(store.DataDir, "external-test-profile");
            Assert(!UiPreferences.Load(store.DataDir).IsVisible(ExternalSection)
                && UiPreferences.Load(store.DataDir).IsVisible("act"), "hiding external acts also hid ordinary acts or failed to persist");
            Assert(UiPreferences.Load(store.DataDir, "other-test-profile").IsVisible(ExternalSection), "external visibility leaked into another personal profile");
            using (var form = new MainForm(store))
            {
                Show(form);
                Control button = Field<FlowLayoutPanel>(form, "nav").Controls.Cast<Control>().Single(control => Convert.ToString(control.Tag) == ExternalSection);
                Assert(!button.Visible, "external sidebar entry ignored the saved hidden preference");
                hidden.SetVisible(ExternalSection, true); hidden.Save(store.DataDir); form.ApplyInterfacePreferences(hidden); Application.DoEvents();
                Assert(button.Visible, "external sidebar entry could not be restored");
                Navigate(form, ExternalSection);
                hidden.SetVisible(ExternalSection, false); hidden.Save(store.DataDir); form.ApplyInterfacePreferences(hidden); Application.DoEvents();
                Assert(!button.Visible && Field<string>(form, "current") != ExternalSection, "hiding the active external section left an unusable selection");
                form.Close();
            }
            using (var restarted = new MainForm(new Store(store.DataDir)))
            {
                Show(restarted);
                Assert(!Field<FlowLayoutPanel>(restarted, "nav").Controls.Cast<Control>().Single(control => Convert.ToString(control.Tag) == ExternalSection).Visible,
                    "restart restored a hidden external sidebar entry");
                restarted.Close();
            }
            Assert(store.Revision == revision && original.SequenceEqual(File.ReadAllBytes(store.DataPath)), "external section visibility changed accounting data");
        }
        private static void TestCreationEditingAndDocuments(string path)
        {
            Directory.CreateDirectory(path); var store = new Store(Path.Combine(path, "data"));
            Record own = Add(store, "settings", "name", "Исполнитель внешнего акта", "currency", "BYN", "hourlyRate", "40.00", "unp", "123456789", "bankAccount", "BY-EXTERNAL-EXECUTOR");
            Record party = Add(store, "party", "name", "Заказчик внешнего акта", "role", "client", "unp", "987654321", "bankAccount", "BY-EXTERNAL-CUSTOMER");
            Record project = Add(store, "project", "name", "Объект внешнего акта", "partyId", party.Id, "status", "active");
            Record contract = Add(store, "contract", "number", "ВНЕ-Д-1", "date", "2026-10-12", "partyId", party.Id, "projectId", project.Id,
                "direction", "income", "currency", "BYN", "amount", "1000.00", "status", "active");
            Record act;
            using (var form = new MainForm(store))
            {
                Show(form); var visible = new UiPreferences { AnimationsEnabled = false }; form.ApplyInterfacePreferences(visible);
                Navigate(form, ExternalSection);
                Record card = form.NewRecordForCurrentSection();
                Assert(card.Kind == "act" && Store.IsExternalAct(card), "new external act card has the wrong record kind or accounting scope");
                Navigate(form, "create-act");
                HourlyActComposer ordinaryComposer = Field<HourlyActComposer>(form, "composer");
                ordinaryComposer.SelectCounterparty(party.Id);
                ordinaryComposer.WorkGrid.Rows.Add("Незавершённая обычная работа", "1", null, null);
                Navigate(form, "create-external-act");
                HourlyActComposer composer = Field<HourlyActComposer>(form, "composer");
                Assert(composer.IsExternal, "external creation opened an ordinary act composer");
                composer.SelectCounterparty(party.Id);
                composer.WorkGrid.Rows.Add("Проверка внешнего оборудования", "1,5", null, null);
                composer.WorkGrid.Rows.Add("Настройка внешнего оборудования", "0,25", null, null);
                long beforeHiding = store.Revision;
                visible.SetVisible(ExternalSection, false); visible.Save(store.DataDir); form.ApplyInterfacePreferences(visible); Application.DoEvents();
                Navigate(form, "create-act");
                Assert(Object.ReferenceEquals(ordinaryComposer, Field<HourlyActComposer>(form, "composer"))
                    && !Store.IsExternalAct(ordinaryComposer.BuildDraft())
                    && Store.GetHourlyActLines(ordinaryComposer.BuildDraft()).Single().Description == "Незавершённая обычная работа",
                    "switching to external acts discarded or relabelled the ordinary unfinished draft");
                Navigate(form, "create-external-act");
                Assert(Object.ReferenceEquals(composer, Field<HourlyActComposer>(form, "composer")) && composer.IsExternal
                    && Store.GetHourlyActLines(composer.BuildDraft()).Count == 2 && store.Revision == beforeHiding,
                    "hiding the external section discarded, saved or relabelled its unfinished draft");
                act = composer.SaveDraft(); Application.DoEvents();
                Assert(act.Kind == "act" && Store.IsExternalAct(act) && act.Get("amount") == "70.00", "external draft lost its scope or financial total while saving");
                Assert(Field<string>(form, "current") == ExternalSection && Table(form).Ids.Contains(act.Id), "saved external act was opened in the ordinary section");
                Navigate(form, "act"); Assert(!Table(form).Ids.Contains(act.Id), "external act leaked into the ordinary act list");
                form.Close();
            }
            using (var editor = new HourlyActComposer(store, act))
            {
                Assert(editor.IsExternal && editor.WorkGrid.Rows.Cast<DataGridViewRow>().Count(row => !row.IsNewRow) == 2, "reopening an external act lost its mode or work rows");
                editor.WorkGrid.Rows[1].Cells["description"].Value = "Изменённая настройка внешнего оборудования";
                act = editor.SaveDraft();
                Assert(Store.IsExternalAct(act) && act.Get("amount") == "70.00", "hourly editing changed external accounting scope or total");
            }
            act.Set("notes", "Исходное примечание"); store.Upsert(act);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), store.Find(act.Id)))
            {
                Field<Dictionary<string, Control>>(editor, "inputs")["notes"].Text = "Примечание внешнего акта";
                Invoke(editor, "SaveClick", null, EventArgs.Empty);
                Assert(editor.ResultRecord != null && Store.IsExternalAct(editor.ResultRecord), "ordinary record editor dropped the hidden scope field");
            }
            act = new Store(store.DataDir).Find(act.Id);
            Assert(Store.IsExternalAct(act) && act.Get("notes") == "Примечание внешнего акта" && Store.GetHourlyActLines(act).Count == 2, "restart lost external scope, memo or works");
            own.Set("name", "Изменённый исполнитель"); store.Upsert(own); party.Set("name", "Изменённый заказчик"); store.Upsert(party);
            string html = Path.Combine(path, "external-act.html"), excel = Path.Combine(path, "external-act.xlsx"), word = Path.Combine(path, "external-act.docx");
            long beforePrint = store.Revision; byte[] ledgerBeforePrint = File.ReadAllBytes(store.DataPath);
            ActHtmlDocument.WriteHtml(html, store, act); HourlyActDocuments.WriteXlsx(excel, store, act); ActDocument.Export(store, act, word);
            string printedHtml = File.ReadAllText(html, Encoding.UTF8), printedExcel = PackageText(excel, "xl/worksheets/sheet1.xml"), printedWord = PackageText(word, "word/document.xml");
            foreach (string content in new[] { printedHtml, printedExcel, printedWord })
            {
                Assert(content.Contains("Исполнитель внешнего акта") && content.Contains("Заказчик внешнего акта")
                    && content.Contains("BY-EXTERNAL-EXECUTOR") && content.Contains("BY-EXTERNAL-CUSTOMER"), "external print format lost immutable requisites");
                Assert(content.Contains("Проверка внешнего оборудования") && content.Contains("Изменённая настройка внешнего оборудования"), "external print format lost work descriptions");
                Assert(content.Contains("70.00") || content.Contains("70,00"), "external print format changed the total");
                Assert(!content.Contains("Изменённый исполнитель") && !content.Contains("Изменённый заказчик"), "external print format replaced saved requisites with current ones");
            }
            Assert(printedExcel.Contains("ВНЕ-Д-1") && printedExcel.Contains(project.Get("name")) && printedExcel.Contains("Примечание внешнего акта"), "external Excel lost contract, object or notes");
            Assert(store.Revision == beforePrint && ledgerBeforePrint.SequenceEqual(File.ReadAllBytes(store.DataPath)), "printing external acts changed the ledger");
        }
        private static void TestReportsAndHiddenAccounting(string path)
        {
            var store = new Store(Path.Combine(path, "data")); Add(store, "settings", "currency", "BYN");
            Record party = Add(store, "party", "name", "Клиент общих расчётов", "role", "both");
            Record project = Add(store, "project", "name", "Объект общих расчётов", "partyId", party.Id, "status", "active");
            Record contract = Add(store, "contract", "number", "ОБЩ-Д", "date", "2026-10-12", "partyId", party.Id, "projectId", project.Id,
                "direction", "income", "currency", "BYN", "amount", "1000.00", "status", "active");
            Record ordinary = SignedAct(store, party, project, contract, "ОБЫЧ-1", "100.00", "", "income");
            Record external = SignedAct(store, party, project, contract, "ВНЕ-1", "250.00", "external", "income");
            Record cost = Add(store, "act", "number", "ВНЕ-РАСХОД", "date", "2026-10-12", "partyId", party.Id, "projectId", project.Id,
                "currency", "BYN", "direction", "expense", "amount", "30.00", "status", "signed", "accountingScope", "external");
            Record draft = Add(store, "act", "number", "ВНЕ-ЧЕРНОВИК", "date", "2026-10-12", "partyId", party.Id, "projectId", project.Id,
                "currency", "BYN", "direction", "income", "amount", "45.00", "status", "draft", "accountingScope", "external");
            Record archived = SignedAct(store, party, project, contract, "ВНЕ-АРХИВ", "500.00", "external", "income"); store.Archive(archived.Id);
            byte[] bytes = File.ReadAllBytes(store.DataPath); long revision = store.Revision;
            var hidden = new UiPreferences { AnimationsEnabled = false }; hidden.SetVisible(ExternalSection, false); hidden.Save(store.DataDir);
            using (var form = new MainForm(store))
            {
                Show(form); Navigate(form, "act");
                Assert(Table(form).Ids.SequenceEqual(new[] { ordinary.Id }), "ordinary section mixed external scope into the act list");
                Navigate(form, ExternalSection);
                Assert(new HashSet<string>(Table(form).Ids).SetEquals(new[] { external.Id, cost.Id, draft.Id }), "external section did not select all live external acts independently of the hidden menu");
                form.SelectReport(0); Application.DoEvents();
                Assert(Table(form).Ids.Contains(ordinary.Id) && Table(form).Ids.Contains(external.Id) && Table(form).Ids.Contains(cost.Id), "common debt report excluded external signed acts");
                form.SelectReport(1); Application.DoEvents();
                List<string> profit = Table(form).Rows[Table(form).Ids.IndexOf(project.Id)];
                Assert(Amount(profit[1]) == 850m && Amount(profit[2]) == 30m && Amount(profit[5]) == 820m, "common project accounting dropped external revenue or cost");
                form.SelectReport(2); Application.DoEvents();
                Assert(Table(form).Ids.Contains(external.Id) && Table(form).Ids.Contains(cost.Id) && Table(form).Ids.Contains(archived.Id)
                    && !Table(form).Ids.Contains(draft.Id) && Amount(Table(form).Rows.Last()[6]) == 820m, "reconciliation omitted external signed acts or included an external draft");
                form.SelectReport(3); Application.DoEvents();
                List<string> execution = Table(form).Rows[Table(form).Ids.IndexOf(contract.Id)];
                Assert(Amount(execution[6]) == 850m, "common contract execution omitted external signed work");
                form.SelectReport(5); Application.DoEvents();
                Assert(Table(form).Ids.Contains(draft.Id), "common pending-document report omitted external drafts");
                form.SelectReport(6); Application.DoEvents();
                Assert(Table(form).Ids.Where(id => id != "").SequenceEqual(new[] { ordinary.Id }), "tax accounting included external acts or omitted ordinary signed acts");
                Navigate(form, "overview");
                var captions = Descendants(Field<Panel>(form, "overview")).OfType<Label>().ToList();
                Label revenueCaption = captions.Single(label => label.Text == "Работы для заказчиков");
                Label revenueValue = revenueCaption.Parent.Controls.OfType<Label>().Single(label => label.Top == 48);
                Label profitCaption = captions.Single(label => label.Text == "Прибыль по работам");
                Label profitValue = profitCaption.Parent.Controls.OfType<Label>().Single(label => label.Top == 48);
                Assert(Amount(revenueValue.Text.Replace(" BYN", "")) == 850m && Amount(profitValue.Text.Replace(" BYN", "")) == 820m, "overview totals changed when the external sidebar section was hidden");
                form.Close();
            }
            Assert(store.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), "viewing common or tax accounting changed the ledger");
        }
        private static Dictionary<string, string> ImportRow(Record party, string number, string scope)
        {
            var row = new Dictionary<string, string> {
                { "Номер", number }, { "Дата", "12.10.2026" }, { "Контрагент (ID)", party.Id },
                { "Валюта", "BYN" }, { "Направление", "Заказчик платит нам" }, { "Сумма", "123,45" },
                { "Статус", "Подписан" }, { "Примечание", "Импортируемое примечание" }
            };
            if (scope != null) row.Add("Учет акта", scope);
            return row;
        }
        private static void TestSectionExports(string path)
        {
            Directory.CreateDirectory(path); var store = new Store(Path.Combine(path, "data"));
            Record own = Add(store, "settings", "currency", "BYN");
            Record party = Add(store, "party", "name", "Клиент выгрузки актов", "role", "client");
            Record project = Add(store, "project", "name", "Объект выгрузки", "partyId", party.Id, "status", "active");
            Record contract = Add(store, "contract", "number", "ВЫГ-Д", "date", "2026-10-12", "partyId", party.Id, "projectId", project.Id,
                "direction", "income", "currency", "BYN", "amount", "1000.00", "status", "active");
            Record ordinary = SignedAct(store, party, project, contract, "ВЫГ-ОБЫЧНЫЙ", "100.00", "", "income");
            Record external = SignedAct(store, party, project, contract, "ВЫГ-ВНЕ", "123.45", "external", "income");
            ordinary.Set("notes", "Примечание обычного акта"); store.Upsert(ordinary);
            external.Set("notes", "Примечание внешнего акта"); store.Upsert(external);
            using (var form = new MainForm(store))
            {
                Show(form); form.ApplyInterfacePreferences(new UiPreferences { AnimationsEnabled = false });
                foreach (bool externalActs in new[] { false, true })
                {
                    string section = externalActs ? ExternalSection : "act"; Record expected = externalActs ? external : ordinary;
                    Navigate(form, section); TableData exported = form.BuildExportTable();
                    Assert(exported.Rows.Count == 1 && exported.Headers.Contains("Учет акта")
                        && exported.Rows.Single()[exported.Headers.IndexOf("Учет акта")] == (externalActs ? "external" : "standard"),
                        "actual section export omitted or changed its accounting marker");
                    Assert(exported.Rows.All(row => row.Count == exported.Headers.Count), "actual section export has unlabelled or missing columns");
                    foreach (string extension in new[] { ".csv", ".xlsx" })
                    {
                        string file = Path.Combine(path, section + extension), targetPath = Path.Combine(path, (externalActs ? "e" : "o") + extension.Substring(1));
                        if (extension == ".csv") TableIO.WriteCsv(file, exported.Headers, exported.Rows); else TableIO.WriteXlsx(file, exported.Headers, exported.Rows);
                        var target = new Store(targetPath); target.Upsert(own.Clone()); target.Upsert(party.Clone()); target.Upsert(project.Clone()); target.Upsert(contract.Clone());
                        using (var dialog = new ImportDialog(target, Catalog.Get(section), TableIO.Read(file), "BYN", externalActs))
                        {
                            List<Record> prepared = dialog.PrepareImport();
                            Assert(prepared.Count == 1 && Store.IsExternalAct(prepared.Single()) == externalActs
                                && prepared.Single().Get("number") == expected.Get("number") && prepared.Single().Get("amount") == expected.Get("amount")
                                && prepared.Single().Get("notes") == expected.Get("notes") && prepared.Single().Get("partyId") == party.Id
                                && prepared.Single().Get("projectId") == project.Id && prepared.Single().Get("contractId") == contract.Id,
                                "actual section export/import lost scope, amount, notes or explicit reference IDs");
                            target.ImportBatch(prepared);
                        }
                        Assert(Store.IsExternalAct(new Store(targetPath).Of("act").Single()) == externalActs, "exported accounting marker changed after import and restart");
                    }
                }
                form.SelectReport(0); Application.DoEvents(); TableData reportExport = form.BuildExportTable();
                Assert(reportExport.Rows.Count == 2 && reportExport.Rows.All(row => row.Count == reportExport.Headers.Count), "common report export contains unlabelled record identifiers");
                form.Close();
            }
        }
        private static void TestImportAndBackup(string path)
        {
            Directory.CreateDirectory(path); var store = new Store(Path.Combine(path, "data")); Add(store, "settings", "currency", "BYN");
            Record party = Add(store, "party", "name", "Клиент импорта внешних актов", "role", "client");
            foreach (string extension in new[] { ".csv", ".xlsx" })
            {
                Dictionary<string, string> row = ImportRow(party, "ВНЕ-ИМПОРТ-" + extension.Substring(1), "external");
                string file = Path.Combine(path, "external-import" + extension);
                var headers = row.Keys.ToList(); var values = new List<List<string>> { row.Values.ToList() };
                if (extension == ".csv") TableIO.WriteCsv(file, headers, values); else TableIO.WriteXlsx(file, headers, values);
                using (var dialog = new ImportDialog(store, Catalog.Get(ExternalSection), TableIO.Read(file), "BYN", true))
                {
                    long revision = store.Revision; List<Record> prepared = dialog.PrepareImport();
                    Assert(store.Revision == revision && prepared.Count == 1 && prepared[0].Kind == "act" && Store.IsExternalAct(prepared[0])
                        && prepared[0].Get("amount") == "123.45" && prepared[0].Get("notes") == "Импортируемое примечание", "external table import changed preview state, scope or financial fields");
                    Invoke(dialog, "ImportClick", null, EventArgs.Empty);
                    Assert(dialog.ImportedCount == 1, "external table import did not commit prepared records");
                }
            }
            using (var legacy = new ImportDialog(store, Catalog.Get(ExternalSection), new List<Dictionary<string, string>> { ImportRow(party, "ВНЕ-БЕЗ-МЕТКИ", null) }, "BYN", true))
                Assert(Store.IsExternalAct(legacy.PrepareImport().Single()), "import without scope failed to inherit the external destination");
            byte[] original = File.ReadAllBytes(store.DataPath); long beforeRejected = store.Revision;
            foreach (bool destinationExternal in new[] { false, true })
            {
                string marker = destinationExternal ? "standard" : "external";
                using (var mismatch = new ImportDialog(store, Catalog.Get(destinationExternal ? ExternalSection : "act"),
                    new List<Dictionary<string, string>> { ImportRow(party, "КОНФЛИКТ-" + marker, marker) }, "BYN", destinationExternal))
                {
                    bool rejected = false;
                    try { mismatch.PrepareImport(); } catch (InvalidOperationException) { rejected = true; }
                    Assert(rejected, "import silently reassigned an explicit scope to the wrong accounting section");
                }
            }
            Assert(store.Revision == beforeRejected && original.SequenceEqual(File.ReadAllBytes(store.DataPath)), "rejected scope import partially changed accounting data");
            string archive = Path.Combine(path, "external-backup.zip"), restoredPath = Path.Combine(path, "restored");
            BackupArchive.Create(store.DataDir, archive);
            BackupArchive.Restore(archive, restoredPath, delegate(string staged) {
                var restored = new Store(staged); Assert(restored.Of("act").Count() == 2 && restored.Of("act").All(Store.IsExternalAct), "staged backup lost external act scope");
            });
            Store reloaded = new Store(restoredPath);
            Assert(reloaded.Of("act").Count() == 2 && reloaded.Of("act").All(Store.IsExternalAct) && !reloaded.OfTaxActs().Any(), "backup restoration changed external acts into taxable ordinary acts");
        }
        public static void Run(string root)
        {
            string path = Path.Combine(root, "ext-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(path);
            TestFieldsAndVisibility(Path.Combine(path, "v"));
            TestCreationEditingAndDocuments(Path.Combine(path, "d"));
            TestReportsAndHiddenAccounting(Path.Combine(path, "r"));
            TestImportAndBackup(Path.Combine(path, "i"));
            TestSectionExports(Path.Combine(path, "x"));
            File.WriteAllText(Path.Combine(path, "PASS.txt"), "PASS: external act field parity, personal visibility/restart without ledger mutation, creation and edit routing, independent unfinished drafts, Word/HTML/XLSX works and immutable requisites, common accounting and explicit tax exclusion, actual section CSV/XLSX export/import preserving scope and reference IDs, conflicting scope rejection, report export column alignment and backup restoration.", Encoding.UTF8);
        }
    }
}
