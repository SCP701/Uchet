using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    public sealed class ActItemsEditor : Form
    {
        private readonly Store store;
        private readonly Record source;
        private readonly DataGridView items = new DataGridView();
        private readonly TextBox description = new TextBox();
        private readonly Label total = new Label();
        private readonly Label status = new Label();
        private readonly Label mode = new Label();
        private readonly Button save = new Button();
        private readonly ToolTip tips = new ToolTip();
        private bool updating;
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
        public Record ResultRecord { get; private set; }

        public ActItemsEditor(Store store, Record act)
        {
            if (store == null || act == null || act.Kind != "act") throw new ArgumentException("Выберите акт для редактирования позиций.");
            this.store = store;
            source = act.Clone();
            Text = "Позиции акта № " + source.Get("number");
            Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(247, 249, 252);
            ForeColor = Color.FromArgb(31, 41, 55);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1120, Math.Min(790, Screen.PrimaryScreen.WorkingArea.Height - 50));
            MinimumSize = new Size(870, 560);
            ShowIcon = false; ShowInTaskbar = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;

            Panel header = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.White };
            header.Controls.Add(new Label
            {
                Text = Text, Font = new Font("Segoe UI", 17, FontStyle.Bold),
                Dock = DockStyle.Top, Height = 48, Padding = new Padding(22, 12, 14, 0)
            });
            header.Controls.Add(new Label
            {
                Text = "Сохранение обновит позиции и сумму акта вместе. Оплаты и удержания проверяются перед сохранением.",
                Dock = DockStyle.Bottom, Height = 31, Padding = new Padding(23, 0, 14, 0),
                ForeColor = Color.FromArgb(100, 116, 139)
            });
            Panel aggregate = new Panel { Dock = DockStyle.Top, Height = 114, Padding = new Padding(22, 10, 22, 12) };
            aggregate.Controls.Add(new Label
            {
                Text = "Общее описание услуги для первой страницы акта", Dock = DockStyle.Top, Height = 25
            });
            description.Multiline = true; description.ScrollBars = ScrollBars.Vertical;
            description.Dock = DockStyle.Bottom; description.Height = 63; description.MaxLength = 32767;
            description.Text = source.Get("printDescription") != "" ? source.Get("printDescription") : source.Get("description");
            aggregate.Controls.Add(description);
            Panel commands = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(22, 2, 22, 8) };
            Button add = SmallButton("+ Добавить строку", AddLine); add.SetBounds(22, 2, 175, 34);
            Button remove = SmallButton("Удалить строку", RemoveLine); remove.SetBounds(209, 2, 164, 34);
            commands.Controls.Add(add); commands.Controls.Add(remove);
            if (source.Get("workItems") != "")
            {
                commands.Height = 88;
                mode.SetBounds(22, 43, 1015, 39);
                mode.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                mode.ForeColor = Color.FromArgb(100, 116, 139);
                commands.Controls.Add(mode);
            }

            items.Dock = DockStyle.Fill; items.BackgroundColor = Color.White; items.BorderStyle = BorderStyle.FixedSingle;
            items.AllowUserToAddRows = false; items.AllowUserToDeleteRows = false; items.AllowUserToOrderColumns = false;
            items.RowHeadersVisible = false; items.MultiSelect = true; items.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            items.EnableHeadersVisualStyles = false; items.ColumnHeadersHeight = 37;
            items.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(227, 235, 241);
            items.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            items.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 251);
            items.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 119, 116); items.DefaultCellStyle.SelectionForeColor = Color.White;
            items.RowTemplate.Height = 36; items.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "description", HeaderText = "Работа / услуга", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 250 });
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "unit", HeaderText = "Ед.", Width = 90 });
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "qty", HeaderText = "Количество", Width = 130 });
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "price", HeaderText = "Цена", Width = 130 });
            items.Columns.Add(new DataGridViewTextBoxColumn { Name = "amount", HeaderText = "Сумма", Width = 150, ReadOnly = true });
            items.Columns["description"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            foreach (string key in new[] { "qty", "price", "amount" }) items.Columns[key].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            foreach (DataGridViewColumn column in items.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            items.CellValueChanged += delegate { UpdateTotal(); };
            items.RowsRemoved += delegate { UpdateTotal(); };
            items.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e)
            {
                e.ThrowException = false;
                ShowError("Проверьте введённое число. Можно использовать запятую или точку.");
            };
            Panel tablePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 0, 22, 10) };
            tablePanel.Controls.Add(items);

            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 122, BackColor = Color.White, Padding = new Padding(22, 12, 22, 10) };
            total.Font = new Font("Segoe UI", 13, FontStyle.Bold); total.Dock = DockStyle.Top; total.Height = 34;
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 265, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 9, 0, 0) };
            Button cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 110, Height = 40, Margin = new Padding(8, 0, 0, 0), FlatStyle = FlatStyle.Flat };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            save.Text = "Сохранить"; save.Width = 136; save.Height = 40; save.Margin = new Padding(0); save.FlatStyle = FlatStyle.Flat;
            save.BackColor = Color.FromArgb(37, 99, 235); save.ForeColor = Color.White; save.FlatAppearance.BorderSize = 0;
            save.Click += SaveClick;
            buttons.Controls.Add(cancel); buttons.Controls.Add(save);
            status.Text = "Новая сумма рассчитывается по строкам. Изменения применятся только после «Сохранить».";
            status.ForeColor = Color.FromArgb(100, 116, 139); status.Dock = DockStyle.Fill; status.Padding = new Padding(0, 9, 14, 0);
            footer.Controls.Add(status); footer.Controls.Add(buttons); footer.Controls.Add(total);
            Controls.Add(tablePanel); Controls.Add(footer); Controls.Add(commands); Controls.Add(aggregate); Controls.Add(header);
            CancelButton = cancel;

            updating = true;
            foreach (ActPrintLine line in ActDocument.ReadItems(source))
                items.Rows.Add(line.Description, line.Unit, line.Quantity.ToString("0.######", Russian), line.Price.ToString("0.00", Russian), line.Amount.ToString("N2", Russian));
            updating = false;
            UpdateTotal();
        }

        private Button SmallButton(string text, Action action)
        {
            Button button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Height = 34 };
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            button.Click += delegate { action(); };
            return button;
        }

        private void AddLine()
        {
            int maximum = source.Get("workItems") != "" && AllHourlyUnits() ? 100 : 200;
            if (items.Rows.Count >= maximum) { ShowError("В этом режиме допускается не более " + maximum + " позиций."); return; }
            string unit = source.Get("workItems") != "" ? "ч" : "";
            string price = source.Get("workItems") != "" && source.Get("hourlyRate") != ""
                ? Store.ParseMoney(source.Get("hourlyRate")).ToString("0.00", Russian) : "0,00";
            int index = items.Rows.Add("", unit, "1", price, "0,00");
            items.ClearSelection(); items.Rows[index].Selected = true; items.CurrentCell = items.Rows[index].Cells[0];
            items.Focus(); items.BeginEdit(true); UpdateTotal();
        }

        private void RemoveLine()
        {
            List<int> selected = items.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index).OrderByDescending(index => index).ToList();
            if (selected.Count == 0 && items.CurrentRow != null) selected.Add(items.CurrentRow.Index);
            foreach (int index in selected) items.Rows.RemoveAt(index);
            UpdateTotal();
        }

        private static bool TryNumber(object value, out decimal number)
        {
            string text = Convert.ToString(value).Trim().Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "").Replace(',', '.');
            return Decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
        }

        private bool AllHourlyUnits()
        {
            return items.Rows.Cast<DataGridViewRow>().All(row => String.Equals(Convert.ToString(row.Cells["unit"].Value).Trim(), "ч", StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateTotal()
        {
            if (updating || items.IsDisposed) return;
            updating = true;
            if (source.Get("workItems") != "")
            {
                bool hourly = AllHourlyUnits();
                mode.Text = hourly
                    ? "Почасовой режим: часы и ставки сохраняются. Другие единицы переведут акт в обычные позиции."
                    : "После сохранения — обычные позиции: единица измерения изменена. Часы и ставки останутся в строках с единицей «ч».";
                mode.ForeColor = hourly ? Color.FromArgb(100, 116, 139) : Color.FromArgb(146, 64, 14);
            }
            decimal sum = 0;
            bool valid = true;
            try
            {
                foreach (DataGridViewRow row in items.Rows)
                {
                    decimal quantity, price;
                    if (!TryNumber(row.Cells["qty"].Value, out quantity) || !TryNumber(row.Cells["price"].Value, out price)
                        || quantity <= 0 || price < 0 || Decimal.Round(quantity, 6) != quantity || Decimal.Round(price, 2) != price)
                    {
                        row.Cells["amount"].Value = "—"; valid = false; continue;
                    }
                    decimal amount = Decimal.Round(quantity * price, 2, MidpointRounding.AwayFromZero);
                    row.Cells["amount"].Value = amount.ToString("N2", Russian);
                    sum += amount;
                }
                total.Text = "Новая сумма акта: " + sum.ToString("N2", Russian) + " " + source.Get("currency")
                    + "   ·   Было: " + Store.ParseMoney(source.Get("amount")).ToString("N2", Russian) + " " + source.Get("currency")
                    + (valid ? "" : "   ·   Проверьте числа");
                total.ForeColor = valid ? ForeColor : Color.FromArgb(185, 28, 28);
            }
            catch (OverflowException)
            {
                total.Text = "Новая сумма акта: проверьте слишком большие числа.";
                total.ForeColor = Color.FromArgb(185, 28, 28);
            }
            finally { updating = false; }
        }

        internal Record PrepareRecord()
        {
            if (!items.EndEdit()) throw new InvalidOperationException("Завершите редактирование текущей строки.");
            List<ActPrintLine> lines = new List<ActPrintLine>();
            decimal sum = 0;
            foreach (DataGridViewRow row in items.Rows)
            {
                decimal quantity, price;
                string prefix = "Строка " + (row.Index + 1).ToString(CultureInfo.InvariantCulture) + ": ";
                string text = Convert.ToString(row.Cells["description"].Value).Trim();
                if (text == "") throw new InvalidOperationException(prefix + "укажите работу или услугу.");
                if (!TryNumber(row.Cells["qty"].Value, out quantity) || quantity <= 0 || Decimal.Round(quantity, 6) != quantity)
                    throw new InvalidOperationException(prefix + "количество должно быть больше нуля; допускается до 6 знаков после запятой.");
                if (!TryNumber(row.Cells["price"].Value, out price) || price < 0 || Decimal.Round(price, 2) != price)
                    throw new InvalidOperationException(prefix + "цена должна быть неотрицательной, с точностью до копеек.");
                ActPrintLine line = new ActPrintLine { Description = text, Unit = Convert.ToString(row.Cells["unit"].Value).Trim(), Quantity = quantity, Price = price };
                try { sum += line.Amount; }
                catch (OverflowException) { throw new InvalidOperationException(prefix + "количество или цена слишком велики."); }
                lines.Add(line);
            }
            if (lines.Count == 0) throw new InvalidOperationException("Добавьте хотя бы одну позицию акта.");
            Record result = source.Clone();
            result.Set("amount", Store.MoneyString(sum));
            if (source.Get("workItems") != "")
            {
                if (lines.All(line => String.Equals(line.Unit, "ч", StringComparison.OrdinalIgnoreCase)))
                    Store.SetHourlyActLines(result, lines.Select(line => new HourlyActLine
                    {
                        Description = line.Description, Hours = line.Quantity, Rate = line.Price
                    }));
                else
                {
                    // Changing an hour unit explicitly converts this act to general positions.
                    result.Fields.Remove("workItems"); result.Fields.Remove("hours"); result.Fields.Remove("hourlyRate");
                    result.Set("description", String.Join("\r\n", lines.Select(line => line.Description + " — " + line.Quantity.ToString("0.######", Russian) + " " + line.Unit)));
                }
            }
            result.Set("printDescription", description.Text.Trim());
            ActDocument.ValidateItems(result, lines);
            result.Set("printItemsJson", ActDocument.SerializeItems(lines));
            return result;
        }

        private void SaveClick(object sender, EventArgs e)
        {
            try
            {
                save.Enabled = false;
                Record result = PrepareRecord();
                store.Upsert(result);
                Record persisted = store.Find(result.Id) ?? result;
                ActDocument.ValidateItems(persisted, ActDocument.ReadItems(persisted));
                ResultRecord = persisted;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception) { ShowError(exception.Message); }
            finally { save.Enabled = true; }
        }

        private void ShowError(string message)
        {
            status.Text = message;
            status.ForeColor = Color.FromArgb(185, 28, 28);
            tips.SetToolTip(status, message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }
    }
}

