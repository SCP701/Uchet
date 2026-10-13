using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace WorkLedger
{
    public sealed class ActWorkDescriptionDialog : Form
    {
        private readonly TextBox description = new TextBox();
        public TextBox DescriptionBox { get { return description; } }
        public string FullDescription { get; private set; }

        public ActWorkDescriptionDialog(string text, int workNumber)
        {
            FullDescription = text ?? "";
            Name = "actWorkDescriptionDialog";
            Text = "Полное описание работы " + workNumber.ToString(CultureInfo.InvariantCulture);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 520); MinimumSize = new Size(560, 380);
            Font = new Font("Segoe UI", 10F); AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = UiStyle.Background; ForeColor = UiStyle.Ink;
            ShowIcon = false; ShowInTaskbar = false; MinimizeBox = false;

            var header = new Label { Text = Text, Dock = DockStyle.Top, Height = 62,
                Padding = new Padding(18, 15, 18, 10), Font = new Font("Segoe UI", 17F, FontStyle.Bold) };
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 8) };
            description.Name = "fullWorkDescriptionText";
            description.AccessibleName = "Полное описание выбранной работы";
            description.Dock = DockStyle.Fill; description.Multiline = true; description.ReadOnly = true;
            description.WordWrap = true; description.ScrollBars = ScrollBars.Vertical;
            description.ShortcutsEnabled = true; description.AcceptsReturn = true; description.MaxLength = Int32.MaxValue;
            description.BackColor = Color.White; description.Text = FullDescription;
            content.Controls.Add(description);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(18, 10, 18, 10),
                FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var close = new UiButton { Text = "Закрыть", Name = "closeWorkDescription", Width = 120, Height = 36,
                DialogResult = DialogResult.Cancel, CausesValidation = false };
            footer.Controls.Add(close);
            Controls.Add(content); Controls.Add(footer); Controls.Add(header);
            AcceptButton = close; CancelButton = close;
            UiStyle.ApplyTo(this);
            Shown += delegate { description.Focus(); description.SelectionStart = 0; description.SelectionLength = 0; description.ScrollToCaret(); };
        }
    }
}
