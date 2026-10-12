using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class EditorTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Editor test: " + message);
        }
        private static Dictionary<string, Control> Inputs(RecordEditor editor)
        {
            return (Dictionary<string, Control>)typeof(RecordEditor).GetField("inputs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
        }
        private static string ChoiceValue(ComboBox combo)
        {
            object selected = combo.SelectedItem;
            return selected == null ? "" : (string)selected.GetType().GetField("Value").GetValue(selected);
        }
        private static void InvokeAction(Form form, string method)
        {
            try { form.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { null, EventArgs.Empty }); }
            catch (TargetInvocationException exception) { throw new InvalidOperationException("Editor action failed: " + method, exception.InnerException); }
        }
        private static void SelectDefault(ImportDialog dialog, string key, string value)
        {
            Dictionary<string, ComboBox> mapping = (Dictionary<string, ComboBox>)typeof(ImportDialog).GetField("mappings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
            ComboBox combo = mapping[key];
            for (int i = 0; i < combo.Items.Count; ++i)
            {
                object item = combo.Items[i];
                if ((string)item.GetType().GetField("Constant").GetValue(item) == value) { combo.SelectedIndex = i; return; }
            }
            throw new InvalidOperationException("Import default option not found: " + key + "=" + value);
        }
        private static Record Save(Store store, string kind, params string[] pairs)
        {
            Record record = new Record(kind);
            for (int i = 0; i < pairs.Length; i += 2) record.Set(pairs[i], pairs[i + 1]);
            store.Upsert(record); return record;
        }
        private static Dictionary<string, string> ActRow(string number, string party, string project, string contract)
        {
            return new Dictionary<string, string>
            {
                { "Номер", number }, { "Дата", "46000" }, { "Контрагент", party },
                { "Объект", project }, { "Договор", contract }, { "Сумма", "12\u00A0345,67" },
                { "Статус", "Подписан" }, { "Примечание", "Тест импорта" },
                { "archived", "true" }, { "Id", "source-must-not-replace" }
            };
        }
        private static void AssertNoChange(Store store, long revision, byte[] bytes, int count, string scenario)
        {
            Assert(store.Revision == revision && store.Records.Count == count && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), scenario + " partially changed the database");
        }

        public static void Run(string tempdir)
        {
            string dataDir = Path.Combine(tempdir, "editor-" + Guid.NewGuid().ToString("N"));
            Store store = new Store(dataDir);
            Record organization = Save(store, "settings", "name", "ИП Тестовый", "currency", "BYN",
                "requisites", "Старые реквизиты", "contact", "Старые контакты", "customField", "preserve-organization");
            long beforeSettings = store.Revision;
            Dictionary<string, string> organizationValues = new Dictionary<string, string>
            {
                { "unp", "001234567" }, { "registrationNumber", "СВ-001" }, { "registrationAuthority", "Тестовый орган" },
                { "registrationAddress", "Адрес регистрации\nквартира 1" }, { "postalAddress", "Почтовый адрес\nиндекс 123456" },
                { "actualAddress", "Фактический адрес\nофис 2" }, { "bankName", "Тестовый банк" },
                { "bankAccount", "BY00 TEST 0000 0000 0000 0000 0000" }, { "bankCode", "TESTBY00" },
                { "phone", "+375 29 000-00-00" }, { "email", "test@example.com" }, { "website", "https://example.com" },
                { "chiefAccountantName", "Бухгалтер Тестовый" }, { "chiefAccountantPhone", "+375 17 000-00-01" },
                { "signatoryName", "Тестовый Подписант" }, { "signatoryPosition", "Индивидуальный предприниматель" },
                { "signatoryBasis", "Тестовое свидетельство" }, { "vatNote", "Тестовые условия НДС" }
            };
            using (RecordEditor editor = new RecordEditor(store, Catalog.Get("settings"), store.Find(organization.Id)))
            {
                Dictionary<string, Control> inputs = Inputs(editor);
                TabControl tabs = editor.Controls.OfType<TabControl>().Single();
                Assert(tabs.TabCount == 6 && tabs.TabPages.Cast<TabPage>().Any(t => t.Text == "Создание актов"), "organization sections missing");
                Assert(!((DateTimePicker)inputs["registrationDate"]).Checked, "registration date selected for legacy organization");
                foreach (var pair in organizationValues) inputs[pair.Key].Text = pair.Value;
                DateTimePicker registrationDate = (DateTimePicker)inputs["registrationDate"];
                registrationDate.Value = new DateTime(2020, 4, 15); registrationDate.Checked = true;
                Assert(store.Revision == beforeSettings, "organization editor wrote data before save");
                tabs.SelectedIndex = tabs.TabCount - 1;
                ((ComboBox)inputs["currency"]).SelectedIndex = -1;
                InvokeAction(editor, "SaveClick");
                Assert(editor.ResultRecord == null && store.Revision == beforeSettings && tabs.SelectedTab.Text == "Основные данные",
                    "hidden invalid field did not focus its tab or changed data");
                ((ComboBox)inputs["currency"]).SelectedIndex = 0;
                tabs.SelectedIndex = tabs.TabCount - 1;
                InvokeAction(editor, "SaveClick");
                Assert(editor.DialogResult == DialogResult.OK && editor.ResultRecord != null, "organization editor save failed");
            }
            Store loadedOrganization = new Store(dataDir);
            Record savedOrganization = loadedOrganization.Find(organization.Id);
            foreach (var pair in organizationValues) Assert(savedOrganization.Get(pair.Key) == pair.Value, "organization field lost: " + pair.Key);
            Assert(savedOrganization.Get("registrationDate") == "2020-04-15" && savedOrganization.Get("currency") == "BYN", "organization date or currency changed");
            Assert(savedOrganization.Get("requisites") == "Старые реквизиты" && savedOrganization.Get("contact") == "Старые контакты"
                && savedOrganization.Get("customField") == "preserve-organization", "organization editor lost legacy fields");
            long beforeCancel = store.Revision;
            using (RecordEditor editor = new RecordEditor(store, Catalog.Get("settings"), savedOrganization))
            {
                Dictionary<string, Control> inputs = Inputs(editor);
                Assert(((DateTimePicker)inputs["registrationDate"]).Checked, "saved registration date was not restored");
                Assert(inputs["postalAddress"].Text == organizationValues["postalAddress"], "saved multiline address was not restored");
                inputs["unp"].Text = "unsaved-change";
            }
            Assert(store.Revision == beforeCancel && store.Find(organization.Id).Get("unp") == "001234567", "canceling organization edit saved data");
            using (RecordEditor editor = new RecordEditor(store, Catalog.Get("settings"), null))
            {
                Assert(!((DateTimePicker)Inputs(editor)["registrationDate"]).Checked && Inputs(editor)["unp"].Text == "", "new optional organization fields have invented values");
            }
            Record party = Save(store, "party", "name", "Заказчик теста", "role", "both");
            using (RecordEditor editor = new RecordEditor(store, Catalog.Get("party"), store.Find(party.Id)))
            {
                Dictionary<string, Control> inputs = Inputs(editor);
                TabControl tabs = editor.Controls.OfType<TabControl>().Single();
                Assert(tabs.TabCount == 5 && inputs["unp"].Text == "", "legacy counterparty sections or defaults changed");
                foreach (var pair in organizationValues) if (inputs.ContainsKey(pair.Key)) inputs[pair.Key].Text = pair.Value;
                inputs["contact"].Text = "Контактное лицо теста";
                tabs.SelectedIndex = tabs.TabCount - 1;
                InvokeAction(editor, "SaveClick");
                Assert(editor.ResultRecord != null && editor.DialogResult == DialogResult.OK, "counterparty editor save failed");
            }
            Record savedParty = new Store(dataDir).Find(party.Id);
            foreach (var pair in organizationValues) if (Catalog.Get("party").Fields.Any(f => f.Key == pair.Key))
                Assert(savedParty.Get(pair.Key) == pair.Value, "counterparty field lost: " + pair.Key);
            Assert(savedParty.Get("name") == "Заказчик теста" && savedParty.Get("role") == "both"
                && savedParty.Get("contact") == "Контактное лицо теста", "counterparty editor lost original data");
            string legacyPartyCsv = Path.Combine(dataDir, "legacy-party.csv");
            TableIO.WriteCsv(legacyPartyCsv,
                new List<string> { "Название / ФИО", "Роль", "Реквизиты", "Контактное лицо", "УНП", "Расчётный счёт (IBAN)", "Почтовый адрес", "Главный бухгалтер", "Сайт" },
                new List<List<string>> { new List<string> { "Импорт реквизитов", "Заказчик", "Старые реквизиты из CSV", "Контакт из CSV",
                    "001234567", "BY00 TEST 0000 0000 0000 0000 0000", "Адрес из CSV\nофис 3", "Бухгалтер из CSV", "https://example.com/csv" } });
            using (ImportDialog dialog = new ImportDialog(store, Catalog.Get("party"), TableIO.Read(legacyPartyCsv), "BYN"))
            {
                List<Record> prepared = dialog.PrepareImport();
                Assert(prepared[0].Get("requisites") == "Старые реквизиты из CSV" && prepared[0].Get("contact") == "Контакт из CSV",
                    "old counterparty CSV headers lost requisites or contact");
                InvokeAction(dialog, "ImportClick");
                Assert(dialog.ImportedCount == 1, "counterparty CSV import failed");
            }
            Record csvParty = new Store(dataDir).Of("party").Single(r => r.Get("name") == "Импорт реквизитов");
            Assert(csvParty.Get("unp") == "001234567" && csvParty.Get("postalAddress") == "Адрес из CSV\nофис 3"
                && csvParty.Get("bankAccount") == "BY00 TEST 0000 0000 0000 0000 0000"
                && csvParty.Get("chiefAccountantName") == "Бухгалтер из CSV" && csvParty.Get("website") == "https://example.com/csv",
                "counterparty CSV lost new requisites");
            Record otherParty = Save(store, "party", "name", "Другой заказчик", "role", "both");
            Record project = Save(store, "project", "name", "Тестовый объект", "partyId", party.Id, "status", "active");
            Record contract = Save(store, "contract", "number", "Д-001", "date", "2026-10-11", "partyId", party.Id,
                "projectId", project.Id, "direction", "income", "currency", "BYN", "amount", "20000.00", "status", "active");
            Record act = Save(store, "act", "number", "А-001", "date", "2026-10-11", "partyId", party.Id,
                "projectId", project.Id, "contractId", contract.Id, "direction", "income", "currency", "BYN", "amount", "5000.00",
                "status", "signed", "notes", "Старое примечание", "customField", "preserve-me");
            long beforeEdit = store.Revision;
            using (RecordEditor editor = new RecordEditor(store, Catalog.Get("act"), store.Find(act.Id)))
            {
                Dictionary<string, Control> inputs = Inputs(editor);
                Assert(!((DateTimePicker)inputs["dueDate"]).Checked, "optional date was selected automatically");
                inputs["notes"].Text = "Новое примечание";
                Assert(store.Revision == beforeEdit && store.Find(act.Id).Get("notes") == "Старое примечание", "editor changed data before save");
                InvokeAction(editor, "SaveClick");
                Assert(editor.ResultRecord != null && editor.DialogResult == DialogResult.OK, "valid editor save failed");
                Assert(store.Find(act.Id).Get("customField") == "preserve-me" && store.Find(act.Id).Get("notes") == "Новое примечание", "editor discarded unknown or memo fields");
            }
            Record invoiceSeed = new Record("invoice"); invoiceSeed.Set("actId", act.Id);
            using (RecordEditor editor = new RecordEditor(store, Catalog.Get("invoice"), invoiceSeed))
            {
                Dictionary<string, Control> inputs = Inputs(editor);
                Assert(ChoiceValue((ComboBox)inputs["partyId"]) == party.Id && ChoiceValue((ComboBox)inputs["contractId"]) == contract.Id
                    && ChoiceValue((ComboBox)inputs["projectId"]) == project.Id && ChoiceValue((ComboBox)inputs["currency"]) == "BYN", "new linked card autofill failed");
            }
            Record estimate = Save(store, "estimate", "number", "С-001", "date", "2026-10-11", "partyId", party.Id, "currency", "BYN", "direction", "income", "status", "approved");
            Record work = Save(store, "work", "estimateId", estimate.Id, "description", "Работа", "qty", "1.234567", "price", "5.00");
            using (RecordEditor editor = new RecordEditor(store, Catalog.Get("work"), work))
            {
                Inputs(editor)["description"].Text = "Изменённое описание";
                InvokeAction(editor, "SaveClick");
                Assert(store.Find(work.Id).Get("qty") == "1.234567", "unrelated editor save rounded work quantity");
            }
            List<Dictionary<string, string>> validRows = new List<Dictionary<string, string>>
            {
                ActRow("И-001", party.Get("name"), Catalog.Display(store, project), Catalog.Display(store, contract))
            };
            using (ImportDialog dialog = new ImportDialog(store, Catalog.Get("act"), validRows, "BYN"))
            {
                long revision = store.Revision;
                List<Record> preview = dialog.PrepareImport();
                Assert(store.Revision == revision, "preparing import wrote data");
                Assert(preview[0].Get("amount") == "12345.67" && preview[0].Get("date") == DateTime.FromOADate(46000).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Excel date or NBSP money import failed");
                Assert(preview[0].Get("partyId") == party.Id && preview[0].Get("contractId") == contract.Id && preview[0].Get("status") == "signed", "Russian label or reference mapping failed");
                InvokeAction(dialog, "ImportClick");
                Assert(dialog.ImportedCount == 1 && dialog.DialogResult == DialogResult.OK, "valid import save action failed");
            }
            Record imported = store.Of("act").Single(r => r.Get("number") == "И-001");
            Assert(imported.Get("archived") != "true" && imported.Id != "source-must-not-replace" && imported.Get("notes") == "Тест импорта", "import used exported Id/archive status or lost notes");
            Dictionary<string, string> paymentRow = new Dictionary<string, string>
            {
                { "date", "11.10.2026" }, { "partyId", party.Id }, { "amount", "12\u202F345,67" }
            };
            using (ImportDialog dialog = new ImportDialog(store, Catalog.Get("payment"), new List<Dictionary<string, string>> { paymentRow }, "BYN"))
            {
                SelectDefault(dialog, "currency", "USD"); SelectDefault(dialog, "direction", "expense");
                List<Record> preview = dialog.PrepareImport();
                Assert(preview[0].Get("currency") == "USD" && preview[0].Get("direction") == "expense" && preview[0].Get("amount") == "12345.67", "editable defaults or narrow NBSP parsing failed");
                InvokeAction(dialog, "ImportClick");
                Assert(dialog.ImportedCount == 1 && store.Of("payment").Single().Get("currency") == "USD", "changed import defaults were not saved");
            }
            long checkpoint = store.Revision; byte[] checkpointBytes = File.ReadAllBytes(store.DataPath); int checkpointCount = store.Records.Count;
            List<Dictionary<string, string>> duplicateRows = new List<Dictionary<string, string>>
            {
                ActRow("И-002", party.Id, project.Id, contract.Id), ActRow("И-001", party.Id, project.Id, contract.Id)
            };
            using (ImportDialog dialog = new ImportDialog(store, Catalog.Get("act"), duplicateRows, "BYN"))
            {
                bool failed = false;
                try { dialog.PrepareImport(); } catch (InvalidOperationException exception) { failed = exception.Message.Contains("Строка 2"); }
                Assert(failed, "duplicate import did not identify the failing row");
            }
            AssertNoChange(store, checkpoint, checkpointBytes, checkpointCount, "duplicate import");
            List<Dictionary<string, string>> missingRows = new List<Dictionary<string, string>>
            {
                ActRow("И-003", party.Id, project.Id, contract.Id), ActRow("И-004", "Нет такого контрагента", project.Id, contract.Id)
            };
            using (ImportDialog dialog = new ImportDialog(store, Catalog.Get("act"), missingRows, "BYN"))
            {
                bool failed = false;
                try { dialog.PrepareImport(); } catch (InvalidOperationException exception) { failed = exception.Message.Contains("Строка 2") && exception.Message.Contains("Контрагент"); }
                Assert(failed, "missing reference import did not identify row and field");
            }
            AssertNoChange(store, checkpoint, checkpointBytes, checkpointCount, "missing reference import");
            List<Dictionary<string, string>> conflictRows = new List<Dictionary<string, string>>
            {
                ActRow("И-005", party.Id, project.Id, contract.Id), ActRow("И-006", otherParty.Id, project.Id, contract.Id)
            };
            using (ImportDialog dialog = new ImportDialog(store, Catalog.Get("act"), conflictRows, "BYN"))
            {
                List<Record> prepared = dialog.PrepareImport();
                bool failed = false;
                try { store.ImportBatch(prepared); } catch (InvalidOperationException) { failed = true; }
                Assert(failed, "inconsistent related records were accepted");
            }
            AssertNoChange(store, checkpoint, checkpointBytes, checkpointCount, "atomic invalid batch");
            Record ambiguousParty = Save(store, "party", "name", party.Get("name"), "role", "both");
            Record ambiguousContract = Save(store, "contract", "number", contract.Get("number"), "date", "2026-10-11", "partyId", otherParty.Id,
                "direction", "income", "currency", "BYN", "amount", "2000.00", "status", "active");
            Dictionary<string, string> idRow = ActRow("И-007", party.Get("name"), project.Id, contract.Get("number"));
            idRow.Add("Контрагент (ID)", party.Id); idRow.Add("Договор (id)", contract.Id);
            idRow.Add("partyId", ambiguousParty.Id); idRow.Add("contractId", ambiguousContract.Id);
            idRow.Add("Идентификатор записи", "source-record-id");
            using (ImportDialog dialog = new ImportDialog(store, Catalog.Get("act"), new List<Dictionary<string, string>> { idRow }, "BYN"))
            {
                List<Record> prepared = dialog.PrepareImport();
                Assert(prepared[0].Get("partyId") == party.Id && prepared[0].Get("contractId") == contract.Id,
                    "reference Label (ID) did not take precedence over key and ambiguous human label");
                Assert(prepared[0].Id != "source-record-id", "source record identifier was reused");
            }
            Console.WriteLine("PASS: editor save/autofill/precision; import mapping/defaults/Excel dates/NBSP money/ref ID precedence; invalid rows and atomic rollback.");
        }
    }
}

