using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class LedgerGridLayout
    {
        private sealed class LayoutState
        {
            public bool Applying;
            public int RowCount = -1;
            public List<DataGridViewColumn> Columns = new List<DataGridViewColumn>();
            public readonly Dictionary<DataGridViewColumn, int> Minimums = new Dictionary<DataGridViewColumn, int>();
            public Font Font;
            public bool Dirty = true;
            public float Dpi;
        }

        private static readonly Dictionary<DataGridView, LayoutState> States = new Dictionary<DataGridView, LayoutState>();

        public static int AvailableWidth(DataGridView grid)
        {
            if (grid == null || grid.IsDisposed) return 0;
            int headers = grid.RowHeadersVisible ? grid.RowHeadersWidth : 0;
            // Reserving the vertical bar keeps the same layout when the row count grows.
            return Math.Max(0, grid.ClientSize.Width - headers - SystemInformation.VerticalScrollBarWidth - 3);
        }

        public static int VisibleColumnsWidth(DataGridView grid)
        {
            return grid == null ? 0 : grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).Sum(column => column.Width);
        }

        public static bool Fits(DataGridView grid)
        {
            return grid != null && !grid.IsDisposed && VisibleColumnsWidth(grid) <= AvailableWidth(grid)
                && grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).All(column => column.Width >= column.MinimumWidth);
        }

        public static bool IsCurrencyColumn(DataGridViewColumn column)
        {
            return column != null && column.HeaderText.IndexOf("валюта", StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        public static bool IsNumericColumn(DataGridViewColumn column)
        {
            if (column == null) return false;
            string header = column.HeaderText.ToLowerInvariant();
            return new[] { "сумм", "зачтено", "остаток", "удержано", "к оплате", "белая часть", "серая часть", "аванс", "цена", "количество", "часов", "с допсоглашениями", "закрыто актами", "осталось работ", "выручка", "затрат", "прибыль", "поступило денег", "выплачено денег", "нам должны", "мы должны", "сальдо", "расход" }
                .Any(value => header.IndexOf(value, StringComparison.Ordinal) >= 0);
        }

        private static bool IsDateColumn(DataGridViewColumn column)
        {
            string header = column.HeaderText.ToLowerInvariant();
            return Object.Equals(column.Tag, "date") || header.Contains("дата") || header.Contains("срок");
        }

        private static string[] Priorities(string section, int report)
        {
            if (section == "reconciliation") return new[] { "Тип", "Дата", "Номер", "Контрагент", "Сумма", "Валюта", "Проверка" };
            if (section == "reports")
            {
                if (report == 0) return new[] { "Акт", "Дата", "Контрагент", "К оплате", "Состояние", "Сумма", "Срок", "Зачтено", "Удержано", "Общий остаток", "Объект", "Направление" };
                if (report == 1) return new[] { "Объект", "Выручка по актам", "Всего затрат", "Прибыль", "Поступило денег", "Выплачено денег", "Акты подрядчиков", "Прочие расходы" };
                if (report == 2) return new[] { "Дата", "Документ", "Контрагент", "Сальдо", "Изменение сальдо", "Нам должны (+)", "Мы должны (-)" };
                if (report == 3) return new[] { "Договор", "Контрагент", "С допсоглашениями", "Закрыто актами", "Осталось работ", "Первоначальная сумма", "Объект", "Направление" };
                if (report == 4) return new[] { "Дата", "Платёж", "Контрагент", "Сумма", "Направление", "Осталось авансом", "Зачтено в акты", "Объект" };
                if (report == 6) return new[] { "Акт", "Дата", "Контрагент", "Сумма", "Валюта", "Направление", "Дата подписания", "Договор", "Объект" };
                return new[] { "Документ", "Контрагент", "Срок / дата", "Задача", "Раздел", "Объект" };
            }
            if (Catalog.IsActSection(section)) return new[] { "Номер", "Дата", "Контрагент", "Сумма", "Валюта", "Проверка сверки", "Статус", "Состояние", "К оплате", "Оплата", "Срок оплаты", "Зачтено", "Удержано", "Объект", "Договор", "Направление" };
            if (Catalog.IsPaymentSection(section)) return new[] { "Дата", "Номер документа", "Контрагент", "Сумма", "Валюта", "Проверка сверки", "Направление", "Аванс в этом разделе", "Зачтено в этом разделе", "Белая часть", "Серая часть", "Вид оплаты", "Способ оплаты", "Объект", "Договор", "Акт" };
            if (section == "party") return new[] { "Название / ФИО", "УНП", "Телефон", "Электронная почта", "Роль", "Сумма обслуживания", "Валюта обслуживания", "Ежемесячное обслуживание", "Расчётный счёт (IBAN)" };
            if (section == "document") return new[] { "Название", "Дата", "Контрагент", "Статус", "Состояние", "Проверка сверки", "Объект", "Договор", "Акт" };
            if (section == "project") return new[] { "Название", "Контрагент", "Статус", "Состояние", "Прибыль", "Выручка", "Затраты", "Адрес / расположение" };
            if (section == "contract") return new[] { "Номер", "Дата", "Контрагент", "Сумма", "Валюта", "Статус", "Состояние", "Осталось работ", "Объект", "С допсоглашениями", "Закрыто актами", "Направление" };
            if (section == "invoice") return new[] { "Номер", "Дата", "Контрагент", "Сумма", "Валюта", "Статус", "Состояние", "Оплата по акту", "Срок оплаты", "Акт", "Объект", "Договор" };
            if (section == "estimate") return new[] { "Номер", "Дата", "Контрагент", "Сумма сметы", "Валюта", "Статус", "Состояние", "Объект", "Договор" };
            if (section == "work") return new[] { "Наименование работы", "Сумма", "Количество", "Цена за единицу", "Единица измерения", "Смета", "Валюта" };
            if (section == "expense") return new[] { "Дата", "Объект", "Сумма", "Валюта", "Категория", "Контрагент" };
            if (section == "hold") return new[] { "Акт", "Дата", "Сумма", "Валюта", "Статус", "Состояние", "Причина" };
            if (section == "audit") return new[] { "Дата и время", "Описание", "Раздел", "Действие", "Пользователь" };
            return new[] { "Номер", "Дата", "Название", "Контрагент", "Сумма", "Валюта", "Статус", "Состояние" };
        }

        private static int Measure(DataGridView grid, string value)
        {
            return (value ?? "").Split(new[] { '\r', '\n' }).Max(line =>
                TextRenderer.MeasureText(line, grid.Font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
        }

        private static int Minimum(DataGridView grid, DataGridViewColumn column)
        {
            int padding = 16;
            if (IsCurrencyColumn(column)) return Math.Max(62, Measure(grid, "Валюта") + padding);
            if (IsDateColumn(column))
            {
                int width = Measure(grid, column.HeaderText == "Дата и время" ? "31.12.2026 23:59:59 (Минск)" : "31.12.2026") + padding;
                foreach (DataGridViewRow row in grid.Rows)
                {
                    string value = Convert.ToString(row.Cells[column.Index].Value);
                    if (value.Length > 0 && Char.IsDigit(value[0])) width = Math.Max(width, Measure(grid, value) + padding);
                }
                return Math.Max(90, width);
            }
            if (IsNumericColumn(column))
            {
                int width = Measure(grid, "12 880,25") + padding;
                foreach (DataGridViewRow row in grid.Rows)
                {
                    string value = Convert.ToString(row.Cells[column.Index].Value);
                    if (!String.IsNullOrWhiteSpace(value)) width = Math.Max(width, Measure(grid, value) + padding);
                }
                return Math.Max(100, width);
            }
            string header = column.HeaderText;
            if (header == "Контрагент" || header == "Название / ФИО" || header == "Название" || header == "Наименование работы" || header == "Описание" || header == "Объект") return 145;
            if (header == "Номер" || header == "Номер документа" || header == "Документ" || header == "Акт" || header == "Договор")
                return Math.Max(104, Measure(grid, "BLR_2025_0140") + padding);
            if (header.IndexOf("Проверка", StringComparison.OrdinalIgnoreCase) >= 0) return 115;
            if (header == "Статус" || header == "Состояние" || header == "Оплата") return 100;
            if (header == "Телефон") return 128;
            if (header == "Электронная почта") return 160;
            return 104;
        }

        public static bool TryNumber(object value, out decimal number)
        {
            string raw = Convert.ToString(value).Trim().Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "").Replace(',', '.');
            return Decimal.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out number);
        }

        private static LayoutState State(DataGridView grid)
        {
            LayoutState state;
            if (States.TryGetValue(grid, out state)) return state;
            state = new LayoutState(); States.Add(grid, state);
            grid.Disposed += delegate { States.Remove(grid); };
            grid.CellValueChanged += delegate { state.Dirty = true; };
            grid.RowsAdded += delegate { state.Dirty = true; };
            grid.RowsRemoved += delegate { state.Dirty = true; };
            grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (e.ColumnIndex < 0 || !IsCurrencyColumn(grid.Columns[e.ColumnIndex])) return;
                string value = Convert.ToString(e.Value).Trim();
                foreach (string code in new[] { "BYN", "RUB", "USD", "EUR" })
                    if (value == code || value.StartsWith(code + " ", StringComparison.Ordinal)) { e.Value = code; e.FormattingApplied = true; break; }
            };
            grid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                if (e.RowIndex >= 0 && e.ColumnIndex >= 0) e.ToolTipText = Convert.ToString(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value);
            };
            return state;
        }

        public static void Apply(DataGridView grid, string section, int reportIndex)
        {
            if (grid == null || grid.IsDisposed || grid.Columns.Count == 0 || AvailableWidth(grid) < 150) return;
            LayoutState state = State(grid);
            if (state.Applying) return;
            state.Applying = true; grid.SuspendLayout();
            try
            {
                var columns = grid.Columns.Cast<DataGridViewColumn>().ToList();
                float dpi;
                using (Graphics graphics = grid.CreateGraphics()) dpi = graphics.DpiX;
                if (state.Dirty || state.Dpi != dpi || state.RowCount != grid.Rows.Count || state.Font != grid.Font || !state.Columns.SequenceEqual(columns))
                {
                    state.Minimums.Clear(); state.Columns = columns; state.RowCount = grid.Rows.Count; state.Font = grid.Font; state.Dpi = dpi; state.Dirty = false;
                    foreach (DataGridViewColumn column in columns) state.Minimums[column] = Minimum(grid, column);
                    foreach (DataGridViewRow row in grid.Rows)
                        foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = Convert.ToString(cell.Value);
                }
                int available = AvailableWidth(grid), used = 0;
                var ordered = new List<DataGridViewColumn>();
                foreach (string header in Priorities(section, reportIndex))
                {
                    DataGridViewColumn column = columns.FirstOrDefault(value => value.HeaderText == header);
                    if (column != null && !ordered.Contains(column)) ordered.Add(column);
                }
                ordered.AddRange(columns.Where(column => !ordered.Contains(column) && column.HeaderText != "В архиве"));
                int display = 0;
                foreach (DataGridViewColumn column in ordered) column.DisplayIndex = display++;
                foreach (DataGridViewColumn column in columns.Where(column => !ordered.Contains(column))) column.DisplayIndex = display++;
                var visible = new HashSet<DataGridViewColumn>();
                foreach (DataGridViewColumn column in ordered)
                {
                    int minimum = state.Minimums[column];
                    if (used + minimum > available) continue;
                    visible.Add(column); used += minimum;
                }
                if (visible.Count == 0) visible.Add(columns[0]);
                foreach (DataGridViewColumn column in columns.Where(visible.Contains)) column.Visible = true;
                if (grid.CurrentCell != null && !visible.Contains(grid.CurrentCell.OwningColumn))
                    grid.CurrentCell = grid.CurrentCell.OwningRow.Cells[columns.First(column => visible.Contains(column)).Index];
                grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
                grid.AllowUserToResizeColumns = false;
                grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
                foreach (DataGridViewColumn column in columns)
                {
                    column.MinimumWidth = Math.Min(state.Minimums[column], available);
                    column.Visible = visible.Contains(column);
                    column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    bool text = !IsNumericColumn(column) && !IsDateColumn(column) && !IsCurrencyColumn(column);
                    column.FillWeight = column.MinimumWidth * (text && (column.HeaderText == "Контрагент" || column.HeaderText == "Название" || column.HeaderText == "Название / ФИО" || column.HeaderText == "Описание" || column.HeaderText == "Объект") ? 1.55F : 1F);
                    column.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
                    column.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
                    column.DefaultCellStyle.Alignment = IsNumericColumn(column) ? DataGridViewContentAlignment.MiddleRight
                        : IsDateColumn(column) || IsCurrencyColumn(column) ? DataGridViewContentAlignment.MiddleCenter : DataGridViewContentAlignment.MiddleLeft;
                    column.HeaderCell.Style.WrapMode = DataGridViewTriState.True;
                    column.HeaderCell.Style.Padding = new Padding(4, 5, 4, 5);
                }
                var displayed = columns.Where(visible.Contains).OrderBy(column => column.DisplayIndex).ToList();
                int minimumTotal = displayed.Sum(column => column.MinimumWidth);
                int extra = Math.Max(0, available - minimumTotal), remaining = extra;
                float weights = displayed.Sum(column => column.FillWeight);
                for (int i = 0; i < displayed.Count; i++)
                {
                    DataGridViewColumn column = displayed[i];
                    int addition = i == displayed.Count - 1 ? remaining : (int)Math.Floor(extra * column.FillWeight / weights);
                    remaining -= addition; column.Width = column.MinimumWidth + addition;
                }
                grid.HorizontalScrollingOffset = 0;
            }
            finally { grid.ResumeLayout(); state.Applying = false; }
        }
    }
}
