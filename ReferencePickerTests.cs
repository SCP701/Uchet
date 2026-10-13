using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class ReferencePickerTests
    {
        private sealed class Choice
        {
            public string Id, Title;
            public Choice(string id, string title) { Id = id; Title = title; }
            public override string ToString() { return Title; }
        }
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        private static void Assert(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Reference picker test: " + reason); }
        private static Record Add(Store store, string kind, params string[] fields)
        {
            var record = new Record(kind);
            for (int i = 0; i < fields.Length; i += 2) record.Set(fields[i], fields[i + 1]);
            store.Upsert(record); return store.Find(record.Id);
        }
        private static ReferencePickerCombo Combo(Store store, string kind, bool optional, params Record[] records)
        {
            var combo = new ReferencePickerCombo();
            combo.Items.Add(new Choice("", optional ? "— Без договора —" : "— Выберите контрагента —"));
            foreach (Record record in records)
                combo.Items.Add(new Choice(record.Id, record.Get("name") != "" ? record.Get("name") : record.Get("number")));
            combo.SelectedIndex = 0; combo.Configure(store, kind, "Проверка выбора", item => ((Choice)item).Id, optional);
            return combo;
        }
        private static Button Button(ReferencePickerDialog dialog, string name)
        { return (Button)dialog.Controls.Find(name, true).Single(); }
        private static void CloseModal(ReferencePickerDialog dialog, Action action)
        {
            dialog.Opacity = 0;
            dialog.Shown += delegate { dialog.BeginInvoke((MethodInvoker)delegate { action(); }); };
            dialog.ShowDialog();
        }
        private static string Id(ReferencePickerCombo combo)
        { return combo.SelectedItem == null ? "" : ((Choice)combo.SelectedItem).Id; }
        private static void TestParties(Store store, Record first, Record second, Record vendor, Record archived, Record excluded)
        {
            using (var combo = Combo(store, "party", false, first, second, vendor, archived))
            {
                combo.SelectedIndex = 1;
                using (var dialog = combo.CreateDialog(""))
                {
                    Assert(dialog.ResultIds.SequenceEqual(new[] { first.Id, second.Id, vendor.Id }), "default archive filtering or allowed ordering changed");
                    Assert(!dialog.ResultIds.Contains(excluded.Id), "an unrelated store record leaked into allowed choices");
                    dialog.SearchBox.Text = second.Get("unp"); dialog.ApplyQueryNow();
                    Assert(dialog.ResultIds.SequenceEqual(new[] { second.Id }), "UNP search merged duplicate names");
                    dialog.SearchBox.Text = "375291234567"; dialog.ApplyQueryNow();
                    Assert(dialog.ResultIds.SequenceEqual(new[] { second.Id }), "phone search failed for unformatted digits");
                    dialog.SearchBox.Text = "ЕЛКА тест"; dialog.ApplyQueryNow();
                    Assert(dialog.ResultIds.SequenceEqual(new[] { first.Id, second.Id }), "Cyrillic ё/е or multi-word search failed");
                    dialog.SearchBox.Text = ""; dialog.ApplyQueryNow();
                    dialog.SetFilter("role", "client");
                    Assert(dialog.ResultIds.SequenceEqual(new[] { first.Id, second.Id }), "client filter excluded the both-role party or included a vendor");
                    dialog.SetFilter("role", "vendor");
                    Assert(dialog.ResultIds.SequenceEqual(new[] { second.Id, vendor.Id }), "vendor filter excluded the both-role party");
                    dialog.SetFilter("role", ""); dialog.IncludeArchive(true);
                    Assert(dialog.ResultIds.Contains(archived.Id) && !dialog.ResultIds.Contains(excluded.Id), "archive expanded beyond allowed IDs");
                    dialog.SearchBox.Text = "no-such-record"; dialog.ApplyQueryNow();
                    Assert(dialog.ResultGrid.Rows.Count == 0 && !Button(dialog, "picker-select").Enabled, "empty result retained a selectable stale row");
                    Assert(!Button(dialog, "picker-clear").Visible, "required reference exposed a clear action");
                }
                combo.SelectedIndex = 4;
                using (var currentArchived = combo.CreateDialog(""))
                    Assert(currentArchived.ResultIds.Contains(archived.Id), "current allowed archived record disappeared from its chooser");
                combo.SelectedIndex = 1;
                using (var canceled = combo.CreateDialog(""))
                {
                    CloseModal(canceled, delegate { canceled.SearchBox.Text = second.Get("unp"); canceled.ApplyQueryNow(); Button(canceled, "picker-cancel").PerformClick(); });
                    Assert(canceled.DialogResult == DialogResult.Cancel && canceled.SelectedId == null && Id(combo) == first.Id, "cancel changed original selection");
                }
                using (var chosen = combo.CreateDialog(""))
                {
                    CloseModal(chosen, delegate
                    {
                        // Pressing Select during search debounce must use the just-entered query.
                        chosen.SearchBox.Text = second.Get("unp"); Button(chosen, "picker-select").PerformClick();
                    });
                    Assert(chosen.DialogResult == DialogResult.OK && chosen.SelectedId == second.Id, "pending search accepted a duplicate-name stale ID");
                }
                using (var selectedDetails = combo.CreateDialog(second.Get("unp")))
                {
                    TextBox details = (TextBox)selectedDetails.Controls.Find("picker-details", true).Single();
                    Assert(details.Text.Contains(second.Get("unp")) && details.Text.Contains(second.Get("bankAccount")), "selected full requisites were absent");
                }
                using (var owner = new Form { ShowInTaskbar = false, Opacity = 0 })
                {
                    combo.Dock = DockStyle.Top; owner.Controls.Add(combo); owner.Show(); Application.DoEvents();
                    int changes = 0; combo.SelectedIndexChanged += delegate { changes++; };
                    SendMessage(combo.Handle, 0x020A, new IntPtr(120 << 16), IntPtr.Zero); Application.DoEvents();
                    Assert(Id(combo) == first.Id && changes == 0 && !combo.DroppedDown, "mouse wheel silently switched the counterparty");
                    // Modal cancel must keep the actual ComboBox ID and its event contract.
                    using (var timer = new Timer { Interval = 10 })
                    {
                        timer.Tick += delegate
                        {
                            ReferencePickerDialog active = Application.OpenForms.OfType<ReferencePickerDialog>().FirstOrDefault();
                            if (active == null) return;
                            timer.Stop(); active.Opacity = 0; active.SearchBox.Text = second.Get("unp"); active.ApplyQueryNow();
                            Button(active, "picker-cancel").PerformClick();
                        };
                        timer.Start(); combo.OpenPicker("");
                    }
                    Assert(Id(combo) == first.Id && changes == 0, "OpenPicker/Cancel fired a selection change");
                    owner.Controls.Remove(combo); owner.Close();
                }
            }
        }
        private static Record Contract(Store store, Record party, string number, string status, string currency)
        {
            return Add(store, "contract", "number", number, "date", "2026-10-13", "partyId", party.Id, "direction", "income",
                "amount", "100.00", "currency", currency, "status", status);
        }
        private static void TestContracts(Store store, Record party, Record secondParty)
        {
            Record active = Contract(store, party, "FILTER-A-" + Guid.NewGuid().ToString("N"), "active", "BYN");
            Record completed = Contract(store, secondParty, "FILTER-C-" + Guid.NewGuid().ToString("N"), "complete", "USD");
            Record blocked = Contract(store, party, "FILTER-X-" + Guid.NewGuid().ToString("N"), "active", "EUR");
            using (var combo = Combo(store, "contract", true, active, completed))
            {
                combo.SelectedIndex = 1;
                using (var dialog = combo.CreateDialog(""))
                {
                    Assert(dialog.ResultIds.SequenceEqual(new[] { active.Id, completed.Id }) && !dialog.ResultIds.Contains(blocked.Id), "contract selector escaped allowed Items");
                    dialog.SetFilter("currency", "USD");
                    Assert(dialog.ResultIds.SequenceEqual(new[] { completed.Id }), "contract currency filter failed");
                    dialog.SetFilter("currency", ""); dialog.SetFilter("status", "active");
                    Assert(dialog.ResultIds.SequenceEqual(new[] { active.Id }), "contract status filter failed");
                    dialog.SetFilter("status", ""); dialog.SearchBox.Text = secondParty.Get("unp"); dialog.ApplyQueryNow();
                    Assert(dialog.ResultIds.SequenceEqual(new[] { completed.Id }), "contract search omitted the linked counterparty UNP");
                    dialog.SearchBox.Text = blocked.Get("number"); dialog.ApplyQueryNow();
                    Assert(dialog.ResultIds.Length == 0, "search leaked an incompatible contract");
                }
                using (var clear = combo.CreateDialog(""))
                {
                    CloseModal(clear, delegate { Button(clear, "picker-clear").PerformClick(); });
                    Assert(clear.DialogResult == DialogResult.OK && clear.SelectedId == "", "optional clear did not explicitly return the empty ID");
                    Assert(Id(combo) == active.Id, "creating/clearing a detached chooser changed the combo prematurely");
                }
                // OpenPicker resolves the accepted ID against the original objects, even for duplicate labels.
                using (var owner = new Form { Opacity = 0, ShowInTaskbar = false })
                {
                    owner.Controls.Add(combo); owner.Show(); Application.DoEvents(); int changes = 0;
                    combo.SelectedIndexChanged += delegate { changes++; };
                    using (var timer = new Timer { Interval = 10 })
                    {
                        timer.Tick += delegate
                        {
                            ReferencePickerDialog activeDialog = Application.OpenForms.OfType<ReferencePickerDialog>().FirstOrDefault();
                            if (activeDialog == null) return;
                            timer.Stop(); activeDialog.Opacity = 0; activeDialog.SearchBox.Text = completed.Get("number");
                            Button(activeDialog, "picker-select").PerformClick();
                        };
                        timer.Start(); combo.OpenPicker("");
                    }
                    Assert(Id(combo) == completed.Id && changes == 1, "accepted picker choice did not apply exactly one original ID/event");
                    using (var timer = new Timer { Interval = 10 })
                    {
                        timer.Tick += delegate
                        {
                            ReferencePickerDialog activeDialog = Application.OpenForms.OfType<ReferencePickerDialog>().FirstOrDefault();
                            if (activeDialog == null) return;
                            timer.Stop(); activeDialog.Opacity = 0; Button(activeDialog, "picker-clear").PerformClick();
                        };
                        timer.Start(); combo.OpenPicker("");
                    }
                    Assert(Id(combo) == "" && changes == 2, "explicit clear did not choose original placeholder once");
                    owner.Controls.Remove(combo); owner.Close();
                }
            }
        }
        public static void Run(string dataDir)
        {
            var store = new Store(Path.Combine(dataDir, "reference-picker"));
            Record first = Add(store, "party", "name", "Ёлка Тест", "role", "client", "unp", "111111111");
            Record second = Add(store, "party", "name", "Ёлка Тест", "role", "both", "unp", "222222222", "phone", "+375 29 123-45-67", "bankAccount", "BY00TEST123");
            Record vendor = Add(store, "party", "name", "Подрядчик", "role", "vendor", "unp", "333333333");
            Record archived = Add(store, "party", "name", "Архивный доступный", "role", "client"); store.Archive(archived.Id); archived = store.Find(archived.Id);
            Record excluded = Add(store, "party", "name", "Недоступный другой", "role", "client"); store.Archive(excluded.Id); excluded = store.Find(excluded.Id);
            long revision = store.Revision; byte[] bytes = File.ReadAllBytes(store.DataPath);
            TestParties(store, first, second, vendor, archived, excluded);
            Assert(store.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), "search/filter/select/cancel changed the ledger");
            TestContracts(store, first, second);
            revision = store.Revision; bytes = File.ReadAllBytes(store.DataPath);
            using (var combo = Combo(store, "party", false, first))
            using (var dialog = combo.CreateDialog("111111111"))
            { dialog.IncludeArchive(true); dialog.SearchBox.Text = "Контрагент"; dialog.ApplyQueryNow(); }
            Assert(store.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), "opening and disposing the chooser saved data");
            Console.WriteLine("PASS: reference chooser allowed IDs, duplicate names/UNP, linked search, role/status/currency/archive filters, full requisites, pending-query acceptance, native wheel suppression, actual modal select/clear/cancel, exact selection events and unchanged ledger.");
        }
    }
}