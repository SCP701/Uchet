using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace WorkLedger
{
    public static class HourlyActDocumentTests
    {
        static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        static void Assert(bool condition, string reason) { if (!condition) throw new Exception("Act document test failed: " + reason); }
        static Record Add(Store store, string kind, params string[] values)
        {
            var record = new Record(kind);
            for (int i = 0; i < values.Length; i += 2) record.Set(values[i], values[i + 1]);
            store.Upsert(record); return store.Find(record.Id);
        }
        static string CellText(XElement cell) { return String.Concat(cell.Descendants(S + "t").Select(t => t.Value)); }
        static XDocument PackageXml(ZipArchive zip, string name)
        {
            using (var stream = zip.GetEntry(name).Open()) return XDocument.Load(stream);
        }
        public static void Run(string path)
        {
            string root = Path.Combine(path, "act-documents-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var store = new Store(Path.Combine(root, "data"));
            var own = Add(store, "settings", "name", "Исполнитель <старый>", "currency", "BYN", "hourlyRate", "2", "unp", "123456789", "signatoryName", "Иванов & Иван", "signatoryPosition", "ИП", "requisites", "Счёт исполнителя\nМинск");
            var party = Add(store, "party", "name", "Заказчик <старый>", "role", "client", "unp", "987654321", "signatoryName", "Петров Пётр", "requisites", "Счёт заказчика\nБрест");
            var act = store.CreateHourlyActDraft(party.Id);
            act.Set("number", "500");
            string description = "=HYPERLINK(\"https://example.test\",\"Тест\") <script> &\nВторая строка";
            Store.SetHourlyActLines(act, new[]
            {
                new HourlyActLine { Description = description, Hours = 0.000001m, Rate = 10000m },
                new HourlyActLine { Description = "Округление строки", Hours = 1.005m, Rate = 2m }
            });
            act.Set("notes", "Примечание <тест>");
            own.Set("name", "HIDDEN-NEW-OWN"); party.Set("name", "HIDDEN-NEW-PARTY"); store.Upsert(own); store.Upsert(party);
            string htmlPath = Path.Combine(root, "act.html"), xlsxPath = Path.Combine(root, "act.xlsx");
            HourlyActDocuments.WriteHtml(htmlPath, store, act); HourlyActDocuments.WriteXlsx(xlsxPath, store, act);
            HourlyActDocuments.WriteXlsx(xlsxPath, store, act);
            string html = File.ReadAllText(htmlPath);
            Assert(html.Contains("Акт выполненных работ № 500") && html.Contains("Исполнитель &lt;старый&gt;") && html.Contains("Заказчик &lt;старый&gt;"), "HTML includes act and preserved parties");
            Assert(!html.Contains("HIDDEN-NEW") && !html.Contains("<script>") && html.Contains("&lt;script&gt; &amp;") && html.Contains("Примечание &lt;тест&gt;"), "HTML escapes descriptions/notes and uses snapshots after directory updates");
            Assert(html.Contains("0.000001") && html.Contains("1.005001") && html.Contains("2.02") && html.Contains("ИП Иванов &amp; Иван"), "HTML preserves fractional hours, per-line money rounding and signatory");
            using (var zip = ZipFile.OpenRead(xlsxPath))
            {
                Assert(zip.GetEntry("[Content_Types].xml") != null && zip.GetEntry("xl/workbook.xml") != null, "individual Excel export is an XLSX package");
                XDocument worksheet = PackageXml(zip, "xl/worksheets/sheet1.xml"), styles = PackageXml(zip, "xl/styles.xml");
                var cells = worksheet.Descendants(S + "c").ToList();
                string text = String.Join("\n", cells.Select(CellText));
                Assert(text.Contains("Исполнитель <старый>") && text.Contains("Заказчик <старый>") && !text.Contains("HIDDEN-NEW"), "XLSX includes preserved requisites");
                Assert(text.Contains("УНП: 123456789") && text.Contains("УНП: 987654321") && text.Contains("Счёт заказчика\nБрест"), "XLSX exports both parties' labelled multiline requisites");
                var descriptionCell = cells.Single(c => CellText(c) == description);
                Assert((string)descriptionCell.Attribute("t") == "inlineStr" && !worksheet.Descendants(S + "f").Any(), "work text starting with equals remains text, not an Excel formula");
                var headerRow = worksheet.Descendants(S + "row").Single(r => r.Elements(S + "c").Any(c => CellText(c) == "Описание выполненных работ"));
                int firstLine = Int32.Parse((string)headerRow.Attribute("r"), CultureInfo.InvariantCulture) + 1;
                var firstHours = cells.Single(c => (string)c.Attribute("r") == "C" + firstLine);
                Assert(firstHours.Attribute("t") == null && (string)firstHours.Element(S + "v") == "0.000001" && (string)firstHours.Attribute("s") == "5", "hours are numeric and display up to six fractional places");
                var firstAmount = cells.Single(c => (string)c.Attribute("r") == "E" + firstLine);
                Assert((string)firstAmount.Element(S + "v") == "0.01", "per-line amount is rounded to cents in Excel");
                var totalRow = worksheet.Descendants(S + "row").Single(r => r.Elements(S + "c").Any(c => CellText(c) == "Итого"));
                Assert(totalRow.Elements(S + "c").Any(c => (string)c.Element(S + "v") == "1.005001") && totalRow.Elements(S + "c").Any(c => (string)c.Element(S + "v") == "2.02"), "Excel totals use exact hours and sum rounded line amounts");
                Assert(worksheet.Root.Element(S + "autoFilter") == null && worksheet.Root.Element(S + "mergeCells") != null, "document layout has merged heading/requisites and no registry filter");
                Assert((string)styles.Root.Element(S + "cellXfs").Attribute("count") == "10" && styles.Descendants(S + "numFmt").Any(f => (string)f.Attribute("formatCode") == "0.######"), "document numeric/text styles form a complete stylesheet");
            }
            act.Set("direction", "expense"); HourlyActDocuments.WriteHtml(htmlPath, store, act); html = File.ReadAllText(htmlPath);
            Assert(html.Contains("<b>Исполнитель:</b> Наименование: Заказчик &lt;старый&gt;") && html.Contains("<b>Заказчик:</b> Наименование: Исполнитель &lt;старый&gt;"), "expense acts correctly reverse executor and customer");
            // Hourly editing maintains workItems; unit conversion deliberately removes it.
            act.Set(ActDocument.ItemsField, ActDocument.SerializeItems(new[] { new ActPrintLine { Description = "Старый кэш печати", Unit = "усл.", Quantity = 1, Price = 2.02m } }));
            HourlyActDocuments.WriteXlsx(xlsxPath, store, act);
            using (var zip = ZipFile.OpenRead(xlsxPath))
            {
                var worksheet = PackageXml(zip, "xl/worksheets/sheet1.xml");
                Assert(!worksheet.Descendants(S + "t").Any(t => t.Value == "Старый кэш печати") && worksheet.Descendants(S + "t").Any(t => t.Value == description), "authoritative hourly descriptions override stale print JSON in Excel");
                Assert(worksheet.Descendants(S + "t").Any(t => t.Value == "Часы") && worksheet.Descendants(S + "v").Any(v => v.Value == "0.000001"), "hourly headers and quantities follow authoritative workItems");
            }
            var converted = act.Clone(); converted.Fields.Remove("workItems"); converted.Fields.Remove("hours"); converted.Fields.Remove("hourlyRate");
            converted.Set(ActDocument.ItemsField, ActDocument.SerializeItems(new[] { new ActPrintLine { Description = "Работа после смены единицы", Unit = "усл.", Quantity = 1, Price = 2.02m } }));
            HourlyActDocuments.WriteXlsx(xlsxPath, store, converted);
            using (var zip = ZipFile.OpenRead(xlsxPath))
            {
                var worksheet = PackageXml(zip, "xl/worksheets/sheet1.xml");
                Assert(worksheet.Descendants(S + "t").Any(t => t.Value == "Работа после смены единицы") && worksheet.Descendants(S + "t").Any(t => t.Value == "усл."), "converted hourly act exports edited general positions and units");
                Assert(worksheet.Descendants(S + "t").Any(t => t.Value == "Количество") && !worksheet.Descendants(S + "t").Any(t => t.Value == "Часы"), "converted act uses general quantity header");
            }
            var legacy = new Record("act"); legacy.Set("number", "А-старый"); legacy.Set("date", "2026-10-12"); legacy.Set("amount", "15.70"); legacy.Set("currency", "BYN"); legacy.Set("description", "Старый акт"); legacy.Set("partyId", party.Id);
            HourlyActDocuments.WriteXlsx(xlsxPath, store, legacy);
            using (var zip = ZipFile.OpenRead(xlsxPath))
            {
                var worksheet = PackageXml(zip, "xl/worksheets/sheet1.xml");
                Assert(worksheet.Descendants(S + "t").Any(t => t.Value == "Старый акт") && worksheet.Descendants(S + "v").Any(v => v.Value == "15.70"), "old acts without hourly lines remain exportable");
            }
            Assert(!Directory.GetFiles(root, ".ledger-export-*").Any(), "atomic export leaves no temporary files");
            Console.WriteLine("Hourly act document tests passed.");
        }
    }
}