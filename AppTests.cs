using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
namespace WorkLedger
{
    public static class AppTests
    {
        private static void Assert(bool condition, string reason) { if (!condition) throw new Exception("App test failed: " + reason); }
        private static Record Add(Store store, string kind, params string[] values)
        {
            var r = new Record { Kind = kind };
            for (int i = 0; i < values.Length; i += 2) r.Fields[values[i]] = values[i + 1];
            store.Upsert(r); return r;
        }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls) { yield return child; foreach (var sub in Descendants(child)) yield return sub; }
        }
        private static void TestOrganizationPrinting(string path)
        {
            Assert(PrintReport.OrganizationDetails(null) == "", "missing organization has no printed details");
            var blank = new Record("settings");
            blank.Set("name", "  "); blank.Set("unp", "\t"); blank.Set("actualAddress", "\r\n");
            blank.Set("currency", "BYN"); blank.Set("privateUnknownField", "HIDDEN");
            Assert(PrintReport.OrganizationDetails(blank) == "", "blank, currency and unknown organization fields are not printed");
            var blankParty = blank.Clone(); blankParty.Kind = "party";
            Assert(PrintReport.OrganizationDetails(blankParty) == "", "blank counterparty fields are not printed");

            var legacy = new Record("settings");
            legacy.Set("name", "ИП Иванов");
            legacy.Set("requisites", "Старые реквизиты\nВторая строка");
            legacy.Set("contact", "Старый контакт");
            string legacyDetails = PrintReport.OrganizationDetails(legacy);
            Assert(legacyDetails.Contains("ИП Иванов") && legacyDetails.Contains("Старые реквизиты\nВторая строка") && legacyDetails.Contains("Дополнительные контакты: Старый контакт"), "legacy organization requisites and contacts stay printable");
            Assert(!legacyDetails.Contains("Дата регистрации:") && !legacyDetails.Contains("УНП:"), "optional organization data does not add empty labels");
            var legacyParty = legacy.Clone(); legacyParty.Kind = "party";
            string legacyPartyDetails = PrintReport.OrganizationDetails(legacyParty);
            Assert(legacyPartyDetails.Contains("Дополнительные реквизиты: Старые реквизиты\nВторая строка") && legacyPartyDetails.Contains("Контактное лицо: Старый контакт"), "legacy counterparty requisites and contact person stay printable");

            var own = new Record("settings");
            var values = new Dictionary<string, string>
            {
                { "name", "ИП <Иванов> & \"Пётр\" 'Петрович'" }, { "unp", "123456789" },
                { "registrationNumber", "Р-007" }, { "registrationDate", "2026-01-09" },
                { "registrationAuthority", "Исполком <script>alert('test')</script>" },
                { "registrationAddress", "Минск, адрес регистрации" }, { "postalAddress", "Минск, почта\nИндекс 220000" },
                { "actualAddress", "Минск, мастерская\n<офис 7>" }, { "phone", "+375 29 123 45 67" }, { "email", "ip@example.test" },
                { "website", "https://example.test/?a=<script>&b=\"7\"" },
                { "chiefAccountantName", "Главбух <Петрова> & партнёр" }, { "chiefAccountantPhone", "+375 17 000 00 00 <доб.1>" },
                { "bankName", "Банк & партнёры" }, { "bankAccount", "BY00TEST00000000000000000000" }, { "bankCode", "TESTBY2X" },
                { "signatoryName", "Иванов Пётр Петрович" }, { "signatoryPosition", "Индивидуальный предприниматель" },
                { "signatoryBasis", "Свидетельство № <7>" }, { "vatNote", "Без НДС\nОснование & примечание" },
                { "requisites", "Старые реквизиты <сохранены>\nВторая строка" }, { "contact", "Старый контакт & информация" }
            };
            foreach (var item in values) own.Set(item.Key, item.Value);
            own.Set("currency", "HIDDEN-CURRENCY"); own.Set("privateUnknownField", "HIDDEN-UNKNOWN");
            string details = PrintReport.OrganizationDetails(own);
            var party = own.Clone(); party.Kind = "party";
            string partyDetails = PrintReport.OrganizationDetails(party);
            foreach (string text in new[] { details, partyDetails })
            {
                Assert(text.Contains("УНП: 123456789") && text.Contains("Почтовый адрес: Минск, почта\nИндекс 220000") && text.Contains("Фактический адрес: Минск, мастерская\n<офис 7>"), "own and counterparty UNP and addresses have labels");
                Assert(text.Contains("Дата регистрации: 09.01.2026") && !text.Contains("2026-01-09"), "registration date prints in local day-month-year format");
                Assert(text.Contains("Сайт: " + values["website"]) && text.Contains("Главный бухгалтер: " + values["chiefAccountantName"]) && text.Contains("Телефон главного бухгалтера: " + values["chiefAccountantPhone"]), "website and accountant print with labels");
                Assert(!text.Contains("HIDDEN"), "organization printing ignores currency and unrelated fields");
            }
            Assert(details.Contains("Адрес регистрации: ") && details.Contains("Дополнительные контакты: ") && !details.Contains("Контактное лицо:"), "own organization uses own address and legacy contact labels");
            Assert(partyDetails.Contains("Адрес регистрации / юридический адрес: ") && partyDetails.Contains("Контактное лицо: ") && !partyDetails.Contains("Дополнительные контакты:"), "counterparty uses legal address and contact-person labels");

            string document = Path.Combine(path, "organization-document.html");
            const string footer = "Подписант <ИП>\nВторая строка & подпись";
            PrintReport.WriteDocument(document, "Акт № <1>", new Dictionary<string, string> { { "Организация", details }, { "Реквизиты контрагента", partyDetails } }, new List<string>(), new List<List<string>>(), footer);
            string content = File.ReadAllText(document);
            foreach (var item in values)
                Assert(content.Contains(PrintReport.Escape(item.Key == "registrationDate" ? "09.01.2026" : item.Value)), "printed organization contains escaped " + item.Key);
            Assert(content.Contains("<b>Организация:</b>") && content.Contains("<b>Реквизиты контрагента:</b>"), "document prints separate own and counterparty blocks");
            Assert(!content.Contains("<script>") && !content.Contains("<офис 7>") && !content.Contains("<Петрова>") && !content.Contains("<доб.1>") && !content.Contains("HIDDEN"), "organization website, accountant and other fields cannot inject HTML or leak unrelated settings");
            Assert(content.Contains(".details div{overflow-wrap:anywhere;white-space:pre-wrap}") && content.Contains("Минск, почта\nИндекс 220000"), "organization multiline addresses keep line breaks in HTML");
            Assert(content.Contains(".footer{white-space:pre-wrap;") && content.Contains("Подписант &lt;ИП&gt;\nВторая строка &amp; подпись"), "multiline document footer stays escaped and preserves line breaks");
        }
        private static void TestDocumentOrganizationIntegration(MainForm form, Store store, string customerId, string path)
        {
            var own = store.Of("settings").Single();
            own.Set("unp", "123456789"); own.Set("postalAddress", "Почта исполнителя\nМинск");
            own.Set("actualAddress", "Мастерская исполнителя <офис>");
            own.Set("website", "https://own.example.test/?a=1&b=2");
            own.Set("chiefAccountantName", "Бухгалтер исполнителя <Иванова>");
            own.Set("chiefAccountantPhone", "+375 17 123 45 67"); own.Set("contact", "Контакт исполнителя");
            store.Upsert(own);
            var party = store.Find(customerId);
            party.Set("unp", "987654321"); party.Set("registrationAddress", "Юридический адрес заказчика");
            party.Set("postalAddress", "Почта заказчика\nБрест"); party.Set("actualAddress", "Склад заказчика <вход>");
            party.Set("website", "https://client.example.test/?c=3&d=4");
            party.Set("chiefAccountantName", "Бухгалтер заказчика <Петрова>");
            party.Set("chiefAccountantPhone", "+375 17 765 43 21"); party.Set("contact", "Контакт заказчика");
            store.Upsert(party);
            foreach (string kind in new[] { "contract", "act", "invoice", "estimate" })
            {
                var record = store.Of(kind).First(r => r.Get("partyId") == customerId);
                var details = form.BuildDocumentDetails(record);
                Assert(details.ContainsKey("Организация") && details["Организация"] == PrintReport.OrganizationDetails(own), kind + " uses complete own organization details");
                Assert(details.ContainsKey("Реквизиты контрагента") && details["Реквизиты контрагента"] == PrintReport.OrganizationDetails(party), kind + " uses complete counterparty details");
                string document = Path.Combine(path, "organization-integration-" + kind + ".html");
                PrintReport.WriteDocument(document, Catalog.Get(kind).Singular, details, new List<string>(), new List<List<string>>(), "");
                string content = File.ReadAllText(document);
                Assert(content.Contains("УНП: 123456789") && content.Contains("УНП: 987654321") && content.Contains("Почта исполнителя\nМинск") && content.Contains("Почта заказчика\nБрест"), kind + " HTML includes both organizations and multiline addresses");
                Assert(content.Contains("Бухгалтер исполнителя &lt;Иванова&gt;") && content.Contains("Бухгалтер заказчика &lt;Петрова&gt;") && content.Contains("a=1&amp;b=2") && content.Contains("c=3&amp;d=4"), kind + " HTML escapes website and accountant details for both organizations");
                Assert(content.Contains("Дополнительные контакты: Контакт исполнителя") && content.Contains("Контактное лицо: Контакт заказчика"), kind + " HTML uses correct contact labels");
            }
        }

        public static void Run(string path)
        {
            path = Path.Combine(path, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            var store = new Store(Path.Combine(path, "data"));
            Add(store, "settings", "name", "Демонстрационная организация", "currency", "BYN", "requisites", "Тестовые реквизиты");
            var customer = Add(store, "party", "name", "Заказчик «Север»", "role", "client", "requisites", "Тест");
            var vendor = Add(store, "party", "name", "Подрядчик «Монтаж»", "role", "vendor");
            var project = Add(store, "project", "name", "Ремонт офиса", "partyId", customer.Id, "location", "Минск", "status", "active");
            const string date = "2026-10-11";
            var contract = Add(store, "contract", "number", "Д-001", "date", date, "partyId", customer.Id, "projectId", project.Id, "direction", "income", "currency", "BYN", "amount", "2000", "status", "active");
            var vc = Add(store, "contract", "number", "П-001", "date", date, "partyId", vendor.Id, "projectId", project.Id, "direction", "expense", "currency", "BYN", "amount", "500", "status", "active");
            Add(store, "amendment", "number", "ДС-1", "date", date, "contractId", contract.Id, "amount", "250", "status", "signed");
            var estimate = Add(store, "estimate", "number", "СМ-1", "date", date, "partyId", customer.Id, "projectId", project.Id, "contractId", contract.Id, "currency", "BYN", "direction", "income", "status", "approved");
            Add(store, "work", "estimateId", estimate.Id, "description", "Отделочные работы", "unit", "м²", "qty", "2.5", "price", "400");
            var act = Add(store, "act", "number", "А-001", "date", date, "dueDate", "2026-10-10", "partyId", customer.Id, "projectId", project.Id, "contractId", contract.Id, "direction", "income", "currency", "BYN", "amount", "1200", "status", "signed", "description", "Отделочные работы");
            var va = Add(store, "act", "number", "ПА-001", "date", date, "partyId", vendor.Id, "projectId", project.Id, "contractId", vc.Id, "direction", "expense", "currency", "BYN", "amount", "300", "status", "signed");
            Add(store, "invoice", "number", "СЧ-001", "date", date, "partyId", customer.Id, "projectId", project.Id, "contractId", contract.Id, "actId", act.Id, "direction", "income", "currency", "BYN", "amount", "1200", "status", "sent");
            var payment = Add(store, "payment", "number", "ПП-001", "date", date, "partyId", customer.Id, "projectId", project.Id, "contractId", contract.Id, "direction", "income", "currency", "BYN", "amount", "900", "method", "bank");
            var vp = Add(store, "payment", "number", "ПП-002", "date", date, "partyId", vendor.Id, "projectId", project.Id, "contractId", vc.Id, "direction", "expense", "currency", "BYN", "amount", "200", "method", "bank");
            store.Allocate(payment.Id, act.Id, 900); store.Allocate(vp.Id, va.Id, 200);
            Add(store, "hold", "actId", act.Id, "date", date, "amount", "100", "status", "active", "type", "warranty");
            Add(store, "expense", "date", date, "partyId", vendor.Id, "projectId", project.Id, "currency", "BYN", "amount", "100", "category", "materials", "description", "Материалы");
            string attachment = Path.Combine(path, "акт.txt"); File.WriteAllText(attachment, "Проверка вложения", Encoding.UTF8);
            Add(store, "document", "title", "Скан акта", "date", date, "partyId", customer.Id, "projectId", project.Id, "contractId", contract.Id, "actId", act.Id, "status", "signed", "filePath", store.ImportAttachment(attachment));
            Assert(store.ActDue(act.Id) == 200, "available debt = amount - payments - holds");
            Assert(store.EffectiveContractAmount(contract.Id) == 2250, "contract changes");
            Assert(store.EstimateTotal(estimate.Id) == 1000, "estimate total");

            var headers = new List<string> { "Контрагент", "Номер", "Описание", "Сумма", "Дата" };
            var rows = new List<List<string>> { new List<string> { "Север; «А»", "0007", "Строка 1\nСтрока \"2\"", "12345.60", "11.10.2026" } };
            string csv = Path.Combine(path, "export.csv"), xlsx = Path.Combine(path, "export.xlsx");
            TableIO.WriteCsv(csv, headers, rows); TableIO.WriteXlsx(xlsx, headers, rows);
            foreach (var file in new[] { csv, xlsx })
            {
                var read = TableIO.Read(file);
                Assert(read.Count == 1 && read[0]["Контрагент"] == rows[0][0] && read[0]["Номер"] == "0007" && read[0]["Описание"] == rows[0][2], "CSV/XLSX text roundtrip");
                Assert(Store.ParseMoney(read[0]["Сумма"]) == 12345.60m, "numeric XLSX value");
            }
            using (var zip = ZipFile.OpenRead(xlsx)) Assert(zip.GetEntry("xl/worksheets/sheet1.xml") != null, "genuine XLSX package");
            string html = Path.Combine(path, "report.html");
            PrintReport.Write(html, "<Тест>", headers, rows, "Текст & реквизиты");
            Assert(File.ReadAllText(html).Contains("&lt;Тест&gt;"), "HTML text escaping");
            TestOrganizationPrinting(path);
            string backup = Path.Combine(path, "backup.zip");
            BackupArchive.Create(store.DataDir, backup);
            var extra = Add(store, "party", "name", "После копирования", "role", "both");
            BackupArchive.Restore(backup, store.DataDir, MainForm.ValidateSnapshot);
            store = new Store(store.DataDir);
            Assert(store.Find(extra.Id) == null && store.Find(act.Id) != null && store.ActDue(act.Id) == 200, "backup restores financial state");
            Assert(File.ReadAllText(store.ResolveAttachment(store.Of("document").Single().Get("filePath"))).Contains("Проверка"), "backup restores attachments");
            string badZip = Path.Combine(path, "bad.zip");
            using (var zip = ZipFile.Open(badZip, ZipArchiveMode.Create)) { var e = zip.CreateEntry("../escape.txt"); using (var w = new StreamWriter(e.Open())) w.Write("bad"); }
            string unchanged = File.ReadAllText(store.DataPath); bool rejected = false;
            try { BackupArchive.Restore(badZip, store.DataDir, MainForm.ValidateSnapshot); } catch { rejected = true; }
            Assert(rejected && File.ReadAllText(store.DataPath) == unchanged && !File.Exists(Path.Combine(path, "escape.txt")), "invalid backup preserves live data");

            string incompleteZip = Path.Combine(path, "missing-attachment.zip");
            using (var zip = ZipFile.Open(incompleteZip, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("ledger.json"); using (var target = e.Open()) using (var source = File.OpenRead(store.DataPath)) source.CopyTo(target);
            }
            rejected = false; try { BackupArchive.Restore(incompleteZip, store.DataDir, MainForm.ValidateSnapshot); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected && File.ReadAllText(store.DataPath) == unchanged, "missing attachment rejects restore before replacing live database");

            using (var form = new MainForm(store))
            {
                TestDocumentOrganizationIntegration(form, store, customer.Id, path);
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
                form.Navigate("party"); Application.DoEvents();
                var partyTable = (TableData)typeof(MainForm).GetField("table", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                Assert(partyTable.Headers.Count == 7 && partyTable.Headers.Contains("УНП") && partyTable.Headers.Last() == "В архиве", "counterparty grid keeps six useful fields plus archive status");
                foreach (string key in new[] { "name", "role", "phone", "email", "bankAccount" })
                    Assert(partyTable.Headers.Contains(Catalog.Get("party").Fields.Single(f => f.Key == key).Label), "counterparty grid preserves " + key);
                var customerRow = partyTable.Rows[partyTable.Ids.IndexOf(customer.Id)];
                Assert(customerRow.Count == 7 && customerRow[partyTable.Headers.IndexOf("УНП")] == "987654321" && customerRow.Contains(customer.Get("name")), "counterparty grid includes correct UNP and existing name");
                Assert(!partyTable.Headers.Contains("Сайт") && !partyTable.Headers.Contains("Главный бухгалтер") && !partyTable.Headers.Contains("Почтовый адрес"), "counterparty details stay in editor instead of widening the grid");
                foreach (var schema in Catalog.All.Where(s => s.Kind != "settings")) { form.Navigate(schema.Kind); Application.DoEvents(); }
                for (int i = 0; i < 6; i++) { form.SelectReport(i); Assert(form.BuildReport().Headers.Count > 0, "report renders " + i); }
                form.SelectReport(1); var profit = form.BuildReport().Rows.First(r => r[0] == "Ремонт офиса");
                Assert(Store.ParseMoney(profit[5].Replace("\u00a0", "").Replace(" ", "").Replace("\u202f", "")) == 800, "profit includes supplier works and additional costs");
                form.SelectReport(2); var closing = form.BuildReport().Rows.Last();
                Assert(Store.ParseMoney(closing.Last().Replace("\u00a0", "").Replace(" ", "").Replace("\u202f", "")) == 200, "reconciliation includes actual payments and both directions");
                // Changing screens must not leak inactive financial filters into the dashboard.
                var dirFilter = (ComboBox)typeof(MainForm).GetField("direction", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                var cpFilter = (ComboBox)typeof(MainForm).GetField("party", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                form.Navigate("payment"); dirFilter.SelectedIndex = 2;
                cpFilter.SelectedIndex = 1; form.Navigate("overview"); Application.DoEvents();
                var incomeLabel = Descendants(form).OfType<Label>().Single(l => l.Text == "Работы для заказчиков");
                Assert(incomeLabel.Parent.Controls.OfType<Label>().Any(l => l.Text == "1\u00a0200,00 BYN" || l.Text.Replace("\u00a0", "").Replace(" ", "") == "1200,00BYN"), "dashboard includes income after outgoing/counterparty filters");
                var profitLabel = Descendants(form).OfType<Label>().Single(l => l.Text == "Прибыль по работам");
                Assert(profitLabel.Parent.Controls.OfType<Label>().Any(l => l.Text.Replace("\u00a0", "").Replace(" ", "") == "800,00BYN"), "dashboard includes supplier costs after customer filter");
                cpFilter.SelectedIndex = 0; dirFilter.SelectedIndex = 0;
                // Optional project link is inherited from a contract across acts and payments.
                var linkedProject = Add(store, "project", "name", "Объект через договор", "partyId", customer.Id, "status", "active");
                var linkedContract = Add(store, "contract", "number", "Д-LINK", "date", date, "partyId", customer.Id, "projectId", linkedProject.Id, "direction", "income", "currency", "BYN", "amount", "500", "status", "active");
                Add(store, "act", "number", "А-LINK", "date", date, "partyId", customer.Id, "contractId", linkedContract.Id, "direction", "income", "currency", "BYN", "amount", "100", "status", "signed");
                Add(store, "payment", "date", date, "partyId", customer.Id, "contractId", linkedContract.Id, "direction", "income", "currency", "BYN", "amount", "20", "method", "bank");
                form.SelectReport(1); var linkedRow = form.BuildReport().Rows.Single(r => r[0] == "Объект через договор");
                Assert(Store.ParseMoney(linkedRow[1]) == 100 && Store.ParseMoney(linkedRow[6]) == 20, "contract inherits object for profit and cash reports");
                var start = (DateTimePicker)typeof(MainForm).GetField("from", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                var end = (DateTimePicker)typeof(MainForm).GetField("to", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                start.Value = new DateTime(2026, 12, 1); start.Checked = true; end.Value = new DateTime(2026, 1, 1); end.Checked = true;
                bool badRange = false; try { form.BuildReport(); } catch (InvalidOperationException) { badRange = true; } Assert(badRange, "invalid report interval rejected");
                start.Checked = end.Checked = false;
                form.Navigate("audit"); Application.DoEvents(); form.Navigate("overview"); Application.DoEvents();
                using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height)); image.Save(Path.Combine(path, "overview.png")); }
                foreach (var schema in Catalog.All)
                {
                    using (var editor = new RecordEditor(store, schema, store.Of(schema.Kind).FirstOrDefault()))
                    { editor.ShowInTaskbar = false; editor.Opacity = 0; editor.Show(form); Application.DoEvents(); Assert(editor.Controls.Count > 0, "editor renders " + schema.Kind); if (schema.Kind == "act") using (var image = new Bitmap(editor.Width, editor.Height)) { editor.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height)); image.Save(Path.Combine(path, "act-editor.png")); } editor.Close(); }
                }
                using (var dialog = new AllocationDialog(store, store.Find(act.Id))) { dialog.ShowInTaskbar = false; dialog.Opacity = 0; dialog.Show(form); Application.DoEvents(); dialog.Close(); }
                form.Close();
            }
            File.WriteAllText(Path.Combine(path, "PASS.txt"), "PASS: complete linked workflow, financial reports, CSV/XLSX, backup/restore, invalid zip, every screen/editor.", Encoding.UTF8);
        }
    }
}

