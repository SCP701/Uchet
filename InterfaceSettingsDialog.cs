using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    public sealed class InterfaceSettingsDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(28, 42, 58);
        private static readonly Color Muted = Color.FromArgb(99, 115, 133);
        private static readonly Color Accent = Color.FromArgb(0, 119, 116);
        private readonly string dataDir;
        private readonly UiPreferences original;
        private readonly CheckedListBox sectionsList = new CheckedListBox();
        private readonly CheckBox animations = new CheckBox();
        private readonly Label selectionSummary = new Label();
        public UiPreferences ResultPreferences { get; private set; }

        public InterfaceSettingsDialog(string directory, UiPreferences current)
        {
            if (current == null) throw new ArgumentNullException("current");
            dataDir = directory; original = current.Clone();
            Text = "Интерфейс для меня";
            ClientSize = new Size(640, 780); MinimumSize = new Size(610, 650);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            MinimizeBox = false; MaximizeBox = false;
            Font = new Font("Segoe UI", 10); ForeColor = Ink;
            BackColor = Color.FromArgb(244, 247, 250);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(24, 20, 24, 16) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(layout);

            var header = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            var heading = new Label { Text = "Удобное меню для вашей работы", AutoSize = false, Dock = DockStyle.Top, Height = 36, Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoEllipsis = true };
            var caption = new Label { Text = "Выберите разделы, которые хотите видеть.\nНастройки для Windows-пользователя «" + Environment.UserName + "» в этой базе.", Dock = DockStyle.Bottom, Height = 58, ForeColor = Muted, AutoEllipsis = true };
            header.Controls.Add(heading); header.Controls.Add(caption); layout.Controls.Add(header, 0, 0);

            var presets = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            presets.Controls.Add(PresetButton("Только акты", "acts"));
            presets.Controls.Add(PresetButton("Акты и оплаты", "acts-payments"));
            presets.Controls.Add(PresetButton("Все разделы", "all"));
            layout.Controls.Add(presets, 0, 1);

            var sectionsCard = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14, 12, 14, 8), Margin = new Padding(0, 0, 0, 10) };
            selectionSummary.Dock = DockStyle.Top; selectionSummary.Height = 31;
            selectionSummary.Font = new Font("Segoe UI", 10, FontStyle.Bold); selectionSummary.ForeColor = Ink;
            sectionsList.Dock = DockStyle.Fill; sectionsList.CheckOnClick = true;
            sectionsList.BorderStyle = BorderStyle.None; sectionsList.IntegralHeight = false;
            sectionsList.Font = new Font("Segoe UI", 11); sectionsList.BackColor = Color.White;
            sectionsList.ForeColor = Ink; sectionsList.AccessibleName = "Разделы бокового меню";
            foreach (UiSection section in UiPreferences.Sections) sectionsList.Items.Add(section, original.IsVisible(section.Key));
            sectionsList.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
            {
                int checkedCount = sectionsList.CheckedItems.Count + (e.NewValue == CheckState.Checked ? 1 : 0) - (e.CurrentValue == CheckState.Checked ? 1 : 0);
                UpdateSummary(checkedCount);
            };
            sectionsCard.Controls.Add(sectionsList); sectionsCard.Controls.Add(selectionSummary);
            layout.Controls.Add(sectionsCard, 0, 2);

            var motionCard = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14, 8, 14, 8), Margin = new Padding(0, 0, 0, 8) };
            animations.Text = "Плавные анимации интерфейса"; animations.Checked = original.AnimationsEnabled;
            animations.Dock = DockStyle.Top; animations.Height = 29; animations.AutoSize = false;
            animations.AccessibleName = "Плавные анимации интерфейса";
            var motionHint = new Label { Text = "Переходы между разделами и отклик кнопок.", Dock = DockStyle.Bottom, Height = 21, ForeColor = Muted, Font = new Font("Segoe UI", 9) };
            motionCard.Controls.Add(animations); motionCard.Controls.Add(motionHint); layout.Controls.Add(motionCard, 0, 3);

            var help = new Label { Text = "Скрытые разделы можно вернуть здесь в любой момент.\nКнопка «Интерфейс» всегда остаётся доступной.", Dock = DockStyle.Fill, ForeColor = Muted, Font = new Font("Segoe UI", 9), AutoEllipsis = true, Margin = new Padding(0, 0, 0, 4) };
            layout.Controls.Add(help, 0, 4);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 7, 0, 0) };
            var save = MakeButton("Сохранить", true); save.Name = "saveInterface"; save.Click += delegate { SavePreferences(); };
            var cancel = MakeButton("Отмена", false); cancel.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(save); footer.Controls.Add(cancel); layout.Controls.Add(footer, 0, 5);
            AcceptButton = save; CancelButton = cancel;
            UpdateSummary(sectionsList.CheckedItems.Count);
        }
        private Button MakeButton(string text, bool primary)
        {
            var button = new UiButton { Primary = primary, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(115, 35), FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Padding = new Padding(12, 4, 12, 4), Margin = new Padding(8, 0, 0, 0), UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(205, 215, 224);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0, 103, 100) : Color.FromArgb(235, 242, 246);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(0, 88, 85) : Color.FromArgb(223, 233, 240);
            return button;
        }
        private Button PresetButton(string label, string key)
        {
            Button button = MakeButton(label, false); button.MinimumSize = new Size(0, 35); button.Margin = new Padding(0, 0, 8, 0);
            button.Click += delegate { SelectPreset(key); };
            return button;
        }
        private void UpdateSummary(int count)
        {
            selectionSummary.Text = "Видимые разделы: " + count + " из " + UiPreferences.Sections.Count;
        }
        public void SelectPreset(string key)
        {
            string[] visible;
            if (key == "acts") visible = new[] { "create-act", "party", "act" };
            else if (key == "acts-payments") visible = new[] { "overview", "create-act", "party", "project", "contract", "act", "invoice", "payment", "reports" };
            else if (key == "all") visible = UiPreferences.Sections.Select(section => section.Key).ToArray();
            else throw new ArgumentException("Неизвестный набор разделов.", "key");
            var selected = new HashSet<string>(visible, StringComparer.Ordinal);
            sectionsList.BeginUpdate();
            try
            {
                for (int i = 0; i < sectionsList.Items.Count; i++)
                    sectionsList.SetItemChecked(i, selected.Contains(((UiSection)sectionsList.Items[i]).Key));
            }
            finally { sectionsList.EndUpdate(); }
            UpdateSummary(sectionsList.CheckedItems.Count);
        }
        public UiPreferences GetSelectedPreferences()
        {
            var selected = original.Clone(); selected.AnimationsEnabled = animations.Checked;
            for (int i = 0; i < sectionsList.Items.Count; i++)
                selected.SetVisible(((UiSection)sectionsList.Items[i]).Key, sectionsList.GetItemChecked(i));
            selected.Validate();
            return selected;
        }
        private void SavePreferences()
        {
            try
            {
                UiPreferences selected = GetSelectedPreferences();
                selected.Save(dataDir); ResultPreferences = selected;
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Настройки интерфейса", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}