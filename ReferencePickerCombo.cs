using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    // Keeps the existing reference controls' selection and refill contract.
    public class ReferencePickerCombo : ComboBox
    {
        private Store pickerStore;
        private string pickerKind, pickerTitle;
        private Func<object, string> readId;
        private bool allowClear, opening, queued;

        public void Configure(Store store, string kind, string title, Func<object, string> itemId, bool canClear)
        {
            if (store == null || itemId == null) throw new ArgumentNullException();
            pickerStore = store; pickerKind = kind; pickerTitle = title; readId = itemId; allowClear = canClear;
            DropDownStyle = ComboBoxStyle.DropDownList;
            Cursor = Cursors.Hand;
            AccessibleDescription = "Открывает отдельное окно выбора с поиском и фильтрами. Нажмите F4.";
        }

        public ReferencePickerDialog CreateDialog(string initialQuery)
        {
            if (pickerStore == null) throw new InvalidOperationException("Окно выбора не настроено.");
            var choices = new List<ReferencePickerEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string clearLabel = "Не выбрано";
            foreach (object item in Items)
            {
                string id = readId(item) ?? "";
                if (id == "") { clearLabel = Convert.ToString(item).Trim(' ', '\u2014'); continue; }
                if (seen.Add(id)) choices.Add(new ReferencePickerEntry(id, Convert.ToString(item)));
            }
            string selected = SelectedItem == null ? "" : readId(SelectedItem) ?? "";
            return new ReferencePickerDialog(pickerStore, pickerKind, pickerTitle, choices, selected,
                allowClear, clearLabel, initialQuery ?? "");
        }

        public void OpenPicker(string initialQuery)
        {
            if (pickerStore == null || opening || IsDisposed || !Enabled) return;
            opening = true;
            try
            {
                using (ReferencePickerDialog dialog = CreateDialog(initialQuery))
                {
                    Form owner = FindForm();
                    DialogResult result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                    if (result != DialogResult.OK) return;
                    string wanted = dialog.SelectedId ?? "";
                    // Refill or background changes cannot select a different record with the same name.
                    for (int i = 0; i < Items.Count; ++i)
                    {
                        if ((readId(Items[i]) ?? "") != wanted) continue;
                        if (wanted != "")
                        {
                            Record record = pickerStore.Find(wanted);
                            if (record == null || record.Kind != pickerKind) return;
                        }
                        else if (!allowClear) return;
                        SelectedIndex = i;
                        return;
                    }
                }
            }
            finally { opening = false; }
        }

        private void QueuePicker(string query)
        {
            if (pickerStore == null || opening || queued || !IsHandleCreated || IsDisposed || !Enabled) return;
            queued = true;
            BeginInvoke((MethodInvoker)delegate
            {
                queued = false;
                if (!IsDisposed && Visible && Enabled) OpenPicker(query);
            });
        }

        protected override void WndProc(ref Message message)
        {
            if (pickerStore != null)
            {
                if (message.Msg == 0x0201 || message.Msg == 0x0203) // left down / double click
                { if (Enabled) { Focus(); QueuePicker(""); } message.Result = IntPtr.Zero; return; }
                if (message.Msg == 0x0202 || message.Msg == 0x020A) // left up / wheel: never cycle IDs
                { message.Result = IntPtr.Zero; return; }
                if (message.Msg == 0x014F) // CB_SHOWDROPDOWN: do not open the native giant list
                { if (message.WParam != IntPtr.Zero) QueuePicker(""); message.Result = IntPtr.Zero; return; }
                if (message.Msg == 0x0102)
                {
                    char value = (char)message.WParam.ToInt32();
                    if (!Char.IsControl(value)) { QueuePicker(value == ' ' ? "" : value.ToString()); message.Result = IntPtr.Zero; return; }
                }
            }
            base.WndProc(ref message);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (pickerStore != null)
            {
                Keys key = keyData & Keys.KeyCode;
                if (key == Keys.F4 || key == Keys.Enter || key == Keys.Space || key == Keys.Down || key == Keys.Up
                    || key == Keys.Home || key == Keys.End || key == Keys.PageDown || key == Keys.PageUp)
                { QueuePicker(""); return true; }
            }
            return base.ProcessCmdKey(ref message, keyData);
        }
    }

    // The ComboBox retains IDs/events; both visible buttons open its existing searchable picker.
    public sealed class ReferenceSelectionField : UserControl
    {
        private readonly ReferencePickerCombo input;
        private readonly UiButton valueButton, chooseButton;
        private readonly ToolTip tips = new ToolTip();
        private readonly string fieldTitle, emptyText;
        public ReferenceSelectionField(ReferencePickerCombo input, string title, string emptyText, string name, string icon)
        {
            this.input = input; fieldTitle = title; this.emptyText = emptyText;
            Name = name; BackColor = UiStyle.Surface; TabStop = false;
            Height = 42; MinimumSize = new System.Drawing.Size(0, 42);
            Margin = new Padding(0, 0, 8, 0);
            valueButton = new UiButton { Name = name + "-value", Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft, UseMnemonic = false,
                IconKey = icon, TabIndex = 0 };
            chooseButton = new UiButton { Name = name + "-choose", Dock = DockStyle.Right,
                Width = 108, Text = "Выбрать", Primary = true, TabIndex = 1,
                AccessibleName = "Выбрать: " + title, UseMnemonic = false };
            var gap = new Panel { Dock = DockStyle.Right, Width = 8, TabStop = false };
            input.Visible = false; input.TabStop = false;
            Controls.Add(valueButton); Controls.Add(gap); Controls.Add(chooseButton); Controls.Add(input);
            valueButton.Click += delegate { input.OpenPicker(""); };
            chooseButton.Click += delegate { input.OpenPicker(""); };
            input.SelectedIndexChanged += SelectionChanged;
            input.EnabledChanged += InputEnabledChanged;
            tips.SetToolTip(chooseButton, "Открыть " + title.ToLowerInvariant() + ": поиск и фильтры (F4)");
            UpdateSelection();
        }
        private void SelectionChanged(object sender, EventArgs args) { UpdateSelection(); }
        private void InputEnabledChanged(object sender, EventArgs args)
        { valueButton.Enabled = chooseButton.Enabled = input.Enabled; }
        private void UpdateSelection()
        {
            string text = input.SelectedItem == null ? "" : Convert.ToString(input.SelectedItem).Trim(' ', '\u2014');
            valueButton.Text = text == "" ? emptyText : text;
            valueButton.AccessibleName = fieldTitle + ": " + valueButton.Text;
            valueButton.AccessibleDescription = "Нажмите, чтобы открыть поиск. Полное название доступно в подсказке.";
            tips.SetToolTip(valueButton, valueButton.Text);
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.F4 || keyData == (Keys.Alt | Keys.Down))
            { input.OpenPicker(""); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.SelectedIndexChanged -= SelectionChanged;
                input.EnabledChanged -= InputEnabledChanged;
                tips.Dispose();
            }
            base.Dispose(disposing);
        }
    }
    internal sealed class ReferencePickerEntry
    {
        public readonly string Id, Text;
        public ReferencePickerEntry(string id, string text) { Id = id; Text = text; }
    }
}