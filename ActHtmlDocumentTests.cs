using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace WorkLedger
{
    public static class ActHtmlDocumentTests
    {
        public static void Run(string root)
        {
            string folder = Path.Combine(Path.GetFullPath(root), "act-html-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            int passed = 0;
            Action<string, Action> test = delegate(string name, Action action) { action(); passed++; Console.WriteLine("PASS HTML act " + name); };
            var store = new Store(Path.Combine(folder, "data"));
            Record own = R("settings", "name", "Тестовый исполнитель «Пример»", "currency", "BYN", "bankAccount", "TEST-EXEC-ACCOUNT", "bankName", "Тестовый банк", "bankCode", "TESTCODE", "unp", "TEST-EXEC-TAX", "registrationAddress", "Город Пример, улица Образцовая, дом 1", "phone", "+000 000 000", "email", "example@example.test", "requisites", "Дополнительные реквизиты исполнителя", "signatoryName", "Тестовый Подписант", "signatoryPosition", "Индивидуальный предприниматель");
            store.Upsert(own);
            Record party = R("party", "name", "Тестовый заказчик «Образец»", "role", "both", "bankAccount", "TEST-CUSTOMER-ACCOUNT", "bankName", "Тестовый банк заказчика", "bankCode", "TESTCLIENT", "unp", "TEST-CUSTOMER-TAX", "registrationAddress", "Город Пример, улица Тестовая, дом 2", "phone", "+000 111 111", "requisites", "Дополнительные реквизиты заказчика", "signatoryName", "Подписант Заказчика", "signatoryPosition", "Руководитель");
            store.Upsert(party);
            var lines = new[] {
                L("Обновление и комплексное сопровождение учетной системы", 1, 100),
                L("Разработка функции регистрации заявок по согласованному техническому заданию", 1, 90),
                L("Доработка печатной формы передачи материалов в эксплуатацию", 1, 80),
                L("Настройка печатной формы внутреннего перемещения оборудования", 1, 70),
                L("Проверка ведомости движения и выдачи материалов", 1, 50)
            };
            Record act = Act(party, "QA-390", 390); act.Set(ActDocument.ItemsField, ActDocument.SerializeItems(lines)); act.Set("notes", "PRIVATE-SERVICE-NOTES"); act.Set("source", "PRIVATE-SERVICE-SOURCE"); act.Set("createdUtc", "PRIVATE-SERVICE-CREATED");
            store.CaptureActSnapshots(act); store.Upsert(act); act = store.Find(act.Id);
            string five = Path.Combine(folder, "five-operations-390.html"), longList = Path.Combine(folder, "long-operations-160.html"), unknown = Path.Combine(folder, "seven-operations-unpriced-1050.html");
            test("five separate operations exact totals and readonly accounting", delegate {
                XmlDocument html = WriteAndLoad(five, store, act);
                Eq(Rows(html).Count, 5, "operation count");
                for (int i = 0; i < lines.Length; i++)
                {
                    XmlNode row = Rows(html)[i]; Eq(row.SelectNodes("./td").Count, 9, "nine operation cells");
                    Eq(row.SelectSingleNode("./td[2]").InnerText, lines[i].Description, "operation description");
                    Eq(MoneyText(row.SelectSingleNode("./td[6]").InnerText), Store.MoneyString(lines[i].Amount), "per-operation amount");
                }
                Eq(MoneyText(html.SelectSingleNode("//td[@data-role='total']").InnerText), "390.00", "total");
                True(html.SelectSingleNode("//p[@class='amount-words']").InnerText.Contains("Всего оказано услуг 5"), "service count");
                True(html.InnerText.Contains("триста девяносто белорусских рублей 00 копеек"), "Russian amount words");
                foreach (string marker in new[] { "PRIVATE-SERVICE-NOTES", "PRIVATE-SERVICE-SOURCE", "PRIVATE-SERVICE-CREATED", "проверьте", "Статус:", "Направление:" }) True(!html.SelectSingleNode("/html/body/main").InnerText.Contains(marker), "service metadata absent: " + marker);
            });
            test("A4 grid and print pagination contract", delegate {
                XmlDocument html = Document(five); XmlNodeList columns = html.SelectNodes("//table/colgroup/col"); Eq(columns.Count, 9, "column count"); Eq(html.SelectNodes("//thead/tr/th").Count, 9, "header count");
                int[] widths = { 353, 2948, 970, 1037, 904, 1245, 883, 849, 1138 };
                for (int i = 0; i < widths.Length; i++)
                {
                    string text = columns[i].Attributes["style"].Value.Replace("width:", "").TrimEnd('%');
                    decimal actual = Decimal.Parse(text, CultureInfo.InvariantCulture), expected = widths[i] * 100m / widths.Sum();
                    True(Math.Abs(actual - expected) <= 0.000001m, "source column proportions");
                }
                string css = html.SelectSingleNode("//style").InnerText;
                foreach (string value in new[] { "size:A4 portrait", "25.4mm 10.495mm 17.515mm 17.36mm", "width:182.1568mm", "font-family:Arial", "thead{display:table-header-group}", "closing-group{break-inside:avoid;page-break-inside:avoid}", "page-break-inside:avoid", ".money{text-align:right;white-space:nowrap", ".toolbar{display:none!important}", "height:10mm", ".work-table .closing-content-row{height:auto}", "margin-bottom:8mm" }) True(css.Contains(value), "layout rule: " + value);
                True(html.SelectSingleNode("/html/body/div[@class='toolbar']") != null && html.SelectSingleNode("//main//div[@class='toolbar']") == null, "toolbar outside printed paper");
                Eq(html.SelectNodes("//tfoot").Count, 0, "no repeated print footer");
                XmlNode closing = html.SelectSingleNode("//tbody[@class='closing-group']"); True(closing != null, "closing group present");
                Eq(closing.SelectNodes("./tr").Count, 5, "last operation plus totals plus signatures");
                Eq(closing.SelectNodes("./tr[@data-role='operation']").Count, 1, "one last operation grouped with signatures");
                Eq(closing.SelectSingleNode("./tr[@data-role='operation']/td[1]").InnerText, "5", "last operation retained in closing group");
                Eq(html.SelectNodes("//tr[@class='closing-total-row']").Count, 3, "totals appear once");
                Eq(closing.SelectSingleNode("./tr[@class='closing-total-row'][1]/th").InnerText, "Итого:", "subtotal label"); Eq(closing.SelectSingleNode("./tr[@class='closing-total-row'][3]/th").InnerText, "Всего:", "total label");
                XmlNode closingCell = closing.SelectSingleNode("./tr[@class='closing-content-row']/td[@class='closing-content-cell']"); Eq(closingCell.Attributes["colspan"].Value, "9", "closing section spans full grid");
                True(closingCell.SelectSingleNode("./p[@class='amount-words']") != null && closingCell.SelectSingleNode("./p[@class='acceptance']") != null && closingCell.SelectSingleNode("./div[@class='signatures']") != null, "words acceptance and signatures kept with last operation");
                True(html.SelectSingleNode("//p[@class='amount-words']/br") != null, "VAT on second line");
            });
            test("tricky user text is escaped with line breaks preserved", delegate {
                var line = L("Проверка <script>alert(\"пример\")</script> & «кавычек»\nВторая строка > первая", 1, 12);
                Record exotic = act.Clone(); exotic.Set("amount", "12.00"); exotic.Set("number", "Q<&\"'"); exotic.Set(ActDocument.ItemsField, ActDocument.SerializeItems(new[] { line }));
                XmlDocument html = WriteAndLoad(Path.Combine(folder, "escaping.html"), store, exotic);
                True(html.SelectSingleNode("//script") == null, "user script not markup"); Eq(html.SelectSingleNode("//span[@id='act-number']").InnerText, exotic.Get("number"), "act number escaped");
                XmlNode description = Rows(html)[0].SelectSingleNode("./td[2]"); True(description.InnerText.Contains("<script>alert(\"пример\")</script> & «кавычек»"), "special characters roundtrip"); Eq(description.SelectNodes("./br").Count, 1, "description line break");
                True(!File.ReadAllBytes(Path.Combine(folder, "escaping.html")).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "UTF8 no BOM");
            });
            test("legacy seven operations have unknown prices and unchanged aggregate total", delegate {
                Record legacy = act.Clone(); legacy.Set(ActDocument.ItemsField, ""); legacy.Set("amount", "1050.00"); legacy.Set("description", LegacyDescription());
                XmlDocument html = WriteAndLoad(unknown, store, legacy); Eq(Rows(html).Count, 7, "legacy operation count");
                Eq(html.SelectSingleNode("//p[@data-role='work-summary']").InnerText, "Комплексное сопровождение учетной системы", "package heading separate");
                foreach (XmlNode row in Rows(html))
                {
                    foreach (int column in new[] { 3, 4, 5, 6, 9 }) Eq(row.SelectSingleNode("./td[" + column + "]").InnerText, "—", "unconfirmed financial value");
                    Eq(row.SelectSingleNode("./td[7]").InnerText, "Без НДС", "VAT scope");
                }
                string fourth = Rows(html)[3].SelectSingleNode("./td[2]").InnerText; True(fourth.Contains("- сверка исходных данных") && fourth.Contains("- проверка итогового результата"), "sub-bullets stay in operation four");
                True(Rows(html)[0].InnerText.Contains("60.1") && Rows(html)[1].InnerText.Contains("01.10.2026"), "versions and dates are not markers");
                Eq(MoneyText(html.SelectSingleNode("//td[@data-role='total']").InnerText), "1050.00", "aggregate amount retained");
                True(html.InnerText.Contains("Всего операций: 7") && html.InnerText.Contains("одна тысяча пятьдесят белорусских рублей"), "legacy count and amount words");
            });
            test("separate descriptive items preserve unknown prices for one and multiple operations", delegate {
                foreach (string[] descriptions in new[] { new[] { "Единственная операция без распределения стоимости" },
                    new[] { "Первая операция", "Вторая операция с версией 60.1 и датой 01.10.2026", "Третья операция\n- подробность" } })
                {
                    Record descriptive = act.Clone(); descriptive.Set(ActDocument.ItemsField, "");
                    descriptive.Set("amount", "1050.00"); descriptive.Set("description", "СТАРЫЙ ОБЩИЙ ТЕКСТ 1. Старое 2. Другое");
                    descriptive.Set(ActDocument.DescriptionField, "Актуальный заголовок");
                    descriptive.Set(ActHtmlDocument.DescriptiveItemsField, ActHtmlDocument.SerializeDescriptions(descriptions));
                    string summary; List<ActHtmlLine> displayRows = ActHtmlDocument.ReadDisplayRows(descriptive, out summary);
                    Eq(summary, "Актуальный заголовок", "descriptive caption"); Eq(displayRows.Count, descriptions.Length, "stored descriptive row count");
                    for (int i = 0; i < descriptions.Length; i++)
                    {
                        Eq(displayRows[i].Description, descriptions[i], "stored descriptive text");
                        True(!displayRows[i].Quantity.HasValue && !displayRows[i].Price.HasValue && !displayRows[i].Amount.HasValue, "unknown price remains unknown");
                    }
                    XmlDocument html = WriteAndLoad(Path.Combine(folder, "descriptive-" + descriptions.Length + ".html"), store, descriptive);
                    Eq(Rows(html).Count, descriptions.Length, "descriptive HTML rows");
                    foreach (XmlNode row in Rows(html))
                        foreach (int column in new[] { 3, 4, 5, 6, 9 }) Eq(row.SelectSingleNode("./td[" + column + "]").InnerText, "—", "unpriced descriptive field");
                    True(!html.InnerText.Contains("СТАРЫЙ ОБЩИЙ ТЕКСТ"), "stale aggregate body absent");
                    Eq(MoneyText(html.SelectSingleNode("//td[@data-role='total']").InnerText), "1050.00", "descriptive aggregate retained");
                    True(html.InnerText.Contains("Всего операций: " + descriptions.Length), "descriptive count");
                }
                string serialized = ActHtmlDocument.SerializeDescriptions(new[] { "  Проверка <данных> & текста  " });
                Record escaped = act.Clone(); escaped.Set(ActDocument.ItemsField, ""); escaped.Set(ActHtmlDocument.DescriptiveItemsField, serialized);
                XmlDocument escapedHtml = WriteAndLoad(Path.Combine(folder, "descriptive-escaping.html"), store, escaped);
                Eq(Rows(escapedHtml)[0].SelectSingleNode("./td[2]").InnerText, "Проверка <данных> & текста", "descriptive escaping and trimming");
            });
            test("invalid descriptive rows preserve prior output and ledger", delegate {
                string path = Path.Combine(folder, "descriptive-preserve.html"); File.WriteAllText(path, "previous descriptive document", new UTF8Encoding(false));
                long revision = store.Revision; byte[] before = File.ReadAllBytes(store.DataPath);
                foreach (string invalid in new[] { "{broken", "null", "{}", "[]", "[null]", "[42]", "[{}]", "[true]", "[[\"nested\"]]", "[\" \"]",
                    "[\"" + new string('x', 2001) + "\"]",
                    "[" + String.Join(",", Enumerable.Range(1, 201).Select(i => "\"Операция " + i + "\"")) + "]", new string(' ', 100001) + "[]" })
                {
                    Record malformed = act.Clone(); malformed.Set(ActDocument.ItemsField, ""); malformed.Set(ActHtmlDocument.DescriptiveItemsField, invalid);
                    Reject(delegate { ActHtmlDocument.WriteHtml(path, store, malformed); }, "invalid descriptive list");
                    Eq(File.ReadAllText(path), "previous descriptive document", "old descriptive document retained");
                }
                Reject(delegate { ActHtmlDocument.SerializeDescriptions(null); }, "null descriptions");
                Reject(delegate { ActHtmlDocument.SerializeDescriptions(new string[0]); }, "empty descriptions");
                Reject(delegate { ActHtmlDocument.SerializeDescriptions(new[] { "Работа", " " }); }, "blank description");
                Reject(delegate { ActHtmlDocument.SerializeDescriptions(new[] { new string('x', 2001) }); }, "oversized description");
                Reject(delegate { ActHtmlDocument.SerializeDescriptions(Enumerable.Range(0, 201).Select(i => "Работа")); }, "too many descriptions");
                Reject(delegate { ActHtmlDocument.SerializeDescriptions(Enumerable.Range(0, 100).Select(i => new string('x', 2000))); }, "oversized JSON list");
                Eq(store.Revision, revision, "invalid descriptive revision"); True(before.SequenceEqual(File.ReadAllBytes(store.DataPath)), "invalid descriptive ledger");
            });
            test("priced and hourly sources outrank stale descriptive JSON", delegate {
                Record priced = act.Clone(); priced.Set(ActHtmlDocument.DescriptiveItemsField, "{broken");
                string summary; List<ActHtmlLine> rows = ActHtmlDocument.ReadDisplayRows(priced, out summary);
                Eq(rows.Count, 5, "priced rows authoritative"); True(rows.All(row => row.Amount.HasValue), "priced amounts authoritative");
                Record hourly = act.Clone(); hourly.Set(ActHtmlDocument.DescriptiveItemsField, "{broken");
                Store.SetHourlyActLines(hourly, new[] { new HourlyActLine { Description = "Актуальная почасовая операция", Hours = 2, Rate = 25 } });
                rows = ActHtmlDocument.ReadDisplayRows(hourly, out summary); Eq(rows.Count, 1, "hourly source authoritative");
                Eq(rows[0].Description, "Актуальная почасовая операция", "hourly text authoritative"); Eq(rows[0].Amount.Value, 50m, "hourly amount authoritative");
            });
            test("imported heading joined to first numbered operation splits conservatively", delegate {
                Record imported = act.Clone(); imported.Set(ActDocument.ItemsField, "");
                imported.Set("description", "Системное администрирование1. Перенесены данные организации 2. Настроены параметры учета 3. Проверены версии 60.1 и дата 01.10.2026");
                string summary; List<ActHtmlLine> rows = ActHtmlDocument.ReadDisplayRows(imported, out summary);
                Eq(summary, "Системное администрирование", "joined heading isolated"); Eq(rows.Count, 3, "joined operation count");
                Eq(rows[0].Description, "Перенесены данные организации", "joined first operation"); True(!rows[0].Price.HasValue, "joined unknown price retained");
                XmlDocument html = WriteAndLoad(Path.Combine(folder, "joined-numbered-description.html"), store, imported); Eq(Rows(html).Count, 3, "joined HTML count");
                foreach (string ambiguous in new[] { "Пакет1. Первая работа 3. Третья работа", "Пакет01. Первая работа 2. Вторая работа",
                    "Версия 60.1 от 01.10.2026", "Пакет1. Первая работа 2. Вторая работа 1. Вложенная работа 3. Третья работа" })
                {
                    List<ActHtmlLine> ignored; string heading; True(!ActHtmlDocument.TryReadNumberedOperations(ambiguous, out ignored, out heading), "joined ambiguous input not split");
                }
            });
            test("ambiguous numbering stays intact instead of inventing rows", delegate {
                foreach (string description in new[] {
                    "Пакет 1. Первая операция 3. Третья операция",
                    "Пакет 1. Первая операция 2. Вторая операция 1. Вложенная операция 3. Третья операция",
                    "Пакет 0. Вводная операция 1. Первая операция 2. Вторая операция",
                    "Пакет 01. Первая операция 2. Вторая операция",
                    "Пакет 2. Вторая операция 3. Третья операция",
                    "Обновление версии 60.1 от 01.10.2026"
                })
                {
                    List<ActHtmlLine> rows; string summary; True(!ActHtmlDocument.TryReadNumberedOperations(description, out rows, out summary), "ambiguous list rejected");
                    Record legacy = act.Clone(); legacy.Set(ActDocument.ItemsField, ""); legacy.Set("description", description);
                    XmlDocument html = WriteAndLoad(Path.Combine(folder, "ambiguous.html"), store, legacy); Eq(Rows(html).Count, 1, "unsplit legacy row"); Eq(Rows(html)[0].SelectSingleNode("./td[2]").InnerText, description, "full description retained");
                }
                List<ActHtmlLine> rejected; string ignored; string tooMany = String.Join(" ", Enumerable.Range(1, 201).Select(i => i.ToString(CultureInfo.InvariantCulture) + ". Работа"));
                True(!ActHtmlDocument.TryReadNumberedOperations(tooMany, out rejected, out ignored), "list beyond 200 rejected");
            });
            test("structured rows outrank numbered description and stale hourly JSON", delegate {
                Record structured = act.Clone(); structured.Set(ActDocument.ItemsField, ActDocument.SerializeItems(new[] { L("1. Первая часть 2. Вторая часть", 1, 390) })); structured.Set("description", LegacyDescription());
                XmlDocument html = WriteAndLoad(Path.Combine(folder, "structured-priority.html"), store, structured); Eq(Rows(html).Count, 1, "structured numbering remains single financial row"); Eq(Rows(html)[0].SelectSingleNode("./td[2]").InnerText, "1. Первая часть 2. Вторая часть", "structured source wins");
                Record hourly = act.Clone(); Store.SetHourlyActLines(hourly, new[] { new HourlyActLine { Description = "Актуальные почасовые работы", Hours = 2.005m, Rate = 1 } });
                html = WriteAndLoad(Path.Combine(folder, "hourly-authoritative.html"), store, hourly); Eq(Rows(html).Count, 1, "authoritative hourly rows"); Eq(Rows(html)[0].SelectSingleNode("./td[3]").InnerText, "ч", "hours unit"); Eq(Rows(html)[0].SelectSingleNode("./td[4]").InnerText, "2,005", "hours precision"); Eq(MoneyText(Rows(html)[0].SelectSingleNode("./td[6]").InnerText), "2.01", "half-cent rounds away from zero"); Eq(MoneyText(html.SelectSingleNode("//td[@data-role='total']").InnerText), "2.01", "hourly total"); True(!html.InnerText.Contains(lines[0].Description), "stale print JSON ignored");
                Record free = act.Clone(); free.Set("amount", "2.01"); free.Set(ActDocument.ItemsField, ActDocument.SerializeItems(new[] { L("Проверка округления", 2.005m, 1), L("Бесплатная контрольная проверка", 1, 0) }));
                html = WriteAndLoad(Path.Combine(folder, "zero-priced-line.html"), store, free); Eq(Rows(html).Count, 2, "zero-priced operation retained"); Eq(MoneyText(Rows(html)[1].SelectSingleNode("./td[6]").InnerText), "0.00", "known free amount distinct from unknown amount");
            });
            test("snapshots preserve bank fields address and signatories", delegate {
                Record changed = store.Find(own.Id); changed.Set("name", "ИЗМЕНЕННЫЙ ИСПОЛНИТЕЛЬ"); changed.Set("bankAccount", "NEW-EXEC"); changed.Set("signatoryName", "НОВЫЙ ПОДПИСАНТ"); store.Upsert(changed);
                changed = store.Find(party.Id); changed.Set("name", "ИЗМЕНЕННЫЙ ЗАКАЗЧИК"); changed.Set("bankAccount", "NEW-CUSTOMER"); store.Upsert(changed);
                XmlDocument html = WriteAndLoad(Path.Combine(folder, "snapshots.html"), store, store.Find(act.Id));
                foreach (string value in new[] { own.Get("name"), party.Get("name"), "TEST-EXEC-ACCOUNT", "TEST-CUSTOMER-ACCOUNT", "TEST-EXEC-TAX", "TEST-CUSTOMER-TAX", own.Get("registrationAddress"), own.Get("phone"), "Тестовый Подписант", "Индивидуальный предприниматель", "Подписант Заказчика" }) True(html.InnerText.Contains(value), "snapshot value: " + value);
                foreach (string value in new[] { "NEW-EXEC", "NEW-CUSTOMER", "ИЗМЕНЕННЫЙ", "НОВЫЙ ПОДПИСАНТ" }) True(!html.InnerText.Contains(value), "live changed value not printed");
                XmlNode executor = html.SelectSingleNode("//section[@data-role='executor']"); Eq(executor.SelectNodes("./p").Count, 3, "executor three logical lines"); True(executor.SelectSingleNode("./p[@class='contact-line']").InnerText.Contains("Адрес:") && executor.SelectSingleNode("./p[@class='contact-line']").InnerText.Contains(own.Get("phone")), "address and phone on one logical line");
            });
            test("expense act reverses executor and customer without changing sums", delegate {
                Record outgoing = act.Clone(); outgoing.Set("direction", "expense"); XmlDocument html = WriteAndLoad(Path.Combine(folder, "expense.html"), store, outgoing);
                Eq(html.SelectSingleNode("//section[@data-role='executor']/p[@class='party-name']").InnerText, party.Get("name"), "supplier is executor");
                Eq(html.SelectSingleNode("//section[@data-role='customer']/p[@class='party-name']").InnerText, "Заказчик: " + own.Get("name"), "organization is customer");
                Eq(MoneyText(html.SelectSingleNode("//td[@data-role='total']").InnerText), "390.00", "expense total");
            });
            test("supported currencies and precise Russian dates", delegate {
                foreach (string currency in new[] { "BYN", "RUB", "USD", "EUR" })
                {
                    Record converted = act.Clone(); converted.Set("currency", currency); XmlDocument html = WriteAndLoad(Path.Combine(folder, "currency-" + currency + ".html"), store, converted);
                    string expected = ActDocument.AmountInWords(390, currency); expected = Char.ToLowerInvariant(expected[0]) + expected.Substring(1);
                    Eq(html.SelectSingleNode("//span[@data-role='amount-words']").InnerText, expected, "currency words");
                    True(html.InnerText.Contains("1 октября 2026 г.") && html.InnerText.Contains("Валюта: " + currency), "date and code");
                    string unit = currency == "USD" ? "долл. США" : currency == "EUR" ? "евро" : "руб."; True(html.SelectSingleNode("//thead").InnerText.Contains(unit), "header currency unit");
                }
                Record invalid = act.Clone(); invalid.Set("date", "2026-02-30"); Reject(delegate { ActHtmlDocument.WriteHtml(Path.Combine(folder, "bad-date.html"), store, invalid); }, "invalid date");
                invalid = act.Clone(); invalid.Set("currency", "JPY"); Reject(delegate { ActHtmlDocument.WriteHtml(Path.Combine(folder, "bad-currency.html"), store, invalid); }, "unsupported currency");
            });
            test("unfilled requisites show explicit blanks", delegate {
                var empty = new Store(Path.Combine(folder, "empty-data")); Record incomplete = Act(new Record("party"), "BLANK", 25);
                XmlDocument html = WriteAndLoad(Path.Combine(folder, "placeholders.html"), empty, incomplete);
                foreach (string value in new[] { "[наименование исполнителя]", "[наименование заказчика]", "[банковские реквизиты и УНП исполнителя]", "[адрес заказчика]" }) True(html.InnerText.Contains(value), "clear missing value: " + value);
                True(!File.Exists(empty.DataPath), "printing empty database does not create ledger");
            });
            test("invalid details never overwrite document or database", delegate {
                string path = Path.Combine(folder, "preserve.html"); File.WriteAllText(path, "previous document", new UTF8Encoding(false)); byte[] ledger = File.ReadAllBytes(store.DataPath); long revision = store.Revision;
                Record mismatch = act.Clone(); mismatch.Set("amount", "389.00"); Reject(delegate { ActHtmlDocument.WriteHtml(path, store, mismatch); }, "sum mismatch");
                Record malformed = act.Clone(); malformed.Set(ActDocument.ItemsField, "{broken"); Reject(delegate { ActHtmlDocument.WriteHtml(path, store, malformed); }, "malformed rows not guessed");
                Reject(delegate { ActHtmlDocument.WriteHtml(path, store, act, new[] { new ActHtmlLine { Description = "Неполные числа", Quantity = 1 } }); }, "partial display finance");
                Eq(File.ReadAllText(path), "previous document", "previous document preserved"); Eq(store.Revision, revision, "revision preserved"); True(File.ReadAllBytes(store.DataPath).SequenceEqual(ledger), "ledger bytes preserved");
                True(!Directory.GetFiles(folder, "*.tmp-*").Any(), "no temporary leftovers");
            });
            test("long list has sixteen distinct rows with a single aggregate footer", delegate {
                var many = Enumerable.Range(1, 16).Select(i => L("Операция " + i.ToString(CultureInfo.InvariantCulture) + ": проверка, сопровождение и настройка учетной системы по согласованному заданию заказчика", 1, 10)).ToList();
                Record large = act.Clone(); large.Set("number", "QA-160"); large.Set("amount", "160.00"); large.Set(ActDocument.ItemsField, ActDocument.SerializeItems(many));
                XmlDocument html = WriteAndLoad(longList, store, large); Eq(Rows(html).Count, 16, "long list count"); Eq(html.SelectNodes("//td[@data-role='total']").Count, 1, "one aggregate total");
                True(Rows(html)[15].SelectSingleNode("./td[2]").InnerText.StartsWith("Операция 16:"), "last operation retained"); Eq(MoneyText(html.SelectSingleNode("//td[@data-role='total']").InnerText), "160.00", "long list total");
                Eq(html.SelectSingleNode("//tbody[@class='closing-group']/tr[@data-role='operation']/td[1]").InnerText, "16", "long closing group starts at final operation");
                Eq(html.SelectNodes("//tbody[not(@class='closing-group')]/tr[@data-role='operation']").Count, 15, "preceding operations remain independently pageable");
            });
            File.WriteAllText(Path.Combine(folder, "PASS.txt"), "PASS: " + passed.ToString(CultureInfo.InvariantCulture) + " groups, financial totals, legacy display-only rows, escaping, snapshots, currency, readonly storage and print structure.", new UTF8Encoding(false));
            Console.WriteLine("HTML act: " + passed.ToString(CultureInfo.InvariantCulture) + " groups passed. QA fixtures: " + five + " | " + longList + " | " + unknown);
        }
        private static string LegacyDescription()
        {
            return "Комплексное сопровождение учетной системы\n1. Обновление программного обеспечения до версии 60.1 2. Проверка данных за 01.10.2026 3. Настройка печатной формы 4. Доработка контрольного отчета\n- сверка исходных данных\n- проверка итогового результата\n5. Настройка обмена данными 6. Проверка документов учета 7. Итоговая контрольная проверка";
        }
        private static XmlDocument WriteAndLoad(string path, Store store, Record act)
        {
            bool existed = File.Exists(store.DataPath); byte[] before = existed ? File.ReadAllBytes(store.DataPath) : null; long revision = store.Revision;
            ActHtmlDocument.WriteHtml(path, store, act); Eq(store.Revision, revision, "printing revision"); Eq(File.Exists(store.DataPath), existed, "printing ledger existence");
            if (existed) True(File.ReadAllBytes(store.DataPath).SequenceEqual(before), "printing ledger bytes");
            return Document(path);
        }
        private static XmlDocument Document(string path)
        {
            var result = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null })) result.Load(reader);
            return result;
        }
        private static XmlNodeList Rows(XmlDocument html) { return html.SelectNodes("//tbody/tr[@data-role='operation']"); }
        private static string MoneyText(string text) { return text.Replace("\u00a0", "").Replace("\u202f", "").Replace(" ", "").Replace(",", "."); }
        private static ActPrintLine L(string description, decimal qty, decimal price) { return new ActPrintLine { Description = description, Unit = "ч", Quantity = qty, Price = price }; }
        private static Record R(string kind, params string[] fields) { var result = new Record(kind); for (int i = 0; i < fields.Length; i += 2) result.Set(fields[i], fields[i + 1]); return result; }
        private static Record Act(Record party, string number, decimal amount) { return R("act", "number", number, "date", "2026-10-01", "partyId", party.Id, "direction", "income", "currency", "BYN", "amount", Store.MoneyString(amount), "status", "draft", "description", "Сопровождение учетной системы"); }
        private static void True(bool condition, string message) { if (!condition) throw new Exception("HTML act assertion failed: " + message); }
        private static void Eq<T>(T actual, T expected, string message) { if (!Object.Equals(actual, expected)) throw new Exception("HTML act " + message + ": expected " + expected + ", got " + actual); }
        private static void Reject(Action action, string message) { try { action(); } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; } throw new Exception("Invalid HTML act accepted: " + message); }
    }
}
