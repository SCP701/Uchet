using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    public sealed class QuarterlyTaxesPage : UserControl
    {
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
        private static readonly string[] Currencies = { "BYN", "RUB", "USD", "EUR" };
        private readonly Store store;
        private readonly NumericUpDown year;
        private readonly DateTimePicker fromDate, toDate;
        private readonly Label yearCaption, quarterCaption;
        private readonly ComboBox mode, quarter, currency;
        private readonly TableLayoutPanel layout, cards;
        private readonly List<Control> summaryCards = new List<Control>();
        private readonly Label period, receiptsValue, preliminaryValue, taxValue, remainingValue, paymentCount, emptyState;
        private readonly DataGridView payments;
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly Font[] valueFonts;
        private bool changingPeriod, fittingAmount, arrangingCards;
        public event Action<string> PaymentRequested;
        public QuarterlyTaxResult CurrentResult { get; private set; }
        public bool AllTime { get { return mode.SelectedIndex == 1; } }
        public bool CustomPeriod { get { return mode.SelectedIndex == 2; } }
        public DateTime SelectedFromDate { get { return fromDate.Value.Date; } }
        public DateTime SelectedToDate { get { return toDate.Value.Date; } }
        public int SelectedYear { get { return Decimal.ToInt32(year.Value); } }
        public int SelectedQuarter { get { return quarter.SelectedIndex + 1; } }
        public string SelectedCurrency { get { return Convert.ToString(currency.SelectedItem); } }

        public QuarterlyTaxesPage(Store data)
        {
            if (data == null) throw new ArgumentNullException("data");
            store = data; Name = "quarterlyTaxesPage"; Dock = DockStyle.Fill;
            BackColor = UiStyle.Background; ForeColor = UiStyle.Ink;
            Font = OwnFont(9.5F, FontStyle.Regular); DoubleBuffered = true;
            valueFonts = new[] { OwnFont(22, FontStyle.Bold), OwnFont(20, FontStyle.Bold),
                OwnFont(18, FontStyle.Bold), OwnFont(16, FontStyle.Bold), OwnFont(14, FontStyle.Bold), OwnFont(12, FontStyle.Bold) };
            layout = new TableLayoutPanel { Name = "taxLayout", Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 4, BackColor = UiStyle.Background, Padding = new Padding(0), Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 192));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var selectors = new UiPanel { Name = "taxSelectors", Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 8), Margin = new Padding(0, 0, 0, 4) };
            var selectorLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, Margin = new Padding(0) };
            selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
            selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selectorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            selectorLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            selectors.Controls.Add(selectorLayout); layout.Controls.Add(selectors, 0, 0);
            selectorLayout.Controls.Add(SmallLabel("Расчёт"), 0, 0);
            yearCaption = SmallLabel("Год"); quarterCaption = SmallLabel("Квартал");
            selectorLayout.Controls.Add(yearCaption, 1, 0); selectorLayout.Controls.Add(quarterCaption, 2, 0);
            selectorLayout.Controls.Add(SmallLabel("Валюта"), 3, 0);
            selectorLayout.Controls.Add(SmallLabel("Период расчёта"), 4, 0);
            mode = new ComboBox { Name = "taxMode", DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                Width = 176, Font = Font, Margin = new Padding(0), AccessibleName = "Режим расчёта налога" };
            mode.Items.AddRange(new object[] { "По кварталам", "За всё время", "За выбранный период" });
            year = new NumericUpDown { Name = "taxYear", Minimum = 1, Maximum = 9999, Width = 84,
                Font = Font, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0), AccessibleName = "Год расчёта налога" };
            quarter = new ComboBox { Name = "taxQuarter", DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                Width = 140, Font = Font, Margin = new Padding(0), AccessibleName = "Квартал расчёта налога" };
            quarter.Items.AddRange(new object[] { "I квартал", "II квартал", "III квартал", "IV квартал" });
            currency = new ComboBox { Name = "taxCurrency", DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                Width = 84, Font = Font, Margin = new Padding(0), AccessibleName = "Валюта расчёта налога" };
            currency.Items.AddRange(Currencies);
            period = new Label { Name = "taxPeriod", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Font = OwnFont(10, FontStyle.Bold), Margin = new Padding(0), AutoEllipsis = false };
            fromDate = new DateTimePicker { Name = "taxFrom", Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy",
                Width = 96, Font = Font, Margin = new Padding(0), AccessibleName = "Начало периода налога включительно", Visible = false };
            toDate = new DateTimePicker { Name = "taxTo", Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy",
                Width = 140, Font = Font, Margin = new Padding(0), AccessibleName = "Конец периода налога включительно", Visible = false };
            var yearSlot = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            var quarterSlot = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            yearSlot.Controls.Add(year); yearSlot.Controls.Add(fromDate);
            quarterSlot.Controls.Add(quarter); quarterSlot.Controls.Add(toDate);
            selectorLayout.Controls.Add(mode, 0, 1); selectorLayout.Controls.Add(yearSlot, 1, 1); selectorLayout.Controls.Add(quarterSlot, 2, 1);
            selectorLayout.Controls.Add(currency, 3, 1); selectorLayout.Controls.Add(period, 4, 1);

            cards = new TableLayoutPanel { Name = "taxSummaryCards", Dock = DockStyle.Fill,
                Margin = new Padding(0), BackColor = UiStyle.Background };
            summaryCards.Add(SummaryCard("Поступления (n)", "Белые оплаты", UiStyle.Accent, "taxReceipts", out receiptsValue));
            summaryCards.Add(SummaryCard("Предварительный налог (p)", "n × 20%", UiStyle.Accent, "taxPreliminary", out preliminaryValue));
            summaryCards.Add(SummaryCard("Сумма после вычета (g)", "n − p", UiStyle.Accent, "taxRemaining", out remainingValue));
            summaryCards.Add(SummaryCard("Окончательный налог", "g × 20%", UiStyle.Blue, "taxAmount", out taxValue));
            layout.Controls.Add(cards, 0, 1);
            SizeChanged += delegate { ArrangeSummaryCards(); };
            ArrangeSummaryCards();

            var explanation = new Label { Name = "taxExplanation", Dock = DockStyle.Fill, Margin = new Padding(1, 2, 0, 4),
                ForeColor = UiStyle.Muted, TextAlign = ContentAlignment.MiddleLeft, Font = Font,
                Text = "p = n × 20%; g = n − p; окончательный налог = g × 20%.\r\nБелые оплаты и авансы, включая архив. Серые и исходящие оплаты исключены." };
            layout.Controls.Add(explanation, 0, 2);

            var detail = new UiPanel { Name = "taxPaymentsPanel", Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0) };
            var detailLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            detail.Controls.Add(detailLayout); layout.Controls.Add(detail, 0, 3);
            detailLayout.Controls.Add(new Label { Name = "taxPaymentsTitle", Text = "Оплаты, вошедшие в расчёт", Dock = DockStyle.Fill,
                Font = OwnFont(12, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) }, 0, 0);
            var gridHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiStyle.Surface };
            detailLayout.Controls.Add(gridHost, 0, 1);
            payments = new DataGridView { Name = "taxPaymentsGrid", Dock = DockStyle.Fill, ReadOnly = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText, AccessibleName = "Входящие оплаты за выбранный период" };
            UiStyle.ApplyGrid(payments);
            payments.DefaultCellStyle.FormatProvider = Russian;
            AddColumn("date", "Дата", 13, 99, typeof(DateTime)); payments.Columns["date"].DefaultCellStyle.Format = "dd.MM.yyyy";
            AddColumn("number", "Номер", 16, 108, typeof(string));
            AddColumn("party", "Контрагент", 35, 160, typeof(string));
            AddColumn("method", "Способ", 17, 113, typeof(string));
            AddColumn("amount", "Сумма", 19, 132, typeof(decimal));
            payments.Columns["amount"].DefaultCellStyle.Format = "N2";
            payments.Columns["amount"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            payments.Columns["amount"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            gridHost.Controls.Add(payments);
            emptyState = new Label { Name = "taxEmptyState", Dock = DockStyle.Fill, ForeColor = UiStyle.Muted,
                Font = OwnFont(11, FontStyle.Regular), TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(12),
                Text = "За выбранный период белых поступлений нет.\r\nВыберите другой период или добавьте входящую оплату." };
            gridHost.Controls.Add(emptyState); emptyState.BringToFront();
            paymentCount = new Label { Name = "taxPaymentCount", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiStyle.Muted, Font = Font, Margin = new Padding(0, 5, 0, 0) };
            detailLayout.Controls.Add(paymentCount, 0, 2);

            fromDate.ValueChanged += DateRangeChanged; toDate.ValueChanged += DateRangeChanged;
            mode.SelectedIndexChanged += PeriodChanged; year.ValueChanged += PeriodChanged; quarter.SelectedIndexChanged += PeriodChanged; currency.SelectedIndexChanged += PeriodChanged;
            payments.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) RequestPayment(payments.Rows[e.RowIndex]); };
            payments.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && payments.CurrentRow != null && PaymentRequested != null)
                { RequestPayment(payments.CurrentRow); e.Handled = true; e.SuppressKeyPress = true; }
            };
            Record settings = store.Of("settings").FirstOrDefault();
            string defaultCurrency = settings == null ? "BYN" : settings.Get("currency");
            if (!Currencies.Contains(defaultCurrency)) defaultCurrency = "BYN";
            Tuple<int, int> previous = QuarterlyTaxes.PreviousQuarter(DateTime.Today);
            int firstMonth = (previous.Item2 - 1) * 3 + 1, lastMonth = firstMonth + 2;
            changingPeriod = true;
            try { fromDate.Value = new DateTime(previous.Item1, firstMonth, 1); toDate.Value = new DateTime(previous.Item1, lastMonth, DateTime.DaysInMonth(previous.Item1, lastMonth)); }
            finally { changingPeriod = false; }
            SetPeriod(previous.Item1, previous.Item2, defaultCurrency);
        }

        public void SetPeriod(int selectedYear, int selectedQuarter, string selectedCurrency)
        {
            if (selectedYear < 1 || selectedYear > 9999) throw new ArgumentOutOfRangeException("selectedYear");
            if (selectedQuarter < 1 || selectedQuarter > 4) throw new ArgumentOutOfRangeException("selectedQuarter");
            if (!Currencies.Contains(selectedCurrency)) throw new ArgumentException("Выберите валюту BYN, RUB, USD или EUR.", "selectedCurrency");
            changingPeriod = true;
            try { mode.SelectedIndex = 0; year.Value = selectedYear; quarter.SelectedIndex = selectedQuarter - 1; currency.SelectedItem = selectedCurrency; }
            finally { changingPeriod = false; }
            RefreshData();
        }

        public void SetAllTime(string selectedCurrency)
        {
            if (!Currencies.Contains(selectedCurrency)) throw new ArgumentException("Выберите валюту BYN, RUB, USD или EUR.", "selectedCurrency");
            changingPeriod = true;
            try { mode.SelectedIndex = 1; currency.SelectedItem = selectedCurrency; }
            finally { changingPeriod = false; }
            RefreshData();
        }

        public void SetDateRange(DateTime selectedFrom, DateTime selectedTo, string selectedCurrency)
        {
            selectedFrom = selectedFrom.Date; selectedTo = selectedTo.Date;
            if (selectedFrom > selectedTo) throw new ArgumentException("Дата начала периода должна быть не позже даты окончания.", "selectedFrom");
            if (selectedFrom < fromDate.MinDate || selectedFrom > fromDate.MaxDate) throw new ArgumentOutOfRangeException("selectedFrom");
            if (selectedTo < toDate.MinDate || selectedTo > toDate.MaxDate) throw new ArgumentOutOfRangeException("selectedTo");
            if (!Currencies.Contains(selectedCurrency)) throw new ArgumentException("Выберите валюту BYN, RUB, USD или EUR.", "selectedCurrency");
            changingPeriod = true;
            try { fromDate.Value = selectedFrom; toDate.Value = selectedTo; currency.SelectedItem = selectedCurrency; mode.SelectedIndex = 2; }
            finally { changingPeriod = false; }
            RefreshData();
        }

        public void RefreshData()
        {
            if (changingPeriod || SelectedQuarter < 1 || currency.SelectedIndex < 0) return;
            year.Visible = quarter.Visible = !CustomPeriod;
            fromDate.Visible = toDate.Visible = CustomPeriod;
            year.Enabled = quarter.Enabled = !AllTime && !CustomPeriod;
            yearCaption.Text = CustomPeriod ? "С" : "Год"; quarterCaption.Text = CustomPeriod ? "По" : "Квартал";
            QuarterlyTaxResult result = AllTime ? QuarterlyTaxes.CalculateAllTime(store, SelectedCurrency)
                : CustomPeriod ? QuarterlyTaxes.CalculateRange(store, SelectedFromDate, SelectedToDate, SelectedCurrency)
                : QuarterlyTaxes.Calculate(store, SelectedYear, SelectedQuarter, SelectedCurrency);
            CurrentResult = result;
            period.Text = AllTime ? "Все даты поступлений" : CustomPeriod
                ? result.FromDate.ToString("dd.MM.yyyy", Russian) + " — " + result.ToDate.ToString("dd.MM.yyyy", Russian)
                : result.FromDate.ToString("d MMMM", Russian) + " — " + result.ToDate.ToString("d MMMM yyyy", Russian) + " г.";
            SetAmount(receiptsValue, result.Receipts, result.Currency); SetAmount(preliminaryValue, result.PreliminaryTax, result.Currency);
            SetAmount(taxValue, result.Tax, result.Currency);
            SetAmount(remainingValue, result.Remaining, result.Currency);
            payments.SuspendLayout();
            try
            {
                payments.Rows.Clear(); payments.Columns["amount"].HeaderText = "Белая часть, " + result.Currency;
                foreach (TaxReceipt receipt in result.Payments)
                {
                    string number = String.IsNullOrWhiteSpace(receipt.Number) ? "Без номера" : receipt.Number;
                    int index = payments.Rows.Add(receipt.Date, number, receipt.PartyName, MethodLabel(receipt.Method), receipt.Amount);
                    DataGridViewRow row = payments.Rows[index]; row.Tag = receipt.PaymentId;
                    if (receipt.GreyAmount > 0)
                    {
                        foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = "Платёж: " + receipt.TotalAmount.ToString("N2", Russian)
                            + " " + result.Currency + ". Белая часть: " + receipt.Amount.ToString("N2", Russian)
                            + "; серая часть исключена: " + receipt.GreyAmount.ToString("N2", Russian) + ".";
                    }
                    if (receipt.Archived)
                    {
                        row.Cells["number"].Value = number + " (архив)";
                        foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = (cell.ToolTipText == "" ? "" : cell.ToolTipText + " ") + "Оплата в архиве. Белая часть включена в расчёт.";
                    }
                }
                payments.ClearSelection();
            }
            finally { payments.ResumeLayout(); }
            emptyState.Visible = result.Payments.Count == 0; payments.Visible = result.Payments.Count > 0;
            if (emptyState.Visible) emptyState.BringToFront();
            int archived = result.Payments.Count(p => p.Archived);
            paymentCount.Text = "Всего оплат: " + result.Payments.Count.ToString(Russian) + "    ·    Валюта: " + result.Currency
                + (archived == 0 ? "" : "    ·    В архиве: " + archived.ToString(Russian));
        }

        private void ArrangeSummaryCards()
        {
            if (arrangingCards || cards == null || summaryCards.Count != 4) return;
            int columns = ClientSize.Width >= 1000 ? 4 : 2;
            if (cards.ColumnCount == columns) return;
            arrangingCards = true; layout.SuspendLayout(); cards.SuspendLayout();
            try
            {
                cards.Controls.Clear(); cards.ColumnStyles.Clear(); cards.RowStyles.Clear();
                cards.ColumnCount = columns; cards.RowCount = 4 / columns;
                for (int i = 0; i < columns; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
                for (int i = 0; i < cards.RowCount; i++) cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / cards.RowCount));
                for (int i = 0; i < summaryCards.Count; i++)
                {
                    summaryCards[i].Margin = new Padding(0, 0, i % columns == columns - 1 ? 0 : 8, i / columns == cards.RowCount - 1 ? 0 : 6);
                    cards.Controls.Add(summaryCards[i], i % columns, i / columns);
                }
                layout.RowStyles[1].Height = columns == 4 ? 112 : 192;
            }
            finally { cards.ResumeLayout(true); layout.ResumeLayout(true); arrangingCards = false; }
        }
        private void DateRangeChanged(object sender, EventArgs args)
        {
            if (changingPeriod) return;
            changingPeriod = true;
            try
            {
                if (SelectedFromDate > SelectedToDate)
                {
                    if (Object.ReferenceEquals(sender, fromDate)) toDate.Value = fromDate.Value;
                    else fromDate.Value = toDate.Value;
                }
            }
            finally { changingPeriod = false; }
            if (CustomPeriod) RefreshData();
        }
        private void PeriodChanged(object sender, EventArgs args) { RefreshData(); }
        private void RequestPayment(DataGridViewRow row)
        {
            Action<string> requested = PaymentRequested; string id = Convert.ToString(row.Tag);
            if (requested != null && !String.IsNullOrEmpty(id)) requested(id);
        }
        private static string MethodLabel(string method)
        {
            if (method == "bank") return "Банк";
            if (method == "cash") return "Наличные";
            return "Другое";
        }
        private Label SmallLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, ForeColor = UiStyle.Muted, Font = Font, Margin = new Padding(0) };
        }
        private Control SummaryCard(string caption, string description, Color color, string valueName, out Label value)
        {
            var card = new UiPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0) };
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            content.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, Font = OwnFont(10, FontStyle.Regular),
                ForeColor = UiStyle.Muted, Margin = new Padding(0), TextAlign = ContentAlignment.TopLeft }, 0, 0);
            value = new Label { Name = valueName, Dock = DockStyle.Fill, Margin = new Padding(0), Font = valueFonts[0],
                ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            content.Controls.Add(value, 0, 1);
            content.Controls.Add(new Label { Text = description, Dock = DockStyle.Fill, Font = OwnFont(9, FontStyle.Regular),
                ForeColor = UiStyle.Muted, Margin = new Padding(0), TextAlign = ContentAlignment.BottomLeft }, 0, 2);
            card.Controls.Add(content);
            Label captured = value; captured.SizeChanged += delegate { FitAmount(captured); };
            return card;
        }
        private void AddColumn(string name, string title, float weight, int minimum, Type valueType)
        {
            payments.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight,
                MinimumWidth = minimum, ValueType = valueType, SortMode = DataGridViewColumnSortMode.Automatic });
        }
        private void SetAmount(Label label, decimal amount, string moneyCurrency)
        {
            label.Text = amount.ToString("N2", Russian) + " " + moneyCurrency; label.AccessibleName = label.Text; FitAmount(label);
        }
        private void FitAmount(Label label)
        {
            if (fittingAmount) return;
            int width = Math.Max(0, label.ClientSize.Width - 8);
            if (width == 0) return;
            Font selected = valueFonts[valueFonts.Length - 1];
            foreach (Font candidate in valueFonts)
            {
                Size measured = TextRenderer.MeasureText(label.Text, candidate, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                if (measured.Width <= width && measured.Height <= label.ClientSize.Height)
                { selected = candidate; break; }
            }
            if (Object.ReferenceEquals(label.Font, selected)) return;
            Rectangle bounds = label.Bounds; fittingAmount = true;
            try { label.Font = selected; label.Bounds = bounds; }
            finally { fittingAmount = false; }
        }
        private Font OwnFont(float size, FontStyle style)
        {
            var font = new Font("Segoe UI", size, style); ownedFonts.Add(font); return font;
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { foreach (Font font in ownedFonts) font.Dispose(); ownedFonts.Clear(); }
        }
    }
}