using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkLedger
{
    public sealed class ActHtmlLine
    {
        public string Description { get; set; }
        public string Unit { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? Price { get; set; }
        public decimal? Amount { get; set; }
        public ActHtmlLine() { Description = ""; Unit = ""; }
    }
    public static class ActHtmlDocument
    {
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
        private static readonly int[] Grid = { 353, 2948, 970, 1037, 904, 1245, 883, 849, 1138 };
        public static void WriteHtml(string path, Store store, Record act)
        {
            var items = ActDocument.ReadItems(act); ActDocument.ValidateItems(act, items);
            var rows = items.Select(x => new ActHtmlLine { Description = x.Description, Unit = x.Unit, Quantity = x.Quantity, Price = x.Price, Amount = x.Amount }).ToList();
            string summary = "";
            if (String.IsNullOrWhiteSpace(act.Get("workItems")) && String.IsNullOrWhiteSpace(act.Get(ActDocument.ItemsField)))
            {
                List<ActHtmlLine> operations; string heading;
                if (TryReadNumberedOperations(act.Get("description"), out operations, out heading)) { rows = operations; summary = heading; }
            }
            WriteHtml(path, store, act, rows, summary);
        }
        public static void WriteHtml(string path, Store store, Record act, IEnumerable<ActHtmlLine> displayRows) { WriteHtml(path, store, act, displayRows, ""); }
        private static void WriteHtml(string path, Store store, Record act, IEnumerable<ActHtmlLine> displayRows, string summary)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (act == null || act.Kind != "act") throw new InvalidOperationException("Выберите акт выполненных работ.");
            string currency = act.Get("currency");
            if (!new[] { "BYN", "RUB", "USD", "EUR" }.Contains(currency)) throw new InvalidOperationException("Укажите валюту акта: BYN, RUB, USD или EUR.");
            string date = ActDocument.FormatDate(act.Get("date")); decimal total = Store.ParseMoney(act.Get("amount"));
            if (total <= 0) throw new InvalidOperationException("Сумма акта должна быть больше нуля.");
            if (act.Get("direction") != "income" && act.Get("direction") != "expense") throw new InvalidOperationException("Укажите направление оплаты акта.");
            if (displayRows == null) throw new InvalidOperationException("Не указан перечень операций акта.");
            var rows = displayRows.ToList(); ValidateRows(rows, total);
            var own = Store.GetActSnapshot(act, "organizationSnapshot") ?? store.Of("settings").FirstOrDefault();
            var party = Store.GetActSnapshot(act, "partySnapshot") ?? store.Find(act.Get("partyId"));
            var executor = act.Get("direction") == "expense" ? party : own; var customer = act.Get("direction") == "expense" ? own : party;
            string units = currency == "USD" ? "долл. США" : currency == "EUR" ? "евро" : "руб.";
            var html = new StringBuilder();
            html.Append("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\"/><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"/>");
            html.Append("<title>Акт № ").Append(H(Value(act, "number", "[номер акта]"))).Append("</title><style type=\"text/css\">");
            html.Append(Css()).Append("</style></head><body><div class=\"toolbar\"><button type=\"button\" onclick=\"window.print()\">Печать / Сохранить PDF</button></div><main class=\"sheet\"><div class=\"paper\">");
            PartyBlock(html, executor, "executor", "Исполнитель");
            html.Append("<h1>Акт № <span id=\"act-number\">").Append(H(Value(act, "number", "[номер акта]"))).Append("</span></h1>");
            html.Append("<div class=\"act-meta\"><span>от ").Append(H(date)).Append("</span><span>Валюта: ").Append(H(currency)).Append("</span></div>");
            PartyBlock(html, customer, "customer", "Заказчик");
            if (!String.IsNullOrWhiteSpace(summary)) html.Append("<p class=\"work-summary\" data-role=\"work-summary\">").Append(B(summary)).Append("</p>");
            html.Append("<table class=\"work-table\" aria-label=\"Выполненные работы\"><colgroup>");
            int gridTotal = Grid.Sum();
            foreach (int size in Grid) html.Append("<col style=\"width:").Append(((decimal)size * 100m / gridTotal).ToString("0.######", CultureInfo.InvariantCulture)).Append("%\"/>");
            html.Append("</colgroup><thead><tr><th scope=\"col\">№</th><th scope=\"col\">Наименование работы (услуги)</th><th scope=\"col\">Единица<br/>измерения</th><th scope=\"col\">Количество</th><th scope=\"col\">Цена,<br/>").Append(H(units));
            html.Append("</th><th scope=\"col\">Сумма,<br/>").Append(H(units)).Append("</th><th scope=\"col\">Ставка НДС, %</th><th scope=\"col\">Сумма НДС,<br/>").Append(H(units)).Append("</th><th scope=\"col\">Всего с НДС,<br/>").Append(H(units)).Append("</th></tr></thead><tbody>");
            for (int i = 0; i < rows.Count; i++)
            {
                if (i == rows.Count - 1) html.Append("</tbody><tbody class=\"closing-group\">");
                var row = rows[i];
                html.Append("<tr data-role=\"operation\"><td class=\"center\">").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("</td><td class=\"work-description\">").Append(B(row.Description));
                html.Append("</td><td class=\"center\">").Append(H(String.IsNullOrWhiteSpace(row.Unit) ? "—" : row.Unit)).Append("</td><td class=\"center quantity\">").Append(row.Quantity.HasValue ? H(row.Quantity.Value.ToString("0.######", Russian)) : "—");
                html.Append("</td><td class=\"money\" data-role=\"unit-price\">").Append(row.Price.HasValue ? H(Money(row.Price.Value)) : "—");
                html.Append("</td><td class=\"money\" data-role=\"item-amount\">").Append(row.Amount.HasValue ? H(Money(row.Amount.Value)) : "—");
                html.Append("</td><td class=\"center vat-rate\">Без НДС</td><td class=\"money\" data-role=\"vat-amount\">—</td><td class=\"money\" data-role=\"item-total\">").Append(row.Amount.HasValue ? H(Money(row.Amount.Value)) : "—").Append("</td></tr>");
            }
            html.Append("<tr class=\"closing-total-row\"><th colspan=\"8\" scope=\"row\">Итого:</th><td class=\"money\" data-role=\"subtotal\">").Append(H(Money(total))).Append("</td></tr><tr class=\"closing-total-row\"><th colspan=\"8\" scope=\"row\">НДС:</th><td class=\"money\">—</td></tr><tr class=\"closing-total-row\"><th colspan=\"8\" scope=\"row\">Всего:</th><td class=\"money\" data-role=\"total\">").Append(H(Money(total))).Append("</td></tr><tr class=\"closing-content-row\"><td colspan=\"9\" class=\"closing-content-cell\">");
            string words = ActDocument.AmountInWords(total, currency); words = Char.ToLowerInvariant(words[0]) + words.Substring(1);
            html.Append("<p class=\"amount-words\">").Append(rows.All(x => !x.Amount.HasValue) ? "Всего операций: " : "Всего оказано услуг ").Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(", на сумму <span data-role=\"amount-words\">").Append(H(words)).Append("</span>.<br/>НДС: без НДС.</p>");
            html.Append("<p class=\"acceptance\">Вышеперечисленные услуги выполнены полностью и в срок. Заказчик претензий по объему, качеству и срокам оказания услуг не имеет.</p>");
            html.Append("<div class=\"signatures\">"); SignatureBlock(html, executor, "Исполнитель"); SignatureBlock(html, customer, "Заказчик"); html.Append("</div></td></tr></tbody></table></div></main></body></html>");
            AtomicWrite(path, html.ToString());
        }
        internal static bool TryReadNumberedOperations(string description, out List<ActHtmlLine> operations, out string heading)
        {
            operations = null; heading = "";
            if (String.IsNullOrWhiteSpace(description)) return false;
            var matches = Regex.Matches(description, @"(?<!\S)(\d+)[.)]\s+");
            if (matches.Count < 2 || matches.Count > 200) return false;
            var rows = new List<ActHtmlLine>();
            for (int i = 0; i < matches.Count; i++)
            {
                if (matches[i].Groups[1].Value != (i + 1).ToString(CultureInfo.InvariantCulture)) return false;
                int start = matches[i].Index + matches[i].Length;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : description.Length;
                string text = description.Substring(start, end - start).Trim(); if (text == "" || text.Length > 2000) return false;
                rows.Add(new ActHtmlLine { Description = text });
            }
            operations = rows; heading = description.Substring(0, matches[0].Index).Trim(); return true;
        }
        private static void ValidateRows(List<ActHtmlLine> rows, decimal total)
        {
            if (rows.Count == 0 || rows.Count > 200) throw new InvalidOperationException("В акте должно быть от 1 до 200 операций.");
            bool detailed = rows.All(x => x != null && x.Quantity.HasValue && x.Price.HasValue && x.Amount.HasValue);
            bool descriptive = rows.All(x => x != null && !x.Quantity.HasValue && !x.Price.HasValue && !x.Amount.HasValue);
            if (!detailed && !descriptive) throw new InvalidOperationException("Перечень должен содержать полные суммы по всем операциям либо только подтверждённые описания без распределения стоимости.");
            foreach (var row in rows)
            {
                if (String.IsNullOrWhiteSpace(row.Description) || row.Description.Length > 2000 || (row.Unit != null && row.Unit.Length > 50)) throw new InvalidOperationException("Укажите описание операции до 2000 символов и единицу измерения до 50 символов.");
                if (detailed && (row.Quantity.Value <= 0 || row.Quantity.Value > 1000000000000m || Decimal.Round(row.Quantity.Value, 6) != row.Quantity.Value || row.Price.Value < 0 || row.Price.Value > 1000000000000m || Decimal.Round(row.Price.Value, 2) != row.Price.Value || row.Amount.Value < 0 || row.Amount.Value != Decimal.Round(row.Quantity.Value * row.Price.Value, 2, MidpointRounding.AwayFromZero))) throw new InvalidOperationException("Количество, цена или сумма операции указаны некорректно.");
            }
            if (detailed && rows.Sum(x => x.Amount.Value) != total) throw new InvalidOperationException("Сумма операций не совпадает с суммой акта. Учёт и документ сохранены без изменений.");
        }
        private static void PartyBlock(StringBuilder html, Record record, string role, string label)
        {
            html.Append("<section class=\"party-block ").Append(role).Append("\" data-role=\"").Append(role).Append("\">");
            html.Append("<p class=\"party-name\">"); if (role == "customer") html.Append("Заказчик: ");
            html.Append(H(Value(record, "name", "[наименование " + RoleGenitive(label) + "]"))).Append("</p>");
            html.Append("<p class=\"bank-line\">").Append(B(BankLine(record, label))).Append("</p><p class=\"contact-line\">").Append(B(ContactLine(record, label))).Append("</p></section>");
        }
        private static string BankLine(Record record, string label)
        {
            bool structured = record != null && new[] { "bankAccount", "bankName", "bankCode", "unp" }.Any(k => record.Get(k) != "");
            string additional = record == null ? "" : record.Get("requisites");
            if (!structured) return additional == "" ? "[банковские реквизиты и УНП " + RoleGenitive(label) + "]" : additional;
            var pieces = new List<string>();
            pieces.Add("Р/сч: " + Value(record, "bankAccount", "[расчётный счёт]"));
            if (record.Get("bankName") != "") pieces.Add("в " + record.Get("bankName"));
            if (record.Get("bankCode") != "") pieces.Add("код " + record.Get("bankCode"));
            if (record.Get("unp") != "") pieces.Add("УНП: " + record.Get("unp"));
            return String.Join(", ", pieces) + (additional == "" ? "" : "\n" + additional);
        }
        private static string ContactLine(Record record, string label)
        {
            string address = record == null ? "" : record.Get("registrationAddress");
            if (address == "" && record != null) address = record.Get("actualAddress"); if (address == "" && record != null) address = record.Get("postalAddress");
            var values = new List<string> { "Адрес: " + (address == "" ? "[адрес " + RoleGenitive(label) + "]" : address) };
            if (record != null && record.Get("phone") != "") values.Add("Телефон: " + record.Get("phone")); if (record != null && record.Get("email") != "") values.Add(record.Get("email"));
            return String.Join(" ", values);
        }
        private static void SignatureBlock(StringBuilder html, Record party, string label)
        {
            html.Append("<div><p class=\"signatory-role\">").Append(H(label)).Append(":</p>");
            if (party != null && !String.IsNullOrWhiteSpace(party.Get("signatoryPosition"))) html.Append("<p class=\"signatory-position\">").Append(H(party.Get("signatoryPosition"))).Append("</p>");
            html.Append("<p class=\"signature-line\">______________________</p><p class=\"signature-caption\">подпись</p>");
            if (party != null && !String.IsNullOrWhiteSpace(party.Get("signatoryName"))) html.Append("<p class=\"signatory-name\">").Append(H(party.Get("signatoryName"))).Append("</p>");
            html.Append("<p class=\"stamp\">М.П.</p></div>");
        }
        private static string RoleGenitive(string label) { return label == "Исполнитель" ? "исполнителя" : "заказчика"; }
        private static string Value(Record record, string key, string fallback) { return record == null || String.IsNullOrWhiteSpace(record.Get(key)) ? fallback : record.Get(key); }
        private static string Money(decimal value) { return value.ToString("N2", Russian); }
        private static string H(string value) { return WebUtility.HtmlEncode(value ?? ""); }
        private static string B(string value) { return H((value ?? "").Replace("\r\n", "\n").Replace('\r', '\n')).Replace("\n", "<br/>"); }
        private static string Css()
        {
            return "@page{size:A4 portrait;margin:25.4mm 10.495mm 17.515mm 17.36mm;}*{box-sizing:border-box}html,body{margin:0;padding:0}body{background:#eceff2;color:#000;font-family:Arial,sans-serif;font-size:9pt;line-height:1.18}.toolbar{max-width:210mm;margin:16px auto;display:flex;justify-content:flex-end}.toolbar button{font:14px Arial,sans-serif;padding:9px 16px;border:1px solid #bcc5cc;border-radius:4px;background:#fff;cursor:pointer}.sheet{width:210mm;min-height:297mm;margin:0 auto 24px;padding:25.4mm 10.495mm 17.515mm 17.36mm;background:#fff;box-shadow:0 2px 14px #c8cdd2}.paper{width:182.1568mm}.party-block p{margin:0}.party-name{font-size:9pt;font-weight:bold;font-style:italic;margin-bottom:.32mm!important}.executor .party-name{text-decoration:underline}.bank-line{font-size:9pt;font-weight:bold;font-style:italic;line-height:1.12}.executor .bank-line{max-width:130.5mm}.contact-line{font-size:9pt;line-height:1.12}.executor{margin-bottom:8mm}.customer{margin-bottom:2mm}.work-summary{font-size:9.5pt;font-weight:bold;margin:0 0 2mm;white-space:normal}h1{margin:0 0 1.7mm;text-align:center;font-size:13.5pt;line-height:1.1;font-weight:bold}.act-meta{display:flex;justify-content:space-between;margin-bottom:6.25mm;font-size:9.5pt;font-weight:bold}.work-table{width:100%;border-collapse:collapse;table-layout:fixed;font-size:9pt;line-height:1.12;margin:0}.work-table th,.work-table td{border:1px solid #222;padding:1.1mm .7mm;vertical-align:middle}.work-table thead th{text-align:center;font-size:8pt;font-weight:bold;white-space:normal;overflow-wrap:normal;word-break:normal}.work-table tbody tr{height:10mm;break-inside:avoid;page-break-inside:avoid}.work-description{font-size:9.5pt;white-space:normal;overflow-wrap:break-word;word-break:normal;hyphens:none}.center{text-align:center}.quantity{white-space:nowrap}.money{text-align:right;white-space:nowrap;font-variant-numeric:tabular-nums}.vat-rate{font-size:8pt}.closing-group{break-inside:avoid;page-break-inside:avoid}.work-table .closing-total-row{height:4.357mm}.work-table .closing-total-row th,.work-table .closing-total-row td{padding:.3mm .7mm;height:4.357mm;font-size:9pt;font-weight:bold}.work-table .closing-total-row th{text-align:right;border:0}.work-table .closing-content-row{height:auto}.work-table .closing-content-cell{border:0;padding:0;font-weight:normal;vertical-align:top}.amount-words{font-size:10.5pt;font-style:italic;line-height:1.12;margin:2mm 0 2.5mm}.acceptance{font-size:10.5pt;line-height:1.18;margin:0 0 3mm;text-indent:9mm}.signatures{display:flex;gap:8mm;break-inside:avoid;page-break-inside:avoid}.signatures>div{width:50%}.signatures p{margin:0 0 1.3mm;font-size:8.5pt}.signature-line{white-space:nowrap;text-align:center}.signature-caption{text-align:center;font-size:7.5pt!important}.signatory-name{text-align:center}.stamp{text-align:center;margin-top:2mm!important}@media print{html,body{background:#fff}.toolbar{display:none!important}.sheet{width:auto;min-height:0;margin:0;padding:0;box-shadow:none}.paper{width:182.1568mm}thead{display:table-header-group}.work-table tr{break-inside:avoid;page-break-inside:avoid}.amount-words,.acceptance{orphans:2;widows:2}}";
        }
        private static void AtomicWrite(string path, string html)
        {
            if (String.IsNullOrWhiteSpace(path) || !(Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Выберите файл печатной формы с расширением .html.");
            string target = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(target)); string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(html); using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (File.Exists(target)) File.Replace(temporary, target, null, true); else File.Move(temporary, target);
            }
            catch (IOException error) { throw new IOException("Не удалось сохранить печатную форму. Проверьте папку и закройте файл, если он открыт.", error); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

