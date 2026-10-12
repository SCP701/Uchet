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
        public int ImportedCount { get; private set; }

        public ImportDialog(Store store, Schema schema, List<Dictionary<string, string>> incoming, string currency)
        {
            this.store = store;
            this.schema = schema;
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
                    string defaultValue = field.Key == "currency" ? this.currency : field.Options.First().Key;
                    for (int i = 0; i < combo.Items.Count; ++i)
                        if (((Mapping)combo.Items[i]).Constant == defaultValue) { combo.SelectedIndex = i; break; }
                }
                mappings.Add(field.Key, combo);
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
                foreach (FieldDef field in fields)
                {
                    Mapping map = (Mapping)mappings[field.Key].SelectedItem;
                    string raw = "";
                    if (map != null && map.Constant != null) raw = map.Constant;
                    else if (map != null && map.Column != null) source.TryGetValue(map.Column, out raw);
                    raw = (raw ?? "").Trim();
                    if (raw == "" && field.Key == "currency") raw = currency;
                    try { record.Fields[field.Key] = Normalize(field, raw); }
                    catch (Exception exception)
                    {
                        ++errorCount;
                        if (rowErrors.Count < 20) rowErrors.Add("Строка " + (i + 1) + ", «" + field.Label + "»: " + exception.Message);
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
        private string Normalize(FieldDef field, string raw)
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



