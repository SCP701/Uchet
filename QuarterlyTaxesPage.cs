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
        private readonly ComboBox quarter, currency;
        private readonly Label period, receiptsValue, taxValue, remainingValue, paymentCount, emptyState;
        private readonly DataGridView payments;
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly Font[] valueFonts;
        private bool changingPeriod, fittingAmount;
        public event Action<string> PaymentRequested;
        public QuarterlyTaxResult CurrentResult { get; private set; }
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
            var layout = new TableLayoutPanel { Name = "taxLayout", Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 4, BackColor = UiStyle.Background, Padding = new Padding(0), Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 57));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var selectors = new UiPanel { Name = "taxSelectors", Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 8), Margin = new Padding(0, 0, 0, 10) };
            var selectorLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = new Padding(0) };
            selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
            selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
            selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selectorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            selectorLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            selectors.Controls.Add(selectorLayout); layout.Controls.Add(selectors, 0, 0);
            selectorLayout.Controls.Add(SmallLabel("Год"), 0, 0);
            selectorLayout.Controls.Add(SmallLabel("Квартал"), 1, 0);
            selectorLayout.Controls.Add(SmallLabel("Валюта"), 2, 0);
            selectorLayout.Controls.Add(SmallLabel("Период расчёта"), 3, 0);
            year = new NumericUpDown { Name = "taxYear", Minimum = 1, Maximum = 9999, Width = 108,
                Font = Font, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0), AccessibleName = "Год расчёта налога" };
            quarter = new ComboBox { Name = "taxQuarter", DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                Width = 169, Font = Font, Margin = new Padding(0), AccessibleName = "Квартал расчёта налога" };
            quarter.Items.AddRange(new object[] { "I квартал", "II квартал", "III квартал", "IV квартал" });
            currency = new ComboBox { Name = "taxCurrency", DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                Width = 100, Font = Font, Margin = new Padding(0), AccessibleName = "Валюта расчёта налога" };
            currency.Items.AddRange(Currencies);
            period = new Label { Name = "taxPeriod", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Font = OwnFont(10, FontStyle.Bold), Margin = new Padding(0), AutoEllipsis = false };
            selectorLayout.Controls.Add(year, 0, 1); selectorLayout.Controls.Add(quarter, 1, 1);
            selectorLayout.Controls.Add(currency, 2, 1); selectorLayout.Controls.Add(period, 3, 1);

            var cards = new TableLayoutPanel { Name = "taxSummaryCards", Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                Margin = new Padding(0), BackColor = UiStyle.Background };
            for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3F));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cards.Controls.Add(SummaryCard("Белые поступления", "По датам оплат за квартал", UiStyle.Accent, "taxReceipts", out receiptsValue, true), 0, 0);
            cards.Controls.Add(SummaryCard("Налог 20%", "Поступления × 20%", UiStyle.Blue, "taxAmount", out taxValue, true), 1, 0);
            cards.Controls.Add(SummaryCard("Остаток белых оплат", "Белые поступления − налог", UiStyle.Accent, "taxRemaining", out remainingValue, false), 2, 0);
            layout.Controls.Add(cards, 0, 1);

            var explanation = new Label { Name = "taxExplanation", Dock = DockStyle.Fill, Margin = new Padding(1, 3, 0, 8),
                ForeColor = UiStyle.Muted, TextAlign = ContentAlignment.MiddleLeft, Font = Font,
                Text = "Налог = поступления × 20%. Остаток = поступления − налог.\r\nВ расчёте — белые оплаты и авансы, включая архив. Серые оплаты исключены." };
            layout.Controls.Add(explanation, 0, 2);

            var detail = new UiPanel { Name = "taxPaymentsPanel", Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0) };
            var detailLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
            detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            detail.Controls.Add(detailLayout); layout.Controls.Add(detail, 0, 3);
            detailLayout.Controls.Add(new Label { Name = "taxPaymentsTitle", Text = "Оплаты, вошедшие в расчёт", Dock = DockStyle.Fill,
                Font = OwnFont(12, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) }, 0, 0);
            var gridHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiStyle.Surface };
            detailLayout.Controls.Add(gridHost, 0, 1);
            payments = new DataGridView { Name = "taxPaymentsGrid", Dock = DockStyle.Fill, ReadOnly = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText, AccessibleName = "Входящие оплаты за выбранный квартал" };
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
                Text = "За этот квартал белых поступлений нет.\r\nВыберите другой период или добавьте входящую оплату." };
            gridHost.Controls.Add(emptyState); emptyState.BringToFront();
            paymentCount = new Label { Name = "taxPaymentCount", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiStyle.Muted, Font = Font, Margin = new Padding(0, 5, 0, 0) };
            detailLayout.Controls.Add(paymentCount, 0, 2);

            year.ValueChanged += PeriodChanged; quarter.SelectedIndexChanged += PeriodChanged; currency.SelectedIndexChanged += PeriodChanged;
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
            SetPeriod(previous.Item1, previous.Item2, defaultCurrency);
        }

        public void SetPeriod(int selectedYear, int selectedQuarter, string selectedCurrency)
        {
            if (selectedYear < 1 || selectedYear > 9999) throw new ArgumentOutOfRangeException("selectedYear");
            if (selectedQuarter < 1 || selectedQuarter > 4) throw new ArgumentOutOfRangeException("selectedQuarter");
            if (!Currencies.Contains(selectedCurrency)) throw new ArgumentException("Выберите валюту BYN, RUB, USD или EUR.", "selectedCurrency");
            changingPeriod = true;
            try { year.Value = selectedYear; quarter.SelectedIndex = selectedQuarter - 1; currency.SelectedItem = selectedCurrency; }
            finally { changingPeriod = false; }
            RefreshData();
        }

        public void RefreshData()
        {
            if (changingPeriod || SelectedQuarter < 1 || currency.SelectedIndex < 0) return;
            QuarterlyTaxResult result = QuarterlyTaxes.Calculate(store, SelectedYear, SelectedQuarter, SelectedCurrency);
            CurrentResult = result;
            period.Text = result.FromDate.ToString("d MMMM", Russian) + " — " + result.ToDate.ToString("d MMMM yyyy", Russian) + " г.";
            SetAmount(receiptsValue, result.Receipts, result.Currency); SetAmount(taxValue, result.Tax, result.Currency);
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
        private Control SummaryCard(string caption, string description, Color color, string valueName, out Label value, bool gap)
        {
            var card = new UiPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, gap ? 10 : 0, 0) };
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
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
                if (TextRenderer.MeasureText(label.Text, candidate, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width <= width)
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