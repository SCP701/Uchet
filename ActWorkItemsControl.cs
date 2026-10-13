using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    public sealed class ActWorkItemsControl : UserControl
    {
        private readonly Record source;
        private readonly TextBox amountInput, descriptionInput, hoursInput;
        private readonly DataGridView items = new DataGridView();
        private readonly CheckBox manualPricing = new CheckBox();
        private readonly TextBox caption = new TextBox();
        private readonly Label totals = new Label(), guidance = new Label(), currency = new Label();
        private bool loading;
        private string lastManualAmount;
        private string originalRows;
        private bool originalManual;
        private string originalCaption;
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
        public DataGridView WorkGrid { get { return items; } }
        public bool IsManualPricing { get { return manualPricing.Checked; } }
        public event EventHandler TotalsChanged;

        public ActWorkItemsControl(Record act, TextBox amount, TextBox description, TextBox hours)
        {
            source = act.Clone(); amountInput = amount; descriptionInput = description; hoursInput = hours;
            Dock = DockStyle.Fill; Padding = new Padding(14, 12, 14, 10);
            BackColor = Color.FromArgb(247, 249, 252); Font = new Font("Segoe UI", 10F);
            var heading = new TableLayoutPanel { Dock = DockStyle.Top, Height = 53, ColumnCount = 2 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.Controls.Add(new Label { Text = "Название услуги", AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, 0, 0);
            caption.Dock = DockStyle.Fill; caption.MaxLength = 2000; caption.Margin = new Padding(0, 3, 0, 0);
            heading.Controls.Add(caption, 1, 0);
            heading.Controls.Add(new Label { Text = "Необязательно. Каждая выполненная работа — в отдельной строке ниже.",
                AutoSize = true, ForeColor = Color.FromArgb(100, 116, 139), Margin = new Padding(0, 7, 0, 0) }, 1, 1);
            var commands = new TableLayoutPanel { Dock = DockStyle.Top, Height = 41, ColumnCount = 3 };
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 159));
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 149));
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var add = new UiButton { Text = "+ Добавить работу", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 5) };
            var remove = new UiButton { Text = "Удалить работу", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 5) };
            add.Click += delegate { AddRow(); }; remove.Click += delegate { RemoveRows(); };
            manualPricing.Text = "Общая сумма без цен по строкам"; manualPricing.AutoSize = true;
            manualPricing.Anchor = AnchorStyles.Right; manualPricing.Margin = new Padding(0, 3, 0, 8);
            manualPricing.CheckedChanged += delegate
            {
                if (loading) return;
                if (manualPricing.Checked) { if (lastManualAmount != null) amountInput.Text = lastManualAmount; }
                else lastManualAmount = amountInput.Text;
                ApplyMode(); RefreshTotals();
            };
            commands.Controls.Add(add, 0, 0); commands.Controls.Add(remove, 1, 0); commands.Controls.Add(manualPricing, 2, 0);

            items.Dock = DockStyle.Fill; items.BackgroundColor = Color.White; items.BorderStyle = BorderStyle.FixedSingle;
            items.AllowUserToAddRows = false; items.AllowUserToDeleteRows = false; items.RowHeadersVisible = false;
            items.SelectionMode = DataGridViewSelectionMode.FullRowSelect; items.MultiSelect = true;
            items.ColumnHeadersHeight = 37; items.EnableHeadersVisualStyles = false;
            items.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(227, 235, 241);
            items.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            items.DefaultCellStyle.WrapMode = DataGridViewTriState.True; items.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            items.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 251);
            items.RowTemplate.Height = 39; items.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            items.AccessibleName = "Работы акта отдельными пунктами";
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "number", HeaderText = "№", Width = 43, ReadOnly = true });
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "description", HeaderText = "Выполненная работа", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 220, MaxInputLength = 2000 });
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "unit", HeaderText = "Ед.", Width = 56, MaxInputLength = 50 });
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "qty", HeaderText = "Кол-во", Width = 84 });
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "price", HeaderText = "Цена", Width = 94 });
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "amount", HeaderText = "Сумма", Width = 112, ReadOnly = true });
            foreach (DataGridViewColumn column in items.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            foreach (string key in new[] { "qty", "price", "amount" }) items.Columns[key].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            items.Columns["number"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            items.Columns["amount"].DefaultCellStyle.BackColor = Color.FromArgb(240, 244, 249);
            items.CellValueChanged += delegate { if (!loading) RefreshTotals(); };
            items.RowsRemoved += delegate { if (!loading) RefreshTotals(); };
            items.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs e)
            {
                TextBox edit = e.Control as TextBox;
                if (edit != null) { edit.TextChanged -= EditingTextChanged; edit.TextChanged += EditingTextChanged; }
            };
            items.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; e.Cancel = true; };
            var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 66, ColumnCount = 4, Padding = new Padding(0, 9, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 162));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 63));
            totals.Dock = DockStyle.Fill; totals.Font = new Font(Font, FontStyle.Bold); totals.Margin = new Padding(0, 7, 8, 0);
            footer.Controls.Add(totals, 0, 0);
            footer.Controls.Add(new Label { Text = "Итого по акту", AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, 1, 0);
            amountInput.Dock = DockStyle.Fill; amountInput.Margin = new Padding(0, 3, 8, 0);
            amountInput.Font = new Font("Segoe UI", 12F, FontStyle.Bold); amountInput.AccessibleName = "Итоговая сумма акта";
            footer.Controls.Add(amountInput, 2, 0);
            currency.AutoSize = true; currency.Margin = new Padding(0, 8, 0, 0); footer.Controls.Add(currency, 3, 0);
            guidance.Dock = DockStyle.Fill; guidance.ForeColor = Color.FromArgb(100, 116, 139);
            guidance.Margin = new Padding(0, 7, 0, 0); footer.Controls.Add(guidance, 0, 1); footer.SetColumnSpan(guidance, 4);
            Controls.Add(items); Controls.Add(footer); Controls.Add(commands); Controls.Add(heading);
            loading = true;
            string summary = ""; List<ActHtmlLine> rows;
            bool hasWork = source.Get("workItems") != "" || source.Get(ActDocument.ItemsField) != "" ||
                source.Get("description") != "" || source.Get(ActHtmlDocument.DescriptiveItemsField) != "";
            if (hasWork && Store.ParseMoney(source.Get("amount") == "" ? "0" : source.Get("amount")) > 0)
            {
                try { rows = ActHtmlDocument.ReadDisplayRows(source, out summary); }
                catch (InvalidOperationException)
                {
                    if (source.Get("workItems") != "" || source.Get(ActDocument.ItemsField) != "" ||
                        source.Get(ActHtmlDocument.DescriptiveItemsField) != "" || source.Get("description").Length <= 2000) throw;
                    rows = new List<ActHtmlLine> { new ActHtmlLine { Description = source.Get("description") } };
                }
            }
            else rows = new List<ActHtmlLine>();
            caption.Text = source.Get(ActDocument.DescriptionField) != "" ? source.Get(ActDocument.DescriptionField) : summary;
            manualPricing.Checked = rows.Count > 0 && rows.All(row => !row.Quantity.HasValue);
            if (rows.Count == 0 && source.Get("amount") != "" && Store.ParseMoney(source.Get("amount")) > 0)
            {
                manualPricing.Checked = true;
                rows.Add(new ActHtmlLine { Description = "Выполненные работы (услуги)" });
            }
            foreach (ActHtmlLine row in rows)
                items.Rows.Add(items.Rows.Count + 1, row.Description, row.Unit,
                    row.Quantity.HasValue ? row.Quantity.Value.ToString("0.######", Russian) : "",
                    row.Price.HasValue ? row.Price.Value.ToString("0.00", Russian) : "", "");
            if (items.Rows.Count == 0) items.Rows.Add(1, "", "усл.", "1", "", "");
            originalManual = manualPricing.Checked; originalCaption = caption.Text; originalRows = RowsFingerprint();
            lastManualAmount = originalManual ? amountInput.Text : null;
            loading = false; ApplyMode(); RefreshTotals();
            amountInput.TextChanged += delegate { if (!loading && manualPricing.Checked) { lastManualAmount = amountInput.Text; RefreshTotals(); } };
        }

        private void EditingTextChanged(object sender, EventArgs e) { if (!loading) RefreshTotals(); }
        public void SetCurrency(string value) { currency.Text = value; }
        private object CellValue(DataGridViewRow row, string key)
        {
            if (items.IsCurrentCellInEditMode && items.CurrentCell != null && items.CurrentCell.RowIndex == row.Index &&
                items.CurrentCell.OwningColumn.Name == key && items.EditingControl is TextBox)
                return ((TextBox)items.EditingControl).Text;
            return row.Cells[key].Value;
        }
        private static bool Number(object value, out decimal number)
        {
            string text = Convert.ToString(value).Trim().Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "").Replace(',', '.');
            return Decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
        }
        private void ApplyMode()
        {
            amountInput.ReadOnly = !manualPricing.Checked; amountInput.BackColor = manualPricing.Checked ? Color.White : Color.FromArgb(240, 244, 249);
            foreach (string key in new[] { "unit", "qty", "price", "amount" }) items.Columns[key].Visible = !manualPricing.Checked;
        }
        public void AddRow()
        {
            if (items.Rows.Count >= ActDocument.MaxItems) return;
            items.EndEdit();
            bool hourly = source.Get("workItems") != "" && items.Rows.Cast<DataGridViewRow>().All(r => Convert.ToString(r.Cells["unit"].Value) == "ч");
            string unit = manualPricing.Checked ? "" : hourly ? "ч" : "усл.";
            string price = hourly && source.Get("hourlyRate") != "" ? Store.ParseMoney(source.Get("hourlyRate")).ToString("0.00", Russian) : "";
            int index = items.Rows.Add(items.Rows.Count + 1, "", unit, manualPricing.Checked ? "" : "1", price, "");
            items.ClearSelection(); items.CurrentCell = items.Rows[index].Cells["description"]; items.Focus(); items.BeginEdit(true);
            RefreshTotals();
        }
        private void RemoveRows()
        {
            items.EndEdit();
            var selected = items.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index).OrderByDescending(i => i).ToList();
            foreach (int index in selected) items.Rows.RemoveAt(index);
            RefreshTotals();
        }
        public void RefreshTotals()
        {
            if (loading || items.IsDisposed) return;
            loading = true;
            try
            {
                decimal sum = 0, hours = 0; bool valid = true;
                foreach (DataGridViewRow row in items.Rows)
                {
                    row.Cells["number"].Value = row.Index + 1;
                    if (manualPricing.Checked) { row.Cells["amount"].Value = "—"; continue; }
                    decimal quantity, price;
                    if (!Number(CellValue(row, "qty"), out quantity) || !Number(CellValue(row, "price"), out price) ||
                        quantity <= 0 || quantity > 1000000000000m || price < 0 || price > 1000000000000m ||
                        Decimal.Round(quantity, 6) != quantity || Decimal.Round(price, 2) != price)
                    { row.Cells["amount"].Value = "—"; valid = false; continue; }
                    decimal itemAmount = Decimal.Round(quantity * price, 2, MidpointRounding.AwayFromZero);
                    row.Cells["amount"].Value = itemAmount.ToString("N2", Russian); sum += itemAmount;
                    if (String.Equals(Convert.ToString(CellValue(row, "unit")).Trim(), "ч", StringComparison.OrdinalIgnoreCase)) hours += quantity;
                }
                if (!manualPricing.Checked) amountInput.Text = sum.ToString("0.00", CultureInfo.CurrentCulture);
                totals.Text = "Работ: " + items.Rows.Count + (hours > 0 ? "   ·   Часов: " + hours.ToString("0.######", Russian) : "");
                guidance.Text = manualPricing.Checked ? "Цены по отдельным работам не указаны. Введите подтверждённую общую сумму."
                    : valid ? "Сумма рассчитывается автоматически: количество × цена каждой работы."
                    : "Заполните количество и цену каждой работы. Итог обновляется автоматически.";
                guidance.ForeColor = valid || manualPricing.Checked ? Color.FromArgb(100, 116, 139) : Color.FromArgb(146, 64, 14);
            }
            catch (OverflowException) { guidance.Text = "Количество или цена слишком велики."; guidance.ForeColor = Color.FromArgb(185, 28, 28); }
            finally { loading = false; }
            if (TotalsChanged != null) TotalsChanged(this, EventArgs.Empty);
        }
        private string RowsFingerprint()
        {
            return String.Join("\u001e", items.Rows.Cast<DataGridViewRow>().Select(row => String.Join("\u001f",
                new[] { "description", "unit", "qty", "price" }.Select(key => Convert.ToString(row.Cells[key].Value)))));
        }
        public void ApplyTo(Record result)
        {
            if (!items.EndEdit()) throw new InvalidOperationException("Завершите редактирование текущей работы.");
            if (items.Rows.Count == 0 || items.Rows.Count > 200) throw new InvalidOperationException("Добавьте от 1 до 200 работ.");
            bool changed = RowsFingerprint() != originalRows || manualPricing.Checked != originalManual;
            string[] descriptions = items.Rows.Cast<DataGridViewRow>().Select(row => Convert.ToString(row.Cells["description"].Value).Trim()).ToArray();
            for (int i = 0; i < descriptions.Length; ++i)
                if (descriptions[i] == "" || descriptions[i].Length > 2000 && (changed || !originalManual)) throw new InvalidOperationException("Работа " + (i + 1) + ": укажите описание до 2000 символов.");
            if (manualPricing.Checked)
            {
                decimal manual;
                if (!Number(result.Get("amount"), out manual) || manual <= 0 || manual > 1000000000000m || Decimal.Round(manual, 2) != manual)
                    throw new InvalidOperationException("Укажите общую сумму акта больше нуля, с точностью до копеек.");
                if (changed)
                {
                    result.Set(ActHtmlDocument.DescriptiveItemsField, ActHtmlDocument.SerializeDescriptions(descriptions));
                    result.Fields.Remove(ActDocument.ItemsField); result.Fields.Remove("workItems"); result.Fields.Remove("hours"); result.Fields.Remove("hourlyRate");
                }
            }
            else
            {
                var lines = new List<ActPrintLine>();
                foreach (DataGridViewRow row in items.Rows)
                {
                    decimal quantity, price; string prefix = "Работа " + (row.Index + 1) + ": ";
                    if (!Number(row.Cells["qty"].Value, out quantity) || quantity <= 0 || quantity > 1000000000000m || Decimal.Round(quantity, 6) != quantity)
                        throw new InvalidOperationException(prefix + "количество должно быть больше нуля; до 6 знаков после запятой.");
                    if (!Number(row.Cells["price"].Value, out price) || price < 0 || price > 1000000000000m || Decimal.Round(price, 2) != price)
                        throw new InvalidOperationException(prefix + "укажите неотрицательную цену с точностью до копеек.");
                    lines.Add(new ActPrintLine { Description = descriptions[row.Index], Unit = Convert.ToString(row.Cells["unit"].Value).Trim(), Quantity = quantity, Price = price });
                }
                decimal sum;
                try { sum = lines.Sum(line => line.Amount); }
                catch (OverflowException) { throw new InvalidOperationException("Сумма работ превышает допустимый предел."); }
                if (sum <= 0 || sum > 1000000000000m) throw new InvalidOperationException("Сумма акта должна быть больше нуля и не больше одного триллиона.");
                if (Store.ParseMoney(result.Get("amount")) != sum)
                    throw new InvalidOperationException("Позиции акта определяют итоговую сумму. Измените количество или цену в строках работ.");
                if (changed)
                {
                    result.Set("amount", Store.MoneyString(sum)); result.Fields.Remove(ActHtmlDocument.DescriptiveItemsField);
                    if (source.Get("workItems") != "" && lines.All(line => String.Equals(line.Unit, "ч", StringComparison.OrdinalIgnoreCase)))
                        Store.SetHourlyActLines(result, lines.Select(line => new HourlyActLine { Description = line.Description, Hours = line.Quantity, Rate = line.Price }));
                    else { result.Fields.Remove("workItems"); result.Fields.Remove("hours"); result.Fields.Remove("hourlyRate"); }
                    result.Set(ActDocument.ItemsField, ActDocument.SerializeItems(lines));
                }
                ActDocument.ValidateItems(result, lines);
            }
            if (changed)
            {
                string joined = String.Join("\r\n", descriptions.Select((text, i) => (i + 1) + ". " + text));
                result.Set("description", joined); descriptionInput.Text = joined;
                hoursInput.Text = result.Get("hours");
            }
            if (changed || caption.Text.Trim() != originalCaption) result.Set(ActDocument.DescriptionField, caption.Text.Trim());
        }
    }
}
