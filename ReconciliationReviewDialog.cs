using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    public sealed class ReconciliationReviewDialog : Form
    {
        private readonly Store store;
        private readonly Action<Record> openRecord;
        private readonly DataGridView grid = new DataGridView();
        private readonly ComboBox filter = new ComboBox();
        private readonly TextBox detail = new TextBox();
        private readonly Label summary = new Label();

        public ReconciliationReviewDialog(Store store, Action<Record> openRecord)
        {
            this.store = store; this.openRecord = openRecord;
            Text = "Акты и банковская выписка"; StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1120, 720); MinimumSize = new Size(960, 600);
            Font = new Font("Segoe UI", 10F); BackColor = UiStyle.Background; ForeColor = UiStyle.Ink;
            ShowIcon = false; ShowInTaskbar = false;
            var header = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(18, 12, 18, 0) };
            header.Controls.Add(new Label { Text = "Сверка актов и оплат", Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI", 18F, FontStyle.Bold) });
            summary.Dock = DockStyle.Bottom; summary.Height = 29; summary.AutoEllipsis = true; header.Controls.Add(summary);
            var commands = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(18, 4, 18, 8), WrapContents = true };
            commands.Controls.Add(MakeButton("Загрузить файл сверки", ImportFile, 200));
            filter.Width = 220; filter.DropDownStyle = ComboBoxStyle.DropDownList; filter.Margin = new Padding(7, 6, 10, 0);
            filter.Items.AddRange(new object[] { "Все записи", "Не проверено", "Найдена связь", "Проверено" }); filter.SelectedIndex = 1;
            filter.SelectedIndexChanged += delegate { Reload(); }; commands.Controls.Add(filter);
            commands.Controls.Add(MakeButton("Открыть запись", OpenSelected, 160));
            commands.Controls.Add(MakeButton("Отметить проверено", MarkChecked, 190));
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 182, Padding = new Padding(18, 8, 18, 12) };
            detail.Dock = DockStyle.Fill; detail.Multiline = true; detail.ReadOnly = true; detail.ScrollBars = ScrollBars.Vertical;
            detail.BackColor = Color.White; detail.BorderStyle = BorderStyle.FixedSingle;
            footer.Controls.Add(detail);
            footer.Controls.Add(new Label { Text = "Причина проверки и сведения из исходных документов", Dock = DockStyle.Top, Height = 28 });
            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false; grid.RowHeadersVisible = false;
            grid.SizeChanged += delegate { LedgerGridLayout.Apply(grid, "reconciliation", 0); };
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            foreach (string name in new[] { "Тип", "Дата", "Номер", "Контрагент", "Сумма", "Валюта", "Проверка" }) grid.Columns.Add(name, name);
            grid.SelectionChanged += delegate { ShowDetails(); }; grid.CellDoubleClick += delegate { Run(OpenSelected); };
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 0) }; content.Controls.Add(grid);
            Controls.Add(content); Controls.Add(footer); Controls.Add(commands); Controls.Add(header);
            UiStyle.ApplyTo(this); Reload();
        }
        private Button MakeButton(string text, Action action, int width)
        {
            var button = new UiButton { Text = text, Width = width, Height = 38, Margin = new Padding(0, 2, 8, 2) };
            button.Click += delegate { Run(action); }; return button;
        }
        private void Run(Action action)
        {
            try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, "Сверка", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        public static string ReviewLabel(string value)
        {
            return value == "checked" ? "Проверено" : value == "pending" ? "Требует проверки"
                : value == "matched" ? "Не проверено · связь найдена" : "Не проверено";
        }
        public static bool NeedsReview(Record record)
        {
            return record != null && record.Get("reviewStatus") != "checked";
        }
        public static bool IsReviewRecord(Record record)
        {
            return record != null && (record.Kind == "act" || record.Kind == "payment"
                || record.Kind == "document" && (record.Get("reviewStatus") != "" || record.Get("reviewPackageId") != "" || record.Get("reviewNotes") != ""));
        }
        private Record Selected()
        {
            return grid.SelectedRows.Count == 0 ? null : store.Find(Convert.ToString(grid.SelectedRows[0].Tag));
        }
        public void Reload()
        {
            string selected = grid.SelectedRows.Count == 0 ? "" : Convert.ToString(grid.SelectedRows[0].Tag);
            var all = store.Records.Where(IsReviewRecord).ToList();
            string state = filter.SelectedIndex == 1 ? "pending" : filter.SelectedIndex == 2 ? "matched" : filter.SelectedIndex == 3 ? "checked" : "";
            grid.Rows.Clear();
            foreach (Record record in all.Where(r => filter.SelectedIndex == 1 ? NeedsReview(r) : state == "" || r.Get("reviewStatus") == state).OrderByDescending(r => r.Get("date")).ThenBy(r => r.Get("number")))
            {
                Record party = store.Find(record.Get("partyId")); DateTime date;
                string day = DateTime.TryParseExact(record.Get("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ? date.ToString("dd.MM.yyyy") : record.Get("date");
                string amount = record.Get("amount") == "" ? "" : Store.ParseMoney(record.Get("amount")).ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
                int row = grid.Rows.Add(record.Kind == "act" ? "Акт" : record.Kind == "payment" ? "Оплата" : "Документ", day,
                    record.Get("number") == "" ? record.Get("title") : record.Get("number"), party == null ? "" : party.Get("name"), amount, record.Get("currency"), ReviewLabel(record.Get("reviewStatus")));
                grid.Rows[row].Tag = record.Id;
                if (NeedsReview(record)) grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 224);
                if (record.Id == selected) grid.Rows[row].Selected = true;
            }
            summary.Text = all.Count == 0 ? "Загрузите файл сверки. Записи появятся здесь вместе с причинами проверки."
                : "Записей: " + all.Count + " · Не проверено: " + all.Count(NeedsReview) + " · Зачёт оплат выполняется отдельно.";
            LedgerGridLayout.Apply(grid, "reconciliation", 0);
            ShowDetails();
        }
        private void ShowDetails()
        {
            Record record = Selected();
            if (record == null) { detail.Text = ""; return; }
            var lines = new List<string>();
            if (record.Get("reviewNotes") != "") lines.Add(record.Get("reviewNotes"));
            if (record.Get("candidateActIds") != "")
            {
                lines.Add("Возможные акты:");
                foreach (string id in record.Get("candidateActIds").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Record act = store.Find(id); if (act == null) continue;
                    lines.Add("• " + Catalog.Display(store, act) + " · " + act.Get("date") + " · " + act.Get("amount") + " " + act.Get("currency"));
                }
            }
            if (record.Kind == "payment" && record.Get("bankPurpose") != "") lines.Add("Назначение платежа: " + record.Get("bankPurpose"));
            detail.Text = String.Join("\r\n\r\n", lines).Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        private void OpenSelected()
        {
            Record record = Selected(); if (record == null) return;
            if (openRecord != null) openRecord(record); Reload();
        }
        private void MarkChecked()
        {
            Record record = Selected(); if (record == null || record.Get("reviewStatus") == "checked") return;
            record.Set("reviewStatus", "checked"); store.Upsert(record); Reload();
        }
        private void ImportFile()
        {
            using (var file = new OpenFileDialog { Filter = "Файл сверки актов и выписки (*.json)|*.json", Title = "Выберите подготовленный файл сверки" })
            {
                if (file.ShowDialog(this) != DialogResult.OK) return;
                var package = ReconciliationImport.Read(file.FileName);
                var plan = ReconciliationImport.PlanReadOnly(store, package);
                if (plan.Records.Count == 0) { MessageBox.Show(this, "Этот файл уже загружен. Повторные записи не созданы.", "Сверка"); Reload(); return; }
                using (var preview = new ReconciliationPreviewDialog(store, plan))
                {
                    if (preview.ShowDialog(this) != DialogResult.OK) return;
                }
                string backupFolder = Path.Combine(store.DataDir, "backups"); Directory.CreateDirectory(backupFolder);
                string backup = Path.Combine(backupFolder, "before-reconciliation-" + DateTime.UtcNow.AddHours(3).ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
                BackupArchive.Create(store.DataDir, backup);
                ReconciliationImport.Apply(store, plan); Reload();
                MessageBox.Show(this, "Сверка загружена. Неоднозначные записи отмечены «Требует проверки».\r\nРезервная копия: " + backup, "Сверка", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
    internal sealed class ReconciliationPreviewDialog : Form
    {
        public ReconciliationPreviewDialog(Store store, ReconciliationPlan plan)
        {
            Text = "Загрузка сверки"; Size = new Size(1050, 650); MinimumSize = new Size(880, 520); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10F); BackColor = UiStyle.Background; ShowIcon = false; ShowInTaskbar = false;
            var top = new Label { Dock = DockStyle.Top, Height = 84, Padding = new Padding(18, 12, 18, 0),
                Text = plan.Title + "\r\nНовых записей: " + plan.NewCount + " · Дополнений: " + plan.ExistingCount + " · Пользовательских изменений: " + plan.ConflictCount + "\r\nСуществующие суммы и статусы сохраняются. Оплаты не зачитываются автоматически." };
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            foreach (string label in new[] { "Изменение", "Раздел", "Документ", "Контрагент", "Сумма", "Валюта", "Проверка" }) grid.Columns.Add(label, label);
            foreach (Record record in plan.Records)
            {
                Record party = store.Find(record.Get("partyId")) ?? plan.Records.FirstOrDefault(r => r.Id == record.Get("partyId"));
                grid.Rows.Add(store.Find(record.Id) == null ? "Добавить" : "Дополнить", Store.KindLabel(record.Kind), Catalog.Display(store, record),
                    party == null ? "" : party.Get("name"), record.Get("amount"), record.Get("currency"), ReconciliationReviewDialog.ReviewLabel(record.Get("reviewStatus")));
            }
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(18, 12, 18, 12), FlowDirection = FlowDirection.RightToLeft };
            var cancel = new UiButton { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 120, Height = 36 };
            var load = new UiButton { Text = "Загрузить", Primary = true, DialogResult = DialogResult.OK, Width = 150, Height = 36 };
            bottom.Controls.Add(cancel); bottom.Controls.Add(load); Controls.Add(grid); Controls.Add(bottom); Controls.Add(top); AcceptButton = load; CancelButton = cancel;
            UiStyle.ApplyTo(this);
        }
    }
}