using System;
using System.Diagnostics;
using System.Threading;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class UiStyleTests
    {
        private sealed class ProbeButton : UiButton
        {
            public void Hover() { OnMouseEnter(EventArgs.Empty); }
            public void Unhover() { OnMouseLeave(EventArgs.Empty); }
        }
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("UI motion test: " + message);
        }
        private static bool HasOverlay(Control target)
        {
            return target.Controls.Cast<Control>().Any(c => c.GetType().Name == "TransitionOverlay");
        }
        private static void Pump(int milliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(4); }
            Application.DoEvents();
        }
        private static Color Pixel(Control surface)
        {
            using (var bitmap = new Bitmap(surface.Width, surface.Height))
            {
                surface.DrawToBitmap(bitmap, new Rectangle(Point.Empty, surface.Size));
                return bitmap.GetPixel(8, surface.Height - 8);
            }
        }
        private static Color Pixel(ViewTransition transition, Control surface)
        {
            using (var bitmap = transition.CaptureFrame(surface)) return bitmap.GetPixel(8, surface.Height - 8);
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint key, out byte opacity, out uint flags);
        private static int Difference(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        }
        private static void TestContinuousFrames()
        {
            Color first = Color.FromArgb(30, 80, 130), second = Color.FromArgb(210, 165, 55), third = Color.FromArgb(55, 175, 105);
            using (var form = new Form { ShowInTaskbar = false, Opacity = 0, ClientSize = new Size(280, 170) })
            using (var surface = new Panel { Dock = DockStyle.Fill, BackColor = first })
            using (var transition = new ViewTransition())
            {
                var input = new TextBox { Text = "Сохранённый ввод", Location = new Point(15, 30) };
                surface.Controls.Add(input); form.Controls.Add(surface); form.Show(); Application.DoEvents(); input.Focus();
                transition.Prepare(surface, true); surface.BackColor = second; transition.Begin(surface, true);
                Assert(transition.IsRunning && Difference(Pixel(transition, surface), first) <= 3, "navigation starts with a blank flash instead of the outgoing page");
                Assert(input.Focused && input.Text == "Сохранённый ввод", "snapshot changed focus or draft input");
                Pump(65); Color middle = Pixel(transition, surface);
                var layer = surface.Controls.Cast<Control>().Single(c => c.GetType().Name == "TransitionOverlay");
                uint colorKey, flags; byte nativeOpacity;
                Assert(GetLayeredWindowAttributes(layer.Handle, out colorKey, out nativeOpacity, out flags)
                    && nativeOpacity > 0 && nativeOpacity < 255 && (flags & 2) != 0,
                    "Windows composition did not receive the intermediate opacity");
                Assert(Difference(middle, first) > 15 && Difference(middle, second) > 15
                    && middle.R > first.R && middle.R < second.R, "crossfade has no intermediate page frames");
                transition.Prepare(surface, true); surface.BackColor = third; transition.Begin(surface, true);
                Assert(Difference(Pixel(transition, surface), middle) < 35, "rapid navigation jumps to a completed page before retargeting: " + middle + " -> " + Pixel(transition, surface));
                Assert(surface.Controls.Cast<Control>().Count(c => c.GetType().Name == "TransitionOverlay") == 1, "rapid navigation stacks page overlays");
                Pump(240);
                Assert(!transition.IsRunning && !HasOverlay(surface) && Difference(Pixel(transition, surface), third) <= 3, "completed crossfade leaves a stale page or snapshot");
                transition.Prepare(surface, true); surface.BackColor = first; transition.Begin(surface, true);
                Message key = Message.Create(input.Handle, 0x100, new IntPtr((int)Keys.A), IntPtr.Zero);
                Assert(!transition.PreFilterMessage(ref key) && !transition.IsRunning && !HasOverlay(surface),
                    "page animation hides live input or consumes a keyboard message");
                transition.Prepare(surface, true); transition.Enabled = false;
                Assert(!HasOverlay(surface), "disabling animations leaves the prepared outgoing page over live controls");
                form.Close();
            }
        }
        private static void TestButtonSettles()
        {
            using (var form = new Form { ShowInTaskbar = false, Opacity = 0, ClientSize = new Size(250, 100) })
            using (var button = new ProbeButton { Text = "Переход", Size = new Size(160, 40) })
            {
                form.Controls.Add(button); form.Show(); Application.DoEvents();
                button.Hover(); Pump(100);
                for (int i = 0; i < 6; i++) button.Hover();
                Pump(115);
                Assert(!button.AnimationRunning, "duplicate hover events keep an unchanged button animation running");
                button.Unhover(); Pump(70); Color displayed = Pixel(button);
                button.Hover(); Assert(Difference(Pixel(button), displayed) < 12, "button retargeting jumps back to a previous color");
                form.Close();
            }
        }
        public static void Run()
        {
            using (var form = new Form { ShowInTaskbar = false, Opacity = 0, ClientSize = new Size(450, 280) })
            using (var target = new Panel { Dock = DockStyle.Fill, BackColor = UiStyle.Background })
            using (var transition = new ViewTransition())
            {
                var button = new ProbeButton { Text = "Сохранить акт", Primary = true, Size = new Size(150, 40), Location = new Point(20, 20) };
                var status = new Label { Text = "Ошибка ввода", ForeColor = Color.FromArgb(185, 28, 28), Location = new Point(20, 80), Size = new Size(180, 30) };
                var readOnly = new TextBox { Text = "500", ReadOnly = true, Location = new Point(20, 120) };
                target.Controls.Add(button); target.Controls.Add(status); target.Controls.Add(readOnly); form.Controls.Add(target);
                form.Show(); Application.DoEvents();
                UiStyle.ApplyTo(form);
                Assert(status.ForeColor == Color.FromArgb(185, 28, 28) && readOnly.ReadOnly && readOnly.Text == "500",
                    "styling changed domain error state or calculated field");
                int clicks = 0; button.Click += delegate { clicks++; };
                form.AcceptButton = button; button.PerformClick();
                Assert(clicks == 1 && button.Text == "Сохранить акт", "styled button lost normal click behavior or label");

                button.Hover(); Assert(button.AnimationRunning, "hover did not start a transition");
                button.AnimationsEnabled = false;
                Assert(!button.AnimationRunning, "disabling motion left a button timer running");
                button.Hover(); Assert(!button.AnimationRunning, "disabled motion restarted a button timer");
                button.Unhover(); button.AnimationsEnabled = true; button.Hover();
                Assert(button.AnimationRunning, "hover could not restart after enabling motion");
                target.Controls.Remove(button);
                Assert(!button.AnimationRunning, "removed button retained a timer");
                target.Controls.Add(button); button.Hover(); button.Hide();
                Assert(!button.AnimationRunning, "hidden button retained a timer");
                button.Show(); button.Hover(); button.Dispose();
                Assert(button.IsDisposed && !button.AnimationRunning, "disposed button retained a timer");

                transition.Begin(target, true);
                Assert(transition.IsRunning && HasOverlay(target), "visible content did not start an entrance transition");
                transition.Begin(target, true);
                Assert(transition.IsRunning && target.Controls.Cast<Control>().Count(c => c.GetType().Name == "TransitionOverlay") == 1,
                    "rapid navigation retained more than one snapshot overlay");
                transition.Enabled = false;
                Assert(!transition.IsRunning && !HasOverlay(target), "disabling transition retained a timer or overlay");
                transition.Begin(target, true);
                Assert(!transition.IsRunning && !HasOverlay(target), "disabled transition restarted");
                transition.Enabled = true; transition.Begin(target, true); target.Size = new Size(target.Width - 20, target.Height);
                Assert(!transition.IsRunning && !HasOverlay(target), "resize did not release the snapshot");
                transition.Begin(target, true); target.Hide();
                Assert(!transition.IsRunning && !HasOverlay(target), "hiding content did not cancel the transition");
                target.Show(); transition.Begin(target, false);
                Assert(!transition.IsRunning && !HasOverlay(target), "per-navigation motion-off flag was ignored");
                transition.Begin(target, true); transition.Dispose();
                Assert(!transition.IsRunning && !HasOverlay(target), "disposal retained the snapshot");
                transition.Cancel(); transition.Begin(target, true); transition.Dispose();
                Assert(!HasOverlay(target), "disposed transition could be restarted");
                form.Close();
            }
        
            TestContinuousFrames(); TestButtonSettles();
            Assert(UiAnimationClock.ActiveCount == 0, "hidden or disposed UI retains global animation work");
        }
    }
}