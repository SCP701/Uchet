using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WorkLedger
{
    // One UI-thread frame source; idle controls do not own or run native timers.
    internal static class UiAnimationClock
    {
        private static readonly Timer frames = new Timer { Interval = 15 };
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly HashSet<Action<double>> clients = new HashSet<Action<double>>();
        static UiAnimationClock() { frames.Tick += Tick; }
        internal static double Now { get { return clock.Elapsed.TotalMilliseconds; } }
        internal static int ActiveCount { get { return clients.Count; } }
        internal static bool Contains(Action<double> client) { return client != null && clients.Contains(client); }
        internal static void Add(Action<double> client)
        {
            if (client != null && clients.Add(client) && clients.Count == 1) frames.Start();
        }
        internal static void Remove(Action<double> client)
        {
            if (client != null) clients.Remove(client);
            if (clients.Count == 0) frames.Stop();
        }
        internal static float Ease(float progress)
        {
            float p = Math.Max(0, Math.Min(1, progress));
            return p * p * (3F - 2F * p);
        }
        private static void Tick(object sender, EventArgs args)
        {
            double now = Now;
            foreach (var client in clients.ToArray()) if (clients.Contains(client)) client(now);
        }
    }

    public static class UiStyle
    {
        public static readonly Color Ink = Color.FromArgb(32, 45, 64);
        public static readonly Color Muted = Color.FromArgb(102, 117, 138);
        public static readonly Color Accent = Color.FromArgb(0, 132, 132);
        public static readonly Color Blue = Color.FromArgb(43, 96, 217);
        public static readonly Color Background = Color.FromArgb(246, 248, 252);
        public static readonly Color Surface = Color.White;
        public static readonly Color Border = Color.FromArgb(219, 226, 235);
        public static readonly Color Sidebar = Color.FromArgb(22, 32, 50);
        internal static readonly Color SidebarHover = Color.FromArgb(34, 48, 70);
        internal static readonly Color SidebarSelected = Color.FromArgb(40, 61, 95);
        internal static readonly Color Selection = Color.FromArgb(227, 237, 255);

        // Existing business status colors and custom title sizes are deliberately preserved.
        private static readonly PropertyInfo BufferedProperty = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
        private static void BufferSurface(Control control)
        {
            if (control is Panel || control is UserControl || control is DataGridView)
                if (!(bool)BufferedProperty.GetValue(control, null)) BufferedProperty.SetValue(control, true, null);
        }
        public static void ApplyTo(Control control)
        {
            if (control == null || control.IsDisposed) return;
            BufferSurface(control);
            if (control.Font.Name != "Segoe UI" && control.Font.Size <= 10.5F)
                control.Font = new Font("Segoe UI", Math.Max(9.5F, control.Font.Size), control.Font.Style);
            if (control.ForeColor == SystemColors.ControlText || control.ForeColor == Color.Black)
                control.ForeColor = Ink;
            var grid = control as DataGridView;
            if (grid != null) ApplyGrid(grid);
            var text = control as TextBox;
            if (text != null) text.BorderStyle = BorderStyle.FixedSingle;
            var numeric = control as NumericUpDown;
            if (numeric != null) numeric.BorderStyle = BorderStyle.FixedSingle;
            var combo = control as ComboBox;
            if (combo != null) combo.FlatStyle = FlatStyle.Flat;
            foreach (Control child in control.Controls) ApplyTo(child);
        }
        public static void ApplyGrid(DataGridView grid)
        {
            BufferSurface(grid);
            grid.BackgroundColor = Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.GridColor = Color.FromArgb(233, 238, 244);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 241, 249);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
            if (!FontMatches(grid.ColumnHeadersDefaultCellStyle.Font, 10F, FontStyle.Bold))
                grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 5, 10, 5);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Ink;
            grid.ColumnHeadersHeight = Math.Max(42, grid.ColumnHeadersHeight);
            if (!FontMatches(grid.DefaultCellStyle.Font, 10F, FontStyle.Regular))
                grid.DefaultCellStyle.Font = new Font("Segoe UI", 10F);
            grid.DefaultCellStyle.ForeColor = Ink;
            grid.DefaultCellStyle.Padding = new Padding(10, 4, 10, 4);
            grid.DefaultCellStyle.SelectionBackColor = Selection;
            grid.DefaultCellStyle.SelectionForeColor = Ink;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 254);
            grid.RowTemplate.Height = Math.Max(38, grid.RowTemplate.Height);
        }
        private static bool FontMatches(Font font, float size, FontStyle style)
        {
            return font != null && font.Name == "Segoe UI" && Math.Abs(font.Size - size) < .01F && font.Style == style;
        }
        public static void SetAnimations(Control root, bool enabled)
        {
            if (root == null || root.IsDisposed) return;
            var button = root as UiButton;
            if (button != null) button.AnimationsEnabled = enabled;
            foreach (Control child in root.Controls) SetAnimations(child, enabled);
        }
        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 0) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        internal static Color Blend(Color first, Color second, float progress)
        {
            progress = Math.Max(0, Math.Min(1, progress));
            return Color.FromArgb((int)(first.R + (second.R - first.R) * progress),
                (int)(first.G + (second.G - first.G) * progress),
                (int)(first.B + (second.B - first.B) * progress));
        }
    }

    public sealed class UiPanel : Panel
    {
        private int cornerRadius = 12;
        private Color borderColor = UiStyle.Border;
        public int CornerRadius { get { return cornerRadius; } set { cornerRadius = Math.Max(0, value); Invalidate(); } }
        public Color BorderColor { get { return borderColor; } set { borderColor = value; Invalidate(); } }
        public Color SurfaceColor { get { return BackColor; } set { BackColor = value; Invalidate(); } }
        public UiPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = UiStyle.Surface; Padding = new Padding(18);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color surrounding = Parent == null ? UiStyle.Background : Parent.BackColor;
            e.Graphics.Clear(surrounding);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiStyle.Rounded(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), cornerRadius))
            using (var fill = new SolidBrush(BackColor))
            using (var border = new Pen(borderColor))
            { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path); }
        }
    }

    public class UiButton : Button
    {
        private readonly Action<double> animateFrame;
        private double startedAt, duration = 185;
        private bool primary, selected, navigation, animationsEnabled = true, hovered, pressed;
        private string iconKey;
        private Color displayed, initial, destination;
        private float marker, markerInitial, markerDestination;
        public bool Primary { get { return primary; } set { if (primary == value) return; primary = value; ChangeState(); } }
        public bool Selected { get { return selected; } set { if (selected == value) return; selected = value; ChangeState(); } }
        public bool Navigation { get { return navigation; } set { if (navigation == value) return; navigation = value; ChangeState(); } }
        public bool AnimationsEnabled
        {
            get { return animationsEnabled; }
            set { if (animationsEnabled == value) return; animationsEnabled = value; if (!value) Snap(); }
        }
        public string IconKey { get { return iconKey; } set { iconKey = value; Invalidate(); } }
        internal bool AnimationRunning { get { return UiAnimationClock.Contains(animateFrame); } }
        public UiButton()
        {
            animateFrame = Animate;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            BackColor = UiStyle.Surface; ForeColor = UiStyle.Ink; Font = new Font("Segoe UI", 10F);
            Padding = new Padding(12, 0, 12, 0); Height = 38; MinimumSize = new Size(0, 36);
            Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.PushButton;
            displayed = UiStyle.Surface;
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size result = base.GetPreferredSize(proposedSize);
            result.Width += String.IsNullOrEmpty(iconKey) ? 4 : 30;
            result.Height = Math.Max(38, result.Height); return result;
        }
        private Color TargetColor()
        {
            if (!Enabled) return navigation ? UiStyle.Sidebar : Color.FromArgb(238, 241, 246);
            if (navigation) return selected ? UiStyle.SidebarSelected : hovered || pressed ? UiStyle.SidebarHover : UiStyle.Sidebar;
            if (primary) return pressed ? Color.FromArgb(29, 68, 167) : hovered ? Color.FromArgb(36, 83, 194) : UiStyle.Blue;
            return pressed ? Color.FromArgb(225, 232, 243) : hovered ? Color.FromArgb(237, 243, 251) : UiStyle.Surface;
        }
        private void Snap()
        {
            UiAnimationClock.Remove(animateFrame); displayed = TargetColor(); marker = selected && navigation ? 1 : 0; Invalidate();
        }
        private void ChangeState()
        {
            if (IsDisposed || Disposing) return;
            double now = UiAnimationClock.Now;
            if (AnimationRunning) Animate(now);
            destination = TargetColor(); markerDestination = selected && navigation ? 1 : 0;
            if (!animationsEnabled || !Visible || !IsHandleCreated || Parent == null) { Snap(); return; }
            if (displayed.ToArgb() == destination.ToArgb() && Math.Abs(marker - markerDestination) < .001F) { Snap(); return; }
            // Repeated enter/focus notifications must not keep restarting an unchanged tween.
            if (AnimationRunning && destination.ToArgb() == previousDestination.ToArgb() && markerDestination == previousMarker) return;
            previousDestination = destination; previousMarker = markerDestination;
            initial = displayed; markerInitial = marker; startedAt = now; duration = pressed ? 80 : 185;
            UiAnimationClock.Add(animateFrame); Invalidate();
        }
        private Color previousDestination;
        private float previousMarker;
        private void Animate(double now)
        {
            if (IsDisposed || Disposing || !Visible || Parent == null || !animationsEnabled) { Snap(); return; }
            float progress = Math.Min(1, (float)((now - startedAt) / duration));
            float eased = UiAnimationClock.Ease(progress);
            displayed = UiStyle.Blend(initial, destination, eased); marker = markerInitial + (markerDestination - markerInitial) * eased;
            Invalidate(); if (progress >= 1) UiAnimationClock.Remove(animateFrame);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; ChangeState(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = pressed = false; ChangeState(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; ChangeState(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; ChangeState(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); ChangeState(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Snap(); }
        protected override void OnHandleDestroyed(EventArgs e) { UiAnimationClock.Remove(animateFrame); base.OnHandleDestroyed(e); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) { hovered = pressed = false; Snap(); } }
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); if (Parent == null) { hovered = pressed = false; Snap(); } }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; ChangeState(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? UiStyle.Background : Parent.BackColor);
            using (var path = UiStyle.Rounded(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), 8))
            using (var fill = new SolidBrush(displayed))
            {
                g.FillPath(fill, path);
                if (!navigation && !primary) using (var line = new Pen(UiStyle.Border)) g.DrawPath(line, path);
            }
            Color textColor = !Enabled ? (navigation ? Color.FromArgb(110, 125, 147) : UiStyle.Muted)
                : navigation ? (selected ? Color.White : Color.FromArgb(218, 226, 240)) : primary ? Color.White : ForeColor;
            bool hasIcon = !String.IsNullOrEmpty(iconKey);
            int left = navigation ? 14 : Padding.Left;
            if (hasIcon) { DrawIcon(g, new Rectangle(left, (Height - 20) / 2, 20, 20), iconKey, textColor); left += 30; }
            Rectangle textBounds = new Rectangle(left, 0, Math.Max(0, Width - left - Padding.Right), Height);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            flags |= navigation || TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
            if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(g, Text, Font, textBounds, textColor, flags);
            if (marker > 0)
            {
                float barHeight = Math.Max(2, (Height - 18) * marker);
                using (var brush = new SolidBrush(Color.FromArgb((int)(255 * marker), 101, 207, 211)))
                using (var path = UiStyle.Rounded(new RectangleF(1, (Height - barHeight) / 2, 3, barHeight), 1.5F)) g.FillPath(brush, path);
            }
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, new Rectangle(5, 5, Width - 10, Height - 10), textColor, displayed);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void Dispose(bool disposing)
        {
            if (disposing) UiAnimationClock.Remove(animateFrame);
            base.Dispose(disposing);
        }
        private static void DrawIcon(Graphics g, Rectangle bounds, string key, Color color)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(bounds.Left, bounds.Top); g.ScaleTransform(bounds.Width / 20F, bounds.Height / 20F);
            using (var pen = new Pen(color, 1.5F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (key)
                {
                    case "overview":
                        g.DrawRectangle(pen, 3, 3, 5, 5); g.DrawRectangle(pen, 12, 3, 5, 5);
                        g.DrawRectangle(pen, 3, 12, 5, 5); g.DrawRectangle(pen, 12, 12, 5, 5); break;
                    case "plus": case "create-act":
                        g.DrawLine(pen, 10, 4, 10, 16); g.DrawLine(pen, 4, 10, 16, 10);
                        if (key == "create-act") g.DrawEllipse(pen, 1, 1, 18, 18); break;
                    case "party":
                        g.DrawEllipse(pen, 6, 2, 8, 8); g.DrawArc(pen, 3, 11, 14, 12, 180, 180); break;
                    case "taxes":
                        g.DrawEllipse(pen, 3, 3, 5, 5); g.DrawLine(pen, 5, 15, 15, 5);
                        g.DrawEllipse(pen, 12, 12, 5, 5); break;
                    case "payment": case "invoice": case "expense":
                        g.DrawRectangle(pen, 2, 5, 16, 11); g.DrawLine(pen, 2, 9, 18, 9); g.DrawLine(pen, 5, 13, 8, 13); break;
                    case "reports":
                        g.DrawLine(pen, 3, 17, 17, 17); g.DrawLine(pen, 5, 13, 5, 17);
                        g.DrawLine(pen, 10, 8, 10, 17); g.DrawLine(pen, 15, 3, 15, 17); break;
                    case "audit":
                        g.DrawEllipse(pen, 2, 2, 16, 16); g.DrawLine(pen, 10, 5, 10, 10); g.DrawLine(pen, 10, 10, 14, 12); break;
                    case "updates": case "restore":
                        g.DrawArc(pen, 3, 3, 14, 14, 40, 280); g.DrawLines(pen, new[] { new PointF(14, 2), new PointF(17, 5), new PointF(17, 1) }); break;
                    case "settings":
                        g.DrawEllipse(pen, 4, 4, 12, 12); g.DrawEllipse(pen, 8, 8, 4, 4);
                        for (int i = 0; i < 8; i++) { double angle = i * Math.PI / 4; g.DrawLine(pen, 10 + 6 * (float)Math.Cos(angle), 10 + 6 * (float)Math.Sin(angle), 10 + 9 * (float)Math.Cos(angle), 10 + 9 * (float)Math.Sin(angle)); } break;
                    case "export": case "backup":
                        g.DrawLine(pen, 10, 2, 10, 12); g.DrawLines(pen, new[] { new PointF(6, 8), new PointF(10, 12), new PointF(14, 8) });
                        g.DrawLines(pen, new[] { new PointF(3, 12), new PointF(3, 17), new PointF(17, 17), new PointF(17, 12) }); break;
                    case "project":
                        g.DrawRectangle(pen, 3, 5, 14, 12); g.DrawRectangle(pen, 7, 2, 6, 3); g.DrawLine(pen, 3, 10, 17, 10); break;
                    case "organization":
                        g.DrawRectangle(pen, 4, 2, 12, 16); g.DrawLine(pen, 8, 18, 8, 14); g.DrawLine(pen, 12, 18, 12, 14);
                        g.DrawLine(pen, 7, 6, 8, 6); g.DrawLine(pen, 12, 6, 13, 6); g.DrawLine(pen, 7, 10, 8, 10); g.DrawLine(pen, 12, 10, 13, 10); break;
                    default:
                        g.DrawLines(pen, new[] { new PointF(5, 2), new PointF(13, 2), new PointF(17, 6), new PointF(17, 18), new PointF(5, 18), new PointF(5, 2) });
                        g.DrawLine(pen, 13, 2, 13, 6); g.DrawLine(pen, 13, 6, 17, 6); g.DrawLine(pen, 8, 10, 14, 10); g.DrawLine(pen, 8, 14, 14, 14); break;
                }
            }
            g.Restore(state);
        }
    }

    // Keep the outgoing page visible while layout changes, then crossfade two stable frames.
    public sealed class ViewTransition : IDisposable, IMessageFilter
    {
        private readonly Action<double> animateFrame;
        private Control target;
        private Form form;
        private TransitionOverlay overlay;
        private double startedAt;
        private bool disposed, prepared, filtering;
        private bool enabled = true;
        public bool Enabled { get { return enabled; } set { enabled = value; if (!value) Cancel(); } }
        public bool IsRunning { get { return UiAnimationClock.Contains(animateFrame) && overlay != null; } }
        public ViewTransition() { animateFrame = Tick; }
        public void Prepare(Control nextTarget, bool animate)
        {
            if (disposed) return;
            Bitmap snapshot = null;
            try
            {
                if (Usable(nextTarget, animate))
                {
                    snapshot = CaptureFrame(nextTarget);
                }
                Cancel();
                if (snapshot == null) return;
                target = nextTarget;
                overlay = new TransitionOverlay(snapshot) { Bounds = target.ClientRectangle, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
                snapshot = null;
                target.Controls.Add(overlay); overlay.BringToFront(); prepared = true;
                target.Disposed += TargetInvalidated; target.HandleDestroyed += TargetInvalidated;
                target.VisibleChanged += TargetInvalidated; target.EnabledChanged += TargetInvalidated;
                target.SizeChanged += TargetInvalidated; target.ParentChanged += TargetInvalidated;
                form = target.FindForm();
                if (form != null) { form.Deactivate += TargetInvalidated; form.ResizeBegin += TargetInvalidated; }
                Application.AddMessageFilter(this); filtering = true;
            }
            catch (ArgumentException) { Cancel(); }
            catch (InvalidOperationException) { Cancel(); }
            catch (System.Runtime.InteropServices.ExternalException) { Cancel(); }
            finally { if (snapshot != null) snapshot.Dispose(); }
        }
        public void Begin(Control nextTarget, bool animate)
        {
            if (disposed) return;
            if (!Usable(nextTarget, animate)) { Cancel(); return; }
            if (!prepared || target != nextTarget || overlay == null) Prepare(nextTarget, animate);
            if (overlay == null) return;
            try
            {
                var host = target; var cover = overlay;
                host.PerformLayout();
                if (target != host || overlay != cover || host.IsDisposed) return;
                cover.BringToFront(); prepared = false;
                if (!cover.NativeSupported) { Cancel(); return; }
                startedAt = UiAnimationClock.Now; UiAnimationClock.Add(animateFrame);
            }
            catch (ArgumentException) { Cancel(); }
            catch (InvalidOperationException) { Cancel(); }
            catch (System.Runtime.InteropServices.ExternalException) { Cancel(); }
        }
        internal Bitmap CaptureFrame(Control control)
        {
            var cover = overlay;
            if (cover == null || target != control) return Capture(control);
            bool visible = cover.Visible;
            cover.Visible = false;
            try
            {
                Bitmap frame = Capture(control);
                using (Graphics graphics = Graphics.FromImage(frame)) cover.DrawOver(graphics, control.ClientRectangle);
                return frame;
            }
            finally { if (!cover.IsDisposed) cover.Visible = visible; }
        }
        private bool Usable(Control control, bool animate)
        {
            return enabled && animate && control != null && !control.IsDisposed && control.IsHandleCreated && control.Visible
                && control.Enabled && control.ClientSize.Width > 0 && control.ClientSize.Height > 0;
        }
        private static Bitmap Capture(Control control)
        {
            Bitmap snapshot = new Bitmap(control.ClientSize.Width, control.ClientSize.Height);
            try { control.DrawToBitmap(snapshot, new Rectangle(Point.Empty, control.ClientSize)); return snapshot; }
            catch { snapshot.Dispose(); throw; }
        }
        private void Tick(double now)
        {
            if (target == null || target.IsDisposed || !target.Visible || !target.Enabled || overlay == null || overlay.IsDisposed) { Cancel(); return; }
            float progress = Math.Min(1, (float)((now - startedAt) / 200));
            overlay.Opacity = 1F - UiAnimationClock.Ease(progress);
            if (progress >= 1 || !overlay.NativeSupported) Cancel();
        }
        public bool PreFilterMessage(ref Message message)
        {
            if (target != null && (message.Msg == 0x201 || message.Msg == 0x204 || message.Msg == 0x207
                || message.Msg == 0x20A || message.Msg == 0x20E || message.Msg == 0x100 || message.Msg == 0x104))
            {
                Control input = Control.FromChildHandle(message.HWnd);
                if (input == target || (input != null && target.Contains(input))) Cancel();
            }
            return false;
        }
        private void TargetInvalidated(object sender, EventArgs e) { Cancel(); }
        public void Cancel()
        {
            if (disposed) return;
            UiAnimationClock.Remove(animateFrame);
            if (filtering) { Application.RemoveMessageFilter(this); filtering = false; }
            if (target != null)
            {
                target.Disposed -= TargetInvalidated; target.HandleDestroyed -= TargetInvalidated;
                target.VisibleChanged -= TargetInvalidated; target.EnabledChanged -= TargetInvalidated;
                target.SizeChanged -= TargetInvalidated; target.ParentChanged -= TargetInvalidated;
            }
            if (form != null) { form.Deactivate -= TargetInvalidated; form.ResizeBegin -= TargetInvalidated; }
            var old = overlay; overlay = null; target = null; form = null; prepared = false;
            if (old != null) { if (old.Parent != null) old.Parent.Controls.Remove(old); old.Dispose(); }
        }
        public void Dispose()
        {
            if (disposed) return;
            Cancel(); disposed = true;
        }
        private sealed class TransitionOverlay : Control
        {
            private readonly Bitmap outgoing;
            private readonly ImageAttributes attributes = new ImageAttributes();
            private readonly ColorMatrix alpha = new ColorMatrix();
            private float opacity = 1F;
            internal bool NativeSupported;
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte opacity, uint flags);
            internal float Opacity
            {
                get { return opacity; }
                set
                {
                    opacity = Math.Max(0, Math.Min(1, value));
                    if (IsHandleCreated) NativeSupported = SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(opacity * 255), 2);
                }
            }
            public TransitionOverlay(Bitmap image)
            {
                outgoing = image; TabStop = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
                SetStyle(ControlStyles.Selectable, false);
            }
            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    // Windows 10/11 composites the cached outgoing bitmap over the live incoming page.
                    parameters.ExStyle |= 0x80000 | 0x20;
                    return parameters;
                }
            }
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                NativeSupported = SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(opacity * 255), 2);
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
                base.WndProc(ref m);
            }
            protected override void OnPaint(PaintEventArgs e) { e.Graphics.DrawImageUnscaled(outgoing, Point.Empty); }
            internal void DrawOver(Graphics graphics, Rectangle bounds)
            {
                alpha.Matrix33 = opacity; attributes.SetColorMatrix(alpha);
                graphics.DrawImage(outgoing, bounds, 0, 0, outgoing.Width, outgoing.Height, GraphicsUnit.Pixel, attributes);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { outgoing.Dispose(); attributes.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
