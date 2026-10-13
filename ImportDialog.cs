using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WorkLedger
{
    public sealed class ImportDialog : Form
    {
        private sealed class Mapping
        {
            public string Column;
            public string Constant;
            public string Text;
            public override string ToString() { return Text; }
        }
        private readonly Store store;
        private readonly Schema schema;
        private readonly List<Dictionary<string, string>> incoming;
        private readonly Dictionary<string, ComboBox> mappings = new Dictionary<string, ComboBox>();
        private readonly List<FieldDef> fields;
        private readonly Button importButton;
        private readonly Label status;
        private readonly string currency;
        private readonly bool externalActs, grayPayments;
        private readonly Dictionary<string, Mapping> initialMappings = new Dictionary<string, Mapping>();
        public int ImportedCount { get; private set; }

        public ImportDialog(Store store, Schema schema, List<Dictionary<string, string>> incoming, string currency, bool externalActs = false, bool grayPayments = false)
        {
            this.store = store;
            this.schema = schema;
            this.externalActs = externalActs;
            this.grayPayments = grayPayments || schema.SectionKey == Catalog.GrayPaymentSection;
            if (this.grayPayments && schema.Kind != "payment") throw new ArgumentException("Раздел «Серые оплаты» предназначен для оплат.", "grayPayments");
            if (externalActs && schema.Kind != "act") throw new ArgumentException("Раздел «Акты вне» предназначен для актов.", "externalActs");
            this.incoming = incoming ?? new List<Dictionary<string, string>>();
            this.currency = new[] { "BYN", "RUB", "USD", "EUR" }.Contains(currency) ? currency : "BYN";
            fields = schema.Fields.Where(delegate(FieldDef f) { return f.Key != "filePath" && !(schema.Kind == "work" && f.Key == "amount"); }).ToList();
            Text = "Импорт: " + schema.Title;
            Font = new Font("Segoe UI", 9.5F);
            ForeColor = Color.FromArgb(31, 41, 55);
            BackColor = Color.FromArgb(247, 249, 252);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1100, Math.Min(800, Screen.PrimaryScreen.WorkingArea.Height - 50));
            MinimumSize = new Size(860, 560);
            ShowIcon = false; ShowInTaskbar = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;

            Label heading = new Label
            {
                Text = "Сопоставьте столбцы и проверьте данные",
                Dock = DockStyle.Top, Height = 59, Padding = new Padding(20, 15, 10, 0),
                Font = new Font("Segoe UI", 16F, FontStyle.Bold), BackColor = Color.White
            };
            int blanks = this.incoming.Count(delegate(Dictionary<string, string> r) { return r == null || r.Values.All(String.IsNullOrWhiteSpace); });
            Label info = new Label
            {
                Text = "Строк: " + this.incoming.Count + ". Пустых строк: " + blanks + " (будут пропущены). " +
                    "Предпросмотр: первые 100 строк. Импорт создаёт новые записи.",
                Dock = DockStyle.Top, Height = 48, Padding = new Padding(20, 5, 10, 0),
                ForeColor = Color.FromArgb(100, 116, 139), BackColor = Color.White
            };
            SplitContainer split = new SplitContainer
            {
                Size = new Size(1040, 550), Dock = DockStyle.Fill, SplitterDistance = 355, FixedPanel = FixedPanel.Panel1,
                Panel1MinSize = 300, Panel2MinSize = 350, Padding = new Padding(16)
            };
            Panel mappingScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(4, 8, 12, 12) };
            TableLayoutPanel mappingTable = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Top
            };
            mappingTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            List<string> columns = this.incoming.Take(10000).Where(r => r != null).SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal).ToList();
            int row = 0;
            foreach (FieldDef field in fields)
            {
                Label label = new Label
                {
                    Text = field.Label + (field.Required ? " *" : ""), AutoSize = true,
                    Margin = new Padding(0, 6, 0, 4), Dock = DockStyle.Fill
                };
                ComboBox combo = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill,
                    Margin = new Padding(0, 0, 0, 8), IntegralHeight = false, DropDownHeight = 320
                };
                combo.Items.Add(new Mapping { Text = "— Не заполнять —" });
                foreach (string column in columns) combo.Items.Add(new Mapping { Column = column, Text = "Столбец: " + column });
                if (field.Type == "enum")
                    foreach (KeyValuePair<string, string> option in field.Options)
                        combo.Items.Add(new Mapping { Constant = option.Key, Text = "Значение: " + option.Value });
                combo.SelectedIndex = 0;
                string match = (field.Type == "ref" ? columns.FirstOrDefault(c => String.Equals(c.Trim(), field.Label + " (ID)", StringComparison.CurrentCultureIgnoreCase)) : null)
                    ?? columns.FirstOrDefault(c => String.Equals(c.Trim(), field.Key, StringComparison.OrdinalIgnoreCase))
                    ?? columns.FirstOrDefault(c => String.Equals(c.Trim(), field.Label, StringComparison.CurrentCultureIgnoreCase));
                if (match != null)
                {
                    for (int i = 0; i < combo.Items.Count; ++i)
                        if (((Mapping)combo.Items[i]).Column == match) { combo.SelectedIndex = i; break; }
                }
                else if (field.Type == "enum" && field.Options.Count > 0)
                {
                    string defaultValue = field.Key == "currency" ? this.currency : field.Key == "paymentScope" ? (this.grayPayments ? "external" : "standard") : field.Options.First().Key;
                    for (int i = 0; i < combo.Items.Count; ++i)
                        if (((Mapping)combo.Items[i]).Constant == defaultValue) { combo.SelectedIndex = i; break; }
                }
                mappings.Add(field.Key, combo);
                initialMappings.Add(field.Key, (Mapping)combo.SelectedItem);
                mappingTable.Controls.Add(label, 0, row++);
                mappingTable.Controls.Add(combo, 0, row++);
            }
            mappingScroll.Controls.Add(mappingTable);
            split.Panel1.Controls.Add(mappingScroll);
            DataGridView preview = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AutoGenerateColumns = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                RowHeadersWidth = 55, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, RowTemplate = { Height = 29 }
            };
            preview.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 240, 249);
            preview.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            preview.EnableHeadersVisualStyles = false;
            foreach (string column in columns) preview.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column, Width = 145, SortMode = DataGridViewColumnSortMode.NotSortable });
            for (int i = 0; i < Math.Min(100, this.incoming.Count); ++i)
            {
                Dictionary<string, string> record = this.incoming[i];
                object[] values = columns.Select(delegate(string column) { string v; return (object)(record != null && record.TryGetValue(column, out v) ? v : ""); }).ToArray();
                int index = preview.Rows.Add(values);
                preview.Rows[index].HeaderCell.Value = (i + 1).ToString(CultureInfo.InvariantCulture);
            }
            split.Panel2.Controls.Add(preview);

            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 88, BackColor = Color.White, Padding = new Padding(20, 14, 20, 12) };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 277, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            Button cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Size = new Size(98, 40), Margin = new Padding(8, 0, 0, 0) };
            importButton = new Button
            {
                Text = "Импортировать", Size = new Size(158, 40), Margin = new Padding(0),
                BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            importButton.FlatAppearance.BorderSize = 0;
            importButton.Click += ImportClick;
            buttons.Controls.Add(cancel); buttons.Controls.Add(importButton);
            status = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(100, 116, 139), Padding = new Padding(0, 5, 10, 0), Text = "Связанные контрагенты, объекты и договоры должны уже быть в базе. Все строки сохраняются одной операцией." };
            if (this.incoming.Count > 10000) { status.Text = "Импорт ограничен 10 000 строками. Разделите файл на части."; status.ForeColor = Color.FromArgb(185, 28, 28); importButton.Enabled = false; }
            else if (this.incoming.Count == 0 || blanks == this.incoming.Count) { status.Text = "В файле нет непустых строк для импорта."; importButton.Enabled = false; }
            footer.Controls.Add(status); footer.Controls.Add(buttons);
            Controls.Add(split); Controls.Add(footer); Controls.Add(info); Controls.Add(heading);
            CancelButton = cancel;
        }

        internal List<Record> PrepareImport()
        {
            if (incoming.Count > 10000) throw new InvalidOperationException("За один импорт допускается не более 10 000 строк.");
            List<Record> records = new List<Record>();
            List<string> rowErrors = new List<string>();
            int errorCount = 0;
            HashSet<string> numbered = new HashSet<string>(store.Of(schema.Kind).Where(r => r.Get("number") != "").Select(NumberKey), StringComparer.Ordinal);
            bool checkNumbers = new[] { "contract", "amendment", "estimate", "act", "invoice" }.Contains(schema.Kind);
            for (int i = 0; i < incoming.Count; ++i)
            {
                Dictionary<string, string> source = incoming[i];
                if (source == null || source.Values.All(String.IsNullOrWhiteSpace)) continue;
                Record record = new Record(schema.Kind);
                Dictionary<string, string> paymentRaw = null;
                string paymentScope = null;
                if (schema.Kind == "payment")
                {
                    try { paymentScope = ReadPaymentScope(source); paymentRaw = PreparePaymentRaw(source, paymentScope); }
                    catch (Exception exception)
                    {
                        ++errorCount;
                        if (rowErrors.Count < 20) rowErrors.Add("Строка " + (i + 1) + ": " + exception.Message);
                        continue;
                    }
                }
                foreach (FieldDef field in fields)
                {
                    string raw = paymentRaw == null ? ReadMappedRaw(field, source) : paymentRaw[field.Key];
                    if (raw == "" && field.Key == "currency") raw = currency;
                    try { record.Fields[field.Key] = field.Key == "paymentScope" && schema.Kind == "payment" ? paymentScope : Normalize(field, raw, paymentScope); }
                    catch (Exception exception)
                    {
                        ++errorCount;
                        if (rowErrors.Count < 20) rowErrors.Add("Строка " + (i + 1) + ", «" + field.Label + "»: " + exception.Message);
                    }
                }
                if (schema.Kind == "act")
                {
                    try { ApplyActScope(record, source); }
                    catch (Exception exception)
                    {
                        ++errorCount;
                        if (rowErrors.Count < 20) rowErrors.Add("Строка " + (i + 1) + ": " + exception.Message);
                    }
                }
                if (checkNumbers && record.Get("number") != "" && !numbered.Add(NumberKey(record)))
                {
                    ++errorCount;
                    if (rowErrors.Count < 20) rowErrors.Add("Строка " + (i + 1) + ": документ № " + record.Get("number") + " уже есть в базе или повторяется в файле для этого контрагента и договора.");
                }
                records.Add(record);
            }
            if (errorCount > 0)
            {
                string message = String.Join(Environment.NewLine, rowErrors) + (errorCount > rowErrors.Count ? Environment.NewLine + "Всего ошибок: " + errorCount + "." : "");
                throw new InvalidOperationException(message);
            }
            if (records.Count == 0) throw new InvalidOperationException("В файле нет непустых строк для импорта.");
            return records;
        }

        private string ReadMappedRaw(FieldDef field, Dictionary<string, string> row)
        {
            Mapping map = (Mapping)mappings[field.Key].SelectedItem;
            string value = "";
            if (map != null && map.Constant != null) value = map.Constant;
            else if (map != null && map.Column != null) row.TryGetValue(map.Column, out value);
            return (value ?? "").Trim();
        }
        private static bool IsPaymentScopeColumn(string name)
        {
            return new[] { "paymentScope", "Вид оплаты", "Тип оплаты", "Учёт оплаты", "Учет оплаты" }
                .Any(alias => String.Equals((name ?? "").Trim(), alias, StringComparison.OrdinalIgnoreCase));
        }
        private string ParsePaymentScope(string raw)
        {
            raw = (raw ?? "").Trim();
            if (raw == "") return "";
            FieldDef field = fields.Single(f => f.Key == "paymentScope");
            foreach (var option in field.Options)
                if (String.Equals(option.Key, raw, StringComparison.OrdinalIgnoreCase) || String.Equals(option.Value, raw, StringComparison.CurrentCultureIgnoreCase)) return option.Key;
            if (String.Equals(raw, "Серая", StringComparison.CurrentCultureIgnoreCase)) return "external";
            throw new InvalidOperationException("В поле «Вид оплаты» выберите «Белая», «Серая (Акты вне)» или «По актам и распределениям».");
        }
        private string ReadPaymentScope(Dictionary<string, string> row)
        {
            var provided = row.Where(x => IsPaymentScopeColumn(x.Key)).Select(x => ParsePaymentScope(x.Value)).Distinct().ToList();
            if (provided.Count > 1) throw new InvalidOperationException("Столбцы вида оплаты содержат разные значения.");
            FieldDef field = fields.Single(f => f.Key == "paymentScope");
            Mapping selected = (Mapping)mappings["paymentScope"].SelectedItem;
            string scope = ParsePaymentScope(ReadMappedRaw(field, row));
            if (provided.Count == 1)
            {
                // A scope column, including an empty exported value, preserves the original marker.
                if (selected != null && selected.Constant != null && !Object.ReferenceEquals(selected, initialMappings["paymentScope"]) && scope != provided[0])
                    throw new InvalidOperationException("Выбранный вид оплаты противоречит значению в файле.");
                if (selected != null && selected.Column != null && scope != provided[0])
                    throw new InvalidOperationException("Сопоставленный вид оплаты противоречит другим столбцам файла.");
                scope = provided[0];
            }
            else if (selected == null || selected.Constant == null && selected.Column == null)
                scope = grayPayments ? "external" : "standard";
            string expected = grayPayments ? "external" : "standard";
            if (scope != "" && scope != expected)
                throw new InvalidOperationException("Оплата относится к другому разделу. Откройте «" + (scope == "external" ? "Серые оплаты" : "Оплаты и авансы") + "» и повторите импорт.");
            return scope;
        }
        private static bool IsPartColumn(string name, bool gray)
        {
            string[] aliases = gray ? new[] { "Серая часть", "GreyAmount", "GrayAmount" } : new[] { "Белая часть", "WhiteAmount" };
            return aliases.Any(alias => String.Equals((name ?? "").Trim(), alias, StringComparison.OrdinalIgnoreCase));
        }
        private void RejectMixedExport(Dictionary<string, string> row, string amountRaw)
        {
            decimal total;
            if (!TryDecimal(amountRaw, out total) || total <= 0) return;
            bool partial = false; decimal white = 0, gray = 0;
            foreach (var item in row)
            {
                bool isWhite = IsPartColumn(item.Key, false), isGray = IsPartColumn(item.Key, true);
                if (!isWhite && !isGray || String.IsNullOrWhiteSpace(item.Value)) continue;
                decimal part;
                if (!TryDecimal(item.Value, out part) || part < 0 || part > total)
                    throw new InvalidOperationException("Некорректная сумма в столбце «" + item.Key + "».");
                if (isWhite) white = Math.Max(white, part); else gray = Math.Max(gray, part);
                if (part > 0 && part < total) partial = true;
            }
            if (partial || white > 0 && gray > 0)
                throw new InvalidOperationException("Смешанная оплата содержит белую и серую части. Импорт полной суммы изменит её вид. Перенесите оплату вместе с распределениями через резервную копию или разделите её на две оплаты.");
            if (gray > 0 && !grayPayments || white > 0 && grayPayments)
                throw new InvalidOperationException("Сумма белой или серой части в файле относится к другому разделу. Импорт здесь изменит вид оплаты. Откройте соответствующий раздел или перенесите оплату с распределениями через резервную копию.");
        }
        private Dictionary<string, string> PreparePaymentRaw(Dictionary<string, string> row, string scope)
        {
            var values = fields.ToDictionary(f => f.Key, f => ReadMappedRaw(f, row));
            RejectMixedExport(row, values["amount"]);
            string actor = values["actId"];
            if (actor == "" && (scope == "external" || grayPayments)) throw new InvalidOperationException("Для серой оплаты укажите акт вне.");
            if (actor != "")
            {
                FieldDef actField = fields.Single(f => f.Key == "actId");
                string actId = Normalize(actField, actor, scope); Record act = store.Find(actId); values["actId"] = actId;
                foreach (string key in new[] { "partyId", "projectId", "contractId", "currency", "direction" })
                {
                    FieldDef field = fields.Single(f => f.Key == key); Mapping mapping = (Mapping)mappings[key].SelectedItem;
                    // Unmapped enum defaults are placeholders; the selected act is the source for these values.
                    if ((key == "currency" || key == "direction") && mapping != null && mapping.Column == null
                        && Object.ReferenceEquals(mapping, initialMappings[key])) values[key] = "";
                    string linked = act.Get(key);
                    if (values[key] == "") values[key] = linked;
                    else if (linked != "" && Normalize(field, values[key], scope) != linked)
                        throw new InvalidOperationException("Поле «" + field.Label + "» противоречит выбранному акту.");
                }
            }
            return values;
        }

        private void ApplyActScope(Record record, Dictionary<string, string> source)
        {
            List<KeyValuePair<string, string>> scopeFields = source.Where(p =>
                String.Equals(p.Key.Trim(), "accountingScope", StringComparison.OrdinalIgnoreCase)
                || String.Equals(p.Key.Trim(), "Учет акта", StringComparison.CurrentCultureIgnoreCase)
                || String.Equals(p.Key.Trim(), "Учёт акта", StringComparison.CurrentCultureIgnoreCase)).ToList();
            string scope = "";
            foreach (KeyValuePair<string, string> field in scopeFields)
            {
                string value = (field.Value ?? "").Trim().ToLowerInvariant();
                if (value == "") continue;
                if (value != "standard" && value != "external")
                    throw new InvalidOperationException("В столбце «Учёт акта» допустимы значения standard или external.");
                if (scope != "" && scope != value)
                    throw new InvalidOperationException("Столбцы учёта акта содержат разные значения.");
                scope = value;
            }
            string expected = externalActs ? "external" : "standard";
            if (scope != "" && scope != expected)
                throw new InvalidOperationException("Акт относится к другому разделу. Откройте «"
                    + (scope == "external" ? "Акты вне" : "Акты") + "» и повторите импорт.");
            if (externalActs || scope != "") record.Set("accountingScope", expected);
        }

        private static string NumberKey(Record record)
        {
            return record.Kind + "|" + record.Get("partyId") + "|" + record.Get("contractId") + "|" + record.Get("number").ToUpperInvariant();
        }

        private void ImportClick(object sender, EventArgs e)
        {
            List<Record> records;
            try { records = PrepareImport(); }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message + Environment.NewLine + Environment.NewLine + "Импорт не выполнен. Исправьте данные или сопоставление столбцов.", "Ошибки импорта", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                importButton.Enabled = false; UseWaitCursor = true;
                store.ImportBatch(records);
                ImportedCount = records.Count;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message + Environment.NewLine + Environment.NewLine + "Импорт не выполнен. Ни одна строка не сохранена.", "Проверка данных", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { UseWaitCursor = false; importButton.Enabled = true; }
        }
        private string Normalize(FieldDef field, string raw, string paymentScope = null)
        {
            if (raw.Length == 0)
            {
                if (field.Required) throw new InvalidOperationException("Обязательное поле не заполнено.");
                return "";
            }
            if (field.Type == "bool")
            {
                string boolean = raw.Trim().ToLowerInvariant();
                if (boolean == "true" || boolean == "да" || boolean == "1") return "true";
                if (boolean == "false" || boolean == "нет" || boolean == "0") return "false";
                throw new InvalidOperationException("Укажите Да/Нет, true/false или 1/0.");
            }
            if (field.Type == "ref")
            {
                List<Record> candidates = store.Of(field.ReferenceKind).ToList();
                if (schema.Kind == "payment" && field.Key == "actId")
                {
                    bool external = paymentScope == "external" || grayPayments;
                    candidates = candidates.Where(r => Store.IsExternalAct(r) == external).ToList();
                }
                Record byId = candidates.FirstOrDefault(r => r.Id == raw);
                if (byId != null) return byId.Id;
                List<Record> matches = candidates.Where(r => String.Equals(Catalog.Display(store, r), raw, StringComparison.CurrentCultureIgnoreCase)
                    || String.Equals(r.Get("name"), raw, StringComparison.CurrentCultureIgnoreCase)
                    || String.Equals(r.Get("number"), raw, StringComparison.CurrentCultureIgnoreCase)).ToList();
                if (matches.Count == 1) return matches[0].Id;
                if (matches.Count == 0) throw new InvalidOperationException("Не найдена активная запись «" + raw + "». Создайте её заранее.");
                throw new InvalidOperationException("Найдено несколько записей «" + raw + "». Укажите полное название из списка или идентификатор из экспорта.");
            }
            if (field.Type == "enum")
            {
                List<KeyValuePair<string, string>> matches = field.Options.Where(p => String.Equals(p.Key, raw, StringComparison.OrdinalIgnoreCase)
                    || String.Equals(p.Value, raw, StringComparison.CurrentCultureIgnoreCase)).ToList();
                if (matches.Count == 1) return matches[0].Key;
                throw new InvalidOperationException("Неизвестное значение «" + raw + "». Допустимо: " + String.Join(", ", field.Options.Values) + ".");
            }
            if (field.Type == "money" || field.Type == "number")
            {
                decimal amount;
                if (!TryDecimal(raw, out amount)) throw new InvalidOperationException("Ожидается число, например 1250,50.");
                int precision = field.Type == "money" ? 2 : 6;
                if (Decimal.Round(amount, precision) != amount) throw new InvalidOperationException("Допустимо не более " + precision + " знаков после запятой.");
                return field.Type == "money" ? Store.MoneyString(amount) : amount.ToString("0.######", CultureInfo.InvariantCulture);
            }
            if (field.Type == "date")
            {
                DateTime date;
                if (!DateTime.TryParseExact(raw, new[] { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                {
                    decimal serial;
                    if (!TryDecimal(raw, out serial) || serial < 1 || serial > 109574) throw new InvalidOperationException("Укажите дату ДД.ММ.ГГГГ, ГГГГ-ММ-ДД или дату Excel.");
                    try { date = DateTime.FromOADate((double)serial).Date; }
                    catch { throw new InvalidOperationException("Некорректная дата Excel."); }
                }
                if (date.Year < 1900 || date.Year > 2200) throw new InvalidOperationException("Дата должна быть в пределах 1900–2200 годов.");
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            return raw;
        }

        private static bool TryDecimal(string value, out decimal result)
        {
            return Decimal.TryParse((value ?? "").Trim().Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "").Replace(',', '.'),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result);
        }
    }
}



