using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;

namespace WorkLedger
{
    public static class ActDocumentTests
    {
        public static void Run(string root)
        {
            string folder = Path.Combine(Path.GetFullPath(root), "act-document-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); int passed = 0;
            Action<string, Action> test = delegate(string name, Action run) { run(); passed++; Console.WriteLine("PASS Word act " + name); };
            var store = new Store(Path.Combine(folder, "data"));
            Record own = R("settings", "name", "Тестовый исполнитель & <Компания>", "currency", "BYN", "bankAccount", "TEST-EXEC-ACCOUNT", "bankName", "Тестовый банк исполнителя", "bankCode", "TESTEXEC", "unp", "TEST-EXEC-TAX", "registrationAddress", "Город Пример, улица Тестовая, дом 1", "phone", "+000 000 000", "requisites", "Дополнительные реквизиты исполнителя"); store.Upsert(own);
            Record party = R("party", "name", "Тестовый заказчик «Пример»", "role", "both", "bankAccount", "TEST-CUSTOMER-ACCOUNT", "bankName", "Тестовый банк заказчика", "bankCode", "TESTCUSTOMER", "unp", "TEST-CUSTOMER-TAX", "registrationAddress", "Город Пример, улица Образцовая, дом 2", "phone", "+000 111 111", "requisites", "Дополнительные реквизиты заказчика"); store.Upsert(party);
            var lines = new List<ActPrintLine> {
                L("Комплексное сопровождение учетной системы", 1, 500),
                L("Разработка функции регистрации заявок по техническому заданию заказчика", 1, 70),
                L("Доработка печатной формы документа передачи материалов в эксплуатацию", 1, 70),
                L("Обновление печатной формы внутреннего перемещения оборудования", 1, 70),
                L("Настройка печатной формы ведомости движения и выдачи материалов", 1, 70)
            };
            Record act = Act(party, "QA-500", "income", "BYN", 780); act.Set(ActDocument.ItemsField, ActDocument.SerializeItems(lines)); store.CaptureActSnapshots(act); store.Upsert(act); act = store.Find(act.Id);
            test("rounded item sums and zero-price lines", delegate {
                Eq(L("Округление", 2.005m, 1).Amount, 2.01m); Record rounding = Act(party, "ROUND", "income", "BYN", 2.01m); ActDocument.ValidateItems(rounding, new[] { L("Округление", 2.005m, 1), L("Бесплатное дополнение", 1, 0) });
                Reject(delegate { ActDocument.ValidateItems(rounding, new[] { L("Округление", 2.004m, 1) }); });
                Reject(delegate { ActDocument.SerializeItems(new[] { L("Минус", -1, 1) }); }); Reject(delegate { ActDocument.SerializeItems(new[] { L("Доля копейки", 1, 0.001m) }); });
                Eq(ActDocument.ReadItems(Act(party, "FALLBACK", "income", "BYN", 12)).Single().Amount, 12m);
            });
            test("JSON roundtrip malformed input and empty list", delegate {
                Eq(ActDocument.ReadItems(act).Count, 5); Eq(ActDocument.ReadItems(act).Sum(x => x.Amount), 780m);
                foreach (string raw in new[] { "{malformed", "null", "[]", "[null]", "[{\"Description\":\"bad\",\"Quantity\":0,\"Price\":1}]" }) { Record invalid = act.Clone(); invalid.Set(ActDocument.ItemsField, raw); Reject(delegate { ActDocument.ReadItems(invalid); }); }
                var exotic = L("Экранирование <>& \"кавычек\" и перенос\nстроки", 1.123456m, 10); Record copy = act.Clone(); copy.Set(ActDocument.ItemsField, ActDocument.SerializeItems(new[] { exotic })); Eq(ActDocument.ReadItems(copy)[0].Description, exotic.Description); Eq(ActDocument.ReadItems(copy)[0].Quantity, 1.123456m);
            });
            test("Russian date currency words declensions and scales", delegate {
                Eq(ActDocument.FormatDate("2026-09-01"), "1 сентября 2026 г."); Reject(delegate { ActDocument.FormatDate("2026-02-30"); });
                Eq(ActDocument.AmountInWords(1.01m, "BYN"), "Один белорусский рубль 01 копейка");
                Eq(ActDocument.AmountInWords(2.02m, "RUB"), "Два российских рубля 02 копейки");
                Eq(ActDocument.AmountInWords(5.05m, "USD"), "Пять долларов США 05 центов");
                Eq(ActDocument.AmountInWords(21.12m, "USD"), "Двадцать один доллар США 12 центов");
                Eq(ActDocument.AmountInWords(101m, "EUR"), "Сто один евро 00 центов");
                Eq(ActDocument.AmountInWords(1001.21m, "BYN"), "Одна тысяча один белорусский рубль 21 копейка");
                Eq(ActDocument.AmountInWords(2000000m, "USD"), "Два миллиона долларов США 00 центов");
                Eq(ActDocument.AmountInWords(0.005m, "BYN"), "Ноль белорусских рублей 01 копейка");
                Eq(ActDocument.AmountInWords(1000000000000m, "BYN"), "Один триллион белорусских рублей 00 копеек");
                Reject(delegate { ActDocument.AmountInWords(-1, "BYN"); }); Reject(delegate { ActDocument.AmountInWords(1, "JPY"); });
            });
            string five = Path.Combine(folder, "five-lines.docx"), longList = Path.Combine(folder, "long-list-21.docx");
            test("five detail rows exact totals escaped XML and no placeholders", delegate {
                long revision = store.Revision; ActDocument.Export(store, act, five); Eq(store.Revision, revision);
                XmlDocument doc = Document(five); var ns = Ns(doc); var tables = doc.SelectNodes("/w:document/w:body/w:tbl", ns);
                Eq(tables.Count, 1); Eq(tables[0].SelectNodes("./w:tr", ns).Count, 9);
                True(!doc.InnerText.Contains("{{")); True(doc.InnerText.Contains("Тестовый исполнитель & <Компания>")); True(doc.InnerText.Contains("TEST-EXEC-ACCOUNT")); True(doc.InnerText.Contains("TEST-CUSTOMER-TAX")); True(doc.InnerText.Contains("Адрес: Город Пример"));
                True(!doc.InnerText.Contains("Всего оказано услуг 1")); True(doc.InnerText.Contains("Всего оказано услуг 5")); True(doc.InnerText.Contains("780,00"));
                foreach (var line in lines) True(doc.InnerText.Contains(line.Description));
                True(doc.InnerText.Contains("Акт № QA-500")); True(!doc.InnerText.Contains("Приложение к акту"));
                True(!doc.InnerText.Contains("согласно приложению"));
            });
            test("opaque template package parts are byte identical", delegate {
                using (var resource = typeof(ActDocument).Assembly.GetManifestResourceStream("WorkLedger.ActTemplate.docx")) using (var template = new ZipArchive(resource, ZipArchiveMode.Read)) using (var output = ZipFile.OpenRead(five))
                {
                    Eq(output.Entries.Count, template.Entries.Count);
                    foreach (var entry in template.Entries.Where(e => e.FullName != "word/document.xml")) True(Bytes(entry).SequenceEqual(Bytes(output.GetEntry(entry.FullName))));
                }
            });
            test("outgoing act swaps customer and executor without changing ledger", delegate {
                Record outgoing = Act(party, "QA-OUT", "expense", "BYN", 780); outgoing.Set(ActDocument.ItemsField, ActDocument.SerializeItems(lines)); store.CaptureActSnapshots(outgoing); store.Upsert(outgoing); outgoing = store.Find(outgoing.Id);
                string path = Path.Combine(folder, "outgoing.docx"); ActDocument.Export(store, outgoing, path); var doc = Document(path); var paragraphs = doc.SelectNodes("/w:document/w:body/w:p", Ns(doc)).Cast<XmlNode>().Select(p => p.InnerText).ToList();
                True(paragraphs[0].Contains(party.Get("name"))); True(paragraphs.Any(p => p.Contains("Заказчик:") && p.Contains(own.Get("name"))));
            });
            test("saved requisites survive subsequent organization and customer edits", delegate {
                Record changed = store.Find(own.Id); changed.Set("name", "ИЗМЕНЁННАЯ ОРГАНИЗАЦИЯ"); changed.Set("bankAccount", "NEW-EXEC-ACCOUNT"); store.Upsert(changed);
                changed = store.Find(party.Id); changed.Set("name", "ИЗМЕНЁННЫЙ КОНТРАГЕНТ"); changed.Set("bankAccount", "NEW-CUSTOMER-ACCOUNT"); store.Upsert(changed);
                string path = Path.Combine(folder, "saved-requisites.docx"); ActDocument.Export(store, store.Find(act.Id), path); string text = Document(path).InnerText;
                True(text.Contains("Тестовый исполнитель & <Компания>")); True(text.Contains("TEST-EXEC-ACCOUNT")); True(text.Contains("Тестовый заказчик «Пример»")); True(text.Contains("TEST-CUSTOMER-ACCOUNT")); True(!text.Contains("NEW-EXEC-ACCOUNT")); True(!text.Contains("ИЗМЕНЁННЫЙ КОНТРАГЕНТ"));
            });
            test("existing hourly rows preserve description hours and rate", delegate {
                Record hourly = Act(party, "QA-HOURS", "income", "BYN", 1); Store.SetHourlyActLines(hourly, new[] { new HourlyActLine { Description = "Почасовая проверка", Hours = 1.25m, Rate = 80m }, new HourlyActLine { Description = "Почасовая настройка", Hours = 0.5m, Rate = 100m } }); store.Upsert(hourly); hourly = store.Find(hourly.Id);
                var read = ActDocument.ReadItems(hourly); Eq(read.Count, 2); Eq(read[0].Quantity, 1.25m); Eq(read[0].Price, 80m); Eq(read[0].Unit, "ч"); Eq(read.Sum(x => x.Amount), 150m);
                string path = Path.Combine(folder, "hourly-fallback.docx"); ActDocument.Export(store, hourly, path); True(Document(path).InnerText.Contains("1,25")); True(Document(path).InnerText.Contains("Почасовая настройка"));
            });
            test("authoritative hourly rows override stale printed JSON at the same total", delegate {
                Record hourly = Act(party, "QA-PRIORITY", "income", "BYN", 100); hourly.Set(ActDocument.ItemsField, ActDocument.SerializeItems(new[] { L("Старое описание", 1, 100) }));
                Store.SetHourlyActLines(hourly, new[] { new HourlyActLine { Description = "Новое описание почасовых работ", Hours = 2, Rate = 50 } }); var read = ActDocument.ReadItems(hourly);
                Eq(read.Count, 1); Eq(read[0].Description, "Новое описание почасовых работ"); Eq(read[0].Quantity, 2m); Eq(read[0].Price, 50m); Eq(read[0].Unit, "ч"); ActDocument.ValidateItems(hourly, read);
                string path = Path.Combine(folder, "hourly-authoritative.docx"); ActDocument.Export(store, hourly, path); True(Document(path).InnerText.Contains("Новое описание почасовых работ")); True(!Document(path).InnerText.Contains("Старое описание"));
            });
            test("legacy numbered operations remain separate without invented prices", delegate {
                Record legacy = Act(party, "QA-LEGACY-7", "income", "BYN", 1050);
                legacy.Set("description", "Комплексное сопровождение учетной системы\n1. Обновление программного обеспечения 2. Проверка данных 3. Настройка печатной формы 4. Доработка контрольного отчета\n5. Настройка обмена данными 6. Проверка документов учета 7. Итоговая контрольная проверка");
                store.Upsert(legacy); long revision = store.Revision; byte[] bytes = File.ReadAllBytes(store.DataPath); int audits = store.Audit.Count;
                string path = Path.Combine(folder, "legacy-seven.docx"); ActDocument.Export(store, store.Find(legacy.Id), path);
                var doc = Document(path); var ns = Ns(doc); var table = doc.SelectSingleNode("/w:document/w:body/w:tbl", ns);
                Eq(doc.SelectNodes("/w:document/w:body/w:tbl", ns).Count, 1); Eq(table.SelectNodes("./w:tr", ns).Count, 11);
                var operationRows = table.SelectNodes("./w:tr", ns).Cast<XmlNode>().Skip(1).Take(7).ToList();
                for (int i = 0; i < operationRows.Count; i++)
                {
                    var cells = operationRows[i].SelectNodes("./w:tc", ns); Eq(cells.Count, 9); Eq(cells[0].InnerText, (i + 1).ToString(CultureInfo.InvariantCulture));
                    foreach (int column in new[] { 2, 3, 4, 5, 7, 8 }) Eq(cells[column].InnerText, "—");
                }
                True(doc.InnerText.Contains("Всего оказано услуг 7")); True(doc.InnerText.Contains("1 050,00")); True(!doc.InnerText.Contains("Приложение"));
                True(doc.SelectNodes("/w:document/w:body/w:p", ns).Cast<XmlNode>().Any(p => p.InnerText == "Комплексное сопровождение учетной системы"));
                Eq(store.Revision, revision); Eq(store.Audit.Count, audits); True(bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)));
            });
            test("known zero amount stays numeric and entered summary stays a caption", delegate {
                Record free = Act(party, "QA-ZERO", "income", "BYN", 100);
                free.Set(ActDocument.ItemsField, ActDocument.SerializeItems(new[] { L("Оплачиваемая операция", 1, 100), L("Бесплатная операция", 1, 0) }));
                free.Set(ActDocument.DescriptionField, "Общее описание, введённое пользователем");
                string path = Path.Combine(folder, "zero-priced.docx"); ActDocument.Export(store, free, path); var doc = Document(path); var ns = Ns(doc);
                var row = doc.SelectSingleNode("/w:document/w:body/w:tbl/w:tr[3]", ns); var cells = row.SelectNodes("./w:tc", ns);
                Eq(cells[1].InnerText, "Бесплатная операция"); Eq(cells[3].InnerText, "1"); Eq(cells[4].InnerText, "0,00"); Eq(cells[5].InnerText, "0,00"); Eq(cells[8].InnerText, "0,00");
                True(doc.SelectNodes("/w:document/w:body/w:p", ns).Cast<XmlNode>().Any(p => p.InnerText == "Общее описание, введённое пользователем"));
                True(doc.InnerText.Contains("Всего оказано услуг 2")); True(!doc.InnerText.Contains("Приложение"));
            });
            test("long list repeats table header and preserves every row", delegate {
                var many = Enumerable.Range(1, 21).Select(i => L("Позиция " + i + ": проверка, сопровождение и настройка учетной системы по согласованному заданию заказчика", 1, 10)).ToList(); Record large = Act(party, "QA-LONG", "income", "BYN", 210); large.Set(ActDocument.ItemsField, ActDocument.SerializeItems(many)); store.Upsert(large); large = store.Find(large.Id); ActDocument.Export(store, large, longList);
                var doc = Document(longList); var ns = Ns(doc); Eq(doc.SelectNodes("/w:document/w:body/w:tbl/w:tr", ns).Count, 25); Eq(doc.SelectNodes("/w:document/w:body/w:tbl/w:tr[1]/w:trPr/w:tblHeader", ns).Count, 1); Eq(doc.SelectNodes("/w:document/w:body/w:tbl/w:tr/w:trPr/w:cantSplit", ns).Count, 25); Eq(doc.SelectNodes("/w:document/w:body/w:tbl/w:tr[last()-3]//w:pPr/w:keepNext", ns).Count, 9); Eq(doc.SelectNodes("/w:document/w:body/w:tbl/w:tr[last()-4]//w:pPr/w:keepNext", ns).Count, 0); True(doc.InnerText.Contains("Позиция 21:")); True(doc.InnerText.Contains("Всего оказано услуг 21"));
            });
            test("all supported currencies appear in words and headers", delegate {
                foreach (string currency in new[] { "BYN", "RUB", "USD", "EUR" })
                {
                    Record converted = act.Clone(); converted.Set("currency", currency); string path = Path.Combine(folder, "currency-" + currency + ".docx"); ActDocument.Export(store, converted, path); string text = Document(path).InnerText;
                    True(text.Contains(ActDocument.AmountInWords(780, currency))); if (currency == "USD") True(text.Contains("долл.")); if (currency == "EUR") True(text.Contains("евро")); True(!text.Contains("{{"));
                }
            });
            test("invalid item sum never overwrites document or accounting", delegate {
                string path = Path.Combine(folder, "preserve.docx"); File.WriteAllText(path, "original"); long revision = store.Revision; Record mismatch = act.Clone(); mismatch.Set("amount", "779.00"); Reject(delegate { ActDocument.Export(store, mismatch, path); }); Eq(File.ReadAllText(path), "original"); Eq(store.Revision, revision); Eq(store.Find(act.Id).Get("amount"), "780.00");
            });
            File.WriteAllText(Path.Combine(folder, "PASS.txt"), "PASS: Word act totals, snapshots, hourly lines, roles, currencies, tables and opaque package preservation.", new UTF8Encoding(false));
            Console.WriteLine("Word act: " + passed.ToString(CultureInfo.InvariantCulture) + " groups passed. QA fixtures: " + five + " | " + longList);
        }
        private static ActPrintLine L(string description, decimal qty, decimal price) { return new ActPrintLine { Description = description, Unit = "шт", Quantity = qty, Price = price }; }
        private static Record R(string kind, params string[] fields) { Record r = new Record(kind); for (int i = 0; i < fields.Length; i += 2) r.Set(fields[i], fields[i + 1]); return r; }
        private static Record Act(Record party, string number, string direction, string currency, decimal amount) { return R("act", "number", number, "date", "2026-09-01", "partyId", party.Id, "direction", direction, "currency", currency, "amount", Store.MoneyString(amount), "status", "draft", "description", "Проверка и сопровождение учетной системы"); }
        private static XmlDocument Document(string path) { using (var zip = ZipFile.OpenRead(path)) { var doc = new XmlDocument(); doc.XmlResolver = null; using (var stream = zip.GetEntry("word/document.xml").Open()) using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader); return doc; } }
        private static XmlNamespaceManager Ns(XmlDocument doc) { var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main"); return ns; }
        private static byte[] Bytes(ZipArchiveEntry entry) { using (var source = entry.Open()) using (var mem = new MemoryStream()) { source.CopyTo(mem); return mem.ToArray(); } }
        private static void True(bool condition) { if (!condition) throw new Exception("Word act assertion failed."); }
        private static void Eq<T>(T a, T b) { if (!Object.Equals(a, b)) throw new Exception("Word act expected " + b + ", got " + a); }
        private static void Reject(Action run) { try { run(); } catch { return; } throw new Exception("Invalid Word act was accepted."); }
    }
}

