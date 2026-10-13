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
        private readonly ComboBox counterparty = new ComboBox(), contract = new ComboBox(), status = new ComboBox();
        private readonly DateTimePicker date = new DateTimePicker(), due = new DateTimePicker();
        private readonly Label number = new Label(), rate = new Label(), requisites = new Label(), totals = new Label();
        private readonly DataGridView works = new DataGridView();
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
            date.Format = DateTimePickerFormat.Custom; date.CustomFormat = "dd.MM.yyyy"; date.Dock = DockStyle.Fill;
            DateTime value; if (source != null && DateTime.TryParse(source.Get("date"), out value)) date.Value = value;
            heading.Controls.Add(FieldLabel("Контрагент"), 0, 0); heading.Controls.Add(counterparty, 1, 0);
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
            options.Controls.Add(FieldLabel("Договор"), 0, 0); options.Controls.Add(contract, 1, 0); options.Controls.Add(FieldLabel("Статус"), 2, 0);
            options.Controls.Add(status, 3, 0); options.Controls.Add(FieldLabel("Срок оплаты"), 4, 0); options.Controls.Add(due, 5, 0);
            var note = new Label { Text = "Договор и срок оплаты необязательны. Новый акт сохраняется как черновик.", AutoSize = true, ForeColor = requisites.ForeColor, Margin = new Padding(0, 8, 0, 0) };
            options.Controls.Add(note, 0, 1); options.SetColumnSpan(note, 6);
            works.Dock = DockStyle.Fill; works.BackgroundColor = Color.White; works.BorderStyle = BorderStyle.FixedSingle;
            works.AllowUserToAddRows = true; works.AllowUserToDeleteRows = true; works.RowHeadersWidth = 34;
            works.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; works.EnableHeadersVisualStyles = false;
            works.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(227, 235, 241); works.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            works.ColumnHeadersHeight = 40; works.RowTemplate.Height = 34; works.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            works.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; works.AccessibleName = "Работы и часы";
            works.Columns.Add(new DataGridViewTextBoxColumn { Name = "description", HeaderText = "Выполненные работы", FillWeight = 60, MinimumWidth = 240 });
            works.Columns.Add(new DataGridViewTextBoxColumn { Name = "hours", HeaderText = "Часы", FillWeight = 12, MinimumWidth = 90 });
            works.Columns.Add(new DataGridViewTextBoxColumn { Name = "rate", HeaderText = "За час", FillWeight = 13, MinimumWidth = 95, ReadOnly = true });
            works.Columns.Add(new DataGridViewTextBoxColumn { Name = "amount", HeaderText = "Сумма", FillWeight = 15, MinimumWidth = 110, ReadOnly = true });
            foreach (string key in new[] { "hours", "rate", "amount" }) works.Columns[key].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            works.Columns["rate"].DefaultCellStyle.Format = works.Columns["amount"].DefaultCellStyle.Format = "N2";
            works.Columns["rate"].DefaultCellStyle.BackColor = works.Columns["amount"].DefaultCellStyle.BackColor = Color.FromArgb(244, 247, 250);
            works.CellValueChanged += delegate { if (!loading) UpdateTotals(); }; works.RowsAdded += delegate { if (!loading) UpdateTotals(); }; works.RowsRemoved += delegate { if (!loading) UpdateTotals(); };
            works.DefaultValuesNeeded += delegate(object sender, DataGridViewRowEventArgs e) { e.Row.Cells["rate"].Value = defaultRate; };
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
        private Label FieldLabel(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, 8, 4) }; }
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
            defaultRate = store.DefaultHourlyRate; loading = true;
            foreach (DataGridViewRow row in works.Rows) if (!row.IsNewRow) row.Cells["rate"].Value = defaultRate;
            loading = false; var settings = store.Of("settings").FirstOrDefault(); documentCurrency = settings == null || settings.Get("currency") == "" ? "BYN" : settings.Get("currency"); FillContracts();
        }
        private void RefreshDetails()
        {
            string actTitle = externalActs ? "Акт вне № " : "Акт № ";
            number.Text = source == null ? actTitle + store.NextActNumber + " · номер при сохранении" : actTitle + source.Get("number");
            rate.Text = defaultRate > 0 ? defaultRate.ToString("N2") + " " + documentCurrency + " / час" : "Стоимость часа не настроена";
            var own = source == null ? store.Of("settings").FirstOrDefault() : Store.GetActSnapshot(source, "organizationSnapshot");
            var cp = source != null && SelectedId(counterparty) == source.Get("partyId") ? Store.GetActSnapshot(source, "partySnapshot") : store.Find(SelectedId(counterparty));
            string ownName = own == null || own.Get("name") == "" ? "Укажите свою организацию в настройках" : own.Get("name");
            requisites.Text = "Исполнитель: " + ownName + (own == null || own.Get("unp") == "" ? "" : ". УНП " + own.Get("unp"))
                + "\r\nЗаказчик: " + (cp == null ? "выберите контрагента" : cp.Get("name") + (cp.Get("unp") == "" ? "" : ". УНП " + cp.Get("unp")))
                + "\r\n" + (defaultRate <= 0 ? "Настройки → Создание актов → Стоимость часа. Затем впишите работы и часы."
                    : "Впишите работы и часы. Стоимость и реквизиты подставляются автоматически.");
            works.Enabled = cp != null;
        }
        private static bool TryHours(object value, out decimal hours)
        {
            string text = Convert.ToString(value).Trim().Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "").Replace(',', '.');
            return Decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out hours) && hours > 0
                && hours <= 1000000000000m && Decimal.Round(hours, 6) == hours;
        }
        private decimal RowRate(DataGridViewRow row)
        { decimal value; return Decimal.TryParse(Convert.ToString(row.Cells["rate"].Value), NumberStyles.Number, CultureInfo.CurrentCulture, out value) ? value : defaultRate; }
        private void UpdateTotals()
        {
            if (loading) return; loading = true;
            try
            {
                decimal hours = 0, amount = 0; bool invalid = false;
                foreach (DataGridViewRow row in works.Rows)
                {
                    if (row.IsNewRow) continue;
                    string description = Convert.ToString(row.Cells["description"].Value).Trim(), entered = Convert.ToString(row.Cells["hours"].Value).Trim();
                    if (row.Cells["rate"].Value == null) row.Cells["rate"].Value = defaultRate;
                    if (description == "" && entered == "") { row.Cells["amount"].Value = null; continue; }
                    decimal quantity, price = RowRate(row);
                    if (description == "" || !TryHours(entered, out quantity) || price <= 0) { row.Cells["amount"].Value = null; invalid = true; continue; }
                    try { decimal sum = Decimal.Round(quantity * price, 2, MidpointRounding.AwayFromZero); row.Cells["amount"].Value = sum; hours += quantity; amount += sum; }
                    catch (OverflowException) { row.Cells["amount"].Value = null; invalid = true; }
                }
                totals.Text = "Всего часов: " + hours.ToString("0.######") + "     Итого: " + amount.ToString("N2") + " " + documentCurrency
                    + (invalid ? "     Проверьте работы, часы и стоимость часа." : "");
            }
            finally { loading = false; }
        }
        public Record BuildDraft()
        {
            if (!works.EndEdit()) throw new InvalidOperationException("Завершите ввод работы и часов.");
            string partyId = SelectedId(counterparty);
            if (partyId == "") throw new InvalidOperationException("Выберите контрагента.");
            if (defaultRate <= 0) throw new InvalidOperationException("Укажите стоимость часа в «Настройки» → «Создание актов».");
            Record result = source == null ? store.CreateHourlyActDraft(partyId, externalActs) : source.Clone();
            result.Set("partyId", partyId); result.Set("date", Store.DateString(date.Value.Date)); result.Set("currency", documentCurrency);
            result.Set("direction", documentDirection); result.Set("contractId", SelectedId(contract));
            Record linked = store.Find(SelectedId(contract));
            if (linked != null) result.Set("projectId", linked.Get("projectId")); else if (source == null || source.Get("contractId") != SelectedId(contract)) result.Set("projectId", "");
            result.Set("status", SelectedId(status)); result.Set("dueDate", due.Checked ? Store.DateString(due.Value.Date) : "");
            if (result.Get("status") == "signed" && result.Get("signedDate") == "") result.Set("signedDate", Store.DateString(DateTime.Today));
            result.Set("hourlyRate", Store.MoneyString(defaultRate));
            if (source != null && source.Get("partyId") != partyId) store.CaptureActSnapshots(result);
            var lines = new List<HourlyActLine>();
            foreach (DataGridViewRow row in works.Rows)
            {
                if (row.IsNewRow) continue;
                string description = Convert.ToString(row.Cells["description"].Value).Trim(), text = Convert.ToString(row.Cells["hours"].Value).Trim();
                if (description == "" && text == "") continue;
                decimal hours;
                if (description == "") throw new InvalidOperationException("Строка " + (row.Index + 1) + ": впишите выполненную работу.");
                if (!TryHours(text, out hours)) throw new InvalidOperationException("Строка " + (row.Index + 1) + ": часы должны быть больше нуля. Можно указать 1,5 часа.");
                lines.Add(new HourlyActLine { Description = description, Hours = hours, Rate = RowRate(row) });
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
