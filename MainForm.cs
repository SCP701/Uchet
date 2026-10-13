using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WorkLedger
{
    public sealed class TableData
    {
        public string Title;
        public List<string> Headers = new List<string>();
        public List<List<string>> Rows = new List<List<string>>();
        public List<string> Ids = new List<string>();
        public void Add(string id, params string[] values) { Ids.Add(id); Rows.Add(values.ToList()); }
    }
    public sealed class MainForm : Form
    {
        private Store store;
        private readonly bool automationEnabled;
        private readonly System.Windows.Forms.Timer monthlyTimer = new System.Windows.Forms.Timer();
        private readonly Label monthlyNotice = new Label();
        private string lastAutomationError;
        private Panel body = new Panel();
        private FlowLayoutPanel nav = new FlowLayoutPanel(), actions = new FlowLayoutPanel();
        private Label title = new Label(), hint = new Label(), count = new Label();
        private DataGridView grid = new DataGridView();
        private TextBox search = new TextBox();
        private ComboBox project = new ComboBox(), status = new ComboBox(), period = new ComboBox();
        private System.Windows.Forms.Button resetFiltersButton;
        private readonly ToolTip filterTips = new ToolTip();
        private ComboBox currency = new ComboBox(), party = new ComboBox(), direction = new ComboBox(), report = new ComboBox();
        private DateTimePicker from = new DateTimePicker(), to = new DateTimePicker();
        private CheckBox archived = new CheckBox();
        private Panel overview = new Panel();
        private FlowLayoutPanel filters;
        private HourlyActComposer composer;
        private readonly Dictionary<string, HourlyActComposer> actComposers = new Dictionary<string, HourlyActComposer>();
        private const string CreateExternalActSection = "create-external-act";
        private QuarterlyTaxesPage taxesPage;
        private PaymentAccounting viewPaymentAccounting;
        private System.Windows.Forms.Button updatesButton;
        private UiPreferences interfacePreferences;
        private readonly ViewTransition viewTransition = new ViewTransition();
        private readonly Icon applicationIcon;
        private string current = "overview";
        private bool refreshing;
        private TableData table = new TableData();
        private static readonly Color Ink = UiStyle.Ink, Accent = UiStyle.Accent, Background = UiStyle.Background;
        public MainForm(Store data) : this(data, false) { }
        public MainForm(Store data, bool runAutomation)
        {
            store = data; automationEnabled = runAutomation;
            if (!File.Exists(store.DataPath)) { var settings = new Record("settings"); settings.Set("currency", "BYN"); store.Upsert(settings); }
            interfacePreferences = UiPreferences.Load(store.DataDir);
            applicationIcon = LoadApplicationIcon(); Icon = applicationIcon; ShowIcon = true;
            Text = "Акты и оплаты — учёт работ"; Width = 1460; Height = 900; MinimumSize = new Size(1120, 680);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Background; ForeColor = Ink;
            var sidebar = new Panel { Dock = DockStyle.Left, Width = 232, BackColor = UiStyle.Sidebar, Padding = new Padding(12, 0, 8, 8) };
            var brand = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = UiStyle.Sidebar };
            brand.Controls.Add(new Label { Text = "Акты и оплаты", Left = 8, Top = 19, Width = 208, Height = 29, ForeColor = Color.White, Font = new Font("Segoe UI", 17, FontStyle.Bold) });
            brand.Controls.Add(new Label { Text = "Работы, документы и расчёты", Left = 9, Top = 52, Width = 208, Height = 24, ForeColor = Color.FromArgb(150, 169, 191), Font = new Font("Segoe UI", 8.5F) });
            var local = new Label { Text = Environment.UserName + "\r\nПерсональный интерфейс", Dock = DockStyle.Bottom, Height = 46, ForeColor = Color.FromArgb(150, 169, 191), Font = new Font("Segoe UI", 8.5F), Padding = new Padding(8, 7, 0, 0) };
            nav.Dock = DockStyle.Fill; nav.FlowDirection = FlowDirection.TopDown; nav.WrapContents = false; nav.AutoScroll = true; nav.BackColor = UiStyle.Sidebar;
            nav.SizeChanged += delegate { FitNavigation(); QueueActiveNavigationVisibility(); };
            sidebar.Controls.Add(nav); sidebar.Controls.Add(brand); sidebar.Controls.Add(local); Controls.Add(sidebar);
            string lastGroup = "";
            foreach (var section in UiPreferences.Sections)
            {
                if (section.Group != lastGroup) { AddNavGroup(section.Group); lastGroup = section.Group; }
                if (section.Key == "updates")
                {
                    var update = NewNavButton("updates", "Обновления");
                    update.Click += delegate { Guard(delegate { viewTransition.Cancel(); UpdateManager.Show(this, store, SetUpdateAvailable); }); };
                    updatesButton = update; nav.Controls.Add(update);
                }
                else AddNav(section.Key, section.Text);
            }
            body.Dock = DockStyle.Fill; body.Padding = new Padding(24, 20, 24, 12); Controls.Add(body); body.BringToFront();
            var header = new Panel { Dock = DockStyle.Top, Height = 91, BackColor = Background };
            title.Font = new Font("Segoe UI", 22, FontStyle.Bold); title.SetBounds(0, 0, 610, 44);
            hint.SetBounds(0, 49, 920, 30); hint.ForeColor = UiStyle.Muted; hint.AutoEllipsis = true;
            header.Controls.Add(title); header.Controls.Add(hint);
            var utilities = new FlowLayoutPanel { Name = "programUtilities", Width = 130, Height = 40,
                FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            utilities.Controls.Add(CommandMenu("program", "Меню",
                Command("settings", "Настройки", Settings),
                Command("interface", "Интерфейс", OpenInterfaceSettings),
                null,
                Command("backup", "Сохранить резервную копию", Backup),
                Command("restore", "Восстановить из копии", Restore),
                null,
                Command("updates", "Обновления", delegate { viewTransition.Cancel(); UpdateManager.Show(this, store, SetUpdateAvailable); })));
            header.SizeChanged += delegate
            {
                utilities.SetBounds(Math.Max(0, header.ClientSize.Width - 130), 2, 130, 40);
                title.Width = Math.Max(240, header.ClientSize.Width - 144); hint.Width = header.ClientSize.Width;
            };
            header.Controls.Add(utilities);
            actions.Dock = DockStyle.Top; actions.AutoSize = true; actions.AutoSizeMode = AutoSizeMode.GrowAndShrink; actions.MinimumSize = new Size(0, 44);
            actions.Padding = new Padding(0, 0, 0, 7);
            actions.WrapContents = false; actions.Name = "sectionCommands";
            filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(10, 8, 10, 4), BackColor = Color.White, Margin = new Padding(0) };
            filters.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(UiStyle.Border)) e.Graphics.DrawRectangle(pen, 0, 0, filters.ClientSize.Width - 1, filters.ClientSize.Height - 1); };
            search.Width = 225; search.TextChanged += delegate { RefreshView(); }; search.AccessibleName = "Поиск";
            currency.Width = 112; currency.DropDownStyle = ComboBoxStyle.DropDownList; currency.Items.AddRange(new object[] { "BYN", "RUB", "USD", "EUR", "Все валюты" });
            currency.SelectedItem = (store.Of("settings").FirstOrDefault() ?? new Record("settings")).Get("currency"); if (currency.SelectedIndex < 0) currency.SelectedIndex = 0;
            party.Width = 200; party.DropDownStyle = ComboBoxStyle.DropDownList; party.AccessibleName = "Фильтр контрагентов";
            direction.Width = 150; direction.DropDownStyle = ComboBoxStyle.DropDownList; direction.Items.AddRange(new object[] { "Оба направления", "Нам платят", "Мы платим" }); direction.SelectedIndex = 0;
            from.Width = to.Width = 166; from.Format = to.Format = DateTimePickerFormat.Custom; from.CustomFormat = to.CustomFormat = "dd.MM.yyyy"; from.ShowCheckBox = to.ShowCheckBox = true;
            from.Value = new DateTime(DateTime.Today.Year, 1, 1); to.Value = DateTime.Today; from.Checked = to.Checked = false;
            project.Width = 200; project.DropDownStyle = ComboBoxStyle.DropDownList; project.AccessibleName = "Фильтр объектов";
            status.Width = 185; status.DropDownStyle = ComboBoxStyle.DropDownList; status.AccessibleName = "Состояние записи";
            period.Width = 165; period.DropDownStyle = ComboBoxStyle.DropDownList; period.AccessibleName = "Быстрый период";
            period.Items.AddRange(new object[] { "За всё время", "Сегодня", "Этот месяц", "Прошлый месяц", "Этот год", "Произвольный" }); period.SelectedIndex = 0;
            currency.AccessibleName = "Фильтр валюты"; direction.AccessibleName = "Направление расчётов";
            from.AccessibleName = "Начало периода"; to.AccessibleName = "Конец периода";
            archived.Text = "Показывать архив"; archived.AutoSize = true; archived.Tag = "filter:archive"; archived.Margin = new Padding(6, 6, 0, 0);
            filterTips.SetToolTip(search, "Все слова запроса ищутся в записи, примечаниях и связанных документах. Регистр и ё/е не важны.");
            filterTips.SetToolTip(from, "Выберите начальную дату или включите галочку. Указанный день входит в период. Снимите галочку, чтобы убрать начальную границу.");
            filterTips.SetToolTip(to, "Выберите конечную дату или включите галочку. Указанный день входит в период. Снимите галочку, чтобы убрать конечную границу.");
            filterTips.SetToolTip(archived, "Показывает действующие и архивные записи. Архив не исключается из финансовых отчётов.");
            AddFilter(filters, "Поиск", search, "search"); AddFilter(filters, "Валюта", currency, "currency");
            AddFilter(filters, "Контрагент", party, "party"); AddFilter(filters, "Объект", project, "project");
            AddFilter(filters, "Направление", direction, "direction"); AddFilter(filters, "Состояние", status, "status");
            AddFilter(filters, "Период", period, "dates"); AddFilter(filters, "С", from, "dates"); AddFilter(filters, "По", to, "dates"); filters.Controls.Add(archived);
            resetFiltersButton = Button("Сбросить", ResetFilters); resetFiltersButton.Tag = "filter:reset"; resetFiltersButton.AccessibleName = "Сбросить фильтры"; filters.Controls.Add(resetFiltersButton);
            foreach (var combo in new[] { currency, party, project, direction, status }) combo.SelectedIndexChanged += delegate { RefreshView(); };
            period.SelectedIndexChanged += delegate { ApplyPeriod(); };
            from.ValueChanged += delegate { CustomPeriod(); RefreshView(); }; to.ValueChanged += delegate { CustomPeriod(); RefreshView(); }; archived.CheckedChanged += delegate { RefreshView(); };
            foreach (var boundary in new[] { from, to })
            {
                boundary.MouseUp += delegate { DateBoundaryStateChanged(); };
                boundary.KeyUp += delegate { DateBoundaryStateChanged(); };
                boundary.CloseUp += delegate { DateBoundaryStateChanged(); };
            }
            report.Width = 285; report.DropDownStyle = ComboBoxStyle.DropDownList;
            report.Items.AddRange(new object[] { "Задолженность по актам", "Прибыль по объектам", "Сверка с контрагентом", "Выполнение договоров", "Движение денег", "Документы и сроки", "Акты для налогового учёта" });
            report.SelectedIndex = 0; report.SelectedIndexChanged += delegate { ConfigureFilters(false); RefreshView(); };
            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells; UiStyle.ApplyGrid(grid);
            grid.SortCompare += CompareDates;
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) Edit(); };
            overview.Dock = DockStyle.Fill; overview.AutoScroll = true; overview.BackColor = Background;
            count.Dock = DockStyle.Bottom; count.Height = 27; count.AutoEllipsis = true; count.ForeColor = UiStyle.Muted; count.Font = new Font("Segoe UI", 9);
            body.Controls.Add(grid); body.Controls.Add(overview); body.Controls.Add(count); body.Controls.Add(filters); body.Controls.Add(actions); body.Controls.Add(header);
            UiStyle.ApplyTo(this); UiStyle.SetAnimations(this, interfacePreferences.AnimationsEnabled);
            monthlyNotice.Dock = DockStyle.Bottom; monthlyNotice.Height = 36; monthlyNotice.AutoEllipsis = true;
            monthlyNotice.Padding = new Padding(0, 6, 0, 0); monthlyNotice.Font = new Font("Segoe UI", 9); monthlyNotice.Visible = false;
            monthlyNotice.Click += delegate { if (monthlyNotice.Cursor == Cursors.Hand) ShowAllActs(); };
            body.Controls.Add(monthlyNotice);
            RefillParties(); ApplyInterfacePreferences(interfacePreferences);
            Navigate(FirstVisibleSection());
            if (automationEnabled)
            {
                Shown += delegate { RefreshMonthlyServiceAutomation(); monthlyTimer.Start(); };
                monthlyTimer.Interval = 60000;
                monthlyTimer.Tick += delegate
                {
                    try { ProcessMonthlyServiceActs(MonthlyServiceRuntime.Today); lastAutomationError = null; }
                    catch (Exception e) { ShowMonthlyServiceError(e.Message); }
                };
            }
            overview.SizeChanged += delegate { if (current == "overview" && IsHandleCreated && !refreshing) RefreshView(); };
        }
        private string BaseCurrency { get { var setting = store.Of("settings").FirstOrDefault(); return setting == null || setting.Get("currency") == "" ? "BYN" : setting.Get("currency"); } }
        private static void AddFilter(FlowLayoutPanel parent, string label, Control input, string tag)
        {
            var group = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 9, 7), Tag = "filter:" + tag };
            if (label != "") group.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = UiStyle.Muted, Font = new Font("Segoe UI", 9), Margin = new Padding(0, 6, 6, 0) });
            input.Margin = new Padding(0); group.Controls.Add(input); parent.Controls.Add(group);
        }


        public int ProcessMonthlyServiceActs(DateTime today)
        {
            viewTransition.Cancel();
            var created = store.GenerateMonthlyServiceActs(today);
            if (created.Count > 0)
            {
                RefreshView();
                monthlyNotice.ForeColor = UiStyle.Blue; monthlyNotice.Cursor = Cursors.Hand;
                monthlyNotice.Text = "Обслуживание: создано актов " + created.Count + ". Нажмите, чтобы открыть все акты.";
                monthlyNotice.Visible = true;
                MonthlyServiceRuntime.Log(store.DataDir, "Создано актов обслуживания: " + created.Count);
            }
            return created.Count;
        }
        private void RefreshMonthlyServiceAutomation()
        {
            if (!automationEnabled) return;
            try
            {
                ProcessMonthlyServiceActs(MonthlyServiceRuntime.Today);
                bool enabled = store.Of("party").Any(p => p.Get("monthlyServiceEnabled") == "true");
                var result = MonthlyServiceScheduler.Ensure(Application.ExecutablePath, store.DataDir, enabled);
                if (!result.Success && enabled)
                {
                    monthlyNotice.Text = "Фоновое создание не настроено. При открытой программе акты создаются автоматически. " + result.Message;
                    monthlyNotice.ForeColor = Color.Firebrick; monthlyNotice.Cursor = Cursors.Default; monthlyNotice.Visible = true;
                    MonthlyServiceRuntime.Log(store.DataDir, result.Message + " " + result.ErrorDetails);
                }
                else if (!enabled || (result.Success && monthlyNotice.ForeColor == Color.Firebrick)) monthlyNotice.Visible = false;
            }
            catch (Exception e) { ShowMonthlyServiceError(e.Message); }
        }
        private void ShowMonthlyServiceError(string message)
        {
            monthlyNotice.Text = "Не удалось создать акты обслуживания: " + message;
            monthlyNotice.ForeColor = Color.Firebrick; monthlyNotice.Cursor = Cursors.Default; monthlyNotice.Visible = true;
            if (message != lastAutomationError) MonthlyServiceRuntime.Log(store.DataDir, message);
            lastAutomationError = message;
        }


        public void SetUpdateAvailable(bool available)
        {
            if (updatesButton == null || updatesButton.IsDisposed) return;
            updatesButton.Text = available ? "Обновления · доступно" : "Обновления";
        }
        public void StartUpdateCheck() { UpdateManager.CheckInBackground(this, store, SetUpdateAvailable); }
        private static void AddDateFilter(FlowLayoutPanel filters, string label, DateTimePicker picker)
        {
            var group = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 4, 0) };
            group.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 4, 0) }); group.Controls.Add(picker); filters.Controls.Add(group);
        }
        private System.Windows.Forms.Button Button(string label, Action click)
        {
            bool primary = label == "+ Добавить" || label == "Сохранить акт" || label == "+ Новый акт";
            var b = new UiButton { Text = label, AutoSize = true, MinimumSize = new Size(0, 36), Height = 36,
                Primary = primary, AnimationsEnabled = interfacePreferences.AnimationsEnabled,
                Padding = new Padding(12, 0, 12, 0), Margin = new Padding(0, 0, 7, 5), BackColor = primary ? UiStyle.Blue : Color.White, ForeColor = primary ? Color.White : Ink };
            b.Click += delegate { Guard(click); }; return b;
        }
        private sealed class MenuCommand
        {
            public readonly string Id, Text;
            public readonly Action Execute;
            public readonly bool NeedsSelection;
            public MenuCommand(string id, string text, Action execute, bool needsSelection)
            { Id = id; Text = text; Execute = execute; NeedsSelection = needsSelection; }
        }
        private static MenuCommand Command(string id, string text, Action execute, bool needsSelection = false)
        { return new MenuCommand(id, text, execute, needsSelection); }
        private System.Windows.Forms.Button CommandMenu(string key, string caption, params MenuCommand[] commands)
        {
            var menu = new ContextMenuStrip { Name = key + "Menu", ShowImageMargin = false,
                Font = this.Font, BackColor = UiStyle.Surface, ForeColor = Ink, Padding = new Padding(3) };
            foreach (MenuCommand command in commands)
            {
                if (command == null) { menu.Items.Add(new ToolStripSeparator()); continue; }
                MenuCommand entry = command;
                var item = new ToolStripMenuItem(entry.Text) { Name = entry.Id, Tag = entry, Padding = new Padding(10, 7, 12, 7) };
                item.Click += delegate { Guard(entry.Execute); };
                menu.Items.Add(item);
            }
            menu.Opening += delegate
            {
                bool selected = Selected() != null;
                foreach (ToolStripMenuItem item in menu.Items.OfType<ToolStripMenuItem>())
                    item.Enabled = !((MenuCommand)item.Tag).NeedsSelection || selected;
            };
            System.Windows.Forms.Button button = null;
            button = Button(caption + " ▾", delegate { menu.Show(button, new Point(0, button.Height)); });
            button.Name = key + "Commands"; button.Tag = "command-menu:" + key;
            button.AccessibleName = caption; button.AccessibleRole = AccessibleRole.ButtonMenu;
            button.ContextMenuStrip = menu;
            button.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Down || e.KeyCode == Keys.F4)
                { menu.Show(button, new Point(0, button.Height)); e.Handled = true; e.SuppressKeyPress = true; }
            };
            button.Disposed += delegate { menu.Dispose(); };
            return button;
        }
        private void BuildRecordCommands(string kind)
        {
            actions.Controls.Add(Button("+ Добавить", Add)); actions.Controls.Add(Button("Изменить", Edit));
            var operations = new List<MenuCommand>();
            if (Catalog.IsActSection(kind)) operations.Add(Command("act-items", "Позиции акта", EditActItems, true));
            if (Catalog.IsActSection(kind) || Catalog.IsPaymentSection(kind)) operations.Add(Command("allocate", "Зачесть оплату", Allocate));
            if (kind == "estimate") operations.Add(Command("create-act", "Создать акт", CreateAct, true));
            if (Catalog.IsActSection(kind)) operations.Add(Command("create-invoice", "Создать счёт", CreateInvoice, true));
            if (operations.Count > 0) operations.Add(null);
            operations.Add(Command("archive", "В архив / вернуть", Archive, true));
            actions.Controls.Add(CommandMenu("record-actions", "Действия", operations.ToArray()));

            var documents = new List<MenuCommand>();
            if (Catalog.IsActSection(kind) || kind == "invoice" || kind == "estimate" || kind == "contract")
                documents.Add(Command("print-document", "Печатная форма", PrintDocument, true));
            if (Catalog.IsActSection(kind))
            {
                documents.Add(Command("act-word", "Акт Word", ExportActWord, true));
                documents.Add(Command("act-excel", "Акт в Excel", ExportActDocument, true));
            }
            if (kind != "work" && kind != "hold" && kind != "amendment")
                documents.Add(Command("attach", "Прикрепить файл", Attach, true));
            if (kind == "document") documents.Add(Command("open-file", "Открыть файл", OpenAttachment, true));
            if (documents.Count > 0) actions.Controls.Add(CommandMenu("record-documents", "Документы", documents.ToArray()));

            var data = new List<MenuCommand> { Command("export", "Excel / CSV", Export), Command("import", "Импорт", Import) };
            if (Catalog.IsActSection(kind))
            {
                data.Add(null);
                data.Add(Command("all-acts", kind == Catalog.ExternalActSection ? "Все акты вне" : "Все акты",
                    delegate { ShowAllActs(kind == Catalog.ExternalActSection); }));
            }
            actions.Controls.Add(CommandMenu("record-data", "Данные", data.ToArray()));
        }

        private UiButton NewNavButton(string key, string text)
        {
            return new UiButton { Text = text, Tag = key, Width = 202, Height = 36, TextAlign = ContentAlignment.MiddleLeft,
                Navigation = true, IconKey = Catalog.IsActSection(key) ? "act" : Catalog.IsPaymentSection(key) ? "payment" : key, AnimationsEnabled = interfacePreferences.AnimationsEnabled,
                BackColor = UiStyle.Sidebar, ForeColor = Color.FromArgb(202, 214, 230), Margin = new Padding(0, 0, 0, 2) };
        }
        private void AddNav(string kind, string label)
        {
            var b = NewNavButton(kind, label);
            b.Click += delegate { Guard(delegate { if (Catalog.IsActSection(kind)) ShowAllActs(kind == Catalog.ExternalActSection); else Navigate(kind); }); }; nav.Controls.Add(b);
        }
        private void AddNavGroup(string group)
        {
            nav.Controls.Add(new Label { Text = group.ToUpperInvariant(), Width = 202, Height = 21, AutoSize = false,
                Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.FromArgb(128, 151, 178),
                Padding = new Padding(10, 0, 0, 0), Margin = new Padding(0, 13, 0, 3), Tag = "nav-group:" + group });
        }
        private bool navigationScrollQueued;
        private void QueueActiveNavigationVisibility()
        {
            if (!IsHandleCreated || IsDisposed || navigationScrollQueued) return;
            navigationScrollQueued = true;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    navigationScrollQueued = false;
                    if (IsDisposed || nav.IsDisposed) return;
                    nav.PerformLayout();
                    var selected = nav.Controls.OfType<UiButton>().FirstOrDefault(button => button.Selected);
                    if (selected != null) nav.ScrollControlIntoView(selected);
                });
            }
            catch (InvalidOperationException) { navigationScrollQueued = false; }
        }
        private bool fittingNavigation;
        private void FitNavigation()
        {
            if (fittingNavigation || nav.IsDisposed) return;
            fittingNavigation = true;
            try
            {
                nav.SuspendLayout();
                int width = Math.Max(140, nav.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
                foreach (Control child in nav.Controls) child.Width = width;
                nav.ResumeLayout(true);
            }
            finally { fittingNavigation = false; }
        }
        private string FirstVisibleSection()
        {
            var first = UiPreferences.Sections.FirstOrDefault(s => s.Key != "updates" && interfacePreferences.IsVisible(s.Key));
            return first == null ? "overview" : first.Key;
        }
        public void ApplyInterfacePreferences(UiPreferences preferences)
        {
            if (preferences == null) throw new ArgumentNullException("preferences");
            preferences.Validate(); interfacePreferences = preferences.Clone(); viewTransition.Cancel();
            nav.SuspendLayout();
            foreach (Control item in nav.Controls)
            {
                string key = Convert.ToString(item.Tag);
                if (key.StartsWith("nav-group:", StringComparison.Ordinal))
                {
                    var following = nav.Controls.Cast<Control>().SkipWhile(c => !Object.ReferenceEquals(c, item)).Skip(1)
                        .TakeWhile(c => !Convert.ToString(c.Tag).StartsWith("nav-group:", StringComparison.Ordinal));
                    item.Visible = following.Any(c => UiPreferences.Sections.Any(s => s.Key == Convert.ToString(c.Tag)) && interfacePreferences.IsVisible(Convert.ToString(c.Tag)));
                }
                else if (UiPreferences.Sections.Any(s => s.Key == key)) item.Visible = interfacePreferences.IsVisible(key);
            }
            nav.ResumeLayout(true); FitNavigation(); UiStyle.SetAnimations(this, interfacePreferences.AnimationsEnabled);
            if (!interfacePreferences.IsVisible(PreferenceSection(current))) Navigate(FirstVisibleSection());
            Invalidate(true);
        }
        public void OpenInterfaceSettings()
        {
            viewTransition.Cancel();
            using (var dialog = new InterfaceSettingsDialog(store.DataDir, interfacePreferences))
            {
                UiStyle.ApplyTo(dialog); UiStyle.SetAnimations(dialog, interfacePreferences.AnimationsEnabled);
                if (dialog.ShowDialog(this) == DialogResult.OK) ApplyInterfacePreferences(dialog.ResultPreferences);
            }
        }
        private static Icon LoadApplicationIcon()
        {
            using (Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("WorkLedger.AppIcon.ico"))
            {
                if (stream == null) throw new InvalidDataException("Не найдена иконка приложения.");
                using (Icon source = new Icon(stream, 32, 32)) return (Icon)source.Clone();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { monthlyTimer.Stop(); monthlyTimer.Dispose(); filterTips.Dispose(); viewTransition.Dispose(); }
            base.Dispose(disposing);
            if (disposing && applicationIcon != null) applicationIcon.Dispose();
        }

        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось выполнить действие", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private static string ChoiceId(ComboBox box) { var selected = box.SelectedItem as Choice; return selected == null ? "" : selected.Id; }
        private static void SelectChoice(ComboBox box, string id)
        {
            box.SelectedIndex = box.Items.Count == 0 ? -1 : 0;
            for (int index = 0; index < box.Items.Count; index++) if (((Choice)box.Items[index]).Id == id) { box.SelectedIndex = index; break; }
        }
        private void RefillParties()
        {
            string selectedParty = ChoiceId(party), selectedProject = ChoiceId(project);
            bool previous = refreshing; refreshing = true;
            try
            {
                party.Items.Clear(); party.Items.Add(new Choice("", "Все контрагенты"));
                foreach (var record in store.Records.Where(r => r.Kind == "party").OrderBy(r => r.Get("name")))
                    party.Items.Add(new Choice(record.Id, Catalog.Display(store, record)));
                SelectChoice(party, selectedParty);
                project.Items.Clear(); project.Items.Add(new Choice("", "Все объекты")); project.Items.Add(new Choice("__none__", "Без объекта"));
                foreach (var record in store.Records.Where(r => r.Kind == "project").OrderBy(r => r.Get("name")))
                    project.Items.Add(new Choice(record.Id, Catalog.Display(store, record)));
                SelectChoice(project, selectedProject);
            }
            finally { refreshing = previous; }
        }
        private bool FilterAvailable(string key)
        {
            if (IsActComposerSection(current) || current == "taxes") return false;
            if (current == "overview") return key == "currency";
            if (current == "audit") return key == "search" || key == "dates" || key == "reset";
            if (key == "search" || key == "reset") return true;
            if (key == "archive") return current != "reports";
            if (key == "currency" || key == "project" || key == "party") return current != "party";
            if (key == "direction") return current != "party" && current != "project" && !(current == "reports" && report.SelectedIndex == 1);
            if (key == "dates") return current != "party" && current != "project";
            if (key == "status")
            {
                var schema = Catalog.Get(current);
                return Catalog.IsPaymentSection(current) || (schema != null && schema.Fields.Any(f => f.Key == "status" || f.Key == "role"));
            }
            return false;
        }
        private void ConfigureFilters(bool refillStatus)
        {
            bool previous = refreshing; refreshing = true;
            try
            {
                bool allCurrencies = current != "overview" && current != "reports" && current != "project";
                if (allCurrencies && !currency.Items.Contains("Все валюты")) currency.Items.Add("Все валюты");
                if (!allCurrencies)
                {
                    if (Curr == "Все валюты") currency.SelectedItem = BaseCurrency;
                    currency.Items.Remove("Все валюты");
                }
                if (refillStatus)
                {
                    status.Items.Clear(); status.Items.Add(new Choice("", current == "party" ? "Все роли" : "Все состояния"));
                    var schema = Catalog.Get(current);
                    var field = schema == null ? null : schema.Fields.FirstOrDefault(f => f.Key == "status" || f.Key == "role");
                    if (field != null) foreach (var option in field.Options) status.Items.Add(new Choice(option.Key, option.Value));
                    if (Catalog.IsActSection(current))
                    {
                        status.Items.Add(new Choice("due", "Есть долг к оплате")); status.Items.Add(new Choice("overdue", "Просроченные"));
                        status.Items.Add(new Choice("paid", "Оплаченные")); status.Items.Add(new Choice("held", "Только удержания"));
                    }
                    if (Catalog.IsPaymentSection(current)) { status.Items.Add(new Choice("advance", "Есть незачтённый аванс")); status.Items.Add(new Choice("allocated", "Полностью зачтены")); }
                    status.SelectedIndex = 0;
                }
                foreach (Control group in filters.Controls)
                {
                    string tag = Convert.ToString(group.Tag);
                    bool available = tag.StartsWith("filter:", StringComparison.Ordinal) && FilterAvailable(tag.Substring(7));
                    group.Visible = group.Enabled = available;
                }
                search.Enabled = FilterAvailable("search"); currency.Enabled = FilterAvailable("currency");
                party.Enabled = FilterAvailable("party"); project.Enabled = FilterAvailable("project");
                direction.Enabled = FilterAvailable("direction"); status.Enabled = FilterAvailable("status");
                from.Enabled = to.Enabled = period.Enabled = FilterAvailable("dates"); archived.Enabled = FilterAvailable("archive");
                resetFiltersButton.Enabled = FilterAvailable("reset");
            }
            finally { refreshing = previous; }
        }
        private bool DateRangeActive { get { return FilterAvailable("dates") && (from.Checked || to.Checked); } }
        private bool InPeriod(DateTime date) { return !DateRangeActive || ((!from.Checked || date.Date >= from.Value.Date) && (!to.Checked || date.Date <= to.Value.Date)); }
        private void CustomPeriod()
        {
            if (refreshing) return;
            int choice = from.Checked || to.Checked ? 5 : 0;
            if (period.SelectedIndex == choice) return;
            bool previous = refreshing; refreshing = true; period.SelectedIndex = choice; refreshing = previous;
        }
        private void ApplyPeriod()
        {
            if (refreshing || period.SelectedIndex < 0 || period.SelectedIndex == 5) return;
            bool previous = refreshing; refreshing = true;
            try
            {
                if (period.SelectedIndex == 0) from.Checked = to.Checked = false;
                else
                {
                    DateTime today = DateTime.Today, begin = today, end = today;
                    if (period.SelectedIndex == 2) { begin = new DateTime(today.Year, today.Month, 1); end = begin.AddMonths(1).AddDays(-1); }
                    else if (period.SelectedIndex == 3) { end = new DateTime(today.Year, today.Month, 1).AddDays(-1); begin = new DateTime(end.Year, end.Month, 1); }
                    else if (period.SelectedIndex == 4) { begin = new DateTime(today.Year, 1, 1); end = new DateTime(today.Year, 12, 31); }
                    from.Value = begin; to.Value = end; from.Checked = to.Checked = true;
                }
            }
            finally { refreshing = previous; }
            RefreshView();
        }
        private void DateBoundaryStateChanged()
        {
            if (refreshing || (from.CustomFormat == DateBoundaryFormat(from) && to.CustomFormat == DateBoundaryFormat(to))) return;
            CustomPeriod(); RefreshView();
        }
        private static string DateBoundaryFormat(DateTimePicker boundary)
        {
            return boundary.Checked ? "dd.MM.yyyy" : "'Без ограничения'";
        }
        private void UpdateDateFilterDisplay()
        {
            from.CustomFormat = DateBoundaryFormat(from);
            to.CustomFormat = DateBoundaryFormat(to);
        }
        private bool IsDateColumn(string header)
        {
            var schema = Catalog.Get(current);
            if (schema != null) return schema.Fields.Any(f => f.Type == "date" && f.Label == header);
            if (current == "audit") return header == "Дата и время";
            return current == "reports" && (header == "Дата" || header == "Дата подписания" || header == "Срок" || header == "Срок / дата");
        }
        private static bool TryGridDate(object value, out DateTime date)
        {
            return DateTime.TryParseExact(Convert.ToString(value),
                new[] { "dd.MM.yyyy", "dd.MM.yyyy HH:mm:ss '(Минск)'", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
        private void CompareDates(object sender, DataGridViewSortCompareEventArgs e)
        {
            if (!Object.Equals(e.Column.Tag, "date")) return;
            DateTime first, second;
            bool hasFirst = TryGridDate(e.CellValue1, out first), hasSecond = TryGridDate(e.CellValue2, out second);
            e.SortResult = hasFirst && hasSecond ? first.CompareTo(second)
                : hasFirst != hasSecond ? hasFirst.CompareTo(hasSecond)
                : String.Compare(Convert.ToString(e.CellValue1), Convert.ToString(e.CellValue2), StringComparison.CurrentCulture);
            if (e.SortResult == 0)
                e.SortResult = String.Compare(Convert.ToString(grid.Rows[e.RowIndex1].Tag), Convert.ToString(grid.Rows[e.RowIndex2].Tag), StringComparison.Ordinal);
            e.Handled = true;
        }
        public void ResetFilters()
        {
            bool previous = refreshing; refreshing = true;
            try
            {
                search.Clear(); SelectChoice(party, ""); SelectChoice(project, ""); SelectChoice(status, "");
                direction.SelectedIndex = 0; from.Checked = to.Checked = false; period.SelectedIndex = 0; archived.Checked = false;
                currency.SelectedItem = BaseCurrency; if (currency.SelectedIndex < 0) currency.SelectedIndex = 0;
            }
            finally { refreshing = previous; }
            RefreshView();
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F) && FilterAvailable("search")) { search.Focus(); search.SelectAll(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.R) && FilterAvailable("reset")) { ResetFilters(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        public void Navigate(string kind)
        {
            string previous = current; viewTransition.Cancel(); current = kind;
            ConfigureFilters(true);
            foreach (var button in nav.Controls.OfType<UiButton>()) button.Selected = Convert.ToString(button.Tag) == PreferenceSection(kind);
            foreach (var pending in actComposers.Values) pending.Visible = false;
            foreach (Control action in actions.Controls.Cast<Control>().ToList())
                if (Object.ReferenceEquals(action, report)) actions.Controls.Remove(action); else action.Dispose();
            actions.Controls.Clear();
            if (IsActComposerSection(kind))
            {
                if (!actComposers.TryGetValue(kind, out composer))
                {
                    var newComposer = new HourlyActComposer(store, null, kind == CreateExternalActSection);
                    composer = newComposer; actComposers.Add(kind, newComposer);
                    newComposer.ActSaved += delegate
                    {
                        actComposers.Remove(kind); FocusRecord(newComposer.ResultRecord);
                        body.Controls.Remove(newComposer); if (Object.ReferenceEquals(composer, newComposer)) composer = null; newComposer.Dispose();
                    };
                    UiStyle.ApplyTo(composer); UiStyle.SetAnimations(composer, interfacePreferences.AnimationsEnabled); body.Controls.Add(composer); composer.BringToFront();
                }
                composer.Visible = true;
                actions.Controls.Add(Button("Сохранить акт", delegate { composer.SaveDraft(); }));
                actions.Controls.Add(Button("Предпросмотр", PreviewAct));
                actions.Controls.Add(CommandMenu("draft-actions", "Действия",
                    Command("hourly-settings", "Настроить стоимость часа", Settings),
                    Command("all-acts", kind == CreateExternalActSection ? "Все акты вне" : "Все акты",
                        delegate { ShowAllActs(kind == CreateExternalActSection); })));
            }
            else if (kind == "taxes")
            {
                EnsureTaxesPage();
                taxesPage.Visible = true;
            }
            else if (Catalog.Get(kind) != null && kind != "settings") BuildRecordCommands(kind);
            else if (kind == "reports")
            {
                actions.Controls.Add(report);
                actions.Controls.Add(CommandMenu("report-file", "Файл",
                    Command("export", "Выгрузить Excel", Export), Command("print-table", "Печать отчёта", PrintTable)));
            }
            else if (kind == "audit") actions.Controls.Add(CommandMenu("audit-file", "Файл", Command("export", "Выгрузить Excel", Export)));
            else { actions.Controls.Add(Button("+ Новый акт", delegate { Navigate("create-act"); })); actions.Controls.Add(Button("+ Оплата", delegate { Navigate("payment"); Add(); })); actions.Controls.Add(Button("Отчёты", delegate { Navigate("reports"); })); }
            title.Text = IsActComposerSection(kind) ? (kind == CreateExternalActSection ? "Создать акт вне" : "Создать акт") : kind == "overview" ? "Обзор" : kind == "reports" ? "Отчёты и сверка" : kind == "audit" ? "История изменений" : kind == "taxes" ? "Налоги" : Catalog.Get(kind).Title;
            hint.Text = kind == CreateExternalActSection ? "Акт войдёт в общий учёт и сверку, вне выборки «Акты для налогового учёта»." : kind == "create-act" ? "Выберите контрагента, впишите работы и часы. Номер и сумма рассчитываются автоматически." : kind == "overview" ? "Показатели за всё время в выбранной валюте. Напоминания — на ближайшие 7 дней."
                : kind == "expense" ? "Дополнительные затраты. Работы подрядчиков учитывайте актами, чтобы не посчитать расход дважды."
                : kind == Catalog.GrayPaymentSection ? "Оплаты по «Актам вне», без налогов. «Действия → Зачесть оплату» уменьшает долг по акту."
                : kind == "payment" ? "Белые оплаты и авансы. «Действия → Зачесть оплату» связывает их с обычными актами."
                : kind == Catalog.ExternalActSection ? "Акты вне входят в общий учёт и сверку, вне выборки «Акты для налогового учёта»."
                : kind == "act" ? "В задолженность входят подписанные акты. Удержания показываются отдельно от суммы к оплате."
                : kind == "taxes" ? "Белые поступления по датам оплат за квартал. Налог — 20%; серые оплаты исключены."
                : kind == "reports" ? "Прибыль считается по подписанным актам и расходам. Движение денег — по платежам."
                : kind == "work" ? "Добавьте позиции к смете. Сумма рассчитывается из количества и цены."
                : "Поиск, контрагент, направление, валюта и даты помогают найти нужные записи.";
            overview.Visible = kind == "overview"; grid.Visible = kind != "overview" && !IsActComposerSection(kind) && kind != "taxes";
            if (taxesPage != null) taxesPage.Visible = kind == "taxes";
            actions.Visible = kind != "taxes";
            if (composer != null) composer.Visible = IsActComposerSection(kind);
            filters.Visible = count.Visible = !IsActComposerSection(kind) && kind != "taxes";
            ConfigureFilters(false);
            RefreshView();
            if (previous != kind) viewTransition.Begin(IsActComposerSection(kind) ? (Control)composer : kind == "overview" ? (Control)overview : kind == "taxes" ? (Control)taxesPage : grid, interfacePreferences.AnimationsEnabled);
            QueueActiveNavigationVisibility();
        }
        private static bool IsActComposerSection(string key) { return key == "create-act" || key == CreateExternalActSection; }
        private static string PreferenceSection(string key) { return key == CreateExternalActSection ? Catalog.ExternalActSection : key; }
        private bool MatchesCurrentSection(Record record)
        {
            if (Catalog.IsActSection(current)) return record.Kind == "act" && Store.IsExternalAct(record) == (current == Catalog.ExternalActSection);
            if (Catalog.IsPaymentSection(current))
            {
                if (record.Kind != "payment") return false;
                PaymentSplit parts = ViewPaymentParts(record);
                return current == Catalog.GrayPaymentSection ? parts.GreyAmount > 0 : parts.WhiteAmount > 0;
            }
            return record.Kind == current;
        }
        private PaymentSplit ViewPaymentParts(Record payment)
        {
            return (viewPaymentAccounting ?? PaymentAccounting.Build(store.Records)).For(payment);
        }
        internal string SectionForRecord(Record record)
        {
            if (Store.IsExternalAct(record)) return Catalog.ExternalActSection;
            if (record.Kind == "payment")
            {
                PaymentSplit parts = PaymentAccounting.Build(store.Records).For(record);
                if (parts.GreyAmount > 0 && parts.WhiteAmount == 0) return Catalog.GrayPaymentSection;
            }
            return record.Kind;
        }
        private string Curr { get { return Convert.ToString(currency.SelectedItem); } }
        private string PartyId { get { return FilterAvailable("party") ? ChoiceId(party) : ""; } }
        private string ProjectId { get { return FilterAvailable("project") ? ChoiceId(project) : ""; } }
        private string Dir { get { if (!FilterAvailable("direction")) return ""; return direction.SelectedIndex == 1 ? "income" : direction.SelectedIndex == 2 ? "expense" : ""; } }
        private bool Match(Record r, bool dateFilter)
        {
            r = Effective(r);
            if (FilterAvailable("currency") && Curr != "Все валюты" && r.Get("currency") != "" && r.Get("currency") != Curr) return false;
            if (!MatchesProject(r)) return false;
            if (PartyId != "" && r.Kind != "party" && r.Get("partyId") != PartyId) return false;
            if (r.Kind == "party" && PartyId != "" && r.Id != PartyId) return false;
            if (Dir != "" && r.Get("direction") != Dir) return false;
            if (FilterAvailable("status") && !MatchesStatus(r)) return false;
            DateTime d;
            if (dateFilter && DateRangeActive && (!DateTime.TryParseExact(r.Get("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) || !InPeriod(d))) return false;
            return true;
        }
        private bool MatchesProject(Record r)
        {
            string selected = ProjectId, actual = r.Kind == "project" ? r.Id : r.Get("projectId");
            return selected == "" || (selected == "__none__" ? actual == "" : actual == selected);
        }
        private bool MatchesStatus(Record r)
        {
            string selected = ChoiceId(status);
            if (selected == "") return true;
            if (r.Kind == "party") return r.Get("role") == selected;
            if (r.Kind == "payment")
            {
                PaymentSplit parts = ViewPaymentParts(r);
                decimal advance = current == Catalog.GrayPaymentSection ? parts.GreyAmount - parts.AllocatedGrey : parts.WhiteAmount - parts.AllocatedWhite;
                return selected == "advance" ? advance > 0 : advance == 0;
            }
            if (r.Kind == "act")
            {
                if (selected == "due") return r.Get("status") == "signed" && store.ActDue(r.Id) > 0;
                if (selected == "overdue") return r.Get("status") == "signed" && store.ActDue(r.Id) > 0 && r.Get("dueDate") != "" && String.CompareOrdinal(r.Get("dueDate"), Store.DateString(DateTime.Today)) < 0;
                if (selected == "paid") return r.Get("status") == "signed" && store.ActGrossDue(r.Id) == 0;
                if (selected == "held") return r.Get("status") == "signed" && store.ActGrossDue(r.Id) > 0 && store.ActDue(r.Id) == 0;
            }
            return r.Get("status") == selected;
        }
        private IEnumerable<string> SearchValues(Record record)
        {
            var r = Effective(record); var schema = Catalog.Get(r.Kind);
            if (schema != null) foreach (var field in schema.Fields.Where(f => f.Key != "filePath"))
                yield return Value(r, field);
            yield return Catalog.Display(store, r);
            foreach (string link in new[] { "partyId", "projectId", "contractId", "estimateId", "actId" })
            {
                var related = store.Find(r.Get(link)); if (related == null) continue;
                yield return Catalog.Display(store, related);
                yield return related.Get("name"); yield return related.Get("unp"); yield return related.Get("title"); yield return related.Get("description");
            }
            if (r.Get("amount") != "") yield return F(Money(r));
            if (r.Get("direction") == "income") yield return "Нам платят Поступление";
            if (r.Get("direction") == "expense") yield return "Мы платим Выплата";
            if (r.Kind == "act")
            {
                yield return Status(r); yield return F(store.AllocatedToAct(r.Id)); yield return F(store.ActGrossDue(r.Id));
                yield return F(store.ActiveHolds(r.Id)); yield return F(store.ActDue(r.Id));
                if (r.Get("status") == "sent") yield return "Получить подпись";
                if (r.Get("status") == "draft") yield return "Подготовить и отправить";
            }
            if (r.Kind == "payment")
            {
                yield return F(store.AllocatedFromPayment(r.Id)); yield return F(store.PaymentUnallocated(r.Id));
                PaymentSplit parts = ViewPaymentParts(r); yield return F(parts.WhiteAmount); yield return F(parts.GreyAmount);
                yield return parts.IsMixed ? "Смешанная оплата Белая часть Серая часть" : parts.GreyAmount > 0 ? "Серая оплата" : "Белая оплата";
            }
            if (r.Kind == "estimate") yield return F(store.EstimateTotal(r.Id));
            if (r.Kind == "contract")
            {
                decimal effectiveAmount = store.EffectiveContractAmount(r.Id), completed = store.ContractActTotal(r.Id);
                yield return F(effectiveAmount); yield return F(completed); yield return F(effectiveAmount - completed);
                yield return "Проверить срок договора";
            }
            if (r.Kind == "invoice") { var act = store.Find(r.Get("actId")); if (act != null) yield return Status(act); yield return "Выставить счёт"; }
            if (r.Kind == "document") yield return r.Get("status") == "missing" ? "Добавить недостающий документ" : r.Get("status") == "sent" ? "Получить подпись" : "Отправить подписать";
            if (r.Kind == "project")
            {
                var totals = ProjectTotals(r.Id, current == "reports");
                yield return F(totals[0]); yield return F(totals[1]); yield return F(totals[0] - totals[1]);
                if (current == "reports" && report.SelectedIndex == 1)
                {
                    yield return F(ProjectRecords("act", true).Where(a => a.Get("projectId") == r.Id && a.Get("status") == "signed" && a.Get("direction") == "expense").Sum(a => Money(a)));
                    yield return F(ProjectRecords("expense", true).Where(e => e.Get("projectId") == r.Id).Sum(e => Money(e)));
                    var payments = ProjectRecords("payment", true).Where(pay => pay.Get("projectId") == r.Id).ToList();
                    yield return F(payments.Where(pay => pay.Get("direction") == "income").Sum(pay => Money(pay)));
                    yield return F(payments.Where(pay => pay.Get("direction") == "expense").Sum(pay => Money(pay)));
                }
            }
        }
        private bool MatchesSearch(Record r) { return !FilterAvailable("search") || FilterText.Matches(SearchValues(r), search.Text); }
        private bool AuditInPeriod(string utc)
        {
            DateTime date;
            return !DateRangeActive || (DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date) && InPeriod(date.ToUniversalTime().AddHours(3)));
        }
        private string Value(Record r, FieldDef f)
        {
            var value = Effective(r).Get(f.Key);
            if (f.Type == "bool") return value == "true" ? "Да" : "Нет";
            if (f.Type == "ref") return Label(value);
            if (f.Type == "enum" && f.Options != null && f.Options.ContainsKey(value)) return f.Options[value];
            DateTime d; if (f.Type == "date" && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d.ToString("dd.MM.yyyy");
            return value;
        }
        private string Label(string id) { var r = store.Find(id); return r == null ? "" : Catalog.Display(store, r); }
        private static decimal Money(Record r) { return Num(r.Get("amount")); }
        private static decimal Num(string text) { return String.IsNullOrWhiteSpace(text) ? 0 : Store.ParseMoney(text); }
        private static string F(decimal value) { return value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")); }
        private static string D(string value) { DateTime d; return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.ToString("dd.MM.yyyy") : value; }
        private Record Selected()
        {
            if (grid.SelectedRows.Count == 0) return null; return store.Find(Convert.ToString(grid.SelectedRows[0].Tag));
        }
        public void RefreshView()
        {
            if (refreshing || party.Items.Count == 0 || IsActComposerSection(current)) return;
            if (current == "taxes")
            {
                grid.Columns.Clear(); grid.Rows.Clear(); table = new TableData { Title = "Налоги" };
                if (taxesPage != null) taxesPage.RefreshData();
                return;
            }
            viewPaymentAccounting = PaymentAccounting.Build(store.Records);
            UpdateDateFilterDisplay();
            string sortedDateHeader = grid.SortedColumn != null && Object.Equals(grid.SortedColumn.Tag, "date") ? grid.SortedColumn.HeaderText : null;
            ListSortDirection dateSortDirection = grid.SortOrder == SortOrder.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            if (FilterAvailable("dates") && from.Checked && to.Checked && from.Value.Date > to.Value.Date)
            {
                grid.Columns.Clear(); grid.Rows.Clear(); table = new TableData { Title = "Некорректный период" };
                count.Text = "Дата «С» должна быть не позже даты «По». Исправьте период или нажмите «Сбросить»."; return;
            }
            if (current == "overview") { RenderOverview(); return; }
            if (current == "reports") { ConfigureFilters(false); hint.Text = report.SelectedIndex == 0 || report.SelectedIndex == 3 ? "Даты фильтруют даты документов; остатки и исполнение показаны на сегодня." : report.SelectedIndex == 1 ? "Затраты включают всех подрядчиков объекта. Контрагент в фильтре — основной заказчик объекта." : report.SelectedIndex == 2 ? "Сальдо по актам и всем платежам, включая авансы. Положительное — нам должны, отрицательное — мы должны." : report.SelectedIndex == 6 ? "Подписанные обычные акты вне архива. Акты вне не включаются. Период — по дате акта." : "Даты фильтруют даты документов. Валюты считаются отдельно."; table = BuildReport(); }
            else if (current == "audit")
            {
                table = new TableData { Title = "История изменений" }; table.Headers.AddRange(new[] { "Дата и время", "Пользователь", "Действие", "Раздел", "Описание" });
                foreach (var a in store.Audit.AsEnumerable().Reverse().Where(a => AuditInPeriod(a.Date)))
                {
                    var values = new[] { AuditTime(a.Date), a.User, Store.ActionLabel(a.Action), Store.KindLabel(a.Kind), a.Summary };
                    if (FilterText.Matches(values, search.Text)) table.Add(a.Id, values);
                }
            }
            else
            {
                var schema = Catalog.Get(current); table = new TableData { Title = schema.Title };
                var fields = schema.Fields.Where(f => f.Key != "filePath" && f.Key != "notes" && f.Key != "requisites" && f.Key != "contact" && (f.Type != "memo" || f.Key == "description")).ToList();
                if (current == "party") fields = fields.Where(f => new[] { "name", "role", "unp", "phone", "email", "bankAccount", "monthlyServiceEnabled", "monthlyServiceAmount", "monthlyServiceCurrency" }.Contains(f.Key)).ToList();
                foreach (var f in fields) table.Headers.Add(f.Label);
                bool derivedCurrency = FilterAvailable("currency") && !fields.Any(f => f.Key == "currency");
                if (derivedCurrency) table.Headers.Add("Валюта");
                if (Catalog.IsActSection(current)) table.Headers.AddRange(new[] { "Зачтено", "Общий остаток", "Удержано", "К оплате", "Оплата" });
                if (current == "invoice") table.Headers.Add("Оплата по акту");
                if (Catalog.IsPaymentSection(current)) table.Headers.AddRange(new[] { "Белая часть", "Серая часть", "Зачтено в этом разделе", "Аванс в этом разделе" });
                if (current == "contract") table.Headers.AddRange(new[] { "С допсоглашениями", "Закрыто актами", "Осталось работ" });
                if (current == "estimate") table.Headers.Add("Сумма сметы");
                if (current == "work") table.Headers.Add("Сумма");
                if (current == "project") table.Headers.AddRange(new[] { "Выручка (" + Curr + ")", "Затраты (" + Curr + ")", "Прибыль (" + Curr + ")" });
                table.Headers.Add("В архиве");
                foreach (var r in store.Records.Where(x => MatchesCurrentSection(x) && (archived.Checked || x.Get("archived") != "true")).Where(x => Match(x, true) && MatchesSearch(x)).OrderByDescending(x => x.Get("date")).ThenBy(x => x.Get("number")))
                {
                    var row = fields.Select(f => Value(r, f)).ToList();
                    if (derivedCurrency) row.Add(Effective(r).Get("currency"));
                    if (Catalog.IsActSection(current)) row.AddRange(new[] { F(store.AllocatedToAct(r.Id)), F(store.ActGrossDue(r.Id)), F(store.ActiveHolds(r.Id)), F(store.ActDue(r.Id)), Status(r) });
                    if (current == "invoice") { var a = store.Find(r.Get("actId")); row.Add(a == null ? "Акт не указан" : Status(a)); }
                    if (Catalog.IsPaymentSection(current))
                    {
                        PaymentSplit parts = ViewPaymentParts(r);
                        decimal sectionAmount = current == Catalog.GrayPaymentSection ? parts.GreyAmount : parts.WhiteAmount;
                        decimal allocatedPart = current == Catalog.GrayPaymentSection ? parts.AllocatedGrey : parts.AllocatedWhite;
                        row.AddRange(new[] { F(parts.WhiteAmount), F(parts.GreyAmount), F(allocatedPart), F(sectionAmount - allocatedPart) });
                    }
                    if (current == "contract") row.AddRange(new[] { F(store.EffectiveContractAmount(r.Id)), F(store.ContractActTotal(r.Id)), F(store.EffectiveContractAmount(r.Id) - store.ContractActTotal(r.Id)) });
                    if (current == "estimate") row.Add(F(store.EstimateTotal(r.Id)));
                    if (current == "work") row.Add(F(Money(r)));
                    if (current == "project") { var p = ProjectTotals(r.Id, false); row.AddRange(new[] { F(p[0]), F(p[1]), F(p[0] - p[1]) }); }
                    row.Add(r.Get("archived") == "true" ? "Да" : "");
                    table.Ids.Add(r.Id); table.Rows.Add(row);
                }
            }
            grid.Columns.Clear(); grid.Rows.Clear();
            foreach (var h in table.Headers)
            {
                int column = grid.Columns.Add("c" + grid.Columns.Count, h);
                if (IsDateColumn(h)) grid.Columns[column].Tag = "date";
            }
            if (Catalog.IsPaymentSection(current))
            {
                string[] priority = { "Дата", "Номер документа", "Номер", "Контрагент", "Сумма", "Белая часть", "Серая часть", "Валюта", "Акт вне", "Акт" };
                int displayIndex = 0;
                foreach (string header in priority)
                {
                    var column = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.HeaderText == header);
                    if (column != null) column.DisplayIndex = displayIndex++;
                }
            }
            int visible = 0;
            for (int i = 0; i < table.Rows.Count; i++)
            {
                int n = grid.Rows.Add(table.Rows[i].Cast<object>().ToArray()); grid.Rows[n].Tag = table.Ids[i]; visible++;
                var r = store.Find(table.Ids[i]);
                if (r != null && r.Kind == "act" && r.Get("status") == "signed" && store.ActDue(r.Id) > 0 && r.Get("dueDate") != "" && String.CompareOrdinal(r.Get("dueDate"), DateTime.Today.ToString("yyyy-MM-dd")) < 0) grid.Rows[n].DefaultCellStyle.ForeColor = Color.Firebrick;
                if (r != null && r.Get("archived") == "true") grid.Rows[n].DefaultCellStyle.ForeColor = Color.Gray;
            }
            if (sortedDateHeader != null)
            {
                var column = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.HeaderText == sortedDateHeader && Object.Equals(c.Tag, "date"));
                if (column != null) grid.Sort(column, dateSortDirection);
            }
            count.Text = (visible == 0 ? "Нет записей по выбранным фильтрам. Нажмите «Сбросить», чтобы начать заново." : "Строк: " + visible)
                + (FilterAvailable("currency") ? "    ·    " + (Curr == "Все валюты" ? Curr : "Валюта: " + Curr) : "")
                + (FilterAvailable("dates") ? "    ·    " + PeriodText() : "");
            if (current == "work") hint.Text = "Период — по дате сметы. Поиск учитывает наименование работ, смету, контрагента и объект.";
            if (current == "audit") hint.Text = "Поиск по описанию, пользователю и разделу. Период — по местному времени Минска.";
            if (current == "reports" && search.Text.Trim() != "") hint.Text += " Поиск применяется до расчёта итогов.";
        }
        private string Status(Record r)
        {
            if (r.Get("status") != "signed") return r.Get("status") == "cancelled" ? "Отменён" : "Ожидает подписи";
            if (store.ActGrossDue(r.Id) == 0) return "Оплачен";
            if (store.ActDue(r.Id) == 0) return "Удержание";
            return store.AllocatedToAct(r.Id) > 0 ? "Частично оплачен" : "Не оплачен";
        }
        internal Record NewRecordForCurrentSection()
        {
            var schema = Catalog.Get(current); var record = new Record(schema == null ? current : schema.Kind);
            if (current == Catalog.ExternalActSection) record.Set("accountingScope", "external");
            if (Catalog.IsPaymentSection(current)) record.Set("paymentScope", current == Catalog.GrayPaymentSection ? "external" : "standard");
            if (schema == null) return record;
            if (schema.Fields.Any(f => f.Key == "currency")) record.Set("currency", Curr == "Все валюты" || Curr == "" ? BaseCurrency : Curr);
            if (schema.Fields.Any(f => f.Key == "partyId") && PartyId != "") record.Set("partyId", PartyId);
            if (schema.Fields.Any(f => f.Key == "projectId") && ProjectId != "" && ProjectId != "__none__") record.Set("projectId", ProjectId);
            if (schema.Fields.Any(f => f.Key == "direction") && Dir != "") record.Set("direction", Dir);
            return record;
        }
        private void Add()
        {
            if (Catalog.IsActSection(current)) { Navigate(current == Catalog.ExternalActSection ? CreateExternalActSection : "create-act"); return; }
            var r = NewRecordForCurrentSection();
            using (var editor = new RecordEditor(store, Catalog.Get(current), r)) if (editor.ShowDialog(this) == DialogResult.OK) { RefillParties(); if (r.Kind == "payment") FocusRecord(editor.ResultRecord); else RefreshView(); if (r.Kind == "party") RefreshMonthlyServiceAutomation(); }
        }
        private void Edit()
        {
            if (current == "audit") { ShowAudit(); return; }
            var r = Selected(); if (r == null) return;
            if (r.Kind == "act" && r.Get("workItems") != "")
            {
                using (var editor = new HourlyActEditor(store, r))
                    if (editor.ShowDialog(this) == DialogResult.OK) FocusRecord(editor.ResultRecord);
                return;
            }
            var schema = Catalog.Get(SectionForRecord(r)); if (schema == null) return;
            using (var editor = new RecordEditor(store, schema, r)) if (editor.ShowDialog(this) == DialogResult.OK) { RefillParties(); if (r.Kind == "payment") FocusRecord(editor.ResultRecord); else RefreshView(); if (r.Kind == "party") RefreshMonthlyServiceAutomation(); }
        }
        private void FocusRecord(Record r)
        {
            Navigate(SectionForRecord(r)); refreshing = true; ResetFilters(); archived.Checked = r.Get("archived") == "true";
            var value = Effective(r).Get("currency"); if (currency.Items.Contains(value)) currency.SelectedItem = value;
            refreshing = false; RefreshView(); SelectId(r.Id);
        }
        private void SelectId(string id) { foreach (DataGridViewRow row in grid.Rows) if (Convert.ToString(row.Tag) == id) { grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells[0]; break; } }
        private void Archive()
        {
            var r = Selected(); if (r == null) return;
            if (r.Get("archived") == "true") { r.Fields["archived"] = "false"; store.Upsert(r); }
            else if (MessageBox.Show(this, "Перенести запись в архив? Связанные и финансовые данные сохраняются.", "Архив", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) store.Archive(r.Id);
            RefillParties(); RefreshView(); if (r.Kind == "party") RefreshMonthlyServiceAutomation();
        }
        private void Allocate()
        {
            using (var dlg = new AllocationDialog(store, Selected())) if (dlg.ShowDialog(this) == DialogResult.OK) RefreshView();
        }
        private void Attach()
        {
            var r = Selected(); if (r == null) throw new InvalidOperationException("Выберите запись для прикрепления файла.");
            using (var dialog = new OpenFileDialog { Title = "Выберите документ", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var doc = r.Kind == "document" ? r.Clone() : new Record { Kind = "document" };
                if (r.Kind != "document")
                {
                    foreach (var key in new[] { "partyId", "projectId", "contractId", "actId" }) if (r.Get(key) != "") doc.Fields[key] = r.Get(key);
                    if (r.Kind == "act") doc.Fields["actId"] = r.Id;
                    if (r.Kind == "party") doc.Fields["partyId"] = r.Id;
                    if (r.Kind == "project") doc.Fields["projectId"] = r.Id;
                    if (r.Kind == "contract") doc.Fields["contractId"] = r.Id;
                    doc.Fields["title"] = Path.GetFileName(dialog.FileName); doc.Fields["date"] = DateTime.Today.ToString("yyyy-MM-dd"); doc.Fields["status"] = "draft";
                }
                doc.Fields["filePath"] = store.ImportAttachment(dialog.FileName); if (doc.Get("status") == "missing") doc.Fields["status"] = "draft";
                store.Upsert(doc); MessageBox.Show(this, "Файл сохранён в разделе «Документы».", "Документ"); RefreshView();
            }
        }
        private void OpenAttachment()
        {
            var r = Selected(); if (r == null || r.Get("filePath") == "") throw new InvalidOperationException("У записи нет прикреплённого файла.");
            Open(AttachmentSafety.ResolveForOpen(store, r.Get("filePath")));
        }
        private void Settings()
        {
            var r = store.Of("settings").FirstOrDefault() ?? new Record { Kind = "settings" };
            using (var editor = new RecordEditor(store, Catalog.Get("settings"), r)) if (editor.ShowDialog(this) == DialogResult.OK) { if (currency.Items.Contains(editor.ResultRecord.Get("currency"))) currency.SelectedItem = editor.ResultRecord.Get("currency"); RefreshView(); foreach (var pending in actComposers.Values) pending.RefreshDefaults(); }
        }
        internal TableData BuildExportTable()
        {
            var headers = table.Headers.ToList(); var schema = Catalog.Get(current);
            var extra = schema == null ? new List<FieldDef>() : schema.Fields.Where(f => !headers.Contains(f.Label) && f.Key != "filePath").ToList();
            headers.AddRange(extra.Select(f => f.Label));
            var refs = schema == null ? new List<FieldDef>() : schema.Fields.Where(f => f.Type == "ref").ToList();
            if (schema != null) headers.Add("Идентификатор записи"); headers.AddRange(refs.Select(f => f.Label + " (ID)"));
            if (Catalog.IsActSection(current)) headers.Add("Учет акта");
            var visible = new List<List<string>>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                var values = row.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value)).ToList();
                var r = store.Find(Convert.ToString(row.Tag)); if (r != null && schema != null) { values.AddRange(extra.Select(f => Value(r, f))); values.Add(r.Id); values.AddRange(refs.Select(f => Effective(r).Get(f.Key))); }
                if (Catalog.IsActSection(current)) values.Add(r != null && Store.IsExternalAct(r) ? "external" : "standard");
                visible.Add(values);
            }
            return new TableData { Title = table.Title, Headers = headers, Rows = visible };
        }
        private void Export()
        {
            TableData export = BuildExportTable();
            using (var dialog = new SaveFileDialog { Filter = "Книга Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", FileName = SafeName(title.Text) + ".xlsx" })
            { if (dialog.ShowDialog(this) == DialogResult.OK) { if (Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)) TableIO.WriteCsv(dialog.FileName, export.Headers, export.Rows); else TableIO.WriteXlsx(dialog.FileName, export.Headers, export.Rows); MessageBox.Show(this, "Выгрузка сохранена.", "Excel"); } }
        }
        private void Import()
        {
            var schema = Catalog.Get(current);
            if (schema == null) return;
            using (var dlg = new OpenFileDialog { Filter = "Excel и CSV (*.xlsx;*.csv)|*.xlsx;*.csv", Title = "Импорт в раздел «" + schema.Title + "»" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var incoming = TableIO.Read(dlg.FileName);
                using (var preview = new ImportDialog(store, schema, incoming, !FilterAvailable("currency") || Curr == "Все валюты" ? BaseCurrency : Curr, current == Catalog.ExternalActSection, current == Catalog.GrayPaymentSection)) if (preview.ShowDialog(this) == DialogResult.OK) { RefillParties(); RefreshView(); if (current == "party") RefreshMonthlyServiceAutomation(); }
            }
        }
        private void Backup()
        {
            using (var dlg = new SaveFileDialog { Filter = "Резервная копия (*.zip)|*.zip", FileName = "Акты-и-оплаты-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".zip" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                ValidateSnapshot(store.DataDir); BackupArchive.Create(store.DataDir, dlg.FileName); MessageBox.Show(this, "База и прикреплённые файлы сохранены в резервную копию.", "Резервная копия");
            }
        }
        private void Restore()
        {
            using (var dlg = new OpenFileDialog { Filter = "Резервная копия (*.zip)|*.zip", Title = "Восстановление базы" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (MessageBox.Show(this, "Текущая база будет заменена выбранной резервной копией. Сохранить текущую базу и продолжить?", "Восстановление", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                string before = Path.Combine(Path.GetDirectoryName(store.DataDir), "WorkLedger-before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                BackupArchive.Create(store.DataDir, before);
                BackupArchive.Restore(dlg.FileName, store.DataDir, ValidateSnapshot);
                ReplaceStore(new Store(store.DataDir)); RefreshMonthlyServiceAutomation();
                MessageBox.Show(this, "База восстановлена.\nПредыдущая база сохранена:\n" + before, "Восстановление");
            }
        }
        private void EnsureTaxesPage()
        {
            if (taxesPage != null) return;
            taxesPage = new QuarterlyTaxesPage(store);
            taxesPage.PaymentRequested += delegate(string paymentId)
            {
                Guard(delegate { var payment = store.Find(paymentId); if (payment != null) FocusRecord(payment); });
            };
            UiStyle.ApplyTo(taxesPage); UiStyle.SetAnimations(taxesPage, interfacePreferences.AnimationsEnabled);
            body.Controls.Add(taxesPage); taxesPage.BringToFront();
        }
        internal void ReplaceStore(Store replacement)
        {
            if (replacement == null) throw new ArgumentNullException("replacement");
            viewTransition.Cancel();
            bool hadTaxesPage = taxesPage != null;
            int taxYear = hadTaxesPage ? taxesPage.SelectedYear : 0;
            int taxQuarter = hadTaxesPage ? taxesPage.SelectedQuarter : 0;
            string taxCurrency = hadTaxesPage ? taxesPage.SelectedCurrency : null;
            if (hadTaxesPage)
            {
                var previousPage = taxesPage; taxesPage = null;
                body.Controls.Remove(previousPage); previousPage.Dispose();
            }
            foreach (var pending in actComposers.Values) { body.Controls.Remove(pending); pending.Dispose(); }
            actComposers.Clear(); composer = null;
            store = replacement;
            RefillParties();
            if (hadTaxesPage || current == "taxes")
            {
                EnsureTaxesPage();
                if (hadTaxesPage) taxesPage.SetPeriod(taxYear, taxQuarter, taxCurrency);
                taxesPage.Visible = current == "taxes";
            }
            Navigate(current);
        }
        public static void ValidateSnapshot(string path)
        {
            var data = new Store(path);
            foreach (var doc in data.Records.Where(r => r.Kind == "document" && r.Get("filePath") != ""))
                if (!File.Exists(data.ResolveAttachment(doc.Get("filePath"))))
                    throw new InvalidDataException("В копии не хватает вложения документа «" + doc.Get("title") + "». База не заменена.");
        }
        private static string SafeName(string value) { foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_'); return value; }
        private static void Open(string path) { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        private string PrintPath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "WorkLedger-Print"); Directory.CreateDirectory(dir); return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".html");
        }
        private void PrintTable()
        {
            string path = PrintPath(); var visible = grid.Rows.Cast<DataGridViewRow>().Select(r => r.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value)).ToList()).ToList();
            PrintReport.Write(path, table.Title, table.Headers, visible, "Валюта: " + Curr + ". Дата отчёта: " + DateTime.Today.ToString("dd.MM.yyyy") + ". " + PeriodText()); Open(path);
        }
        internal Dictionary<string, string> BuildDocumentDetails(Record r)
        {
            var details = new Dictionary<string, string>();
            var own = Store.GetActSnapshot(r, "organizationSnapshot") ?? store.Of("settings").FirstOrDefault();
            string organizationDetails = PrintReport.OrganizationDetails(own);
            if (!String.IsNullOrWhiteSpace(organizationDetails)) details["Организация"] = organizationDetails;
            foreach (var f in Catalog.Get(r.Kind).Fields.Where(f => f.Key != "filePath")) if (r.Get(f.Key) != "") details[f.Label] = Value(r, f);
            var cp = Store.GetActSnapshot(r, "partySnapshot") ?? store.Find(r.Get("partyId"));
            string counterpartyDetails = PrintReport.OrganizationDetails(cp);
            if (!String.IsNullOrWhiteSpace(counterpartyDetails)) details["Реквизиты контрагента"] = counterpartyDetails;
            return details;
        }
        private void EditActItems()
        {
            var act = Selected();
            if (act == null || act.Kind != "act") throw new InvalidOperationException("Выберите акт в таблице.");
            if (act.Get("workItems") != "")
            {
                using (var hourly = new HourlyActEditor(store, act))
                    if (hourly.ShowDialog(this) == DialogResult.OK) FocusRecord(hourly.ResultRecord);
                return;
            }
            using (var editor = new ActItemsEditor(store, act))
                if (editor.ShowDialog(this) == DialogResult.OK) { RefreshView(); SelectId(editor.ResultRecord.Id); }
        }

        private void ExportActWord()
        {
            var act = Selected();
            if (act == null || act.Kind != "act") throw new InvalidOperationException("Выберите акт в таблице.");
            string number = String.Concat(act.Get("number").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (number.Length > 60) number = number.Substring(0, 60);
            using (var dialog = new SaveFileDialog
            {
                Title = "Сохранить акт Word", Filter = "Документ Word (*.docx)|*.docx", DefaultExt = "docx",
                AddExtension = true, OverwritePrompt = true, FileName = "Акт-" + number + ".docx"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ActDocument.Export(store, act, dialog.FileName);
                MessageBox.Show(this, "Акт Word сохранён:\n" + dialog.FileName, "Акт Word", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        private void ShowAllActs()
        {
            ShowAllActs(false);
        }
        private void ShowAllActs(bool externalActs)
        {
            Navigate(externalActs ? Catalog.ExternalActSection : "act"); refreshing = true; ResetFilters(); archived.Checked = true; currency.SelectedItem = "Все валюты";
            refreshing = false; RefreshView();
        }
        private void PreviewAct()
        {
            if (composer == null) return;
            var draft = composer.BuildDraft();
            if (draft.Get("number") == "") draft.Set("number", store.NextActNumber + " (предпросмотр)");
            string path = PrintPath(); ActHtmlDocument.WriteHtml(path, store, draft); Open(path);
        }
        private void ExportActDocument()
        {
            var act = Selected();
            if (act == null || act.Kind != "act") throw new InvalidOperationException("Выберите акт для выгрузки.");
            using (var dialog = new SaveFileDialog { Filter = "Книга Excel (*.xlsx)|*.xlsx", FileName = SafeName("Акт-" + act.Get("number")) + ".xlsx" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                { HourlyActDocuments.WriteXlsx(dialog.FileName, store, act); MessageBox.Show(this, "Акт сохранён в Excel.", "Excel"); }
        }
        private void PrintDocument()
        {
            var r = Selected(); if (r == null) return;
            string path = PrintPath();
            if (r.Kind == "act") { ActHtmlDocument.WriteHtml(path, store, r); Open(path); return; }
            var details = BuildDocumentDetails(r);
            var headers = new List<string>(); var rows = new List<List<string>>();
            if (r.Kind == "estimate")
            {
                headers.AddRange(new[] { "Работа", "Ед.", "Количество", "Цена", "Сумма" });
                foreach (var w in store.Of("work").Where(w => w.Get("estimateId") == r.Id)) rows.Add(new[] { w.Get("description"), w.Get("unit"), w.Get("qty"), F(Num(w.Get("price"))), F(Money(w)) }.ToList());
                details["Итого"] = F(store.EstimateTotal(r.Id)) + " " + r.Get("currency");
            }
            PrintReport.WriteDocument(path, Catalog.Get(r.Kind).Singular + " № " + r.Get("number"), details, headers, rows, "Исполнитель ____________________    Заказчик ____________________\nПечатный шаблон: проверьте реквизиты и условия перед подписанием."); Open(path);
        }


        private Record Effective(Record r)
        {
            var result = r.Clone();
            for (int pass = 0; pass < 4; pass++) foreach (string link in new[] { "actId", "contractId", "estimateId", "projectId" })
            {
                var parent = store.Find(result.Get(link)); if (parent == null) continue;
                foreach (string key in new[] { "partyId", "projectId", "contractId", "currency", "direction" })
                    if (result.Get(key) == "" && parent.Get(key) != "" && !(r.Kind == "expense" && key == "partyId" && parent.Kind == "project")) result.Fields[key] = parent.Get(key);
                if (result.Kind == "work" && parent.Kind == "estimate" && result.Get("date") == "") result.Set("date", parent.Get("date"));
            }
            if (result.Kind == "expense") result.Set("direction", "expense");
            return result;
        }
        private IEnumerable<Record> ProjectRecords(string kind, bool dates)
        {
            return store.Records.Where(r => r.Kind == kind).Select(Effective).Where(r => r.Get("currency") == Curr && MatchesProject(r)).Where(r =>
            {
                if (!dates || !DateRangeActive) return true;
                DateTime d; return DateTime.TryParseExact(r.Get("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) && InPeriod(d);
            });
        }
        private static string AuditTime(string utc)
        {
            DateTime d; return DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d) ? d.ToUniversalTime().AddHours(3).ToString("dd.MM.yyyy HH:mm:ss") + " (Минск)" : utc;
        }
        private void ShowAudit()
        {
            if (grid.SelectedRows.Count == 0) return;
            var a = store.Audit.FirstOrDefault(x => x.Id == Convert.ToString(grid.SelectedRows[0].Tag)); if (a == null) return;
            using (var window = new Form { Text = "Изменение записи", Width = 850, Height = 620, StartPosition = FormStartPosition.CenterParent, Font = Font })
            {
                var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = Font, Text = AuditTime(a.Date) + " · " + a.User + "\r\n" + a.Summary + "\r\n\r\nДО ИЗМЕНЕНИЯ\r\n" + a.Before + "\r\n\r\nПОСЛЕ ИЗМЕНЕНИЯ\r\n" + a.After };
                window.Controls.Add(text); window.ShowDialog(this);
            }
        }
        internal Record BuildActFromEstimate(Record estimate)
        {
            if (estimate == null || estimate.Kind != "estimate") throw new InvalidOperationException("Выберите смету.");
            var works = store.Of("work").Where(w => w.Get("estimateId") == estimate.Id).ToList();
            if (works.Count == 0) throw new InvalidOperationException("Смета не содержит работ. Добавьте работы перед созданием акта.");
            var lines = works.Select(w => new ActPrintLine
            {
                Description = w.Get("description"), Unit = w.Get("unit"),
                Quantity = Store.Quantity(w.Get("qty")), Price = Store.ParseMoney(w.Get("price"))
            }).ToList();
            decimal sum = 0;
            foreach (var line in lines) sum += line.Amount;
            if (sum <= 0) throw new InvalidOperationException("Сумма работ в смете должна быть больше нуля.");
            var act = new Record("act"); act.Set("date", Store.DateString(DateTime.Today));
            foreach (string key in new[] { "partyId", "projectId", "contractId", "currency", "direction" }) act.Set(key, estimate.Get(key));
            act.Set("amount", Store.MoneyString(sum)); act.Set("status", "draft");
            act.Set("description", String.Join("\r\n", works.Select(w => w.Get("description") + " — " + w.Get("qty") + " " + w.Get("unit") + " × " + w.Get("price") + " = " + w.Get("amount"))));
            ActDocument.ValidateItems(act, lines);
            act.Set("printItemsJson", ActDocument.SerializeItems(lines));
            return act;
        }

        private void CreateAct()
        {
            var estimate = Selected(); if (estimate == null) return;
            var act = BuildActFromEstimate(estimate);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), act)) if (editor.ShowDialog(this) == DialogResult.OK) { Navigate("act"); SelectId(editor.ResultRecord.Id); }
        }        private void CreateInvoice()
        {
            var act = Selected(); if (act == null) return; var invoice = new Record("invoice");
            foreach (string key in new[] { "partyId", "projectId", "contractId", "currency", "direction", "amount" }) invoice.Set(key, act.Get(key));
            invoice.Set("actId", act.Id); invoice.Set("status", "draft");
            using (var editor = new RecordEditor(store, Catalog.Get("invoice"), invoice)) if (editor.ShowDialog(this) == DialogResult.OK) { Navigate("invoice"); SelectId(editor.ResultRecord.Id); }
        }
        private IEnumerable<Record> Financial(string kind, bool dates)
        {
            return store.Records.Where(r => r.Kind == kind).Select(Effective).Where(r => Match(r, dates))
                .Where(r => current != "reports" || report.SelectedIndex == 1 || MatchesSearch(r));
        }
        private decimal[] ProjectTotals(string projectId, bool dates)
        {
            var acts = ProjectRecords("act", dates).Where(r => r.Get("projectId") == projectId && r.Get("status") == "signed").ToList();
            decimal earned = acts.Where(r => r.Get("direction") == "income").Sum(r => Money(r));
            decimal costs = acts.Where(r => r.Get("direction") == "expense").Sum(r => Money(r)) + ProjectRecords("expense", dates).Where(r => r.Get("projectId") == projectId).Sum(r => Money(r));
            return new[] { earned, costs };
        }
        private string PeriodText()
        {
            if (!DateRangeActive) return "За всё время";
            if (!from.Checked) return "По " + to.Value.ToString("dd.MM.yyyy");
            if (!to.Checked) return "С " + from.Value.ToString("dd.MM.yyyy");
            return "Период: " + from.Value.ToString("dd.MM.yyyy") + " — " + to.Value.ToString("dd.MM.yyyy");
        }
        public TableData BuildReport()
        {
            if (from.Checked && to.Checked && from.Value.Date > to.Value.Date) throw new InvalidOperationException("Дата начала периода должна быть раньше даты окончания.");
            var t = new TableData { Title = Convert.ToString(report.SelectedItem) };
            if (report.SelectedIndex == 0)
            {
                t.Headers.AddRange(new[] { "Акт", "Дата", "Контрагент", "Объект", "Направление", "Сумма", "Зачтено", "Общий остаток", "Удержано", "К оплате", "Срок", "Состояние" });
                foreach (var r in Financial("act", true).Where(r => r.Get("status") == "signed" && store.ActGrossDue(r.Id) > 0).OrderBy(r => r.Get("dueDate")))
                    t.Add(r.Id, r.Get("number"), D(r.Get("date")), Label(r.Get("partyId")), Label(r.Get("projectId")), r.Get("direction") == "income" ? "Нам платят" : "Мы платим", F(Money(r)), F(store.AllocatedToAct(r.Id)), F(store.ActGrossDue(r.Id)), F(store.ActiveHolds(r.Id)), F(store.ActDue(r.Id)), D(r.Get("dueDate")), Status(r));
            }
            else if (report.SelectedIndex == 1)
            {
                t.Headers.AddRange(new[] { "Объект", "Выручка по актам", "Акты подрядчиков", "Прочие расходы", "Всего затрат", "Прибыль", "Поступило денег", "Выплачено денег" });
                foreach (var p in store.Records.Where(r => r.Kind == "project" && (PartyId == "" || r.Get("partyId") == PartyId) && MatchesProject(r) && MatchesSearch(r)))
                {
                    var acts = ProjectRecords("act", true).Where(r => r.Get("projectId") == p.Id && r.Get("status") == "signed").ToList();
                    decimal revenue = acts.Where(r => r.Get("direction") == "income").Sum(r => Money(r));
                    decimal sub = acts.Where(r => r.Get("direction") == "expense").Sum(r => Money(r)), exp = ProjectRecords("expense", true).Where(r => r.Get("projectId") == p.Id).Sum(r => Money(r));
                    var pay = ProjectRecords("payment", true).Where(r => r.Get("projectId") == p.Id).ToList();
                    t.Add(p.Id, p.Get("name"), F(revenue), F(sub), F(exp), F(sub + exp), F(revenue - sub - exp), F(pay.Where(r => r.Get("direction") == "income").Sum(r => Money(r))), F(pay.Where(r => r.Get("direction") == "expense").Sum(r => Money(r))));
                }
                Func<Record, bool> noProjectMatch = r => r.Get("projectId") == "" && (PartyId == "" || r.Get("partyId") == PartyId)
                    && (FilterText.Matches(new[] { "Без объекта" }, search.Text) || MatchesSearch(r));
                var noProject = ProjectRecords("act", true).Where(noProjectMatch).Where(r => r.Get("status") == "signed").ToList();
                var noProjectExpenses = ProjectRecords("expense", true).Where(noProjectMatch).ToList();
                var noProjectPayments = ProjectRecords("payment", true).Where(noProjectMatch).ToList();
                if (noProject.Count > 0 || noProjectExpenses.Count > 0 || noProjectPayments.Count > 0)
                {
                    decimal earned = noProject.Where(r => r.Get("direction") == "income").Sum(r => Money(r));
                    decimal subcontractors = noProject.Where(r => r.Get("direction") == "expense").Sum(r => Money(r));
                    decimal otherCosts = noProjectExpenses.Sum(r => Money(r));
                    t.Add("", "Без объекта", F(earned), F(subcontractors), F(otherCosts), F(subcontractors + otherCosts), F(earned - subcontractors - otherCosts),
                        F(noProjectPayments.Where(r => r.Get("direction") == "income").Sum(r => Money(r))), F(noProjectPayments.Where(r => r.Get("direction") == "expense").Sum(r => Money(r))));
                }
            }
            else if (report.SelectedIndex == 2)
            {
                t.Headers.AddRange(new[] { "Дата", "Документ", "Контрагент", "Нам должны (+)", "Мы должны (-)", "Изменение сальдо", "Сальдо" });
                var entries = new List<Tuple<Record, decimal>>();
                foreach (var r in Financial("act", false).Where(r => r.Get("status") == "signed"))
                    entries.Add(Tuple.Create(r, Money(r) * (r.Get("direction") == "income" ? 1 : -1)));
                foreach (var r in Financial("payment", false)) entries.Add(Tuple.Create(r, Money(r) * (r.Get("direction") == "income" ? -1 : 1)));
                decimal balance = 0;
                foreach (var e in entries.Where(e => from.Checked && String.CompareOrdinal(e.Item1.Get("date"), from.Value.ToString("yyyy-MM-dd")) < 0)) balance += e.Item2;
                if (from.Checked) t.Add("", from.Value.ToString("dd.MM.yyyy"), "Входящее сальдо", PartyId == "" ? "Все контрагенты" : Label(PartyId), "", "", "", F(balance));
                foreach (var e in entries.Where(e => Match(e.Item1, true)).OrderBy(e => e.Item1.Get("date")).ThenBy(e => e.Item1.Kind))
                {
                    balance += e.Item2;
                    t.Add(e.Item1.Id, D(e.Item1.Get("date")), (Store.IsExternalAct(e.Item1) ? "Акт вне " : e.Item1.Kind == "act" ? "Акт " : "Оплата ") + e.Item1.Get("number"), Label(e.Item1.Get("partyId")), e.Item2 > 0 ? F(e.Item2) : "", e.Item2 < 0 ? F(-e.Item2) : "", F(e.Item2), F(balance));
                }
                t.Add("", "", search.Text.Trim() == "" ? "Итоговое сальдо (включая авансы)" : "Итог по найденным документам (включая авансы)", "", "", "", "", F(balance));
            }
            else if (report.SelectedIndex == 3)
            {
                t.Headers.AddRange(new[] { "Договор", "Контрагент", "Объект", "Направление", "Первоначальная сумма", "С допсоглашениями", "Закрыто актами", "Осталось работ" });
                foreach (var r in Financial("contract", true).Where(r => r.Get("status") != "cancelled"))
                    t.Add(r.Id, r.Get("number"), Label(r.Get("partyId")), Label(r.Get("projectId")), r.Get("direction") == "income" ? "Нам платят" : "Мы платим", F(Money(r)), F(store.EffectiveContractAmount(r.Id)), F(store.ContractActTotal(r.Id)), F(store.EffectiveContractAmount(r.Id) - store.ContractActTotal(r.Id)));
            }
            else if (report.SelectedIndex == 4)
            {
                t.Headers.AddRange(new[] { "Дата", "Платёж", "Контрагент", "Объект", "Направление", "Сумма", "Зачтено в акты", "Осталось авансом" });
                decimal incoming = 0, outgoing = 0;
                foreach (var r in Financial("payment", true).OrderBy(r => r.Get("date")))
                {
                    bool inc = r.Get("direction") == "income"; if (inc) incoming += Money(r); else outgoing += Money(r);
                    t.Add(r.Id, D(r.Get("date")), r.Get("number"), Label(r.Get("partyId")), Label(r.Get("projectId")), inc ? "Поступление" : "Выплата", F(Money(r)), F(store.AllocatedFromPayment(r.Id)), F(store.PaymentUnallocated(r.Id)));
                }
                t.Add("", "", "Всего поступило: " + F(incoming), "", "", "Всего выплачено: " + F(outgoing), "Остаток потока: " + F(incoming - outgoing), "", "");
            }
            else if (report.SelectedIndex == 6)
            {
                t.Headers.AddRange(new[] { "Акт", "Дата", "Контрагент", "Объект", "Договор", "Направление", "Сумма", "Валюта", "Дата подписания" });
                decimal incoming = 0, outgoing = 0;
                foreach (var r in Financial("act", true).Where(r => Store.IsTaxAct(r) && r.Get("status") == "signed" && r.Get("archived") != "true").OrderBy(r => r.Get("date")).ThenBy(r => r.Get("number")))
                {
                    bool income = r.Get("direction") == "income";
                    if (income) incoming += Money(r); else outgoing += Money(r);
                    t.Add(r.Id, r.Get("number"), D(r.Get("date")), Label(r.Get("partyId")), Label(r.Get("projectId")), Label(r.Get("contractId")), income ? "Нам платят" : "Мы платим", F(Money(r)), r.Get("currency"), D(r.Get("signedDate")));
                }
                t.Add("", "Итого", "", "", "", "", "Доход: " + F(incoming) + "; расход: " + F(outgoing), F(incoming - outgoing), Curr, "");
            }
            else
            {
                t.Headers.AddRange(new[] { "Раздел", "Документ", "Контрагент", "Объект", "Срок / дата", "Задача" });
                foreach (var r in Financial("act", true).Where(r => r.Get("archived") != "true" && (r.Get("status") == "draft" || r.Get("status") == "sent")))
                    t.Add(r.Id, Store.IsExternalAct(r) ? "Акт вне" : "Акт", r.Get("number"), Label(r.Get("partyId")), Label(r.Get("projectId")), D(r.Get("date")), r.Get("status") == "sent" ? "Получить подпись" : "Подготовить и отправить");
                foreach (var r in Financial("document", true).Where(r => r.Get("archived") != "true" && r.Get("status") != "signed"))
                    t.Add(r.Id, "Документ", r.Get("title"), Label(r.Get("partyId")), Label(r.Get("projectId")), D(r.Get("date")), r.Get("status") == "missing" ? "Добавить недостающий документ" : r.Get("status") == "sent" ? "Получить подпись" : "Отправить / подписать");
                foreach (var r in Financial("contract", true).Where(r => r.Get("status") == "active" && r.Get("dueDate") != "" && String.CompareOrdinal(r.Get("dueDate"), DateTime.Today.AddDays(7).ToString("yyyy-MM-dd")) <= 0))
                    t.Add(r.Id, "Договор", r.Get("number"), Label(r.Get("partyId")), Label(r.Get("projectId")), D(r.Get("dueDate")), "Проверить срок договора");
                foreach (var r in Financial("invoice", true).Where(r => r.Get("status") == "draft"))
                    t.Add(r.Id, "Счёт", r.Get("number"), Label(r.Get("partyId")), Label(r.Get("projectId")), D(r.Get("dueDate")), "Выставить счёт");
            }
            return t;
        }
        private void RenderOverview()
        {
            foreach (Control control in overview.Controls.Cast<Control>().ToList()) control.Dispose(); overview.Controls.Clear();
            var canvas = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = overview.Width - 24 };
            overview.Controls.Add(canvas);
            var acts = Financial("act", false).Where(r => r.Get("status") == "signed").ToList();
            var pay = Financial("payment", false).ToList();
            decimal earned = acts.Where(r => r.Get("direction") == "income").Sum(r => Money(r));
            decimal spent = acts.Where(r => r.Get("direction") == "expense").Sum(r => Money(r)) + Financial("expense", false).Sum(r => Money(r));
            var cards = new FlowLayoutPanel { AutoSize = true, Width = Math.Max(500, overview.ClientSize.Width - 22), WrapContents = true, Margin = new Padding(0, 14, 0, 0) };
            cards.Controls.Add(Card("Работы для заказчиков", F(earned) + " " + Curr, "Подписанные акты"));
            cards.Controls.Add(Card("С/Р", F(pay.Where(r => r.Get("direction") == "income").Sum(r => Money(r))) + " " + Curr, "Белые + серые поступления, включая авансы"));
            cards.Controls.Add(Card("Долг заказчиков", F(acts.Where(r => r.Get("direction") == "income").Sum(r => store.ActGrossDue(r.Id))) + " " + Curr, "Включает активные удержания"));
            cards.Controls.Add(Card("Долг подрядчикам", F(acts.Where(r => r.Get("direction") == "expense").Sum(r => store.ActGrossDue(r.Id))) + " " + Curr, "Подписанные акты минус зачтённые оплаты"));
            cards.Controls.Add(Card("Затраты", F(spent) + " " + Curr, "Акты подрядчиков + дополнительные расходы"));
            cards.Controls.Add(Card("Прибыль по работам", F(earned - spent) + " " + Curr, "Выручка минус затраты"));
            int columns = cards.Width >= 1050 ? 3 : 2;
            foreach (Control card in cards.Controls) card.Width = Math.Max(240, (cards.Width - 12 * columns) / columns);
            canvas.Controls.Add(cards);
            var header = new Label { Text = "Сроки оплаты и документы", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 20, 0, 10) };
            canvas.Controls.Add(header);
            int reminders = 0;
            foreach (var r in acts.Where(r => store.ActDue(r.Id) > 0 && r.Get("dueDate") != "" && String.CompareOrdinal(r.Get("dueDate"), DateTime.Today.AddDays(7).ToString("yyyy-MM-dd")) <= 0).OrderBy(r => r.Get("dueDate")).Take(15))
            {
                var captured = r;
                string late = String.CompareOrdinal(r.Get("dueDate"), DateTime.Today.ToString("yyyy-MM-dd")) < 0 ? "Просрочено" : "Скоро срок";
                var b = Button(late + " · " + D(r.Get("dueDate")) + " · акт " + r.Get("number") + " · " + Label(r.Get("partyId")) + " · " + F(store.ActDue(r.Id)) + " " + Curr, delegate { FocusRecord(captured); });
                b.ForeColor = late == "Просрочено" ? Color.Firebrick : Ink; canvas.Controls.Add(b); reminders++;
            }
            int missing = Financial("document", false).Count(r => r.Get("archived") != "true" && r.Get("status") == "missing");
            int pending = Financial("act", false).Count(r => r.Get("archived") != "true" && r.Get("status") == "sent");
            if (missing > 0 || pending > 0) { canvas.Controls.Add(Button("Недостающие документы: " + missing + " · Акты ожидают подписи: " + pending, delegate { Navigate("reports"); report.SelectedIndex = 5; })); reminders++; }
            if (reminders == 0) canvas.Controls.Add(new Label { Text = "Ближайших сроков оплаты и недостающих документов нет.", AutoSize = true, ForeColor = Color.FromArgb(93, 107, 124) });
            var tips = new Label { Text = "Создайте акт, добавьте оплату и следите за расчётами здесь.\nРазделы настраиваются через «Меню → Интерфейс». Резервная копия тоже доступна в меню.", AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(0, 22, 0, 0), ForeColor = Color.FromArgb(93, 107, 124) };
            canvas.Controls.Add(tips); count.Text = "Валюта: " + Curr + "    ·    Показатели за всё время";
        }
        private Control Card(string name, string value, string subtitle)
        {
            var p = new UiPanel { Width = 310, Height = 144, Margin = new Padding(0, 0, 12, 12), Padding = new Padding(18), CornerRadius = 12 };
            var caption = new Label { Text = name, Left = 18, Top = 17, Width = 274, Height = 25, ForeColor = UiStyle.Muted, Font = new Font("Segoe UI", 10) };
            var number = new Label { Text = value, Left = 18, Top = 48, Width = 274, Height = 39, Font = new Font("Segoe UI", value.Length > 23 ? 16 : 22, FontStyle.Bold),
                ForeColor = name == "Прибыль по работам" ? UiStyle.Blue : Accent, AutoEllipsis = true };
            var description = new Label { Text = subtitle, Left = 18, Top = 101, Width = 274, Height = 34, ForeColor = UiStyle.Muted, Font = new Font("Segoe UI", 8.5F) };
            p.Controls.Add(caption); p.Controls.Add(number); p.Controls.Add(description);
            p.SizeChanged += delegate { caption.Width = number.Width = description.Width = Math.Max(100, p.ClientSize.Width - 36); };
            return p;
        }

        public void SelectReport(int index) { Navigate("reports"); report.SelectedIndex = index; RefreshView(); }
    }
    internal sealed class Choice
    {
        public string Id; private string caption;
        public Choice(string id, string text) { Id = id; caption = text; }
        public override string ToString() { return caption; }
    }
    public sealed class AllocationDialog : Form
    {
        private Store store;




        private ComboBox payments = new ComboBox(), acts = new ComboBox();
        private TextBox amount = new TextBox();
        private Label help = new Label();
        private ListBox history = new ListBox();
        public AllocationDialog(Store data, Record initial)
        {
            store = data; Text = "Зачёт оплаты в акт"; Width = 660; Height = 515; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
            MinimumSize = MaximumSize = Size;
            var label1 = new Label { Text = "Платёж (незачтённая часть = аванс)", Left = 20, Top = 20, Width = 590 };
            payments.SetBounds(20, 48, 600, 30); payments.DropDownStyle = ComboBoxStyle.DropDownList;
            var label2 = new Label { Text = "Подписанный акт того же контрагента, направления и валюты", Left = 20, Top = 90, Width = 610 };
            acts.SetBounds(20, 118, 600, 30); acts.DropDownStyle = ComboBoxStyle.DropDownList;
            amount.SetBounds(20, 182, 185, 30);
            help.SetBounds(220, 179, 400, 50); help.AutoSize = false;
            var label3 = new Label { Text = "Сумма зачёта", Left = 20, Top = 155, Width = 180 };
            history.SetBounds(20, 251, 600, 100); history.HorizontalScrollbar = true;
            var label4 = new Label { Text = "Зачёты выбранного платежа", Left = 20, Top = 225, Width = 600 };
            var reverse = new System.Windows.Forms.Button { Text = "Отменить выбранный зачёт", Left = 300, Top = 361, Width = 320, Height = 32 };
            reverse.Click += delegate { try { if (history.SelectedItem == null) return; store.ReverseAllocation(((Choice)history.SelectedItem).Id); DialogResult = DialogResult.OK; Close(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Отмена зачёта"); } };
            Controls.Add(reverse);
            var save = new System.Windows.Forms.Button { Text = "Зачесть", Left = 445, Top = 420, Width = 175, Height = 35 };
            var cancel = new System.Windows.Forms.Button { Text = "Закрыть", Left = 20, Top = 420, Width = 110, Height = 35, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { label1, payments, label2, acts, label3, amount, help, label4, history, save, cancel }); CancelButton = cancel;
            foreach (var p in store.Records.Where(r => r.Kind == "payment" && r.Get("archived") != "true")) payments.Items.Add(new Choice(p.Id, Catalog.Display(store, p) + " · аванс " + Store.MoneyString(store.PaymentUnallocated(p.Id)) + " " + p.Get("currency")));
            payments.SelectedIndexChanged += delegate { FillActs(initial); };
            acts.SelectedIndexChanged += delegate { UpdateHint(); };
            save.Click += delegate
            {
                try
                {
                    if (payments.SelectedItem == null || acts.SelectedItem == null) throw new InvalidOperationException("Выберите платёж и акт.");
                    store.Allocate(((Choice)payments.SelectedItem).Id, ((Choice)acts.SelectedItem).Id, Store.ParseMoney(amount.Text)); DialogResult = DialogResult.OK; Close();
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Зачёт оплаты", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            if (payments.Items.Count > 0) payments.SelectedIndex = 0;
            if (initial != null && initial.Kind == "payment") Select(payments, initial.Id);
            if (initial != null && initial.Kind == "act")
            {
                for (int i = 0; i < payments.Items.Count; i++) { var p = store.Find(((Choice)payments.Items[i]).Id); if (p.Get("partyId") == initial.Get("partyId") && p.Get("currency") == initial.Get("currency") && p.Get("direction") == initial.Get("direction") && store.PaymentUnallocated(p.Id) > 0 && Compatible(p, initial)) { payments.SelectedIndex = i; break; } }
                Select(acts, initial.Id);
            }
        }
        private static void Select(ComboBox box, string id) { for (int i = 0; i < box.Items.Count; i++) if (((Choice)box.Items[i]).Id == id) { box.SelectedIndex = i; break; } }
        private void FillActs(Record initial)
        {
            acts.Items.Clear(); history.Items.Clear(); if (payments.SelectedItem == null) return;
            var p = store.Find(((Choice)payments.SelectedItem).Id);
            foreach (var a in store.Records.Where(r => r.Kind == "act" && r.Get("status") == "signed" && r.Get("archived") != "true" && r.Get("partyId") == p.Get("partyId") && r.Get("currency") == p.Get("currency") && r.Get("direction") == p.Get("direction") && store.ActDue(r.Id) > 0 && Compatible(p, r)))
                acts.Items.Add(new Choice(a.Id, Catalog.Display(store, a) + " · к оплате " + Store.MoneyString(store.ActDue(a.Id))));
            foreach (var x in store.Records.Where(r => r.Kind == "allocation" && r.Get("paymentId") == p.Id))
                history.Items.Add(new Choice(x.Id, Catalog.Display(store, store.Find(x.Get("actId"))) + " · " + x.Get("amount") + " " + p.Get("currency") + (x.Get("reversed") == "true" ? " · отменён" : "")));
            if (acts.Items.Count > 0) acts.SelectedIndex = 0;
            UpdateHint();
        }
        private static bool Compatible(Record payment, Record act)
        {
            return (payment.Get("paymentScope") == "" || Store.IsExternalAct(act) == (payment.Get("paymentScope") == "external"))
                && new[] { "projectId", "contractId" }.All(k => payment.Get(k) == "" || act.Get(k) == "" || payment.Get(k) == act.Get(k));
        }
        private void UpdateHint()
        {
            if (payments.SelectedItem == null) return;
            var p = store.Find(((Choice)payments.SelectedItem).Id);
            decimal available = store.PaymentUnallocated(p.Id);
            decimal due = acts.SelectedItem == null ? 0 : store.ActDue(((Choice)acts.SelectedItem).Id);
            amount.Text = Store.MoneyString(Math.Min(available, due)); help.Text = "Аванс: " + Store.MoneyString(available) + " " + p.Get("currency") + "\nК оплате по акту: " + Store.MoneyString(due);
        }
    }
}


