using System;
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
        }
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("UI motion test: " + message);
        }
        private static bool HasOverlay(Control target)
        {
            return target.Controls.Cast<Control>().Any(c => c.GetType().Name == "TransitionOverlay");
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
                button.AnimationsEnabled = true; button.Hover();
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
        }
    }
}