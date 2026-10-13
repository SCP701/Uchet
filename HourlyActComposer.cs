using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
namespace WorkLedger
{
    public sealed class HourlyActComposer : UserControl
    {
        private sealed class Option
        {
            public string Id, Text;
            public Option(string id, string text) { Id = id; Text = text; }
            public override string ToString() { return Text; }
        }
        private readonly Store store;
        private readonly bool externalActs;
        private Record source;
        private readonly ComboBox counterparty = new ReferencePickerCombo(), contract = new ReferencePickerCombo(), status = new ComboBox();
        private readonly DateTimePicker date = new DateTimePicker(), due = new DateTimePicker();
        private readonly Label number = new Label(), rate = new Label(), requisites = new Label(), totals = new Label();
        private readonly DataGridView works = new DataGridView();
        private readonly HashSet<DataGridViewRow> customRateRows = new HashSet<DataGridViewRow>();
        private TextBox activeEditor;
        private DataGridViewCell activeCell;
        private decimal defaultRate;
        private string documentCurrency, documentDirection;
        private bool loading;
        public Record ResultRecord { get; private set; }
        public bool IsExternal { get { return externalActs; } }
        public DataGridView WorkGrid { get { return works; } }
        public event EventHandler ActSaved;
        public HourlyActComposer(Store store) : this(store, null) { }
        public HourlyActComposer(Store store, Record existing) : this(store, existing, false) { }
        public HourlyActComposer(Store store, Record existing, bool externalActs)
        {
            this.store = store; source = existing == null ? null : existing.Clone();
            this.externalActs = source == null ? externalActs : Store.IsExternalAct(source);
            Dock = DockStyle.Fill; BackColor = Color.White; Font = new Font("Segoe UI", 10F); Padding = new Padding(16);
            ((ReferencePickerCombo)counterparty).Configure(store, "party", "Контрагент",
                delegate(object item) { Option option = item as Option; return option == null ? "" : option.Id; }, false);
            ((ReferencePickerCombo)contract).Configure(store, "contract", "Договор",
                delegate(object item) { Option option = item as Option; return option == null ? "" : option.Id; }, true);
            var settings = store.Of("settings").FirstOrDefault();
            documentCurrency = source == null ? (settings == null || settings.Get("currency") == "" ? "BYN" : settings.Get("currency")) : source.Get("currency");
            documentDirection = source == null ? "income" : source.Get("direction");
            defaultRate = source == null ? store.DefaultHourlyRate : (source.Get("hourlyRate") == "" ? Store.GetHourlyActLines(source).First().Rate : Store.ParseMoney(source.Get("hourlyRate")));
            var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 0, 0, 10) };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            counterparty.DropDownStyle = ComboBoxStyle.DropDownList; counterparty.Dock = DockStyle.Fill; counterparty.AccessibleName = "Контрагент для акта";
            counterparty.Items.Add(new Option("", "— Выберите контрагента —"));
            foreach (Record party in store.Of("party").OrderBy(r => r.Get("name"))) counterparty.Items.Add(new Option(party.Id, party.Get("name")));
            if (source != null && !counterparty.Items.Cast<Option>().Any(p => p.Id == source.Get("partyId")))
            { var cp = store.Find(source.Get("partyId")); if (cp != null) counterparty.Items.Add(new Option(cp.Id, cp.Get("name") + " (архив)")); }
            date.Format = DateTimePickerFormat.Custom; date.CustomFormat = "dd.MM.yyyy"; date.Dock = DockStyle.Fill; date.Margin = new Padding(3, 8, 3, 3);
            DateTime value; if (source != null && DateTime.TryParse(source.Get("date"), out value)) date.Value = value;
            heading.Controls.Add(FieldLabel("Контрагент"), 0, 0);
            heading.Controls.Add(new ReferenceSelectionField((ReferencePickerCombo)counterparty, "Контрагент",
                "Выберите контрагента", "composer-counterparty", "party") { Dock = DockStyle.Fill }, 1, 0);
            heading.Controls.Add(FieldLabel("Дата"), 2, 0); heading.Controls.Add(date, 3, 0);
            number.AutoSize = rate.AutoSize = true; number.Margin = rate.Margin = new Padding(0, 10, 0, 6);
            number.Font = new Font(Font, FontStyle.Bold); rate.ForeColor = Color.FromArgb(0, 119, 116);
            heading.Controls.Add(number, 0, 1); heading.SetColumnSpan(number, 2); heading.Controls.Add(rate, 2, 1); heading.SetColumnSpan(rate, 2);
            requisites.Dock = DockStyle.Fill; requisites.Height = 68; requisites.AutoEllipsis = true;
            requisites.ForeColor = Color.FromArgb(93, 107, 124); requisites.Margin = new Padding(0, 4, 0, 4);
            heading.Controls.Add(requisites, 0, 2); heading.SetColumnSpan(requisites, 4);
            var options = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 6, Padding = new Padding(0, 12, 0, 8) };
            foreach (float width in new[] { 85F, 0F, 68F, 138F, 105F, 138F })
                options.ColumnStyles.Add(width == 0 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, width));
            contract.DropDownStyle = status.DropDownStyle = ComboBoxStyle.DropDownList; contract.Dock = status.Dock = due.Dock = DockStyle.Fill;
            contract.AccessibleName = "Договор акта"; status.AccessibleName = "Статус акта";
            foreach (var option in Catalog.Get("act").Fields.Single(f => f.Key == "status").Options) status.Items.Add(new Option(option.Key, option.Value));
            Select(status, source == null ? "draft" : source.Get("status"));
            due.Format = DateTimePickerFormat.Custom; due.CustomFormat = "dd.MM.yyyy"; due.ShowCheckBox = true; due.Checked = false;
            if (source != null && DateTime.TryParse(source.Get("dueDate"), out value)) { due.Value = value; due.Checked = true; }
            options.Controls.Add(FieldLabel("Договор"), 0, 0);
            var contractField = new ReferenceSelectionField((ReferencePickerCombo)contract, "Договор",
                "Без договора", "composer-contract", "contract") { Dock = DockStyle.Fill };
            options.Controls.Add(contractField, 1, 0); options.SetColumnSpan(contractField, 5);
            options.Controls.Add(FieldLabel("Статус"), 0, 1);
            status.Dock = DockStyle.None; status.Width = 138; status.Anchor = AnchorStyles.Left;
            options.Controls.Add(status, 1, 1); options.SetColumnSpan(status, 3);
            options.Controls.Add(FieldLabel("Срок оплаты"), 4, 1); options.Controls.Add(due, 5, 1);
            due.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            var note = new Label { Text = "Договор и срок оплаты необязательны. Новый акт сохраняется как черновик.", AutoSize = true, ForeColor = requisites.ForeColor, Margin = new Padding(0, 8, 0, 0) };
            options.Controls.Add(note, 0, 2); options.SetColumnSpan(note, 6);
            works.Dock = DockStyle.Fill; works.BackgroundColor = Color.White; works.BorderStyle = BorderStyle.FixedSingle;
            works.AllowUserToAddRows = true; works.AllowUserToDeleteRows = true; works.RowHeadersWidth = 34;
            works.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; works.EnableHeadersVisualStyles = false;
            works.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(227, 235, 241); works.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            works.ColumnHeadersHeight = 40; works.RowTemplate.Height = 34; works.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            works.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; works.AccessibleName = "Работы и часы";
            works.Columns.Add(new DataGridViewTextBoxColumn { Name = "description", HeaderText = "Выполненные работы", FillWeight = 60, MinimumWidth = 240 });
            works.Columns.Add(new DataGridViewTextBoxColumn { Name = "hours", HeaderText = "Часы", FillWeight = 12, MinimumWidth = 90 });
            works.Columns.Add(new DataGridViewTextBoxColumn { Name = "rate", HeaderText = "За час", FillWeight = 13, MinimumWidth = 95, ToolTipText = "Стоимость часа можно указать отдельно для каждой работы." });
            works.Columns.Add(new DataGridViewTextBoxColumn { Name = "amount", HeaderText = "Сумма", FillWeight = 15, MinimumWidth = 110, ReadOnly = true });
            foreach (string key in new[] { "hours", "rate", "amount" }) works.Columns[key].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            works.Columns["rate"].DefaultCellStyle.Format = works.Columns["amount"].DefaultCellStyle.Format = "N2";
            works.Columns["amount"].DefaultCellStyle.BackColor = Color.FromArgb(244, 247, 250);
            works.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (loading) return;
                if (e.RowIndex >= 0 && e.ColumnIndex == works.Columns["rate"].Index)
                    customRateRows.Add(works.Rows[e.RowIndex]);
                UpdateTotals();
            };
            works.RowsAdded += delegate(object sender, DataGridViewRowsAddedEventArgs e)
            {
                if (loading) return;
                loading = true;
                try
                {
                    for (int i = e.RowIndex; i < e.RowIndex + e.RowCount; i++)
                    {
                        DataGridViewRow row = works.Rows[i];
                        if (!row.IsNewRow && row.Cells["rate"].Value == null && defaultRate > 0)
                            row.Cells["rate"].Value = defaultRate;
                    }
                }
                finally { loading = false; }
                UpdateTotals();
            };
            works.RowsRemoved += delegate
            {
                customRateRows.RemoveWhere(row => row.DataGridView != works);
                if (!loading) UpdateTotals();
            };
            works.DefaultValuesNeeded += delegate(object sender, DataGridViewRowEventArgs e)
            {
                bool wasLoading = loading; loading = true;
                try { if (defaultRate > 0) e.Row.Cells["rate"].Value = defaultRate; }
                finally { loading = wasLoading; }
            };
            works.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs e)
            {
                DetachEditor();
                activeEditor = e.Control as TextBox; activeCell = works.CurrentCell;
                if (activeEditor != null) activeEditor.TextChanged += ActiveTextChanged;
            };
            works.CellEndEdit += delegate { DetachEditor(); if (!loading) UpdateTotals(); };
            works.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; e.Cancel = true; };
            totals.Dock = DockStyle.Bottom; totals.Height = 46; totals.TextAlign = ContentAlignment.MiddleLeft; totals.Font = new Font(Font, FontStyle.Bold);
            Controls.Add(works); Controls.Add(options); Controls.Add(totals); Controls.Add(heading);
            counterparty.SelectedIndexChanged += delegate { if (!loading) { FillContracts(); RefreshDetails(); } };
            contract.SelectedIndexChanged += delegate { if (!loading) ApplyContract(); };
            loading = true; counterparty.SelectedIndex = 0;
            if (source != null)
            {
                Select(counterparty, source.Get("partyId"));
                foreach (HourlyActLine line in Store.GetHourlyActLines(source)) works.Rows.Add(line.Description, line.Hours, line.Rate, line.Amount);
            }
            loading = false; FillContracts(); RefreshDetails(); UpdateTotals(); UiStyle.ApplyTo(this);
        }
        private Label FieldLabel(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 12, 8, 4) }; }
        private static string SelectedId(ComboBox input) { Option option = input.SelectedItem as Option; return option == null ? "" : option.Id; }
        private static void Select(ComboBox input, string id)
        { for (int i = 0; i < input.Items.Count; i++) if (((Option)input.Items[i]).Id == id) { input.SelectedIndex = i; return; } }
        public void SelectCounterparty(string id) { Select(counterparty, id); }
        private void FillContracts()
        {
            string wanted = source == null ? "" : source.Get("contractId"); loading = true;
            contract.Items.Clear(); contract.Items.Add(new Option("", "— Без договора —"));
            foreach (Record r in store.Records.Where(r => r.Kind == "contract" && r.Get("partyId") == SelectedId(counterparty)
                && ((r.Get("status") == "active" && r.Get("archived") != "true" && (source != null || (r.Get("currency") == documentCurrency && r.Get("direction") == "income"))) || r.Id == wanted)).OrderBy(r => r.Get("number")))
                contract.Items.Add(new Option(r.Id, Catalog.Display(store, r)));
            contract.SelectedIndex = 0; if (wanted != "") Select(contract, wanted); else if (source == null && contract.Items.Count == 2) contract.SelectedIndex = 1;
            loading = false; ApplyContract();
        }
        private void ApplyContract()
        {
            Record selected = store.Find(SelectedId(contract));
            if (selected != null) { documentCurrency = selected.Get("currency"); documentDirection = selected.Get("direction"); }
            else if (source == null)
            {
                var settings = store.Of("settings").FirstOrDefault();
                documentCurrency = settings == null || settings.Get("currency") == "" ? "BYN" : settings.Get("currency"); documentDirection = "income";
            }
            RefreshDetails(); UpdateTotals();
        }
        public void RefreshDefaults()
        {
            if (source != null) return;
            // Commit active text before refreshing inherited prices, including invalid/blank custom input.
            works.EndEdit();
            defaultRate = store.DefaultHourlyRate; loading = true;
            try
            {
                foreach (DataGridViewRow row in works.Rows)
                    if (!row.IsNewRow && !customRateRows.Contains(row))
                        row.Cells["rate"].Value = defaultRate > 0 ? (object)defaultRate : null;
            }
            finally { loading = false; }
            var settings = store.Of("settings").FirstOrDefault(); documentCurrency = settings == null || settings.Get("currency") == "" ? "BYN" : settings.Get("currency"); FillContracts();
        }
        private void RefreshDetails()
        {
            string actTitle = externalActs ? "Акт вне № " : "Акт № ";
            number.Text = source == null ? actTitle + store.NextActNumber + " · номер при сохранении" : actTitle + source.Get("number");
            rate.Text = defaultRate > 0 ? defaultRate.ToString("N2") + " " + documentCurrency + " / час · по умолчанию" : "За час: укажите в строке работы";
            var own = source == null ? store.Of("settings").FirstOrDefault() : Store.GetActSnapshot(source, "organizationSnapshot");
            var cp = source != null && SelectedId(counterparty) == source.Get("partyId") ? Store.GetActSnapshot(source, "partySnapshot") : store.Find(SelectedId(counterparty));
            string ownName = own == null || own.Get("name") == "" ? "Укажите свою организацию в настройках" : own.Get("name");
            requisites.Text = "Исполнитель: " + ownName + (own == null || own.Get("unp") == "" ? "" : ". УНП " + own.Get("unp"))
                + "\r\nЗаказчик: " + (cp == null ? "выберите контрагента" : cp.Get("name") + (cp.Get("unp") == "" ? "" : ". УНП " + cp.Get("unp")))
                + "\r\n" + (defaultRate <= 0 ? "Введите цену в колонке «За час». Ставку по умолчанию можно задать в настройках."
                    : "Впишите работы и часы. Цену в колонке «За час» можно изменить для каждой работы.");
            works.Enabled = cp != null;
        }
        private static bool TryHours(object value, out decimal hours)
        {
            string text = Convert.ToString(value).Trim().Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "").Replace(',', '.');
            return Decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out hours) && hours > 0
                && hours <= 1000000000000m && Decimal.Round(hours, 6) == hours;
        }
        private static bool TryRate(object value, out decimal price)
        {
            string text = Convert.ToString(value).Trim().Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "").Replace(',', '.');
            return Decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out price)
                && price > 0 && price <= 1000000000000m && Decimal.Round(price, 2) == price;
        }
        private void DetachEditor()
        {
            if (activeEditor != null) activeEditor.TextChanged -= ActiveTextChanged;
            activeEditor = null; activeCell = null;
        }
        private void ActiveTextChanged(object sender, EventArgs e)
        {
            if (loading || activeCell == null) return;
            if (activeCell.OwningColumn.Name == "rate") customRateRows.Add(activeCell.OwningRow);
            UpdateTotals();
        }
        private object EnteredValue(DataGridViewRow row, string column)
        {
            DataGridViewCell cell = row.Cells[column];
            return cell == activeCell && activeEditor != null && works.IsCurrentCellInEditMode
                ? (object)activeEditor.Text : cell.Value;
        }
        private void UpdateTotals()
        {
            if (loading) return; loading = true;
            try
            {
                decimal hours = 0, amount = 0; bool invalid = false;
                foreach (DataGridViewRow row in works.Rows)
                {
                    if (row.IsNewRow) continue;
                    string description = Convert.ToString(EnteredValue(row, "description")).Trim(), entered = Convert.ToString(EnteredValue(row, "hours")).Trim();
                    if (description == "" && entered == "") { row.Cells["amount"].Value = null; continue; }
                    decimal quantity, price;
                    if (description == "" || !TryHours(entered, out quantity) || !TryRate(EnteredValue(row, "rate"), out price))
                    { row.Cells["amount"].Value = null; invalid = true; continue; }
                    try
                    {
                        decimal sum = Decimal.Round(quantity * price, 2, MidpointRounding.AwayFromZero);
                        if (sum <= 0 || sum > 1000000000000m) { row.Cells["amount"].Value = null; invalid = true; continue; }
                        row.Cells["amount"].Value = sum; hours += quantity; amount += sum;
                        if (hours > 1000000000000m || amount > 1000000000000m) invalid = true;
                    }
                    catch (OverflowException) { row.Cells["amount"].Value = null; invalid = true; }
                }
                totals.Text = "Всего часов: " + hours.ToString("0.######") + "     Итого: " + amount.ToString("N2") + " " + documentCurrency
                    + (invalid ? "     Проверьте работы, часы и стоимость часа." : "");
            }
            finally { loading = false; }
        }
        public Record BuildDraft()
        {
            if (!works.EndEdit()) throw new InvalidOperationException("Завершите ввод работы, часов и цены за час.");
            string partyId = SelectedId(counterparty);
            if (partyId == "") throw new InvalidOperationException("Выберите контрагента.");
            Record result = source == null ? store.CreateHourlyActDraft(partyId, externalActs) : source.Clone();
            result.Set("partyId", partyId); result.Set("date", Store.DateString(date.Value.Date)); result.Set("currency", documentCurrency);
            result.Set("direction", documentDirection); result.Set("contractId", SelectedId(contract));
            Record linked = store.Find(SelectedId(contract));
            if (linked != null) result.Set("projectId", linked.Get("projectId")); else if (source == null || source.Get("contractId") != SelectedId(contract)) result.Set("projectId", "");
            result.Set("status", SelectedId(status)); result.Set("dueDate", due.Checked ? Store.DateString(due.Value.Date) : "");
            if (result.Get("status") == "signed" && result.Get("signedDate") == "") result.Set("signedDate", Store.DateString(DateTime.Today));
            if (source != null && source.Get("partyId") != partyId) store.CaptureActSnapshots(result);
            var lines = new List<HourlyActLine>();
            foreach (DataGridViewRow row in works.Rows)
            {
                if (row.IsNewRow) continue;
                string description = Convert.ToString(row.Cells["description"].Value).Trim(), text = Convert.ToString(row.Cells["hours"].Value).Trim();
                if (description == "" && text == "") continue;
                decimal hours, price;
                if (description == "") throw new InvalidOperationException("Строка " + (row.Index + 1) + ": впишите выполненную работу.");
                if (!TryHours(text, out hours)) throw new InvalidOperationException("Строка " + (row.Index + 1) + ": часы должны быть больше нуля. Можно указать 1,5 часа.");
                if (!TryRate(row.Cells["rate"].Value, out price)) throw new InvalidOperationException("Строка " + (row.Index + 1) + ": укажите цену за час больше нуля, с точностью до копеек. Можно указать 35,50.");
                lines.Add(new HourlyActLine { Description = description, Hours = hours, Rate = price });
            }
            Store.SetHourlyActLines(result, lines); return result;
        }
        public Record SaveDraft()
        {
            Record result = BuildDraft(); store.Upsert(result); ResultRecord = store.Find(result.Id); source = ResultRecord.Clone(); RefreshDetails();
            if (ActSaved != null) ActSaved(this, EventArgs.Empty);
            return ResultRecord;
        }
    }
    public sealed class HourlyActEditor : Form
    {
        public HourlyActComposer Composer { get; private set; }
        public Record ResultRecord { get { return Composer.ResultRecord; } }
        public HourlyActEditor(Store store, Record existing)
        {
            Text = (Store.IsExternalAct(existing) ? "Изменить акт вне № " : "Изменить акт № ") + existing.Get("number"); Font = new Font("Segoe UI", 10F);
            Width = 1060; Height = 720; MinimumSize = new Size(940, 560); StartPosition = FormStartPosition.CenterParent;
            Composer = new HourlyActComposer(store, existing);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(16, 10, 16, 10), FlowDirection = FlowDirection.RightToLeft };
            var cancel = new UiButton { Text = "Отмена", Width = 110, Height = 34, DialogResult = DialogResult.Cancel };
            var save = new UiButton { Primary = true, Text = "Сохранить", Width = 130, Height = 34 };
            save.Click += delegate
            {
                try { save.Enabled = false; Composer.SaveDraft(); DialogResult = DialogResult.OK; Close(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось сохранить", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                finally { if (!save.IsDisposed) save.Enabled = true; }
            };
            footer.Controls.Add(cancel); footer.Controls.Add(save); Controls.Add(Composer); Controls.Add(footer); UiStyle.ApplyTo(this); UiStyle.SetAnimations(this, UiPreferences.Load(store.DataDir).AnimationsEnabled); CancelButton = cancel;
        }
    }
}
