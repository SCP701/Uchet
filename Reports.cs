using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WorkLedger
{
    public static class TableIO
    {
        const int MaxRows = 100000, MaxColumns = 256;
        const long MaxInputBytes = 128L * 1024 * 1024;
        const string SafeCsvHeader = "Защита текста CSV", SafeCsvSentinel = "Учёт CSV v1";
        const string SheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        static readonly XNamespace S = SheetNamespace;

        public static List<Dictionary<string, string>> Read(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.", path);
            if (new FileInfo(path).Length > MaxInputBytes) throw new InvalidDataException("Файл слишком большой (максимум 128 МБ).");
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".xlsx") return ReadXlsx(path);
            if (extension != ".csv" && extension != ".tsv" && extension != ".txt") throw new InvalidDataException("Выберите CSV или XLSX. Старый XLS нужно сохранить как XLSX.");
            string contents;
            try { contents = File.ReadAllText(path, new UTF8Encoding(false, true)); }
            catch (DecoderFallbackException) { contents = File.ReadAllText(path, Encoding.GetEncoding(1251)); }
            if (contents.Length > 0 && contents[0] == '\uFEFF') contents = contents.Substring(1);
            var matrix = ParseCsv(contents, DetectDelimiter(contents));
            var protectedRows = matrix.Where(row => row.Any(value => !String.IsNullOrWhiteSpace(value))).ToList();
            bool protectedText = StripCsvMetadata(protectedRows);
            if (!protectedText && matrix.Any(row => row.Count > MaxColumns)) throw new InvalidDataException("Максимум 256 столбцов для импорта.");
            if (protectedText)
            {
                matrix = protectedRows;
                foreach (var row in matrix)
                    for (int column = 0; column < row.Count; column++) row[column] = DecodeCsvText(row[column]);
            }
            return ToRecords(matrix);
        }

        public static void WriteCsv(string path, List<string> headers, List<List<string>> rows)
        {
            ValidateTable(headers, rows);
            AtomicWrite(path, delegate(string temporary)
            {
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(true)))
                {
                    string metadataHeader = CsvMetadataHeader(headers);
                    writer.WriteLine(String.Join(";", headers.Select(value => CsvCell(EncodeCsvText("", value, true))).Concat(new[] { CsvCell(metadataHeader) })));
                    foreach (var row in rows) writer.WriteLine(String.Join(";", Enumerable.Range(0, headers.Count).Select(i => CsvCell(EncodeCsvText(headers[i], i < row.Count ? row[i] : "", false))).Concat(new[] { CsvCell(SafeCsvSentinel) })));
                }
            });
        }

        public static void WriteXlsx(string path, List<string> headers, List<List<string>> rows)
        {
            ValidateTable(headers, rows);
            AtomicWrite(path, delegate(string temporary)
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    AddXml(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                    AddXml(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                    AddXml(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"" + SheetNamespace + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Учет работ\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                    AddXml(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                    AddXml(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"" + SheetNamespace + "\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/><xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
                    var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
                    using (var destination = entry.Open())
                    using (var xml = XmlWriter.Create(destination, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize }))
                    {
                        xml.WriteStartDocument(); xml.WriteStartElement("worksheet", SheetNamespace);
                        xml.WriteStartElement("sheetViews"); xml.WriteStartElement("sheetView"); xml.WriteAttributeString("workbookViewId", "0");
                        xml.WriteStartElement("pane"); xml.WriteAttributeString("ySplit", "1"); xml.WriteAttributeString("topLeftCell", "A2"); xml.WriteAttributeString("activePane", "bottomLeft"); xml.WriteAttributeString("state", "frozen"); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
                        xml.WriteStartElement("cols");
                        for (int column = 0; column < headers.Count; column++)
                        {
                            int width = (headers[column] ?? "").Length;
                            foreach (var row in rows.Take(100)) if (column < row.Count) width = Math.Max(width, (row[column] ?? "").Length);
                            xml.WriteStartElement("col"); xml.WriteAttributeString("min", (column + 1).ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("max", (column + 1).ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("width", Math.Min(60, Math.Max(14, width + 2)).ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("customWidth", "1"); xml.WriteEndElement();
                        }
                        xml.WriteEndElement(); xml.WriteStartElement("sheetData");
                        WriteRow(xml, headers, headers, 1, true);
                        for (int i = 0; i < rows.Count; i++) WriteRow(xml, rows[i], headers, i + 2, false);
                        xml.WriteEndElement();
                        xml.WriteStartElement("autoFilter"); xml.WriteAttributeString("ref", "A1:" + ColumnName(headers.Count - 1) + (rows.Count + 1).ToString(CultureInfo.InvariantCulture)); xml.WriteEndElement();
                        xml.WriteStartElement("pageMargins"); foreach (var name in new[] { "left", "right", "top", "bottom", "header", "footer" }) xml.WriteAttributeString(name, "0.3"); xml.WriteEndElement();
                        xml.WriteStartElement("pageSetup"); xml.WriteAttributeString("orientation", "landscape"); xml.WriteAttributeString("paperSize", "9"); xml.WriteAttributeString("fitToWidth", "1"); xml.WriteAttributeString("fitToHeight", "0"); xml.WriteEndElement();
                        xml.WriteEndElement(); xml.WriteEndDocument();
                    }
                }
            });
        }

        public static void WriteDocumentXlsx(string path, string title, Dictionary<string, string> details, List<string> headers, List<List<string>> rows, string footer)
        {
            ValidateTable(headers, rows);
            if (details != null && details.Count > 100) throw new ArgumentException("Слишком много реквизитов документа.");
            AtomicWrite(path, delegate(string temporary)
            {
                WriteXlsx(temporary, headers, rows);
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Update))
                {
                    var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml"); XDocument sheet = LoadXml(sheetEntry);
                    var data = sheet.Root.Element(S + "sheetData");
                    var prefix = new List<string> { title };
                    if (details != null) prefix.AddRange(details.Select(item => item.Key + ": " + item.Value));
                    prefix.Add("");
                    int offset = prefix.Count; var merges = new List<string>();
                    foreach (var row in data.Elements(S + "row").ToList())
                    {
                        int number = Int32.Parse((string)row.Attribute("r"), CultureInfo.InvariantCulture);
                        row.SetAttributeValue("r", number + offset); decimal height = 32;
                        foreach (var cell in row.Elements(S + "c"))
                        {
                            string reference = (string)cell.Attribute("r"); int column = ColumnIndex(reference);
                            cell.SetAttributeValue("r", ColumnName(column) + (number + offset).ToString(CultureInfo.InvariantCulture));
                            if (number == 1) cell.SetAttributeValue("s", 4);
                            else if (cell.Attribute("t") != null) cell.SetAttributeValue("s", 3);
                            else cell.SetAttributeValue("s", headers[column] == "Часы" || headers[column] == "Количество" ? 5 : 6);
                            string text = String.Concat(cell.Descendants(S + "t").Select(t => t.Value));
                            height = Math.Max(height, DocumentHeight(text, column == 1 ? 58 : 19));
                        }
                        row.SetAttributeValue("ht", height); row.SetAttributeValue("customHeight", 1);
                    }
                    for (int i = prefix.Count - 1; i >= 0; i--) data.AddFirst(DocumentRow(prefix[i], i + 1, i == 0 ? 7 : 8, headers.Count, merges));
                    int footerRow = offset + rows.Count + 3;
                    if (!String.IsNullOrWhiteSpace(footer)) data.Add(DocumentRow(footer, footerRow, 9, headers.Count, merges));
                    var views = sheet.Root.Element(S + "sheetViews"); if (views != null) views.Remove();
                    var filter = sheet.Root.Element(S + "autoFilter"); if (filter != null) filter.Remove();
                    foreach (var column in sheet.Root.Element(S + "cols").Elements(S + "col"))
                    {
                        int number = Int32.Parse((string)column.Attribute("min"), CultureInfo.InvariantCulture);
                        column.SetAttributeValue("width", number == 1 ? 8 : number == 2 ? 58 : 19);
                    }
                    if (merges.Count > 0) data.AddAfterSelf(new XElement(S + "mergeCells", new XAttribute("count", merges.Count), merges.Select(reference => new XElement(S + "mergeCell", new XAttribute("ref", reference)))));
                    sheet.Root.AddFirst(new XElement(S + "sheetPr", new XElement(S + "pageSetUpPr", new XAttribute("fitToPage", 1))));
                    SavePackageXml(zip, sheetEntry, sheet);
                    var workbookEntry = zip.GetEntry("xl/workbook.xml"); XDocument workbook = LoadXml(workbookEntry);
                    workbook.Descendants(S + "sheet").First().SetAttributeValue("name", "Акт"); SavePackageXml(zip, workbookEntry, workbook);
                    var styleEntry = zip.GetEntry("xl/styles.xml"); XDocument styles = LoadXml(styleEntry);
                    styles.Root.AddFirst(new XElement(S + "numFmts", new XAttribute("count", 1), new XElement(S + "numFmt", new XAttribute("numFmtId", 165), new XAttribute("formatCode", "0.######"))));
                    AppendXlsxStyle(styles.Root.Element(S + "fonts"), "<font><b/><sz val=\"16\"/><name val=\"Calibri\"/></font>");
                    AppendXlsxStyle(styles.Root.Element(S + "fills"), "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEDF2F6\"/><bgColor indexed=\"64\"/></patternFill></fill>");
                    AppendXlsxStyle(styles.Root.Element(S + "borders"), "<border><left style=\"thin\"/><right style=\"thin\"/><top style=\"thin\"/><bottom style=\"thin\"/><diagonal/></border>");
                    var formats = styles.Root.Element(S + "cellXfs");
                    AppendXlsxStyle(formats, "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>");
                    AppendXlsxStyle(formats, "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>");
                    AppendXlsxStyle(formats, "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"/>");
                    AppendXlsxStyle(formats, "<xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"/>");
                    AppendXlsxStyle(formats, "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>");
                    AppendXlsxStyle(formats, "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>");
                    AppendXlsxStyle(formats, "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>");
                    SavePackageXml(zip, styleEntry, styles);
                }
            });
        }
        static XElement DocumentRow(string value, int number, int style, int columns, List<string> merges)
        {
            if (columns > 1) merges.Add("A" + number + ":" + ColumnName(columns - 1) + number);
            return new XElement(S + "row", new XAttribute("r", number), new XAttribute("ht", Math.Max(style == 7 ? 36 : 22, DocumentHeight(value, 110))), new XAttribute("customHeight", 1),
                new XElement(S + "c", new XAttribute("r", "A" + number), new XAttribute("t", "inlineStr"), new XAttribute("s", style),
                    new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), CleanXml(value)))));
        }
        static decimal DocumentHeight(string value, int width)
        {
            int lines = (value ?? "").Replace("\r", "").Split('\n').Sum(line => Math.Max(1, (line.Length + width - 1) / width));
            return Math.Min(409, lines * 16 + 8);
        }
        static void AppendXlsxStyle(XElement parent, string markup)
        {
            int position = markup.IndexOf('>'); if (markup[position - 1] == '/') position--; parent.Add(XElement.Parse(markup.Insert(position, " xmlns=\"" + SheetNamespace + "\"")));
            parent.SetAttributeValue("count", parent.Elements().Count());
        }
        static void SavePackageXml(ZipArchive zip, ZipArchiveEntry original, XDocument document)
        {
            string name = original.FullName; original.Delete();
            using (var destination = zip.CreateEntry(name, CompressionLevel.Optimal).Open())
            using (var xml = XmlWriter.Create(destination, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize })) document.Save(xml);
        }
        static List<Dictionary<string, string>> ReadXlsx(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var shared = new List<string>();
                var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
                if (sharedEntry != null)
                {
                    foreach (var item in LoadXml(sharedEntry).Root.Elements(S + "si"))
                    {
                        if (shared.Count >= 1000000) throw new InvalidDataException("В книге слишком много строк.");
                        shared.Add(String.Concat(item.Descendants(S + "t").Select(e => e.Value)));
                    }
                }
                string sheetPath = "xl/worksheets/sheet1.xml";
                var workbook = zip.GetEntry("xl/workbook.xml"); var relations = zip.GetEntry("xl/_rels/workbook.xml.rels");
                if (workbook != null && relations != null)
                {
                    var first = LoadXml(workbook).Descendants(S + "sheet").FirstOrDefault();
                    if (first == null) throw new InvalidDataException("В книге нет листов.");
                    XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                    string relationshipId = (string)first.Attribute(r + "id");
                    var relationship = LoadXml(relations).Root.Elements().FirstOrDefault(e => (string)e.Attribute("Id") == relationshipId);
                    if (relationship == null || (string)relationship.Attribute("TargetMode") == "External") throw new InvalidDataException("Лист книги недоступен.");
                    string target = ((string)relationship.Attribute("Target") ?? "").Replace('\\', '/');
                    sheetPath = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target;
                    if (sheetPath.IndexOf("..", StringComparison.Ordinal) >= 0 || sheetPath.IndexOf(':') >= 0) throw new InvalidDataException("Недопустимая ссылка на лист.");
                }
                var sheetEntry = zip.GetEntry(sheetPath);
                if (sheetEntry == null) throw new InvalidDataException("Не найден первый лист XLSX.");
                var matrix = new List<List<string>>();
                foreach (var row in LoadXml(sheetEntry).Descendants(S + "row"))
                {
                    if (matrix.Count > MaxRows) throw new InvalidDataException("Максимум 100 000 строк для импорта.");
                    var values = new List<string>(); int nextColumn = 0;
                    foreach (var cell in row.Elements(S + "c"))
                    {
                        string reference = (string)cell.Attribute("r"); int column = String.IsNullOrEmpty(reference) ? nextColumn : ColumnIndex(reference);
                        if (column >= MaxColumns || column < 0) throw new InvalidDataException("Максимум 256 столбцов для импорта.");
                        while (values.Count <= column) values.Add("");
                        string type = (string)cell.Attribute("t"), value = (string)cell.Element(S + "v") ?? "";
                        if (type == "inlineStr") value = String.Concat(cell.Descendants(S + "t").Select(e => e.Value));
                        else if (type == "s")
                        {
                            int index; if (!Int32.TryParse(value, out index) || index < 0 || index >= shared.Count) throw new InvalidDataException("Поврежден список строк XLSX.");
                            value = shared[index];
                        }
                        else if (type == "b") value = value == "1" ? "Да" : "Нет";
                        values[column] = value; nextColumn = column + 1;
                    }
                    matrix.Add(values);
                }
                return ToRecords(matrix);
            }
        }
        static XDocument LoadXml(ZipArchiveEntry entry)
        {
            if (entry.Length > 64L * 1024 * 1024) throw new InvalidDataException("Лист XLSX слишком большой.");
            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 })) return XDocument.Load(reader);
        }
        static List<Dictionary<string, string>> ToRecords(List<List<string>> matrix)
        {
            var result = new List<Dictionary<string, string>>(); int headerRow = matrix.FindIndex(r => r.Any(v => !String.IsNullOrWhiteSpace(v)));
            if (headerRow < 0) return result;
            var source = matrix[headerRow]; if (source.Count > MaxColumns) throw new InvalidDataException("Максимум 256 столбцов для импорта.");
            var headers = new List<string>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < source.Count; i++)
            {
                string name = (source[i] ?? "").Trim(); if (name.Length == 0) name = "Столбец " + (i + 1);
                string unique = name; int suffix = 2; while (!names.Add(unique)) unique = name + "_" + suffix++; headers.Add(unique);
            }
            for (int i = headerRow + 1; i < matrix.Count; i++)
            {
                var row = matrix[i]; if (!row.Any(v => !String.IsNullOrWhiteSpace(v))) continue;
                if (result.Count >= MaxRows) throw new InvalidDataException("Максимум 100 000 строк для импорта.");
                if (row.Skip(headers.Count).Any(v => !String.IsNullOrWhiteSpace(v))) throw new InvalidDataException("В строке " + (i + 1) + " больше значений, чем заголовков.");
                var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int column = 0; column < headers.Count; column++) record[headers[column]] = column < row.Count ? row[column] : "";
                result.Add(record);
            }
            return result;
        }
        static char DetectDelimiter(string contents)
        {
            int semicolons = 0, commas = 0, tabs = 0; bool quoted = false;
            for (int i = 0; i < contents.Length; i++)
            {
                char c = contents[i];
                if (c == '"') { if (quoted && i + 1 < contents.Length && contents[i + 1] == '"') i++; else quoted = !quoted; }
                else if (!quoted) { if (c == '\n' || c == '\r') break; if (c == ';') semicolons++; else if (c == ',') commas++; else if (c == '\t') tabs++; }
            }
            if (tabs > semicolons && tabs > commas) return '\t'; return commas > semicolons ? ',' : ';';
        }
        static List<List<string>> ParseCsv(string contents, char delimiter)
        {
            var rows = new List<List<string>>(); var row = new List<string>(); var field = new StringBuilder(); bool quoted = false, justClosed = false;
            for (int i = 0; i < contents.Length; i++)
            {
                char c = contents[i];
                if (quoted)
                {
                    if (c == '"') { if (i + 1 < contents.Length && contents[i + 1] == '"') { field.Append('"'); i++; } else { quoted = false; justClosed = true; } }
                    else field.Append(c);
                }
                else if (c == '"' && field.Length == 0 && !justClosed) quoted = true;
                else if (c == delimiter) { row.Add(field.ToString()); field.Length = 0; justClosed = false; }
                else if (c == '\r' || c == '\n')
                {
                    row.Add(field.ToString()); rows.Add(row); row = new List<string>(); field.Length = 0; justClosed = false;
                    if (c == '\r' && i + 1 < contents.Length && contents[i + 1] == '\n') i++;
                }
                else { if (justClosed && !Char.IsWhiteSpace(c)) throw new InvalidDataException("Недопустимые символы после кавычки CSV."); if (!justClosed) field.Append(c); }
                if (row.Count > MaxColumns + 1 || rows.Count > MaxRows + 1) throw new InvalidDataException("Слишком много строк или столбцов в CSV.");
            }
            if (quoted) throw new InvalidDataException("В CSV не закрыта кавычка.");
            if (field.Length > 0 || row.Count > 0 || justClosed) { row.Add(field.ToString()); rows.Add(row); } return rows;
        }
        // Real CSV headers remain the first row. The final versioned metadata
        // column identifies reversible escaping without changing legacy imports.
        static string CsvMetadataHeader(List<string> headers)
        {
            var names = new HashSet<string>(headers.Select(value => (value ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
            string candidate = SafeCsvHeader; int suffix = 2;
            while (names.Contains(candidate)) candidate = SafeCsvHeader + " (" + (suffix++).ToString(CultureInfo.InvariantCulture) + ")";
            return candidate;
        }
        static bool CsvMetadataName(string name)
        {
            if (name == SafeCsvHeader) return true;
            string prefix = SafeCsvHeader + " (";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(")", StringComparison.Ordinal)) return false;
            string suffix = name.Substring(prefix.Length, name.Length - prefix.Length - 1); int number;
            return Int32.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 2 && suffix == number.ToString(CultureInfo.InvariantCulture);
        }
        static bool StripCsvMetadata(List<List<string>> matrix)
        {
            if (matrix.Count == 0 || matrix[0].Count < 2 || matrix[0].Count > MaxColumns + 1) return false;
            int width = matrix[0].Count;
            if (!CsvMetadataName(matrix[0][width - 1])) return false;
            if (matrix.Skip(1).Any(row => row.Count != width || row[width - 1] != SafeCsvSentinel)) return false;
            foreach (var row in matrix) row.RemoveAt(width - 1);
            return true;
        }
        static string EncodeCsvText(string header, string value, bool isHeader)
        {
            value = value ?? ""; decimal numeric;
            if (!isHeader && !CsvLeadingControls(value) && TryNumericCell(header, value, out numeric)) return value;
            if (value.StartsWith("'", StringComparison.Ordinal) || CsvFormulaRisk(value)) return "'" + value;
            return value;
        }
        static string DecodeCsvText(string value)
        {
            if (value.StartsWith("''", StringComparison.Ordinal)) return value.Substring(1);
            if (value.StartsWith("'", StringComparison.Ordinal) && CsvFormulaRisk(value.Substring(1))) return value.Substring(1);
            return value;
        }
        static bool CsvFormulaRisk(string value)
        {
            int position = 0;
            while (position < value.Length && (Char.IsWhiteSpace(value[position]) || Char.IsControl(value[position]) || Char.GetUnicodeCategory(value[position]) == UnicodeCategory.Format)) position++;
            if (CsvLeadingControls(value)) return true;
            if (position == value.Length) return false;
            char first = value[position];
            return first == '=' || first == '+' || first == '-' || first == '@' || first == '\uFF1D' || first == '\uFF0B' || first == '\uFF0D' || first == '\uFF20';
        }
        static bool CsvLeadingControls(string value)
        {
            foreach (char character in value)
            {
                if (Char.IsControl(character) || Char.GetUnicodeCategory(character) == UnicodeCategory.Format) return true;
                if (!Char.IsWhiteSpace(character)) return false;
            }
            return false;
        }
        static string CsvCell(string value) { return "\"" + (value ?? "").Replace("\"", "\"\"") + "\""; }
        static void ValidateTable(List<string> headers, List<List<string>> rows)
        {
            if (headers == null || headers.Count == 0 || headers.Count > MaxColumns) throw new ArgumentException("Укажите от 1 до 256 заголовков.");
            if (rows == null || rows.Count > MaxRows) throw new ArgumentException("Максимум 100 000 строк.");
            if (rows.Any(r => r == null || r.Count > headers.Count)) throw new ArgumentException("Число значений превышает число заголовков.");
        }
        static void AddXml(ZipArchive zip, string name, string contents)
        {
            using (var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open()) using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(contents);
        }
        static readonly HashSet<string> NumericHeaders = new HashSet<string>(new[]
        {
            "Сумма", "Сумма за месяц", "Цена", "Количество", "Часы", "Цена за час", "Зачтено", "Остаток", "К оплате", "Удержано", "Прибыль", "Выручка", "Затраты", "Поступило", "Выплачено", "Закрыто", "Аванс", "С допсоглашениями", "Осталось", "Всего", "Оплачено", "Долг", "Выполнено", "Выполнено работ", "Начислено", "Расходы", "Сумма договора", "amount", "price", "quantity", "qty", "paid", "due", "Общий остаток", "Зачтено в акты", "Выручка по актам", "Акты подрядчиков", "Прочие расходы", "Всего затрат", "Поступило денег", "Выплачено денег", "Осталось авансом", "Цена за единицу", "Закрыто актами", "Осталось работ", "Нам должны", "Мы должны", "Изменение сальдо", "Сальдо", "Первоначальная сумма", "Белая часть", "Серая часть", "Зачтено в этом разделе", "Аванс в этом разделе"
        }, StringComparer.OrdinalIgnoreCase);
        static bool TryNumericCell(string header, string value, out decimal number)
        {
            number = 0;
            string label = (header ?? "").Trim(); int suffix = label.LastIndexOf(" ("); if (suffix >= 0 && label.EndsWith(")", StringComparison.Ordinal)) label = label.Substring(0, suffix);
            if (!NumericHeaders.Contains(label)) return false;
            string normalized = (value ?? "").Trim().Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "");
            int comma = normalized.LastIndexOf(','), dot = normalized.LastIndexOf('.');
            if (comma >= 0 && dot >= 0) normalized = comma > dot ? normalized.Replace(".", "").Replace(',', '.') : normalized.Replace(",", "");
            else normalized = normalized.Replace(',', '.');
            return Decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
        }
        static void WriteRow(XmlWriter xml, List<string> values, List<string> headers, int number, bool header)
        {
            xml.WriteStartElement("row"); xml.WriteAttributeString("r", number.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < headers.Count; i++)
            {
                string value = i < values.Count ? values[i] : ""; decimal numeric;
                xml.WriteStartElement("c"); xml.WriteAttributeString("r", ColumnName(i) + number.ToString(CultureInfo.InvariantCulture));
                if (!header && TryNumericCell(headers[i], value, out numeric))
                {
                    xml.WriteAttributeString("s", "2"); xml.WriteStartElement("v"); xml.WriteString(numeric.ToString(CultureInfo.InvariantCulture)); xml.WriteEndElement();
                }
                else
                {
                    xml.WriteAttributeString("t", "inlineStr"); if (header) xml.WriteAttributeString("s", "1");
                    xml.WriteStartElement("is"); xml.WriteStartElement("t"); xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                    xml.WriteString(CleanXml(value)); xml.WriteEndElement(); xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        static string CleanXml(string value)
        {
            if (value == null) return ""; var clean = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (Char.IsHighSurrogate(value[i]) && i + 1 < value.Length && Char.IsLowSurrogate(value[i + 1])) { clean.Append(value[i]); clean.Append(value[++i]); }
                else if (XmlConvert.IsXmlChar(value[i])) clean.Append(value[i]);
            }
            return clean.ToString();
        }
        static string ColumnName(int index) { string result = ""; for (int number = index + 1; number > 0; number = (number - 1) / 26) result = ((char)('A' + (number - 1) % 26)) + result; return result; }
        static int ColumnIndex(string reference)
        {
            int result = 0; foreach (char c in reference) { if (c >= 'A' && c <= 'Z') result = checked(result * 26 + c - 'A' + 1); else if (c >= 'a' && c <= 'z') result = checked(result * 26 + c - 'a' + 1); else break; } return result - 1;
        }
        internal static void AtomicWrite(string path, Action<string> write)
        {
            path = Path.GetFullPath(path); string parent = Path.GetDirectoryName(path); Directory.CreateDirectory(parent);
            string temporary = Path.Combine(parent, ".ledger-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { write(temporary); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public static class PrintReport
    {
        public static string OrganizationDetails(Record organization)
        {
            if (organization == null) return "";
            var fields = new[]
            {
                new[] { "name", "Наименование" }, new[] { "unp", "УНП" },
                new[] { "registrationNumber", "Номер свидетельства / регистрации" }, new[] { "registrationDate", "Дата регистрации" },
                new[] { "registrationAuthority", "Регистрирующий орган" }, new[] { "registrationAddress", organization.Kind == "party" ? "Адрес регистрации / юридический адрес" : "Адрес регистрации" },
                new[] { "postalAddress", "Почтовый адрес" }, new[] { "actualAddress", "Фактический адрес" },
                new[] { "phone", "Телефон" }, new[] { "email", "Электронная почта" }, new[] { "website", "Сайт" },
                new[] { "chiefAccountantName", "Главный бухгалтер" }, new[] { "chiefAccountantPhone", "Телефон главного бухгалтера" },
                new[] { "bankName", "Банк" }, new[] { "bankAccount", "Расчётный счёт (IBAN)" }, new[] { "bankCode", "БИК / SWIFT" },
                new[] { "signatoryName", "ФИО подписанта" }, new[] { "signatoryPosition", "Статус / должность подписанта" },
                new[] { "signatoryBasis", "Основание полномочий подписанта" }, new[] { "vatNote", "Налоги / НДС" },
                new[] { "requisites", "Дополнительные реквизиты" }, new[] { "contact", organization.Kind == "party" ? "Контактное лицо" : "Дополнительные контакты" }
            };
            var lines = new List<string>();
            foreach (var field in fields)
            {
                string value = organization.Get(field[0]);
                if (String.IsNullOrWhiteSpace(value)) continue;
                DateTime date;
                if (field[0] == "registrationDate" && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) value = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                lines.Add(field[1] + ": " + value);
            }
            return String.Join("\n", lines);
        }
        public static void Write(string path, string title, List<string> headers, List<List<string>> rows, string subtitle) { WriteDocument(path, title, new Dictionary<string, string>(), headers, rows, subtitle); }
        public static void Write(string path, string title, List<string> headers, List<List<string>> rows) { Write(path, title, headers, rows, ""); }
        public static void WriteDocument(string path, string title, Dictionary<string, string> details, List<string> headers, List<List<string>> rows, string footer)
        {
            TableIO.AtomicWrite(path, delegate(string temporary)
            {
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(true)))
                {
                    writer.Write("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>" + Escape(title) + "</title><style>body{font:14px Arial,sans-serif;color:#172534;margin:32px auto;max-width:1200px;padding:0 20px}h1{font-size:25px;margin-bottom:8px}p{line-height:1.5}.meta{color:#576574;font-size:12px}.details{display:grid;grid-template-columns:1fr 1fr;gap:7px 24px;margin:22px 0}.details div{overflow-wrap:anywhere;white-space:pre-wrap}table{border-collapse:collapse;width:100%;margin:22px 0;font-size:12px}th,td{border:1px solid #bec7d0;padding:8px;text-align:left;vertical-align:top;overflow-wrap:anywhere;white-space:pre-wrap}th{background:#edf2f6}thead{display:table-header-group}tr{break-inside:avoid}.footer{white-space:pre-wrap;line-height:1.6;margin-top:26px}.toolbar{margin:18px 0}button{background:#245b8a;border:0;color:white;padding:10px 18px;border-radius:6px;cursor:pointer}@media print{body{margin:0;max-width:none;padding:0;font-size:11px}.toolbar{display:none}th{-webkit-print-color-adjust:exact;print-color-adjust:exact}table{font-size:10px}h1{font-size:20px}@page{size:A4 landscape;margin:12mm}}</style></head><body><div class=\"toolbar\"><button onclick=\"window.print()\">Печать / сохранить PDF</button></div><h1>" + Escape(title) + "</h1><p class=\"meta\">Учет работ · Сформировано " + Escape(DateTime.Now.ToString("dd.MM.yyyy HH:mm")) + "</p>");
                    if (details != null && details.Count > 0)
                    {
                        writer.Write("<div class=\"details\">"); foreach (var item in details) writer.Write("<div><b>" + Escape(item.Key) + ":</b> " + Escape(item.Value) + "</div>"); writer.Write("</div>");
                    }
                    if (headers != null && headers.Count > 0)
                    {
                        writer.Write("<table><thead><tr>"); foreach (var header in headers) writer.Write("<th>" + Escape(header) + "</th>"); writer.Write("</tr></thead><tbody>");
                        if (rows != null) foreach (var row in rows) { writer.Write("<tr>"); for (int i = 0; i < headers.Count; i++) writer.Write("<td>" + Escape(row != null && i < row.Count ? row[i] : "") + "</td>"); writer.Write("</tr>"); }
                        writer.Write("</tbody></table>");
                    }
                    if (!String.IsNullOrWhiteSpace(footer)) writer.Write("<div class=\"footer\">" + Escape(footer) + "</div>"); writer.Write("</body></html>");
                }
            });
        }
        public static string Escape(string value) { return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;"); }
    }
    public static class HourlyActDocuments
    {
        sealed class Document
        {
            public string Title, Footer;
            public Dictionary<string, string> Details = new Dictionary<string, string>();
            public List<string> Headers = new List<string>();
            public List<List<string>> Rows = new List<List<string>>();
        }
        public static void WriteHtml(string path, Store store, Record act)
        {
            var document = Build(store, act);
            PrintReport.WriteDocument(path, document.Title, document.Details, document.Headers, document.Rows, document.Footer);
        }
        public static void WriteXlsx(string path, Store store, Record act)
        {
            var document = Build(store, act);
            TableIO.WriteDocumentXlsx(path, document.Title, document.Details, document.Headers, document.Rows, document.Footer);
        }
        static Document Build(Store store, Record act)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (act == null || act.Kind != "act") throw new ArgumentException("Выберите акт выполненных работ.");
            var document = new Document { Title = "Акт выполненных работ № " + act.Get("number") };
            document.Details["Дата"] = DateText(act.Get("date"));
            var own = Store.GetActSnapshot(act, "organizationSnapshot") ?? store.Of("settings").FirstOrDefault();
            var party = Store.GetActSnapshot(act, "partySnapshot") ?? store.Find(act.Get("partyId"));
            var executor = act.Get("direction") == "expense" ? party : own;
            var customer = act.Get("direction") == "expense" ? own : party;
            document.Details["Исполнитель"] = PrintReport.OrganizationDetails(executor);
            document.Details["Заказчик"] = PrintReport.OrganizationDetails(customer);
            var contract = store.Find(act.Get("contractId"));
            if (contract != null) document.Details["Основание"] = "Договор № " + contract.Get("number") + " от " + DateText(contract.Get("date"));
            var project = store.Find(act.Get("projectId"));
            if (project != null) document.Details["Объект"] = project.Get("name") + (project.Get("location") == "" ? "" : " · " + project.Get("location"));
            document.Details["Валюта"] = act.Get("currency");
            if (act.Get("dueDate") != "") document.Details["Срок оплаты"] = DateText(act.Get("dueDate"));
            document.Details["Статус"] = act.Get("status") == "signed" ? "Подписан" : act.Get("status") == "cancelled" ? "Отменён" : "Черновик";
            string summary = "";
            List<ActHtmlLine> lines, numberedOperations; string numberedHeading;
            if (String.IsNullOrWhiteSpace(act.Get("workItems")) && String.IsNullOrWhiteSpace(act.Get(ActDocument.ItemsField)) && String.IsNullOrWhiteSpace(act.Get(ActHtmlDocument.DescriptiveItemsField)) && act.Get("description").Length > 2000 &&
                !ActHtmlDocument.TryReadNumberedOperations(act.Get("description"), out numberedOperations, out numberedHeading))
                lines = ActDocument.ReadItems(act).Select(line => new ActHtmlLine { Description = line.Description, Unit = line.Unit, Quantity = line.Quantity, Price = line.Price, Amount = line.Amount }).ToList();
            else lines = ActHtmlDocument.ReadDisplayRows(act, out summary);
            if (!String.IsNullOrWhiteSpace(summary)) document.Details["Выполненные работы"] = summary;
            decimal total = Store.ParseMoney(act.Get("amount"));
            int number = 1;
            if (act.Get("workItems") != "")
            {
                document.Headers.AddRange(new[] { "№", "Описание выполненных работ", "Часы", "Цена за час (" + act.Get("currency") + ")", "Сумма (" + act.Get("currency") + ")" });
                foreach (var line in lines) document.Rows.Add(new List<string> { (number++).ToString(CultureInfo.InvariantCulture), line.Description, QuantityText(line.Quantity), MoneyText(line.Price), MoneyText(line.Amount) });
                document.Rows.Add(new List<string> { "", "Итого", HoursText(lines.Sum(line => line.Quantity.Value)), "", Store.MoneyString(total) });
            }
            else
            {
                document.Headers.AddRange(new[] { "№", "Описание выполненных работ", "Единица измерения", "Количество", "Цена за единицу (" + act.Get("currency") + ")", "Сумма (" + act.Get("currency") + ")" });
                foreach (var line in lines) document.Rows.Add(new List<string> { (number++).ToString(CultureInfo.InvariantCulture), line.Description, String.IsNullOrWhiteSpace(line.Unit) ? "—" : line.Unit, QuantityText(line.Quantity), MoneyText(line.Price), MoneyText(line.Amount) });
                document.Rows.Add(new List<string> { "", "Итого", "", "", "", Store.MoneyString(total) });
            }
            document.Footer = "Всего на сумму: " + Store.MoneyString(Store.ParseMoney(act.Get("amount"))) + " " + act.Get("currency") + ".";
            if (act.Get("notes") != "") document.Footer += "\nПримечания: " + act.Get("notes");
            document.Footer += "\n\nИсполнитель ____________________ " + Signatory(executor) + "\nЗаказчик ____________________ " + Signatory(customer);
            return document;
        }
        static string DateText(string value)
        {
            DateTime date; return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : value;
        }
        static string HoursText(decimal hours) { return hours.ToString("0.######", CultureInfo.InvariantCulture); }
        static string QuantityText(decimal? value) { return value.HasValue ? HoursText(value.Value) : "—"; }
        static string MoneyText(decimal? value) { return value.HasValue ? Store.MoneyString(value.Value) : "—"; }
        static string Signatory(Record organization)
        {
            if (organization == null) return "";
            return String.Join(" ", new[] { organization.Get("signatoryPosition"), organization.Get("signatoryName") }.Where(value => !String.IsNullOrWhiteSpace(value)));
        }
    }
    public static class BackupArchive
    {
        const long MaxArchive = 512L * 1024 * 1024, MaxTotal = 1024L * 1024 * 1024, MaxFile = 200L * 1024 * 1024;
        const int MaxFiles = 20000;
        public static void Create(string dataDir, string zipPath)
        {
            string root = SafeRoot(dataDir), database = Path.Combine(root, "ledger.json");
            if (!File.Exists(database)) throw new InvalidDataException("Не найдена база ledger.json.");
            RejectLink(database);
            var files = new List<string> { database };
            foreach(string name in new[]{"service-mail-history.json","service-mail-settings.json"}) {
                string extra=Path.Combine(root,name);
                if(File.Exists(extra)){RejectLink(extra);files.Add(extra);}
            }
            string attachments = Path.Combine(root, "attachments");
            if (Directory.Exists(attachments)) CollectFiles(attachments, files);
            if (files.Count > MaxFiles) throw new InvalidDataException("Слишком много файлов для резервной копии.");
            long total = 0;
            foreach (string file in files) { long size = new FileInfo(file).Length; if (size > MaxFile) throw new InvalidDataException("Вложение превышает 200 МБ: " + Path.GetFileName(file)); total = checked(total + size); }
            if (total > MaxTotal) throw new InvalidDataException("Общий размер данных превышает 1 ГБ.");
            string destination = Path.GetFullPath(zipPath);
            if (files.Any(f => String.Equals(Path.GetFullPath(f), destination, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Резервная копия не может заменить базу или вложение.");
            TableIO.AtomicWrite(destination, delegate(string temporary)
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (string file in files)
                    {
                        string name = file.Substring(root.Length + 1).Replace('\\', '/');
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                        using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var target = entry.Open()) CopyBounded(source, target, MaxFile);
                    }
                }
                if (new FileInfo(temporary).Length > MaxArchive) throw new InvalidDataException("Архив превышает 512 МБ.");
            });
        }
        public static void Restore(string zipPath, string dataDir, Action<string> validateStagedDirectory)
        {
            if (validateStagedDirectory == null) throw new ArgumentNullException("validateStagedDirectory", "Перед восстановлением необходима проверка базы.");
            string root = SafeRoot(dataDir), parent = Path.GetDirectoryName(root); Directory.CreateDirectory(parent);
            string stage = Path.Combine(parent, ".ledger-restore-" + Guid.NewGuid().ToString("N")), previous = Path.Combine(parent, ".ledger-previous-" + Guid.NewGuid().ToString("N"));
            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length > MaxArchive) throw new InvalidDataException("Архив не найден или превышает 512 МБ.");
            Directory.CreateDirectory(stage); bool oldMoved = false, installed = false;
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; bool databaseFound = false;
                using (var source = File.OpenRead(zipPath))
                using (var zip = new ZipArchive(source, ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count > MaxFiles) throw new InvalidDataException("В архиве слишком много файлов.");
                    foreach (var entry in zip.Entries)
                    {
                        string relative = ValidateEntryName(entry.FullName);
                        if (!seen.Add(relative.TrimEnd('/'))) throw new InvalidDataException("В архиве повторяется путь: " + relative);
                        bool directory = relative.EndsWith("/", StringComparison.Ordinal);
                        if (relative != "ledger.json" && relative != "service-mail-history.json" && relative != "service-mail-settings.json" && relative != "attachments/" && !relative.StartsWith("attachments/", StringComparison.Ordinal)) throw new InvalidDataException("В архиве посторонний файл: " + relative);
                        if (relative == "ledger.json") { if (directory || entry.Length == 0) throw new InvalidDataException("База в архиве пуста."); databaseFound = true; }
                        if (entry.Length > MaxFile || entry.Length < 0) throw new InvalidDataException("Файл в архиве превышает 200 МБ.");
                        total = checked(total + entry.Length); if (total > MaxTotal) throw new InvalidDataException("Распакованный архив превышает 1 ГБ.");
                        string target = Path.GetFullPath(Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Недопустимый путь в архиве.");
                        if (directory) { if (entry.Length != 0) throw new InvalidDataException("Некорректная папка в архиве."); Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = entry.Open()) using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write)) { if (CopyBounded(input, output, MaxFile) != entry.Length) throw new InvalidDataException("Поврежденный файл в архиве: " + relative); }
                    }
                }
                if (!databaseFound) throw new InvalidDataException("В архиве нет ledger.json.");
                validateStagedDirectory(stage);
                if (Directory.Exists(root)) { RejectLink(root); Directory.Move(root, previous); oldMoved = true; }
                Directory.Move(stage, root); installed = true;
                // Keep the previous directory when Windows cannot remove it after successful installation.
                if (oldMoved) { try { Directory.Delete(previous, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            catch { if (oldMoved && !installed && !Directory.Exists(root)) Directory.Move(previous, root); throw; }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        static string SafeRoot(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не указана папка данных.");
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), driveRoot = Path.GetPathRoot(Path.GetFullPath(path)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (String.Equals(full, driveRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Корень диска нельзя использовать как папку базы."); return full;
        }
        static void RejectLink(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки на внешние папки и файлы не поддерживаются: " + Path.GetFileName(path)); }
        static void CollectFiles(string folder, List<string> files)
        {
            RejectLink(folder);
            foreach (string file in Directory.GetFiles(folder)) { RejectLink(file); files.Add(file); if (files.Count > MaxFiles) throw new InvalidDataException("Слишком много вложений."); }
            foreach (string child in Directory.GetDirectories(folder)) CollectFiles(child, files);
        }
        static string ValidateEntryName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 240 || name.IndexOf('\\') >= 0 || name.StartsWith("/", StringComparison.Ordinal) || name.IndexOf(':') >= 0 || name.IndexOf('\0') >= 0) throw new InvalidDataException("Недопустимый путь в архиве.");
            string candidate = name.EndsWith("/", StringComparison.Ordinal) ? name.Substring(0, name.Length - 1) : name;
            foreach (string part in candidate.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Недопустимое имя файла в архиве.");
                string device = part.Split('.')[0].ToUpperInvariant();
                if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" || (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] >= '1' && device[3] <= '9')) throw new InvalidDataException("Зарезервированное имя файла в архиве.");
            }
            return name;
        }
        static long CopyBounded(Stream source, Stream target, long limit)
        {
            byte[] buffer = new byte[81920]; int count; long total = 0;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0) { total = checked(total + count); if (total > limit) throw new InvalidDataException("Файл превышает допустимый размер."); target.Write(buffer, 0, count); } return total;
        }
    }
}
