using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

namespace WorkLedger
{
    public sealed class ActPrintLine
    {
        public string Description { get; set; }
        public string Unit { get; set; }
        public decimal Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal Amount { get { return Decimal.Round(Quantity * Price, 2, MidpointRounding.AwayFromZero); } }
        public ActPrintLine() { Description = ""; Unit = ""; }
    }
    internal sealed class ActPrintLineData
    {
        public string Description { get; set; } public string Unit { get; set; }
        public decimal Quantity { get; set; } public decimal Price { get; set; }
    }
    public static class ActDocument
    {
        public const string ItemsField = "printItemsJson";
        public const string DescriptionField = "printDescription";
        public const int MaxItems = 200;
        private const string TokenPattern = "[{][{][A-Z_]+[}][}]";
        private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
        private static readonly string[] ItemTokens = { "{{ITEM_NO}}", "{{ITEM_DESCRIPTION}}", "{{ITEM_UNIT}}", "{{ITEM_QTY}}", "{{ITEM_PRICE}}", "{{ITEM_AMOUNT}}", "{{ITEM_TOTAL}}" };
        public static List<ActPrintLine> ReadItems(Record act)
        {
            if (act == null) throw new ArgumentNullException("act");
            if (act.Get("workItems") != "")
            {
                var hourly = Store.GetHourlyActLines(act).Select(x => new ActPrintLine { Description = x.Description, Unit = "ч", Quantity = x.Hours, Price = x.Rate }).ToList();
                ValidateLines(hourly); return hourly;
            }
            if (act.Get("workItems") != "")
            {
                var hourly = Store.GetHourlyActLines(act).Select(x => new ActPrintLine { Description = x.Description, Unit = "ч", Quantity = x.Hours, Price = x.Rate }).ToList();
                ValidateLines(hourly); return hourly;
            }
            string raw = act.Get(ItemsField);
            if (String.IsNullOrWhiteSpace(raw)) return new List<ActPrintLine> { new ActPrintLine { Description = String.IsNullOrWhiteSpace(act.Get("description")) ? "Выполненные работы (услуги)" : act.Get("description"), Unit = "шт", Quantity = 1, Price = Store.ParseMoney(act.Get("amount")) } };
            try
            {
                if (raw.Length > 100000) throw new InvalidOperationException("Слишком большой перечень работ.");
                var saved = new JavaScriptSerializer { MaxJsonLength = 100000, RecursionLimit = 32 }.Deserialize<List<ActPrintLineData>>(raw);
                if (saved == null || saved.Any(x => x == null)) throw new InvalidOperationException("Пустая или повреждённая строка работы.");
                var result = saved.Select(x => new ActPrintLine { Description = x.Description ?? "", Unit = x.Unit ?? "", Quantity = x.Quantity, Price = x.Price }).ToList();
                ValidateLines(result); return result;
            }
            catch (Exception e) { throw new InvalidOperationException("Не удалось прочитать позиции акта. Проверьте их в окне «Позиции акта». " + e.Message, e); }
        }
        public static string SerializeItems(IEnumerable<ActPrintLine> items)
        {
            var lines = Materialize(items); ValidateLines(lines);
            string json = new JavaScriptSerializer { MaxJsonLength = 100000 }.Serialize(lines.Select(x => new ActPrintLineData { Description = x.Description, Unit = x.Unit, Quantity = x.Quantity, Price = x.Price }).ToList());
            if (json.Length > 100000) throw new InvalidOperationException("Перечень работ слишком велик. Сократите описания или количество строк.");
            return json;
        }
        public static void ValidateItems(Record act, IEnumerable<ActPrintLine> items)
        {
            if (act == null || act.Kind != "act") throw new InvalidOperationException("Выберите акт выполненных работ.");
            var lines = Materialize(items); ValidateLines(lines);
            decimal amount = Store.ParseMoney(act.Get("amount"));
            if (amount <= 0) throw new InvalidOperationException("Сумма акта должна быть больше нуля.");
            if (lines.Sum(x => x.Amount) != amount) throw new InvalidOperationException("Сумма позиций не совпадает с суммой акта. Исправьте перечень в окне «Позиции акта»; учёт оплат и задолженности сохранён.");
            Currency(act.Get("currency")); FormatDate(act.Get("date"));
        }
        public static void Validate(Record act, IEnumerable<ActPrintLine> items) { ValidateItems(act, items); }
        public static void Validate(Record act) { ValidateItems(act, ReadItems(act)); }
        private static List<ActPrintLine> Materialize(IEnumerable<ActPrintLine> items) { if (items == null) throw new InvalidOperationException("Не указан перечень работ акта."); return items.ToList(); }
        private static void ValidateLines(List<ActPrintLine> lines)
        {
            if (lines.Count == 0 || lines.Count > MaxItems) throw new InvalidOperationException("В акте должно быть от 1 до 200 позиций.");
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i]; string prefix = "Позиция " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": ";
                if (line == null || String.IsNullOrWhiteSpace(line.Description) || line.Description.Length > 2000 || (line.Unit != null && line.Unit.Length > 50)) throw new InvalidOperationException(prefix + "укажите описание работы до 2000 символов и единицу измерения до 50 символов.");
                if (line.Quantity <= 0 || line.Quantity > 1000000000000m || Decimal.Round(line.Quantity, 6) != line.Quantity) throw new InvalidOperationException(prefix + "количество должно быть больше нуля, не более триллиона и иметь не более 6 знаков после запятой.");
                if (line.Price < 0 || line.Price > 1000000000000m || Decimal.Round(line.Price, 2) != line.Price) throw new InvalidOperationException(prefix + "цена должна быть неотрицательной и указана с точностью до копеек.");
                if (line.Amount > 1000000000000m) throw new InvalidOperationException(prefix + "сумма превышает допустимый предел.");
            }
        }
        public static void Export(Store store, Record act, string path)
        {
            using (Stream source = typeof(ActDocument).Assembly.GetManifestResourceStream("WorkLedger.ActTemplate.docx"))
            {
                if (source == null) throw new InvalidOperationException("Встроенный шаблон акта не найден. Установите полный выпуск программы.");
                using (var memory = new MemoryStream()) { source.CopyTo(memory); ExportTemplate(store, act, path, memory.ToArray()); }
            }
        }
        internal static void ExportTemplate(Store store, Record act, string path, byte[] template)
        {
            if (store == null) throw new ArgumentNullException("store");
            var lines = ReadItems(act); ValidateItems(act, lines);
            if (String.IsNullOrWhiteSpace(path) || !Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Выберите файл Word с расширением .docx.");
            Record own = Store.GetActSnapshot(act, "organizationSnapshot") ?? store.Of("settings").FirstOrDefault(), party = Store.GetActSnapshot(act, "partySnapshot") ?? store.Find(act.Get("partyId"));
            if (own == null || String.IsNullOrWhiteSpace(own.Get("name"))) throw new InvalidOperationException("Заполните название своей организации в настройках перед созданием акта.");
            if (party == null || party.Kind != "party" || String.IsNullOrWhiteSpace(party.Get("name"))) throw new InvalidOperationException("Не найден контрагент акта.");
            if (act.Get("direction") != "income" && act.Get("direction") != "expense") throw new InvalidOperationException("Укажите направление оплаты акта.");
            Record executor = act.Get("direction") == "income" ? own : party, customer = act.Get("direction") == "income" ? party : own;
            string currency = Currency(act.Get("currency")), main = act.Get(DescriptionField);
            if (String.IsNullOrWhiteSpace(main)) main = lines.Count > 1 ? "Выполненные работы (услуги), согласно приложению" : lines[0].Description;
            if (String.IsNullOrWhiteSpace(main)) main = "Выполненные работы (услуги)";
            if (main.Length > 2000) throw new InvalidOperationException("Итоговое описание услуги слишком длинное. Сократите его в окне «Позиции акта».");
            decimal total = lines.Sum(x => x.Amount);
            var values = new Dictionary<string, string>(StringComparer.Ordinal) {
                { "{{EXECUTOR_NAME}}", executor.Get("name") }, { "{{EXECUTOR_DETAILS}}", Details(executor) }, { "{{EXECUTOR_CONTACT}}", Contact(executor) },
                { "{{ACT_NUMBER}}", act.Get("number") }, { "{{ACT_DATE}}", FormatDate(act.Get("date")) },
                { "{{CUSTOMER_NAME}}", customer.Get("name") }, { "{{CUSTOMER_DETAILS}}", Details(customer) }, { "{{CUSTOMER_CONTACT}}", Contact(customer) },
                { "{{MAIN_DESCRIPTION}}", main }, { "{{MAIN_UNIT}}", "шт" }, { "{{MAIN_QTY}}", "1" }, { "{{MAIN_PRICE}}", Money(total) }, { "{{MAIN_AMOUNT}}", Money(total) }, { "{{MAIN_TOTAL}}", Money(total) },
                { "{{TOTAL}}", Money(total) }, { "{{TOTAL_WORDS}}", AmountInWords(total, currency) },
                { "{{CURRENCY_MAJOR_SHORT}}", currency == "EUR" ? "евро" : currency == "USD" ? "долл." : "руб." }, { "{{CURRENCY_MINOR_SHORT}}", currency == "USD" || currency == "EUR" ? "цент." : "коп." }
            };
            using (var input = new MemoryStream(template, false)) using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                ZipArchiveEntry part = zip.GetEntry("word/document.xml"); if (part == null) throw new InvalidOperationException("Шаблон Word повреждён.");
                var document = LoadXml(part); var ns = Namespace(document);
                var tables = document.SelectNodes("/w:document/w:body/w:tbl", ns).Cast<XmlElement>().ToList();
                if (tables.Count != 2) throw new InvalidOperationException("Шаблон должен содержать таблицы основного акта и приложения.");
                ValidateTemplate(document, ns, values.Keys);
                foreach (XmlElement node in document.SelectNodes("//w:t", ns).Cast<XmlElement>().ToList())
                {
                    string original = node.InnerText;
                    string replacement = Regex.Replace(original, TokenPattern, delegate(Match match) {
                        if (match.Value == "{{ITEM_COUNT}}") return AfterTable(node, tables[1]) ? lines.Count.ToString(CultureInfo.InvariantCulture) : "1";
                        string value; return values.TryGetValue(match.Value, out value) ? value : match.Value;
                    });
                    if (replacement != original) ReplaceText(node, replacement);
                }
                var detailRows = tables[1].SelectNodes("./w:tr", ns).Cast<XmlElement>().Where(row => row.SelectNodes(".//w:t", ns).Cast<XmlElement>().Any(t => t.InnerText.Contains("{{ITEM_NO}}"))).ToList();
                if (detailRows.Count != 1) throw new InvalidOperationException("Не найдена образцовая строка приложения в шаблоне.");
                var prototype = detailRows[0]; int index = 0;
                foreach (var line in lines)
                {
                    index++; var row = (XmlElement)prototype.CloneNode(true);
                    var rowValues = new Dictionary<string, string>(StringComparer.Ordinal) { { "{{ITEM_NO}}", index.ToString(CultureInfo.InvariantCulture) }, { "{{ITEM_DESCRIPTION}}", line.Description }, { "{{ITEM_UNIT}}", line.Unit ?? "" }, { "{{ITEM_QTY}}", line.Quantity.ToString("0.######", Russian) }, { "{{ITEM_PRICE}}", Money(line.Price) }, { "{{ITEM_AMOUNT}}", Money(line.Amount) }, { "{{ITEM_TOTAL}}", Money(line.Amount) } };
                    foreach (XmlElement node in row.SelectNodes(".//w:t", ns).Cast<XmlElement>().ToList()) { string original = node.InnerText; string replacement = Regex.Replace(original, TokenPattern, delegate(Match match) { string value; return rowValues.TryGetValue(match.Value, out value) ? value : match.Value; }); if (replacement != original) ReplaceText(node, replacement); }
                    tables[1].InsertBefore(row, prototype);
                }
                tables[1].RemoveChild(prototype);
                foreach (var table in tables)
                {
                    XmlElement header = table.SelectSingleNode("./w:tr", ns) as XmlElement; if (header == null) throw new InvalidOperationException("В таблице акта отсутствует заголовок.");
                    XmlElement properties = header.SelectSingleNode("./w:trPr", ns) as XmlElement; if (properties == null) { properties = document.CreateElement("w", "trPr", W); header.PrependChild(properties); }
                    if (properties.SelectSingleNode("./w:tblHeader", ns) == null) properties.AppendChild(document.CreateElement("w", "tblHeader", W));
                    foreach (XmlElement height in table.SelectNodes("./w:tr/w:trPr/w:trHeight", ns)) if (height.GetAttribute("hRule", W) == "exact") height.SetAttribute("hRule", W, "atLeast");
                }
                WriteDocument(path, zip, document);
            }
        }
        private static string Details(Record record)
        {
            var fields = new List<string>();
            if (record.Get("bankAccount") != "") fields.Add("р/с " + record.Get("bankAccount"));
            if (record.Get("bankName") != "") fields.Add(record.Get("bankName"));
            if (record.Get("bankCode") != "") fields.Add("код " + record.Get("bankCode"));
            if (record.Get("unp") != "") fields.Add("УНП " + record.Get("unp"));
            return String.Join("\n", new[] { String.Join(" ", fields), record.Get("requisites") }.Where(x => !String.IsNullOrWhiteSpace(x)));
        }
        private static string Contact(Record record)
        {
            string address = record.Get("registrationAddress"); if (address == "") address = record.Get("actualAddress"); if (address == "") address = record.Get("postalAddress");
            return String.Join("\n", new[] { address == "" ? "" : "Адрес: " + address, record.Get("contact"), record.Get("phone"), record.Get("email") }.Where(x => !String.IsNullOrWhiteSpace(x)));
        }
        private static string Money(decimal value) { return value.ToString("N2", Russian); }
        private static bool AfterTable(XmlNode node, XmlNode table)
        {
            XmlNode top = node; while (top.ParentNode != null && top.ParentNode.LocalName != "body") top = top.ParentNode;
            if (top.ParentNode == null) return false;
            bool seen = false; foreach (XmlNode child in top.ParentNode.ChildNodes) { if (child == table) seen = true; if (child == top) return seen; } return false;
        }
        private static void ValidateTemplate(XmlDocument doc, XmlNamespaceManager ns, IEnumerable<string> globalTokens)
        {
            var allowed = new HashSet<string>(globalTokens.Concat(ItemTokens).Concat(new[] { "{{ITEM_COUNT}}" }), StringComparer.Ordinal);
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlElement node in doc.SelectNodes("//w:t", ns)) if (node.InnerText.Contains("{{"))
            {
                var matches = Regex.Matches(node.InnerText, TokenPattern);
                if (matches.Count == 0) throw new InvalidOperationException("Разделённое поле в шаблоне акта.");
                foreach (Match match in matches) { if (!allowed.Contains(match.Value)) throw new InvalidOperationException("Неизвестное поле в шаблоне акта."); found.Add(match.Value); }
            }
            foreach (string token in allowed) if (!found.Contains(token)) throw new InvalidOperationException("В шаблоне акта отсутствует необходимое поле: " + token);
        }
        private static void ReplaceText(XmlElement node, string value)
        {
            string[] lines = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            node.InnerText = lines[0]; node.SetAttribute("space", "http://www.w3.org/XML/1998/namespace", "preserve"); XmlNode previous = node;
            for (int i = 1; i < lines.Length; i++) { XmlElement br = node.OwnerDocument.CreateElement("w", "br", W); node.ParentNode.InsertAfter(br, previous); XmlElement text = node.OwnerDocument.CreateElement("w", "t", W); text.SetAttribute("space", "http://www.w3.org/XML/1998/namespace", "preserve"); text.InnerText = lines[i]; node.ParentNode.InsertAfter(text, br); previous = text; }
        }
        private static XmlNamespaceManager Namespace(XmlDocument doc) { var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("w", W); return ns; }
        private static XmlDocument LoadXml(ZipArchiveEntry entry)
        {
            var doc = new XmlDocument { PreserveWhitespace = true }; doc.XmlResolver = null;
            using (var stream = entry.Open()) using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader); return doc;
        }
        private static void WriteDocument(string path, ZipArchive template, XmlDocument document)
        {
            string target = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(target)); string temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var entry in template.Entries)
                    {
                        var copied = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal); copied.LastWriteTime = entry.LastWriteTime; copied.ExternalAttributes = entry.ExternalAttributes;
                        using (var destination = copied.Open())
                        {
                            if (entry.FullName == "word/document.xml") using (var writer = XmlWriter.Create(destination, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false })) document.Save(writer);
                            else using (var source = entry.Open()) source.CopyTo(destination);
                        }
                    }
                    output.Flush(true);
                }
                if (File.Exists(target)) File.Replace(temp, target, null, true); else File.Move(temp, target);
            }
            catch (IOException e) { throw new IOException("Не удалось сохранить документ Word. Закройте файл, если он открыт, и проверьте папку сохранения.", e); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static string FormatDate(string isoDate)
        {
            DateTime date; if (!DateTime.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) throw new InvalidOperationException("Дата акта должна иметь формат ГГГГ-ММ-ДД.");
            return date.ToString("d MMMM yyyy 'г.'", Russian);
        }
        private static string Currency(string code)
        {
            string value = (code ?? "").Trim().ToUpperInvariant(); if (!new[] { "BYN", "RUB", "USD", "EUR" }.Contains(value)) throw new InvalidOperationException("Шаблон поддерживает валюты BYN, RUB, USD и EUR."); return value;
        }
        public static string AmountInWords(decimal amount, string currency)
        {
            string code = Currency(currency); amount = Decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
            if (amount < 0 || amount > 1000000000000m) throw new InvalidOperationException("Сумма прописью должна быть от нуля до одного триллиона.");
            long major = (long)Decimal.Truncate(amount); int minor = (int)((amount - major) * 100);
            string[] majorForms = code == "BYN" ? new[] { "белорусский рубль", "белорусских рубля", "белорусских рублей" } : code == "RUB" ? new[] { "российский рубль", "российских рубля", "российских рублей" } : code == "USD" ? new[] { "доллар США", "доллара США", "долларов США" } : new[] { "евро", "евро", "евро" };
            string[] minorForms = code == "BYN" || code == "RUB" ? new[] { "копейка", "копейки", "копеек" } : new[] { "цент", "цента", "центов" };
            string words = NumberWords(major) + " " + Form(major, majorForms) + " " + minor.ToString("00", CultureInfo.InvariantCulture) + " " + Form(minor, minorForms);
            return Char.ToUpperInvariant(words[0]) + words.Substring(1);
        }
        private static string Form(long number, string[] forms) { long last100 = number % 100, last = number % 10; return forms[last100 >= 11 && last100 <= 19 ? 2 : last == 1 ? 0 : last >= 2 && last <= 4 ? 1 : 2]; }
        private static string NumberWords(long number)
        {
            if (number == 0) return "ноль";
            long[] scales = { 1000000000000L, 1000000000L, 1000000L, 1000L, 1L };
            string[][] names = { new[] { "триллион", "триллиона", "триллионов" }, new[] { "миллиард", "миллиарда", "миллиардов" }, new[] { "миллион", "миллиона", "миллионов" }, new[] { "тысяча", "тысячи", "тысяч" }, new[] { "", "", "" } };
            var parts = new List<string>();
            for (int i = 0; i < scales.Length; i++) { int group = (int)(number / scales[i]); number %= scales[i]; if (group > 0) { parts.Add(Hundreds(group, scales[i] == 1000)); if (scales[i] != 1) parts.Add(Form(group, names[i])); } }
            return String.Join(" ", parts);
        }
        private static string Hundreds(int number, bool feminine)
        {
            string[] hundreds = { "", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот" };
            string[] teens = { "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать" };
            string[] tens = { "", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто" };
            string[] ones = { "", feminine ? "одна" : "один", feminine ? "две" : "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять" };
            var words = new List<string>(); if (number >= 100) words.Add(hundreds[number / 100]); number %= 100;
            if (number >= 10 && number <= 19) words.Add(teens[number - 10]); else { if (number >= 20) words.Add(tens[number / 10]); if (number % 10 > 0) words.Add(ones[number % 10]); }
            return String.Join(" ", words);
        }
    }
}




