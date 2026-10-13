using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WorkLedger
{
    public sealed class ReferencePickerDialog : Form
    {
        private sealed class Row
        {
            public Record Record;
            public string Title, Party, Project, Search;
        }
        private sealed class FilterChoice
        {
            public string Id, Text;
            public FilterChoice(string id, string text) { Id = id; Text = text; }
            public override string ToString() { return Text; }
        }
        private readonly string kind;
        private readonly List<Row> rows = new List<Row>();
        private readonly Dictionary<string, Record> byId;
        private readonly ComboBox roleFilter, statusFilter, currencyFilter;
        private readonly CheckBox archiveFilter;
        private readonly Label count;
        private readonly TextBox details;
        private readonly UiButton select;
        private readonly Timer searchDelay;
        private bool loading = true;
        public string SelectedId { get; private set; }
        public TextBox SearchBox { get; private set; }
        public DataGridView ResultGrid { get; private set; }

        internal ReferencePickerDialog(Store store, string kind, string title, IList<ReferencePickerEntry> allowed,
            string currentId, bool allowClear, string clearLabel, string initialQuery)
        {
            this.kind = kind;
            Text = "Выбрать: " + title;
            Font = new Font("Segoe UI", 10F); BackColor = UiStyle.Background; ForeColor = UiStyle.Ink;
            ShowInTaskbar = false; ShowIcon = false; MinimizeBox = false; MaximizeBox = true;
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1040, Math.Max(600, area.Width - 48)), Math.Min(720, Math.Max(610, area.Height - 48)));
            MinimumSize = new Size(Math.Min(760, area.Width), Math.Min(610, area.Height));
            byId = store.Records.GroupBy(r => r.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (ReferencePickerEntry choice in allowed)
            {
                Record record;
                if (!byId.TryGetValue(choice.Id, out record) || record.Kind != kind) continue;
                string party = Linked(record, "partyId"), project = Linked(record, "projectId");
                rows.Add(new Row { Record = record, Title = choice.Text, Party = party, Project = project,
                    Search = Normalize(choice.Text + " " + String.Join(" ", record.Fields.Values) + " " + party + " " + project
                        + " " + LinkedFields(record, "partyId") + " " + LinkedFields(record, "projectId")
                        + " " + Digits(record.Get("phone")) + " " + Digits(record.Get("chiefAccountantPhone"))) });
            }

            var header = new Panel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(22, 12, 22, 0), BackColor = UiStyle.Surface };
            header.Controls.Add(new Label { Text = "Поиск по названию, УНП, номеру, телефону и связанным данным",
                Dock = DockStyle.Bottom, Height = 30, ForeColor = UiStyle.Muted });
            header.Controls.Add(new Label { Text = Text, Dock = DockStyle.Top, Height = 38, Font = new Font("Segoe UI", 17F, FontStyle.Bold) });
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 8) };
            var searchPanel = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 2, Margin = Padding.Empty };
            searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 67)); searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchPanel.Controls.Add(new Label { Text = "Поиск", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            SearchBox = new TextBox { Name = "picker-search", AccessibleName = "Поиск", Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 4) };
            searchPanel.Controls.Add(SearchBox, 1, 0);
            var filters = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 4 };
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            roleFilter = FilterCombo("picker-role-filter", "Роль");
            statusFilter = FilterCombo("picker-status-filter", "Статус");
            currencyFilter = FilterCombo("picker-currency-filter", "Валюта");
            archiveFilter = new CheckBox { Name = "picker-archive-filter", AccessibleName = "Показывать архив", Text = "С архивом", AutoSize = true,
                Dock = DockStyle.Fill, Margin = new Padding(10, 4, 0, 2), Enabled = rows.Any(r => r.Record.Get("archived") == "true") };
            BuildFilter(roleFilter, "Все роли", "role", new[] { "client", "vendor", "both" });
            BuildFilter(statusFilter, "Все статусы", "status", rows.Select(r => r.Record.Get("status")).Distinct().ToArray());
            BuildFilter(currencyFilter, "Все валюты", "currency", rows.Select(r => r.Record.Get("currency")).Distinct().ToArray());
            roleFilter.Visible = kind == "party"; statusFilter.Visible = kind != "party" && rows.Any(r => r.Record.Get("status") != "");
            currencyFilter.Visible = rows.Any(r => r.Record.Get("currency") != "");
            filters.Controls.Add(roleFilter, 0, 0); filters.Controls.Add(statusFilter, 1, 0);
            filters.Controls.Add(currencyFilter, 2, 0); filters.Controls.Add(archiveFilter, 3, 0);
            // Reclaim hidden filter columns rather than leave a large blank hole.
            if (!roleFilter.Visible && statusFilter.Visible)
            { filters.Controls.Remove(roleFilter); filters.Controls.Remove(statusFilter); filters.Controls.Add(statusFilter, 0, 0); filters.SetColumnSpan(statusFilter, 2); }
            else if (roleFilter.Visible && !statusFilter.Visible) filters.SetColumnSpan(roleFilter, 2);
            archiveFilter.Checked = rows.Any(r => r.Record.Id == currentId && r.Record.Get("archived") == "true");
            count = new Label { Name = "picker-count", AccessibleName = "Результаты поиска", Dock = DockStyle.Top, Height = 28, ForeColor = UiStyle.Muted };
            ResultGrid = new DataGridView { Name = "picker-results", AccessibleName = "Доступные записи", Dock = DockStyle.Fill,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoGenerateColumns = false };
            BuildColumns();
            details = new TextBox { Name = "picker-details", AccessibleName = "Данные выбранной записи", Dock = DockStyle.Bottom, Height = 124,
                ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = UiStyle.Surface, TabStop = true };
            var detailsCaption = new Label { Text = "Данные выбранной записи", Dock = DockStyle.Bottom, Height = 26, ForeColor = UiStyle.Muted };
            body.Controls.Add(ResultGrid); body.Controls.Add(detailsCaption); body.Controls.Add(details);
            body.Controls.Add(count); body.Controls.Add(filters); body.Controls.Add(searchPanel);
            var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(20, 10, 20, 12), ColumnCount = 4, BackColor = UiStyle.Surface };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            var clear = new UiButton { Name = "picker-clear", AccessibleName = clearLabel, Text = String.IsNullOrWhiteSpace(clearLabel) ? "Не выбрано" : clearLabel,
                AutoSize = true, Dock = DockStyle.Fill, Visible = allowClear, Margin = new Padding(0, 0, 10, 0) };
            select = new UiButton { Name = "picker-select", AccessibleName = "Выбрать", Text = "Выбрать", Primary = true, Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 10, 0), Enabled = false };
            var cancel = new UiButton { Name = "picker-cancel", AccessibleName = "Отмена", Text = "Отмена", Dock = DockStyle.Fill, DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
            footer.Controls.Add(clear, 0, 0); footer.Controls.Add(select, 2, 0); footer.Controls.Add(cancel, 3, 0);
            Controls.Add(body); Controls.Add(footer); Controls.Add(header);
            AcceptButton = select; CancelButton = cancel;
            select.Click += delegate { ChooseSelected(); };
            clear.Click += delegate { if (allowClear) { SelectedId = ""; DialogResult = DialogResult.OK; Close(); } };
            ResultGrid.SelectionChanged += delegate { UpdateDetails(); };
            ResultGrid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) ChooseSelected(); };
            ResultGrid.KeyDown += delegate(object sender, KeyEventArgs e)
            { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; ChooseSelected(); } };
            searchDelay = new Timer { Interval = 140 };
            searchDelay.Tick += delegate { searchDelay.Stop(); RefreshResults(); };
            SearchBox.TextChanged += delegate { if (!loading) { searchDelay.Stop(); searchDelay.Start(); } };
            foreach (ComboBox filter in new[] { roleFilter, statusFilter, currencyFilter })
                filter.SelectedIndexChanged += delegate { if (!loading) RefreshResults(); };
            archiveFilter.CheckedChanged += delegate { if (!loading) RefreshResults(); };
            Resize += delegate { details.Height = Math.Max(64, Math.Min(124, Height - 530)); };
            details.Height = Math.Max(64, Math.Min(124, Height - 530));
            UiStyle.ApplyTo(this);
            loading = false; SearchBox.Text = initialQuery; searchDelay.Stop(); RefreshResults();
            if (initialQuery == "") SelectRow(currentId);
            Shown += delegate { SearchBox.Focus(); SearchBox.SelectionStart = SearchBox.TextLength; };
        }

        private static ComboBox FilterCombo(string name, string label)
        {
            return new ComboBox { Name = name, AccessibleName = label, Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 7, 10, 6), IntegralHeight = false, DropDownHeight = 180 };
        }
        private void BuildFilter(ComboBox combo, string title, string key, IEnumerable<string> values)
        {
            combo.Items.Add(new FilterChoice("", title));
            foreach (string value in values.Where(v => v != "").Distinct())
                combo.Items.Add(new FilterChoice(value, FieldLabel(key, value)));
            combo.SelectedIndex = 0;
        }
        private string Linked(Record record, string key)
        {
            Record linked;
            return byId.TryGetValue(record.Get(key), out linked) ? linked.Get("name") != "" ? linked.Get("name") : linked.Get("number") : "";
        }
        private string LinkedFields(Record record, string key)
        {
            Record linked;
            return byId.TryGetValue(record.Get(key), out linked) ? String.Join(" ", linked.Fields.Values) : "";
        }
        private string FieldLabel(string key, string value)
        {
            Schema schema = Catalog.Get(kind);
            FieldDef field = schema == null ? null : schema.Fields.FirstOrDefault(f => f.Key == key);
            return field == null ? value : Catalog.ValueLabel(field, value);
        }
        private void AddColumn(string key, string title, float fill, int minimum)
        {
            ResultGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = key, HeaderText = title, FillWeight = fill, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable });
        }
        private void BuildColumns()
        {
            if (kind == "party")
            { AddColumn("name", "Контрагент", 44, 190); AddColumn("unp", "УНП", 17, 104); AddColumn("role", "Роль", 22, 154); AddColumn("phone", "Телефон", 22, 130); }
            else if (kind == "project")
            { AddColumn("name", "Объект", 38, 170); AddColumn("party", "Контрагент", 34, 170); AddColumn("location", "Адрес", 28, 130); AddColumn("status", "Статус", 19, 125); }
            else
            { AddColumn("number", "Номер", 18, 90); AddColumn("date", "Дата", 17, 102); AddColumn("party", "Контрагент", 38, 180);
              AddColumn("amount", "Сумма / валюта", 24, 145); AddColumn("status", "Статус", 22, 130); }
        }
        private static string Digits(string text) { return new string((text ?? "").Where(Char.IsDigit).ToArray()); }
        private static string Normalize(string text)
        {
            return (text ?? "").ToLowerInvariant().Replace('\u0451', '\u0435').Replace('\u00a0', ' ');
        }
        private static string SelectedFilter(ComboBox combo)
        { var item = combo.SelectedItem as FilterChoice; return item == null ? "" : item.Id; }
        internal string[] ResultIds { get { return ResultGrid.Rows.Cast<DataGridViewRow>().Select(r => ((Row)r.Tag).Record.Id).ToArray(); } }
        internal void ApplyQueryNow() { searchDelay.Stop(); RefreshResults(); }
        internal void SetFilter(string key, string value)
        {
            ComboBox combo = key == "role" ? roleFilter : key == "currency" ? currencyFilter : statusFilter;
            for (int i = 0; i < combo.Items.Count; ++i) if (((FilterChoice)combo.Items[i]).Id == value) { combo.SelectedIndex = i; return; }
            throw new ArgumentException("Нет такого фильтра.");
        }
        internal void IncludeArchive(bool value) { archiveFilter.Checked = value; RefreshResults(); }

        private void RefreshResults()
        {
            string previous = SelectedRowId();
            string[] terms = Normalize(SearchBox.Text).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            string role = SelectedFilter(roleFilter), status = SelectedFilter(statusFilter), currency = SelectedFilter(currencyFilter);
            ResultGrid.SuspendLayout();
            try
            {
                ResultGrid.Rows.Clear();
                foreach (Row row in rows)
                {
                    Record record = row.Record;
                    if (!archiveFilter.Checked && record.Get("archived") == "true") continue;
                    if (role != "" && record.Get("role") != role && !(record.Get("role") == "both" && (role == "client" || role == "vendor"))) continue;
                    if (status != "" && record.Get("status") != status) continue;
                    if (currency != "" && record.Get("currency") != currency) continue;
                    if (!terms.All(term => row.Search.Contains(term))) continue;
                    object[] cells;
                    if (kind == "party") cells = new object[] { record.Get("name"), record.Get("unp"), FieldLabel("role", record.Get("role")), record.Get("phone") };
                    else if (kind == "project") cells = new object[] { record.Get("name"), row.Party, record.Get("location"), FieldLabel("status", record.Get("status")) };
                    else
                    {
                        string date = record.Get("date"); DateTime parsed;
                        if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) date = parsed.ToString("dd.MM.yyyy");
                        cells = new object[] { record.Get("number"), date, row.Party, record.Get("amount") + (record.Get("currency") == "" ? "" : " " + record.Get("currency")), FieldLabel("status", record.Get("status")) };
                    }
                    int index = ResultGrid.Rows.Add(cells); ResultGrid.Rows[index].Tag = row;
                    if (record.Get("archived") == "true") ResultGrid.Rows[index].DefaultCellStyle.ForeColor = UiStyle.Muted;
                }
                ResultGrid.ClearSelection(); ResultGrid.CurrentCell = null;
                if (!SelectRow(previous) && ResultGrid.Rows.Count > 0)
                { ResultGrid.CurrentCell = ResultGrid.Rows[0].Cells[0]; ResultGrid.Rows[0].Selected = true; }
                count.Text = ResultGrid.Rows.Count == 0 ? "Ничего не найдено. Измените поиск или фильтры." : "Найдено: " + ResultGrid.Rows.Count;
                UpdateDetails();
            }
            finally { ResultGrid.ResumeLayout(); }
        }
        private string SelectedRowId()
        { Row row = ResultGrid.SelectedRows.Count == 0 ? null : ResultGrid.SelectedRows[0].Tag as Row; return row == null ? "" : row.Record.Id; }
        private bool SelectRow(string id)
        {
            if (id == "") return false;
            foreach (DataGridViewRow candidate in ResultGrid.Rows)
            {
                Row row = candidate.Tag as Row;
                if (row == null || row.Record.Id != id) continue;
                ResultGrid.ClearSelection(); ResultGrid.CurrentCell = candidate.Cells[0]; candidate.Selected = true; return true;
            }
            return false;
        }
        private void UpdateDetails()
        {
            Row row = ResultGrid.SelectedRows.Count == 0 ? null : ResultGrid.SelectedRows[0].Tag as Row;
            select.Enabled = row != null;
            if (row == null) { details.Text = "Выберите строку, чтобы увидеть реквизиты и связанные данные."; return; }
            Record record = row.Record; var text = new StringBuilder();
            if (record.Get("archived") == "true") text.AppendLine("АРХИВ");
            text.AppendLine(row.Title);
            Schema schema = Catalog.Get(kind);
            if (schema != null)
            {
                foreach (FieldDef field in schema.Fields)
                {
                    if (field.Key == "name" || field.Key == "monthlyServiceEnabled" || field.Key.StartsWith("monthlyService", StringComparison.Ordinal)
                        || field.Key == "notes" || field.Key == "description") continue;
                    string value = record.Get(field.Key);
                    if (String.IsNullOrWhiteSpace(value)) continue;
                    if (field.Type == "ref") value = Linked(record, field.Key);
                    else value = Catalog.ValueLabel(field, value);
                    if (value != "") text.AppendLine(field.Label + ": " + value);
                }
            }
            details.Text = text.ToString(); details.SelectionStart = 0; details.SelectionLength = 0;
        }
        private void ChooseSelected()
        {
            if (searchDelay.Enabled) { searchDelay.Stop(); RefreshResults(); }
            Row row = ResultGrid.SelectedRows.Count == 0 ? null : ResultGrid.SelectedRows[0].Tag as Row;
            if (row == null || !select.Enabled) return;
            SelectedId = row.Record.Id; DialogResult = DialogResult.OK; Close();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && searchDelay != null) searchDelay.Dispose();
            base.Dispose(disposing);
        }
    }
}