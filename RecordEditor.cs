using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    public sealed class RecordEditor : Form
    {
        private sealed class Choice
        {
            public string Value;
            public string Text;
            public Choice(string value, string text) { Value = value; Text = text; }
            public override string ToString() { return Text; }
        }

        private readonly Store store;
        private readonly Schema schema;
        private readonly Record source;
        private readonly Dictionary<string, Control> inputs = new Dictionary<string, Control>();
        private readonly Dictionary<string, Label> fieldLabels = new Dictionary<string, Label>();
        private Label paymentTitle;
        private readonly ErrorProvider errors = new ErrorProvider();
        private readonly ToolTip tips = new ToolTip();
        private readonly Label hint;
        private readonly Button save;
        private TabControl sections;
        private ActWorkItemsControl actWorks;
        private Label actRequisites;
        private bool actReferenceLoading;
        private bool autoSelecting;
        private Label monthlyServiceInfo;
        public Record ResultRecord { get; private set; }

        public RecordEditor(Store store, Schema schema, Record existing)
        {
            this.store = store;
            this.schema = schema;
            source = existing == null
                ? new Record { Id = Guid.NewGuid().ToString("N"), Kind = schema.Kind }
                : existing.Clone();
            if (source.Fields == null) source.Fields = new Dictionary<string, string>();
            if (schema.Kind == "act" && store.Find(source.Id) == null && schema.SectionKey == Catalog.ExternalActSection && !source.Fields.ContainsKey("accountingScope"))
                source.Set("accountingScope", "external");
            if (schema.Kind == "payment" && store.Find(source.Id) == null && !source.Fields.ContainsKey("paymentScope"))
                source.Set("paymentScope", schema.SectionKey == Catalog.GrayPaymentSection ? "external" : "standard");
            Text = (existing == null || store.Find(existing.Id) == null ? "Добавить: " : "Изменить: ") + schema.Singular;
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.FromArgb(247, 249, 252);
            ForeColor = Color.FromArgb(31, 41, 55);
            StartPosition = FormStartPosition.CenterParent;
            Size = schema.Kind == "act" ? new Size(1020, Math.Min(820, Screen.PrimaryScreen.WorkingArea.Height - 50))
                : new Size(840, Math.Min(820, Screen.PrimaryScreen.WorkingArea.Height - 50));
            MinimumSize = schema.Kind == "act" ? new Size(800, 610) : new Size(680, 520);
            ShowIcon = false;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            errors.ContainerControl = this;
            errors.BlinkStyle = ErrorBlinkStyle.NeverBlink;

            Panel header = new Panel { Dock = DockStyle.Top, Height = schema.Kind == "act" ? 76 : 94, BackColor = Color.White };
            Label title = new Label
            {
                Text = Text,
                Font = new Font("Segoe UI", 17F, FontStyle.Bold),
                AutoSize = false, Dock = DockStyle.Top, Height = schema.Kind == "act" ? 45 : 51, Padding = new Padding(24, 13, 20, 0)
            };
            Label subtitle = new Label
            {
                Text = schema.Kind == "act" ? "Выберите контрагента и договор. Работы добавляйте отдельными строками; итог считается автоматически."
                    : schema.Kind == "work" ? "Сумма работы рассчитывается по количеству и цене. * — обязательные поля."
                    : schema.Kind == "settings" ? "Реквизиты ИП и организации для счетов, актов и договоров."
                    : "Заполните карточку. * — обязательные поля.",
                Dock = DockStyle.Bottom, Height = schema.Kind == "act" ? 31 : 36, Padding = new Padding(25, 0, 20, 0),
                ForeColor = Color.FromArgb(100, 116, 139)
            };
            if (schema.Kind == "payment") paymentTitle = title;
            header.Controls.Add(subtitle);
            header.Controls.Add(title);

            Control content;
            if (schema.Kind == "act") content = BuildActCard();
            else if (schema.Fields.Any(f => !String.IsNullOrWhiteSpace(f.Section)))
            {
                sections = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 7) };
                foreach (var group in schema.Fields.GroupBy(f => f.Section ?? "Карточка"))
                {
                    TabPage page = new TabPage(group.Key) { BackColor = BackColor, Padding = new Padding(0) };
                    page.Controls.Add(BuildFields(group.ToList()));
                    sections.TabPages.Add(page);
                }
                content = sections;
            }
            else content = BuildFields(schema.Fields);
            WireRelatedReferences();
            WireActReferences();
            WireMonthlyService();
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = schema.Kind == "payment" ? 101 : schema.Kind == "act" ? 75 : 83, BackColor = Color.White, Padding = new Padding(24, 15, 24, 15) };
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, Width = 252, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, Padding = new Padding(0)
            };
            Button cancel = new UiButton
            {
                Text = "Отмена", DialogResult = DialogResult.Cancel, Size = new Size(108, 40),
                FlatStyle = FlatStyle.Flat, BackColor = Color.White, Margin = new Padding(10, 0, 0, 0)
            };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            save = new UiButton
            {
                Primary = true, Text = "Сохранить", Size = new Size(126, 40), FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, Margin = new Padding(0)
            };
            save.FlatAppearance.BorderSize = 0;
            save.Click += SaveClick;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            hint = new Label
            {
                Text = "Данные сохраняются на этом компьютере.",
                Dock = DockStyle.Fill, Padding = new Padding(0, 9, 12, 0),
                ForeColor = Color.FromArgb(100, 116, 139)
            };
            footer.Controls.Add(hint);
            footer.Controls.Add(buttons);
            Controls.Add(content);
            Controls.Add(footer);
            Controls.Add(header);
            WirePaymentFields();
            UiStyle.ApplyTo(this); UiStyle.SetAnimations(this, UiPreferences.Load(store.DataDir).AnimationsEnabled);
            AcceptButton = save;
            CancelButton = cancel;
            Shown += delegate { if (schema.Kind == "act") inputs["partyId"].Focus(); else if (inputs.Count > 0) inputs.Values.First().Focus(); };
        }


        private Control BuildActCard()
        {
            foreach (string key in new[] { "amount", "description", "hours" })
                inputs.Add(key, MakeInput(schema.Fields.Single(field => field.Key == key)));
            var body = new Panel { Dock = DockStyle.Fill };
            var links = BuildActFieldCards(new[] { "partyId", "contractId" }, false);
            links.Dock = DockStyle.Top; links.Height = 76; links.Padding = new Padding(16, 7, 16, 0);
            sections = new TabControl { Dock = DockStyle.Fill, Padding = new Point(15, 7) };
            var worksPage = new TabPage("Работы") { BackColor = BackColor };
            actWorks = new ActWorkItemsControl(source, (TextBox)inputs["amount"], (TextBox)inputs["description"], (TextBox)inputs["hours"]);
            worksPage.Controls.Add(actWorks); sections.TabPages.Add(worksPage);
            var dataPage = new TabPage("Данные акта") { BackColor = BackColor };
            var metadata = BuildActFieldCards(new[] { "number", "date", "projectId", "direction", "currency", "status", "dueDate", "signedDate" }, true);
            actRequisites = new Label { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(17, 7, 17, 10),
                ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Segoe UI", 9F), MaximumSize = new Size(940, 0) };
            metadata.Controls.Add(actRequisites); actRequisites.BringToFront();
            dataPage.Controls.Add(metadata); sections.TabPages.Add(dataPage);
            var notesPage = new TabPage("Примечание") { BackColor = BackColor, Padding = new Padding(18, 15, 18, 15) };
            var notes = (TextBox)MakeInput(schema.Fields.Single(field => field.Key == "notes")); notes.Dock = DockStyle.Fill; notes.MaxLength = 32767;
            inputs.Add("notes", notes); notesPage.Controls.Add(notes);
            notesPage.Controls.Add(new Label { Text = "Дополнительная информация по акту", Dock = DockStyle.Top, Height = 29, ForeColor = Color.FromArgb(100, 116, 139) });
            sections.TabPages.Add(notesPage); sections.SelectedIndex = 0;
            var reviewPage = new TabPage("Сверка с выпиской") { BackColor = BackColor };
            reviewPage.Controls.Add(BuildFields(schema.Fields.Where(f => f.Key == "reviewStatus" || f.Key == "reviewNotes").ToList()));
            sections.TabPages.Add(reviewPage);
            body.Controls.Add(sections); body.Controls.Add(links);
            return body;
        }

        private Panel BuildActFieldCards(string[] keys, bool scrollable)
        {
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = scrollable, Padding = new Padding(16, 12, 16, 10) };
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int index = 0; index < keys.Length; ++index)
            {
                FieldDef field = schema.Fields.Single(f => f.Key == keys[index]);
                var cell = new Panel { Dock = DockStyle.Fill, Height = 75, Margin = new Padding(0, 0, index % 2 == 0 ? 18 : 0, 0) };
                var label = new Label { Text = field.Label + (field.Required ? " *" : ""), Dock = DockStyle.Top, Height = 25 };
                var input = MakeInput(field); input.Dock = DockStyle.Top; input.AccessibleName = field.Label;
                inputs.Add(field.Key, input); fieldLabels[field.Key] = label;
                cell.Controls.Add(input); cell.Controls.Add(label);
                table.Controls.Add(cell, index % 2, index / 2);
                if (index % 2 == 0) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
                if (field.Key == "number" && store.Find(source.Id) == null)
                {
                    var number = (TextBox)input;
                    number.Text = "Автоматически при сохранении (" + store.NextActNumber + ")";
                    number.ForeColor = Color.FromArgb(100, 116, 139);
                }
            }
            panel.Controls.Add(table); return panel;
        }

        public void ShowWorks() { if (sections != null && schema.Kind == "act") sections.SelectedIndex = 0; }
        private string SelectedActValue(string key)
        {
            Choice choice = ((ComboBox)inputs[key]).SelectedItem as Choice;
            return choice == null ? "" : choice.Value;
        }
        private void SelectActValue(string key, string value)
        {
            ComboBox combo = (ComboBox)inputs[key];
            for (int i = 0; i < combo.Items.Count; ++i) if (((Choice)combo.Items[i]).Value == value) { combo.SelectedIndex = i; return; }
            combo.SelectedIndex = 0;
        }
        private void RefillActReferences(string key)
        {
            ComboBox combo = (ComboBox)inputs[key]; string wanted = SelectedActValue(key);
            string party = SelectedActValue("partyId"), project = SelectedActValue("projectId");
            bool income = SelectedActValue("direction") == "income";
            combo.Items.Clear(); combo.Items.Add(new Choice("", key == "contractId" ? "— Без договора —" : "— Без объекта —"));
            foreach (Record record in store.Records.Where(r => r.Kind == (key == "contractId" ? "contract" : "project"))
                .Where(r => r.Get("archived") != "true" || r.Id == source.Get(key))
                .Where(r => key == "projectId" ? !income || party == "" || r.Get("partyId") == "" || r.Get("partyId") == party
                    : (party == "" || r.Get("partyId") == party) && (project == "" || r.Get("projectId") == "" || r.Get("projectId") == project)
                        && (r.Get("status") == "active" || r.Id == source.Get(key)))
                .OrderBy(r => Catalog.Display(store, r), StringComparer.CurrentCultureIgnoreCase))
                combo.Items.Add(new Choice(record.Id, Catalog.Display(store, record)));
            SelectActValue(key, wanted);
        }
        private void WireActReferences()
        {
            if (schema.Kind != "act") return;
            foreach (string key in new[] { "partyId", "projectId", "contractId", "currency", "direction" })
            {
                string changedKey = key;
                ((ComboBox)inputs[key]).SelectedIndexChanged += delegate { if (!actReferenceLoading) ActReferenceChanged(changedKey); };
            }
            tips.SetToolTip(inputs["contractId"], "Договор подставляет контрагента, объект, валюту и направление. Срок оплаты и подпись указываются отдельно.");
            tips.SetToolTip(inputs["partyId"], "После выбора контрагента доступны его договоры и объекты.");
            actReferenceLoading = true;
            try
            {
                RefillActReferences("projectId"); RefillActReferences("contractId");
                if (store.Find(source.Id) == null && source.Get("contractId") != "") ApplyActContract();
            }
            finally { actReferenceLoading = false; }
            UpdateActRequisites();
        }
        private void ApplyActContract()
        {
            Record contract = store.Find(SelectedActValue("contractId")); if (contract == null) return;
            SelectActValue("partyId", contract.Get("partyId"));
            SelectActValue("currency", contract.Get("currency")); SelectActValue("direction", contract.Get("direction"));
            RefillActReferences("projectId"); SelectActValue("projectId", contract.Get("projectId"));
        }
        private void ActReferenceChanged(string key)
        {
            actReferenceLoading = true;
            try
            {
                if (key == "contractId") { ApplyActContract(); RefillActReferences("contractId"); }
                else
                {
                    if (key == "projectId")
                    {
                        Record project = store.Find(SelectedActValue("projectId"));
                        if (project != null && SelectedActValue("direction") == "income" && project.Get("partyId") != "")
                            SelectActValue("partyId", project.Get("partyId"));
                    }
                    RefillActReferences("projectId"); RefillActReferences("contractId");
                    Record contract = store.Find(SelectedActValue("contractId"));
                    if (contract != null && (contract.Get("currency") != SelectedActValue("currency") || contract.Get("direction") != SelectedActValue("direction")))
                        SelectActValue("contractId", "");
                    if (key == "partyId" && store.Find(source.Id) == null && SelectedActValue("contractId") == "")
                    {
                        ComboBox combo = (ComboBox)inputs["contractId"];
                        var compatible = combo.Items.Cast<Choice>().Where(c => c.Value != "").Where(c =>
                        {
                            Record record = store.Find(c.Value);
                            return record.Get("currency") == SelectedActValue("currency") && record.Get("direction") == SelectedActValue("direction");
                        }).ToList();
                        if (compatible.Count == 1) { SelectActValue("contractId", compatible[0].Value); ApplyActContract(); }
                    }
                }
            }
            finally { actReferenceLoading = false; }
            UpdateActRequisites();
        }
        private void UpdateActRequisites()
        {
            if (actWorks != null) actWorks.SetCurrency(SelectedActValue("currency"));
            if (actRequisites == null) return;
            string partyId = SelectedActValue("partyId");
            bool preserveSnapshots = store.Find(source.Id) != null && partyId == source.Get("partyId") && source.Get("partySnapshot") != "";
            Record party = preserveSnapshots ? Store.GetActSnapshot(source, "partySnapshot") : null;
            if (party == null) party = store.Find(partyId);
            Record own = preserveSnapshots ? Store.GetActSnapshot(source, "organizationSnapshot") : null;
            if (own == null) own = store.Of("settings").FirstOrDefault();
            actRequisites.Text = "Реквизиты для документа\r\n" + RequisitesLine(own, "Моя организация") + "\r\n" + RequisitesLine(party, "Контрагент");
            actRequisites.MaximumSize = new Size(Math.Max(600, ClientSize.Width - 60), 0);
        }
        private static string RequisitesLine(Record record, string role)
        {
            if (record == null) return role + ": не выбрана";
            string text = role + ": " + record.Get("name");
            if (record.Get("unp") != "") text += " · УНП " + record.Get("unp");
            if (record.Get("bankAccount") != "") text += " · " + record.Get("bankAccount");
            return text;
        }

        private Panel BuildFields(List<FieldDef> fields)
        {
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 16, 28, 16) };
            TableLayoutPanel table = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, RowCount = fields.Count, BackColor = BackColor,
                Padding = new Padding(0, 0, 10, 10)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            int row = 0;
            foreach (FieldDef field in fields)
            {
                Label label = new Label
                {
                    Text = field.Type == "bool" ? "" : field.Label +
                        (field.Required || IsRequiredMonthlyServiceField(field.Key) ? " *" : ""),
                    AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top,
                    Margin = new Padding(0, 8, 12, 15), MaximumSize = new Size(207, 0)
                };
                fieldLabels[field.Key] = label;
                Control input = MakeInput(field);
                input.Dock = DockStyle.Fill;
                input.Margin = new Padding(0, 3, 16, 14);
                inputs.Add(field.Key, input);
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                table.Controls.Add(label, 0, row);
                table.Controls.Add(input, 1, row);
                ++row;
            }
            if (fields.Any(f => f.Key == "monthlyServiceEnabled"))
            {
                monthlyServiceInfo = new Label
                {
                    AutoSize = true, MaximumSize = new Size(710, 0),
                    ForeColor = Color.FromArgb(100, 116, 139),
                    Margin = new Padding(0, 8, 16, 14)
                };
                table.RowCount = fields.Count + 1;
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                table.Controls.Add(monthlyServiceInfo, 0, row);
                table.SetColumnSpan(monthlyServiceInfo, 2);
            }
            scroll.Controls.Add(table);
            return scroll;
        }

        private void FocusInput(Control input)
        {
            Control parent = input.Parent;
            while (parent != null && !(parent is TabPage)) parent = parent.Parent;
            if (sections != null && parent is TabPage) sections.SelectedTab = (TabPage)parent;
            input.Focus();
        }
        private static bool IsRequiredMonthlyServiceField(string key)
        {
            return key == "monthlyServiceAmount" || key == "monthlyServiceCurrency" || key == "monthlyServiceStartMonth";
        }

        private static DateTime DefaultMonthlyServiceStart()
        {
            return Store.DefaultMonthlyServiceStart(DateTime.UtcNow.AddHours(3).Date);
        }

        private void WireMonthlyService()
        {
            Control input;
            if (schema.Kind != "party" || !inputs.TryGetValue("monthlyServiceEnabled", out input)) return;
            CheckBox enabled = (CheckBox)input;
            enabled.CheckedChanged += delegate { UpdateMonthlyServiceFields(); };
            ((DateTimePicker)inputs["monthlyServiceStartMonth"]).ValueChanged += delegate { UpdateMonthlyServiceFields(); };
            tips.SetToolTip(enabled, "Создавать акт с указанной суммой для этого контрагента 1-го числа каждого месяца.");
            tips.SetToolTip(inputs["monthlyServiceStartMonth"], "Выберите месяц первого акта. Каждый акт датируется первым числом месяца.");
            UpdateMonthlyServiceFields();
        }

        private void UpdateMonthlyServiceFields()
        {
            CheckBox enabled = (CheckBox)inputs["monthlyServiceEnabled"];
            foreach (string key in new[] { "monthlyServiceAmount", "monthlyServiceCurrency", "monthlyServiceDescription", "monthlyServiceStartMonth" })
            {
                inputs[key].Enabled = enabled.Checked;
                if (!enabled.Checked) errors.SetError(inputs[key], "");
            }
            DateTime date = ((DateTimePicker)inputs["monthlyServiceStartMonth"]).Value;
            DateTime first = new DateTime(date.Year, date.Month, 1);
            if (monthlyServiceInfo != null)
                monthlyServiceInfo.Text = enabled.Checked
                    ? "Первый акт: " + first.ToString("dd.MM.yyyy") +
                        ". Далее — 1-го числа каждого месяца.\r\nЕсли компьютер был выключен, пропущенные акты будут созданы при следующем запуске."
                    : "Отметьте «Ежемесячное обслуживание» и укажите сумму. Акты будут появляться во вкладке «Акты».";
        }

        private void WireRelatedReferences()
        {
            bool saved = store.Find(source.Id) != null;
            if (schema.Kind == "act" || saved && schema.Kind != "payment") return;
            foreach (string reference in new[] { "contractId", "actId" })
            {
                Control input;
                if (!inputs.TryGetValue(reference, out input)) continue;
                string selectedReference = reference;
                ((ComboBox)input).SelectedIndexChanged += delegate
                {
                    if (!autoSelecting) ApplyRelatedReference(selectedReference);
                };
                tips.SetToolTip(input, "При выборе договора или акта связанные поля подставляются автоматически.");
            }
            if (!saved)
            {
                if (source.Get("actId") != "") ApplyRelatedReference("actId");
                else if (source.Get("contractId") != "") ApplyRelatedReference("contractId");
            }
        }

        private void ApplyRelatedReference(string referenceKey)
        {
            Control input;
            if (!inputs.TryGetValue(referenceKey, out input)) return;
            Choice selected = ((ComboBox)input).SelectedItem as Choice;
            if (selected == null || selected.Value == "") return;
            Record linked = store.Find(selected.Value);
            if (linked == null) return;
            autoSelecting = true;
            try
            {
                string[] keys = referenceKey == "actId"
                    ? new[] { "partyId", "projectId", "contractId", "currency", "direction" }
                    : new[] { "partyId", "projectId", "currency", "direction" };
                foreach (string key in keys)
                {
                    string value = linked.Get(key);
                    Control target;
                    if (value == "" && !(schema.Kind == "payment" && referenceKey == "actId") || !inputs.TryGetValue(key, out target) || !(target is ComboBox)) continue;
                    ComboBox combo = (ComboBox)target;
                    for (int i = 0; i < combo.Items.Count; ++i)
                        if (((Choice)combo.Items[i]).Value == value) { combo.SelectedIndex = i; break; }
                }
            }
            finally { autoSelecting = false; }
        }
        private Control MakeInput(FieldDef field)
        {
            string value = source.Get(field.Key);
            if (field.Type == "bool")
                return new CheckBox
                {
                    Text = field.Label, Checked = String.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
                    AutoSize = true, Height = 31, Padding = new Padding(0, 4, 0, 0),
                    UseVisualStyleBackColor = true
                };
            if (field.Type == "date")
            {
                DateTimePicker picker = new DateTimePicker
                {
                    Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy",
                    ShowCheckBox = !field.Required, Height = 31, MinDate = new DateTime(1900, 1, 1),
                    MaxDate = new DateTime(2200, 12, 31)
                };
                DateTime date;
                if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                    && date >= picker.MinDate && date <= picker.MaxDate) picker.Value = date;
                else picker.Value = DateTime.Today;
                picker.Checked = field.Required || !String.IsNullOrWhiteSpace(value);
                if (field.Key == "monthlyServiceStartMonth")
                {
                    picker.ShowCheckBox = false;
                    picker.ShowUpDown = true;
                    picker.CustomFormat = "MMMM yyyy";
                    if (source.Get("monthlyServiceEnabled") != "true" || String.IsNullOrWhiteSpace(value))
                        picker.Value = DefaultMonthlyServiceStart();
                    else picker.Value = new DateTime(picker.Value.Year, picker.Value.Month, 1);
                    picker.Checked = true;
                }
                else if (!field.Required) tips.SetToolTip(picker, "Отметьте флажок, чтобы указать дату. Снимите флажок, чтобы оставить поле пустым.");
                return picker;
            }
            if (field.Type == "enum" || field.Type == "ref")
            {
                ComboBox combo = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false,
                    MaxDropDownItems = 12, DropDownHeight = 320, Height = 31
                };
                if (field.Type == "ref" || !field.Required && !field.Options.ContainsKey("")) combo.Items.Add(new Choice("", "— Не выбрано —"));
                if (field.Type == "enum")
                    foreach (KeyValuePair<string, string> option in field.Options)
                        combo.Items.Add(new Choice(option.Key, option.Value));
                else
                {
                    foreach (Record record in store.Records.Where(delegate(Record r) { return r.Kind == field.ReferenceKind && (!IsPaymentActField(field) || PaymentActAllowed(r)); })
                        .Where(delegate(Record r) { return r.Get("archived") != "true" || r.Id == value && (!IsPaymentActField(field) || store.Find(source.Id) != null); })
                        .OrderBy(delegate(Record r) { return Catalog.Display(store, r); }, StringComparer.CurrentCultureIgnoreCase))
                        combo.Items.Add(new Choice(record.Id, Catalog.Display(store, record)));
                }
                for (int i = 0; i < combo.Items.Count; ++i)
                    if (((Choice)combo.Items[i]).Value == value) { combo.SelectedIndex = i; break; }
                if ((combo.SelectedIndex < 0 || field.Key == "monthlyServiceCurrency" && String.IsNullOrWhiteSpace(value)) && combo.Items.Count > 0)
                {
                    if (field.Key == "currency" || field.Key == "monthlyServiceCurrency")
                    {
                        string currency = "BYN";
                        Record settings = store.Of("settings").FirstOrDefault();
                        if (settings != null && field.Options.ContainsKey(settings.Get("currency"))) currency = settings.Get("currency");
                        for (int i = 0; i < combo.Items.Count; ++i)
                            if (((Choice)combo.Items[i]).Value == currency) { combo.SelectedIndex = i; break; }
                    }
                    else combo.SelectedIndex = 0;
                }
                return combo;
            }
            TextBox text = new TextBox
            {
                Text = field.Key == "monthlyServiceDescription" && String.IsNullOrWhiteSpace(value) ? "Ежемесячное обслуживание" : value,
                BorderStyle = BorderStyle.FixedSingle,
                Multiline = field.Type == "memo", Height = field.Type == "memo" ? 82 : 31,
                ScrollBars = field.Type == "memo" ? ScrollBars.Vertical : ScrollBars.None,
                AcceptsReturn = field.Type == "memo",
                MaxLength = field.Key == "monthlyServiceDescription" ? 2000 : field.Type == "memo" ? 32767 : 1000
            };
            if (field.Type == "money" || field.Type == "number")
            {
                text.TextAlign = HorizontalAlignment.Right;
                decimal amount;
                if (TryDecimal(value, out amount))
                    text.Text = amount.ToString(field.Type == "money" ? "0.00" : "0.######", CultureInfo.CurrentCulture);
                tips.SetToolTip(text, field.Type == "money" ? "Сумма с точностью до копеек. Можно использовать запятую или точку."
                    : "Количество. Можно использовать запятую или точку.");
            }
            if (schema.Kind == "act" && field.Key == "number" && (store.Find(source.Id) == null || source.Get("autoNumber") == "true"))
            { text.ReadOnly = true; if (store.Find(source.Id) == null) { text.Text = ""; tips.SetToolTip(text, "Номер " + store.NextActNumber + " будет присвоен при сохранении."); } }
            if (schema.Kind == "act" && field.Key == "hours") text.ReadOnly = true;
            return text;
        }

        private bool IsPaymentActField(FieldDef field) { return schema.Kind == "payment" && field.Key == "actId"; }
        private string SelectedPaymentScope
        {
            get
            {
                Control control;
                if (inputs.TryGetValue("paymentScope", out control))
                {
                    Choice selected = ((ComboBox)control).SelectedItem as Choice;
                    return selected == null ? "" : selected.Value;
                }
                return source.Get("paymentScope");
            }
        }
        private bool PaymentActAllowed(Record act)
        {
            string scope = SelectedPaymentScope;
            return scope == "" || (scope == "external" ? Store.IsExternalAct(act) : !Store.IsExternalAct(act));
        }
        private void RefillPaymentActs()
        {
            ComboBox combo = (ComboBox)inputs["actId"];
            Choice previous = combo.SelectedItem as Choice; string selected = previous == null ? source.Get("actId") : previous.Value;
            bool saved = store.Find(source.Id) != null;
            autoSelecting = true;
            try
            {
                combo.Items.Clear(); combo.Items.Add(new Choice("", "— Не выбрано —"));
                foreach (Record act in store.Records.Where(r => r.Kind == "act" && PaymentActAllowed(r)
                    && (r.Get("archived") != "true" || saved && r.Id == source.Get("actId")))
                    .OrderBy(r => Catalog.Display(store, r), StringComparer.CurrentCultureIgnoreCase))
                    combo.Items.Add(new Choice(act.Id, Catalog.Display(store, act)));
                combo.SelectedIndex = 0;
                for (int i = 0; i < combo.Items.Count; i++) if (((Choice)combo.Items[i]).Value == selected) { combo.SelectedIndex = i; break; }
            }
            finally { autoSelecting = false; }
        }
        private void WirePaymentFields()
        {
            if (schema.Kind != "payment") return;
            ((ComboBox)inputs["paymentScope"]).SelectedIndexChanged += delegate
            {
                if (autoSelecting) return;
                RefillPaymentActs(); UpdatePaymentHint(); errors.SetError(inputs["actId"], "");
            };
            RefillPaymentActs(); UpdatePaymentHint();
        }
        private void UpdatePaymentHint()
        {
            string scope = SelectedPaymentScope;
            string singular = scope == "external" ? "Серая оплата" : scope == "standard" ? "Белая оплата" : "Платёж";
            Text = (store.Find(source.Id) == null ? "Добавить: " : "Изменить: ") + singular;
            if (paymentTitle != null) paymentTitle.Text = Text;
            fieldLabels["actId"].Text = scope == "external" ? "Акт вне *" : "Акт";
            hint.Text = scope == "external"
                ? "Серая оплата не входит в расчёт налога. Для погашения долга используйте «Зачесть оплату»."
                : scope == "standard" ? "В налоговом расчёте учитываются белые поступления. Для погашения долга используйте «Зачесть оплату»."
                : "Вид оплаты определяется актом и распределениями. Для погашения долга используйте «Зачесть оплату».";
        }

        private static bool TryDecimal(string value, out decimal result)
        {
            string normalized = (value ?? "").Trim().Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "").Replace(',', '.');
            return Decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out result);
        }


        internal Record PrepareRecord()
        {
            if (schema.Kind != "act") throw new InvalidOperationException("Эта подготовка доступна только для акта.");
            Record result = source.Clone();
            foreach (FieldDef field in schema.Fields)
            {
                if (field.Key == "description" || field.Key == "hours") continue;
                Control input = inputs[field.Key]; string value;
                if (field.Type == "date")
                {
                    DateTimePicker date = (DateTimePicker)input;
                    value = date.Checked ? Store.DateString(date.Value) : "";
                }
                else if (field.Type == "enum" || field.Type == "ref") value = SelectedActValue(field.Key);
                else value = field.Key == "number" && store.Find(source.Id) == null ? "" : input.Text.Trim();
                if (field.Required && value == "") throw new InvalidOperationException("Заполните поле «" + field.Label + "».");
                if (field.Type == "money")
                {
                    decimal number;
                    if (!TryDecimal(value, out number) || Decimal.Round(number, 2) != number)
                        throw new InvalidOperationException("Введите сумму с точностью до копеек.");
                    value = Store.MoneyString(number);
                }
                result.Set(field.Key, value);
            }
            actWorks.ApplyTo(result);
            Record party = store.Find(result.Get("partyId"));
            if (party != null && party.Get("archived") != "true") store.CaptureActSnapshots(result);
            return result;
        }

        private void SaveClick(object sender, EventArgs e)
        {
            errors.Clear();
            Record result = source.Clone();
            Control firstInvalid = null;
            bool monthlyServiceEnabled = schema.Kind == "party" && inputs.ContainsKey("monthlyServiceEnabled")
                && ((CheckBox)inputs["monthlyServiceEnabled"]).Checked;
            foreach (FieldDef field in schema.Fields)
            {
                Control input = inputs[field.Key];
                if (schema.Kind == "act" && (field.Key == "description" || field.Key == "hours")) continue;
                if (!monthlyServiceEnabled && field.Key.StartsWith("monthlyService", StringComparison.Ordinal) &&
                    field.Key != "monthlyServiceEnabled") continue;
                string value;
                if (field.Type == "bool") value = ((CheckBox)input).Checked ? "true" : "false";
                else if (field.Type == "date")
                {
                    DateTimePicker picker = (DateTimePicker)input;
                    DateTime date = field.Key == "monthlyServiceStartMonth"
                        ? new DateTime(picker.Value.Year, picker.Value.Month, 1) : picker.Value;
                    value = picker.Checked ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
                }
                else if (field.Type == "enum" || field.Type == "ref")
                {
                    Choice selected = ((ComboBox)input).SelectedItem as Choice;
                    value = selected == null ? "" : selected.Value;
                }
                else value = schema.Kind == "act" && field.Key == "number" && store.Find(source.Id) == null ? "" : input.Text.Trim();
                string error = "";
                if ((field.Required || monthlyServiceEnabled && IsRequiredMonthlyServiceField(field.Key)
                    || IsPaymentActField(field) && SelectedPaymentScope == "external") && String.IsNullOrWhiteSpace(value))
                    error = "Заполните поле «" + field.Label + "».";
                if (field.Key == "monthlyServiceDescription" && String.IsNullOrWhiteSpace(value))
                    value = "Ежемесячное обслуживание";
                if (field.Key == "monthlyServiceDescription" && value.Length > 2000)
                    error = "Наименование работ не должно превышать 2000 символов.";
                if (error.Length == 0 && value.Length > 0 && (field.Type == "money" || field.Type == "number"))
                {
                    decimal amount;
                    if (!TryDecimal(value, out amount)) error = "Введите число. Например: 1250,50.";
                    else if (Decimal.Round(amount, field.Type == "money" ? 2 : 6) != amount)
                        error = field.Type == "money" ? "Допустимо не более двух знаков после запятой." : "Допустимо не более шести знаков после запятой.";
                    else if (field.Key == "monthlyServiceAmount" && amount <= 0m)
                        error = "Сумма ежемесячного обслуживания должна быть больше нуля.";
                    else if (field.Type == "money") value = Store.MoneyString(amount);
                    else value = amount.ToString("0.######", CultureInfo.InvariantCulture);
                }
                if (error.Length > 0)
                {
                    errors.SetError(input, error);
                    if (firstInvalid == null) firstInvalid = input;
                }
                result.Fields[field.Key] = value;
            }
            string detailError = "";
            if (firstInvalid == null && schema.Kind == "act")
            {
                try { actWorks.ApplyTo(result); }
                catch (Exception exception)
                {
                    detailError = exception.Message; firstInvalid = actWorks.WorkGrid;
                    errors.SetError(firstInvalid, detailError);
                }
            }
            if (firstInvalid != null)
            {
                hint.Text = detailError == "" ? "Проверьте отмеченные поля." : detailError;
                hint.ForeColor = Color.FromArgb(185, 28, 28);
                FocusInput(firstInvalid);
                return;
            }
            try
            {
                save.Enabled = false;
                if (schema.Kind == "act")
                {
                    Record party = store.Find(result.Get("partyId"));
                    if (party != null && party.Get("archived") != "true") store.CaptureActSnapshots(result);
                }
                store.Upsert(result);
                ResultRecord = store.Find(result.Id) ?? result;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Не удалось сохранить", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { save.Enabled = true; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { errors.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
        }
    }
}





