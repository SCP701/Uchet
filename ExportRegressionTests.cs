using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace WorkLedger
{
    public static class ExportRegressionTests
    {
        static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string CsvMetadata = "Защита текста CSV", CsvSentinel = "Учёт CSV v1";
        static void Assert(bool condition, string reason) { if (!condition) throw new Exception("Export regression test failed: " + reason); }
        static string Text(XElement cell) { return String.Concat(cell.Descendants(S + "t").Select(t => t.Value)); }
        static XDocument Sheet(string path)
        {
            using (var zip = ZipFile.OpenRead(path))
            using (var stream = zip.GetEntry("xl/worksheets/sheet1.xml").Open()) return XDocument.Load(stream);
        }
        static string Quoted(string value) { return "\"" + value.Replace("\"", "\"\"") + "\""; }
        public static void Run(string path)
        {
            string root = Path.Combine(path, "export-regressions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            NumericPayments(root);
            CsvProtection(root);
            ActOperations(root);
            LongPlainActs(root);
            DescriptiveActOperations(root);
            Console.WriteLine("Export regression tests passed.");
        }
        static void NumericPayments(string root)
        {
            string path = Path.Combine(root, "payments.xlsx");
            var headers = new List<string> { "Номер", "Белая часть", "Серая часть (BYN)", "Зачтено в этом разделе (USD)", "Аванс в этом разделе", "Описание" };
            TableIO.WriteXlsx(path, headers, new List<List<string>>
            {
                new List<string> { "0007", "1 234,50", "56.70", "0,50", "1\u00a0234,56", "=1+1" },
                new List<string> { "0008", "-10.25", "0", "-0,50", "10", "Обычный текст" }
            });
            var sheet = Sheet(path); var cells = sheet.Descendants(S + "c").ToList();
            foreach (string address in new[] { "B2", "C2", "D2", "E2", "B3", "C3", "D3", "E3" })
                Assert(cells.Single(c => (string)c.Attribute("r") == address).Attribute("t") == null, "payment portion is numeric: " + address);
            Assert(Decimal.Parse((string)cells.Single(c => (string)c.Attribute("r") == "B2").Element(S + "v"), CultureInfo.InvariantCulture) == 1234.50m, "localized white portion preserves amount");
            Assert(Decimal.Parse((string)cells.Single(c => (string)c.Attribute("r") == "E2").Element(S + "v"), CultureInfo.InvariantCulture) == 1234.56m, "NBSP amount is numeric");
            Assert(cells.Single(c => (string)c.Attribute("r") == "A2").Attribute("t").Value == "inlineStr" && Text(cells.Single(c => (string)c.Attribute("r") == "A2")) == "0007", "identifier leading zeroes stay text");
            Assert(cells.Single(c => (string)c.Attribute("r") == "F2").Attribute("t").Value == "inlineStr" && !sheet.Descendants(S + "f").Any(), "formula-looking descriptions stay inline text in Excel");
        }
        static void CsvProtection(string root)
        {
            string path = Path.Combine(root, "protected.csv");
            var headers = new List<string> { "Описание", "Сумма", "Номер", "=Заголовок", "'Свой заголовок", CsvMetadata };
            var dangerous = new[]
            {
                "=1+1", "+1+2", "-1+2", "@SUM(1;2)", " \t=1+1", "\r\n+1+2", "\u0001-1+2",
                "\u200B@SUM(1;2)", "\uFEFF=1+1", "\uFF1D1+1", "\uFF0B1+1", "\uFF0D1+1", "\uFF20SUM(1)",
                "\tОбычный текст", "\u200BОбычный текст", "\r", "'Обычный текст", "''Обычный текст", "'=1+1",
                "Обычный; \"текст\"\nВторая строка", "Проверка -10 и @контакт", ""
            };
            var rows = dangerous.Select((value, index) => new List<string> { value, index == 0 ? "-12.50" : index == 1 ? "\t-12.50" : index == 2 ? "-1+2" : index == 3 ? "=1+1" : "1234.56", "0007", value, "'значение", value }).ToList();
            TableIO.WriteCsv(path, headers, rows);
            string csv = File.ReadAllText(path, Encoding.UTF8);
            Assert(csv.StartsWith("\"Описание\";\"Сумма\";", StringComparison.Ordinal), "real CSV headers remain the first record");
            foreach (string value in dangerous.Take(16))
                Assert(csv.Contains(Quoted("'" + value)), "formula/control-leading text has a protective apostrophe: " + value.Length);
            Assert(csv.Contains(Quoted("''Обычный текст")) && csv.Contains(Quoted("'''Обычный текст")) && csv.Contains(Quoted("''=1+1")), "literal leading apostrophes are escaped reversibly");
            Assert(csv.Contains(Quoted("'=Заголовок")) && csv.Contains(Quoted("''Свой заголовок")), "headers use the same safe text boundary");
            Assert(csv.Contains(Quoted("-12.50")) && csv.Contains(Quoted("'\t-12.50")) && csv.Contains(Quoted("'-1+2")) && csv.Contains(Quoted("'=1+1")), "strict numeric negatives remain numbers while malformed or control-prefixed amounts are protected");
            Assert(csv.Contains(Quoted("Обычный; \"текст\"\nВторая строка")), "ordinary quoted multiline text is unchanged");
            var loaded = TableIO.Read(path);
            Assert(loaded.Count == rows.Count, "metadata does not become an extra data row");
            for (int row = 0; row < rows.Count; row++)
                for (int column = 0; column < headers.Count; column++)
                    Assert(loaded[row].Count == headers.Count && loaded[row][headers[column]] == rows[row][column], "protected CSV roundtrip preserves exact cell " + row + ":" + column);
            StockCsvConsumer(path, headers.Count, rows.Count);
            string blankLines = Path.Combine(root, "protected-with-blank-lines.csv");
            File.WriteAllText(blankLines, "\r\n" + csv + "\r\n\r\n", new UTF8Encoding(true));
            var withBlanks = TableIO.Read(blankLines);
            Assert(withBlanks.Count == rows.Count && withBlanks[0]["Описание"] == "=1+1" && withBlanks[16]["Описание"] == "'Обычный текст", "blank CSV lines remain ignorable without losing reversible escaping");
            foreach (string extension in new[] { ".tsv", ".txt" })
            {
                string alias = Path.Combine(root, "protected-alias" + extension); File.Copy(path, alias);
                Assert(TableIO.Read(alias)[0]["Описание"] == "=1+1", "tagged format stays reversible through supported text alias " + extension);
            }
            string legacy = Path.Combine(root, "legacy.csv");
            File.WriteAllText(legacy, "\"Описание\";\"Сумма\"\r\n\"'=1+1\";\"-12.50\"\r\n\"=1+1\";\"1234.56\"\r\n", new UTF8Encoding(true));
            var original = TableIO.Read(legacy);
            Assert(original.Count == 2 && original[0]["Описание"] == "'=1+1" && original[1]["Описание"] == "=1+1", "untagged legacy CSV does not reinterpret apostrophes or formulas");
            string collision = Path.Combine(root, "metadata-lookalike.csv");
            File.WriteAllText(collision, "\"Описание\";" + Quoted(CsvMetadata) + "\r\n\"'=1+1\";\"Обычные данные\"\r\n\"''другая\";" + Quoted(CsvSentinel) + "\r\n", new UTF8Encoding(true));
            var lookalike = TableIO.Read(collision);
            Assert(lookalike[0]["Описание"] == "'=1+1" && lookalike[0][CsvMetadata] == "Обычные данные" && lookalike[1]["Описание"] == "''другая", "legacy metadata lookalike is untouched unless every row proves the exact version");
            string unique = Path.Combine(root, "metadata-collision.csv");
            var actualHeaders = new List<string> { CsvMetadata, CsvMetadata + " (2)", "Описание" };
            var actualValues = new List<string> { "'=1+1", "данные", "=1+1" };
            TableIO.WriteCsv(unique, actualHeaders, new List<List<string>> { actualValues });
            Assert(File.ReadAllText(unique).Split('\n')[0].TrimEnd('\r').EndsWith(Quoted(CsvMetadata + " (3)"), StringComparison.Ordinal), "metadata header avoids real header collisions");
            var uniqueValues = TableIO.Read(unique)[0];
            for (int i = 0; i < actualHeaders.Count; i++) Assert(uniqueValues[actualHeaders[i]] == actualValues[i], "metadata-named real fields roundtrip");
            string tsv = Path.Combine(root, "legacy.tsv");
            File.WriteAllText(tsv, "Описание\tСумма\r\n'=1+1\t-12.50\r\n", new UTF8Encoding(true));
            Assert(TableIO.Read(tsv)[0]["Описание"] == "'=1+1", "legacy TSV remains unchanged");
            string wide = Path.Combine(root, "wide.csv");
            var wideHeaders = Enumerable.Range(0, 256).Select(i => i == 0 ? "=Поле0" : "Поле" + i).ToList();
            var wideValues = Enumerable.Range(0, 256).Select(i => i == 0 ? "=1+1" : "'текст" + i).ToList();
            TableIO.WriteCsv(wide, wideHeaders, new List<List<string>> { wideValues });
            var wideRead = TableIO.Read(wide);
            Assert(wideRead.Count == 1 && wideRead[0].Count == 256, "256 real fields survive the extra owned metadata column");
            for (int i = 0; i < 256; i++) Assert(wideRead[0][wideHeaders[i]] == wideValues[i], "wide CSV exact field " + i);
            string tooWide = Path.Combine(root, "untagged-257.csv");
            File.WriteAllText(tooWide, String.Join(";", Enumerable.Range(0, 257).Select(i => Quoted("Поле" + i))) + "\r\n" + String.Join(";", Enumerable.Range(0, 257).Select(i => Quoted("значение"))) + "\r\n", new UTF8Encoding(true));
            bool rejected = false; try { TableIO.Read(tooWide); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "untagged 257 columns retain the existing import limit");
            string empty = Path.Combine(root, "empty.csv");
            TableIO.WriteCsv(empty, new List<string> { "=Заголовок", "'Данные" }, new List<List<string>>());
            Assert(TableIO.Read(empty).Count == 0, "empty own CSV has no metadata or header pseudo-record");
        }
        static void StockCsvConsumer(string path, int originalColumns, int dataRows)
        {
            var parserType = Type.GetType("Microsoft.VisualBasic.FileIO.TextFieldParser, Microsoft.VisualBasic, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", true);
            var parser = Activator.CreateInstance(parserType, new object[] { path, Encoding.UTF8 });
            using ((IDisposable)parser)
            {
                var kind = parserType.GetProperty("TextFieldType");
                kind.SetValue(parser, Enum.Parse(kind.PropertyType, "Delimited"), null);
                parserType.GetMethod("SetDelimiters").Invoke(parser, new object[] { new[] { ";" } });
                parserType.GetProperty("HasFieldsEnclosedInQuotes").SetValue(parser, true, null);
                var read = parserType.GetMethod("ReadFields");
                string[] header = (string[])read.Invoke(parser, null);
                Assert(header.Length == originalColumns + 1 && header[0] == "Описание" && header[1] == "Сумма", "stock CSV reader sees real field headers as its first row");
                string[] first = (string[])read.Invoke(parser, null);
                Assert(first.Length == header.Length && first[0] == "'=1+1" && first[1] == "-12.50" && first[first.Length - 1] == CsvSentinel, "stock CSV reader sees protected data immediately after headers");
                int count = 1;
                while (!(bool)parserType.GetProperty("EndOfData").GetValue(parser, null))
                {
                    string[] row = (string[])read.Invoke(parser, null);
                    Assert(row.Length == header.Length, "stock reader header/data widths remain consistent"); count++;
                }
                Assert(count == dataRows, "stock CSV reader sees no extra metadata records");
            }
        }
        static void ActOperations(string root)
        {
            var store = new Store(Path.Combine(root, "data"));
            var party = new Record("party"); party.Set("name", "Заказчик"); party.Set("role", "client"); store.Upsert(party);
            var act = new Record("act"); act.Set("number", "EXP-5"); act.Set("date", "2026-09-30"); act.Set("currency", "BYN");
            act.Set("amount", "390.00"); act.Set("direction", "income"); act.Set("partyId", party.Id); act.Set("status", "draft"); act.Set("description", "Общее описание");
            var lines = new List<ActPrintLine>
            {
                new ActPrintLine { Description = "Подготовка данных", Unit = "усл.", Quantity = 1, Price = 50 },
                new ActPrintLine { Description = "Настройка учёта", Unit = "ч", Quantity = 2, Price = 40 },
                new ActPrintLine { Description = "Перенос остатков", Unit = "усл.", Quantity = 1, Price = 90 },
                new ActPrintLine { Description = "Проверка справочников", Unit = "усл.", Quantity = 1, Price = 70 },
                new ActPrintLine { Description = "Консультация", Unit = "ч", Quantity = 2.5m, Price = 40 }
            };
            act.Set(ActDocument.ItemsField, ActDocument.SerializeItems(lines)); store.Upsert(act);
            string original = File.ReadAllText(store.DataPath);
            string xlsx = Path.Combine(root, "operations.xlsx");
            HourlyActDocuments.WriteXlsx(xlsx, store, act);
            var sheet = Sheet(xlsx);
            foreach (var line in lines)
                Assert(sheet.Descendants(S + "row").Count(row => row.Elements(S + "c").Any(cell => Text(cell) == line.Description)) == 1, "each structured operation has its own row");
            var header = sheet.Descendants(S + "row").Single(row => row.Elements(S + "c").Any(cell => Text(cell) == "Описание выполненных работ"));
            int first = Int32.Parse((string)header.Attribute("r"), CultureInfo.InvariantCulture) + 1;
            Assert(sheet.Descendants(S + "c").Single(cell => (string)cell.Attribute("r") == "D" + (first + 4)).Element(S + "v").Value == "2.5", "normal act quantity remains numeric and fractional");
            var total = sheet.Descendants(S + "row").Single(row => row.Elements(S + "c").Any(cell => Text(cell) == "Итого"));
            Assert(total.Elements(S + "c").Single(cell => ((string)cell.Attribute("r")).StartsWith("F", StringComparison.Ordinal)).Element(S + "v").Value == "390.00", "structured grand total equals recorded amount");
            var legacy = act.Clone(); legacy.Set("number", "EXP-7"); legacy.Set(ActDocument.ItemsField, ""); legacy.Set("amount", "1050.00");
            legacy.Set("description", "Системное администрирование\n1. Перенесены данные 2. Настроен учёт 3. Заполнена политика 4. Заполнены справочники 5. Созданы остатки 6. Перенесены документы 7. Заполнены договоры");
            HourlyActDocuments.WriteXlsx(xlsx, store, legacy);
            sheet = Sheet(xlsx);
            header = sheet.Descendants(S + "row").Single(row => row.Elements(S + "c").Any(cell => Text(cell) == "Описание выполненных работ"));
            first = Int32.Parse((string)header.Attribute("r"), CultureInfo.InvariantCulture) + 1;
            string[] names = { "Перенесены данные", "Настроен учёт", "Заполнена политика", "Заполнены справочники", "Созданы остатки", "Перенесены документы", "Заполнены договоры" };
            for (int index = 0; index < names.Length; index++)
            {
                var row = sheet.Descendants(S + "row").Single(r => (string)r.Attribute("r") == (first + index).ToString(CultureInfo.InvariantCulture));
                Assert(row.Elements(S + "c").Any(c => Text(c) == names[index]), "numbered legacy operation appears in separate row " + (index + 1));
                Assert(row.Elements(S + "c").Where(c => ((string)c.Attribute("r")).StartsWith("D", StringComparison.Ordinal) || ((string)c.Attribute("r")).StartsWith("E", StringComparison.Ordinal) || ((string)c.Attribute("r")).StartsWith("F", StringComparison.Ordinal)).All(c => Text(c) == "—"), "legacy operation does not invent quantity, price or allocation");
            }
            Assert(sheet.Descendants(S + "t").Any(t => t.Value == "Выполненные работы: Системное администрирование"), "legacy heading remains visible");
            total = sheet.Descendants(S + "row").Single(row => row.Elements(S + "c").Any(cell => Text(cell) == "Итого"));
            Assert(total.Elements(S + "c").Single(cell => ((string)cell.Attribute("r")).StartsWith("F", StringComparison.Ordinal)).Element(S + "v").Value == "1050.00", "legacy grand total remains recorded amount");
            var longNames = Enumerable.Range(1, 7).Select(i => "Операция " + i + ": " + new String('я', 350)).ToArray();
            legacy.Set("description", "Длинный старый перечень\n" + String.Join("\n", longNames.Select((name, index) => (index + 1) + ". " + name)));
            string displaySummary;
            var display = ActHtmlDocument.ReadDisplayRows(legacy, out displaySummary);
            Assert(display.Count == 7 && display.All(line => !line.Amount.HasValue), "long numbered legacy descriptions split before aggregate-description validation");
            HourlyActDocuments.WriteXlsx(xlsx, store, legacy);
            sheet = Sheet(xlsx);
            foreach (string name in longNames) Assert(sheet.Descendants(S + "row").Count(row => row.Elements(S + "c").Any(cell => Text(cell) == name)) == 1, "long legacy operations remain separate complete rows");
            Assert(File.ReadAllText(store.DataPath) == original, "document exports do not change stored records");
        }

        static void DescriptiveActOperations(string root)
        {
            var store = new Store(Path.Combine(root, "descriptive-data"));
            var own = new Record("settings"); own.Set("name", "Тестовая организация"); own.Set("currency", "BYN"); store.Upsert(own);
            var party = new Record("party"); party.Set("name", "Заказчик отдельных работ"); party.Set("role", "client"); store.Upsert(party);
            XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            string[][] cases = {
                new[] { "Единственная операция без цены" },
                Enumerable.Range(1, 7).Select(i => "Отдельная операция " + i).ToArray(),
                new[] { new string('Ю', 2000) }
            };
            foreach (string[] descriptions in cases)
            {
                var act = new Record("act"); act.Set("number", "DESCRIPTIVE-" + descriptions.Length + "-" + descriptions[0].Length);
                act.Set("date", "2026-10-13"); act.Set("partyId", party.Id); act.Set("direction", "income"); act.Set("currency", "BYN");
                act.Set("status", "draft"); act.Set("amount", "1050.00");
                // A stale old body must not replace authoritative separate descriptions,
                // including the old long-plain Excel compatibility branch.
                act.Set("description", new string('Я', 2300));
                act.Set(ActHtmlDocument.DescriptiveItemsField, ActHtmlDocument.SerializeDescriptions(descriptions));
                store.Upsert(act); act = store.Find(act.Id);
                long revision = store.Revision; byte[] before = File.ReadAllBytes(store.DataPath);
                string xlsx = Path.Combine(root, act.Get("number") + ".xlsx"), word = Path.Combine(root, act.Get("number") + ".docx");
                HourlyActDocuments.WriteXlsx(xlsx, store, act); ActDocument.Export(store, act, word);
                XDocument sheet = Sheet(xlsx), document;
                using (var zip = ZipFile.OpenRead(word))
                using (var stream = zip.GetEntry("word/document.xml").Open()) document = XDocument.Load(stream);
                foreach (string description in descriptions)
                {
                    var excelRows = sheet.Descendants(S + "row").Where(row => row.Elements(S + "c").Any(cell => Text(cell) == description)).ToList();
                    Assert(excelRows.Count == 1, "descriptive operation has one Excel row");
                    var excelRow = excelRows.Single();
                    foreach (string col in new[] { "D", "E", "F" })
                        Assert(Text(excelRow.Elements(S + "c").Single(cell => ((string)cell.Attribute("r")).StartsWith(col, StringComparison.Ordinal))) == "—",
                            "unpriced descriptive Excel operation does not invent " + col);
                    var wordRows = document.Descendants(W + "tr").Where(row => row.Elements(W + "tc").Any(cell => String.Concat(cell.Descendants(W + "t").Select(x => x.Value)) == description)).ToList();
                    Assert(wordRows.Count == 1 && wordRows[0].Elements(W + "tc").Count() == 9, "descriptive operation has one main Word row");
                    foreach (int index in new[] { 3, 4, 5 })
                        Assert(String.Concat(wordRows[0].Elements(W + "tc").ElementAt(index).Descendants(W + "t").Select(x => x.Value)) == "—",
                            "unpriced descriptive Word operation has no guessed quantity/price/sum");
                }
                var total = sheet.Descendants(S + "row").Single(row => row.Elements(S + "c").Any(cell => Text(cell) == "Итого"));
                Assert(total.Elements(S + "c").Single(cell => ((string)cell.Attribute("r")).StartsWith("F", StringComparison.Ordinal)).Element(S + "v").Value == "1050.00",
                    "descriptive Excel grand total preserves ledger amount");
                Assert(document.Descendants(W + "t").Any(x => x.Value.Replace("\u00A0", "").Replace("\u202F", "").Replace(" ", "") == "1050,00"), "descriptive Word grand total preserves ledger amount");
                Assert(store.Revision == revision && before.SequenceEqual(File.ReadAllBytes(store.DataPath)), "descriptive document exports do not change ledger");
            }
        }

        static void LongPlainActs(string root)
        {
            var store = new Store(Path.Combine(root, "long-data"));
            var party = new Record("party"); party.Set("name", "Заказчик"); party.Set("role", "client"); store.Upsert(party);
            foreach (string description in new[] { new string('Я', 2001), new string('У', 2000) + " за сентябрь 2026" })
            {
                var act = new Record("act"); act.Set("number", "LONG-" + description.Length); act.Set("date", "2026-09-30"); act.Set("currency", "BYN");
                act.Set("amount", "75.00"); act.Set("direction", "income"); act.Set("partyId", party.Id); act.Set("status", "draft"); act.Set("description", description);
                store.Upsert(act); act = store.Find(act.Id);
                string before = File.ReadAllText(store.DataPath);
                string path = Path.Combine(root, "long-" + description.Length + ".xlsx");
                HourlyActDocuments.WriteXlsx(path, store, act); var sheet = Sheet(path);
                Assert(sheet.Descendants(S + "t").Any(t => t.Value == description), "accepted long plain/monthly description exports in full");
                var total = sheet.Descendants(S + "row").Single(row => row.Elements(S + "c").Any(cell => Text(cell) == "Итого"));
                Assert(total.Elements(S + "c").Any(cell => (string)cell.Element(S + "v") == "75.00"), "long act keeps its recorded amount");
                Assert(File.ReadAllText(store.DataPath) == before, "long plain export leaves ledger unchanged");
                var structured = act.Clone();
                // Structured descriptions still obey their existing 2000-character limit.
                structured.Set(ActDocument.ItemsField, "[{\"Description\":\"" + description + "\",\"Unit\":\"усл.\",\"Quantity\":1,\"Price\":75}]");
                bool rejected = false; try { HourlyActDocuments.WriteXlsx(path, store, structured); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "long plain compatibility does not relax structured validation");
            }
        }
    }
}
