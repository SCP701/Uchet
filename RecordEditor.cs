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
        private readonly TabControl sections;
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
            if (schema.Kind == "payment" && store.Find(source.Id) == null && !source.Fields.ContainsKey("paymentScope"))
                source.Set("paymentScope", schema.SectionKey == Catalog.GrayPaymentSection ? "external" : "standard");
            Text = (existing == null || store.Find(existing.Id) == null ? "Добавить: " : "Изменить: ") + schema.Singular;
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.FromArgb(247, 249, 252);
            ForeColor = Color.FromArgb(31, 41, 55);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(840, Math.Min(820, Screen.PrimaryScreen.WorkingArea.Height - 50));
            MinimumSize = new Size(680, 520);
            ShowIcon = false;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            errors.ContainerControl = this;
            errors.BlinkStyle = ErrorBlinkStyle.NeverBlink;

            Panel header = new Panel { Dock = DockStyle.Top, Height = 94, BackColor = Color.White };
            Label title = new Label
            {
                Text = Text,
                Font = new Font("Segoe UI", 17F, FontStyle.Bold),
                AutoSize = false, Dock = DockStyle.Top, Height = 51, Padding = new Padding(24, 13, 20, 0)
            };
            Label subtitle = new Label
            {
                Text = schema.Kind == "work" ? "Сумма работы рассчитывается по количеству и цене. * — обязательные поля."
                    : schema.Kind == "settings" ? "Реквизиты ИП и организации для счетов, актов и договоров."
                    : "Заполните карточку. * — обязательные поля.",
                Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(25, 0, 20, 0),
                ForeColor = Color.FromArgb(100, 116, 139)
            };
            if (schema.Kind == "payment") paymentTitle = title;
            header.Controls.Add(subtitle);
            header.Controls.Add(title);

            Control content;
            if (schema.Fields.Any(f => !String.IsNullOrWhiteSpace(f.Section)))
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
            WireMonthlyService();
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = schema.Kind == "payment" ? 101 : 83, BackColor = Color.White, Padding = new Padding(24, 15, 24, 15) };
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
            Shown += delegate { if (inputs.Count > 0) inputs.Values.First().Focus(); };
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
            if (saved && schema.Kind != "payment") return;
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
                else value = input.Text.Trim();
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
            string detailInput = "amount";
            if (firstInvalid == null && schema.Kind == "act" && (source.Get("printItemsJson") != "" || source.Get("workItems") != ""))
            {
                try
                {
                    if (Store.ParseMoney(result.Get("amount")) != Store.ParseMoney(source.Get("amount")))
                        throw new InvalidOperationException(store.Find(source.Id) == null
                            ? "Сначала сохраните акт с текущей суммой, затем измените её вместе со строками через «Позиции акта»."
                            : "Изменяйте сумму вместе со строками через «Позиции акта».");
                    if (source.Get("workItems") != "" && result.Get("hours") != source.Get("hours"))
                    {
                        detailInput = "hours";
                        throw new InvalidOperationException("Часы изменяйте вместе со строками через «Позиции акта».");
                    }
                    ActDocument.ValidateItems(result, ActDocument.ReadItems(result));
                }
                catch (Exception exception)
                {
                    detailError = exception.Message;
                    firstInvalid = inputs.ContainsKey(detailInput) ? inputs[detailInput] : inputs["amount"];
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





