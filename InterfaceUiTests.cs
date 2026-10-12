using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class InterfaceUiTests
    {
        private static void Assert(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Interface UI test: " + reason);
        }
        private static T Field<T>(MainForm form, string name)
        {
            FieldInfo field = typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing integration field " + name);
            return (T)field.GetValue(form);
        }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control descendant in Descendants(child)) yield return descendant;
            }
        }
        private static Record Add(Store store, string kind, params string[] fields)
        {
            var record = new Record(kind);
            for (int index = 0; index < fields.Length; index += 2) record.Set(fields[index], fields[index + 1]);
            store.Upsert(record);
            return record;
        }
        private static UiPreferences ActsOnly(bool animations)
        {
            var preferences = new UiPreferences { AnimationsEnabled = animations };
            foreach (UiSection section in UiPreferences.Sections)
                preferences.SetVisible(section.Key, section.Key == "create-act" || section.Key == "party" || section.Key == "act");
            return preferences;
        }
        private static void ShowHidden(Form form)
        {
            form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
        }
        private static void AssertNavigation(MainForm form, UiPreferences expected)
        {
            FlowLayoutPanel nav = Field<FlowLayoutPanel>(form, "nav");
            foreach (UiSection section in UiPreferences.Sections)
            {
                Control button = nav.Controls.Cast<Control>().Single(control => Convert.ToString(control.Tag) == section.Key);
                Assert(button.Visible == expected.IsVisible(section.Key), "wrong visibility for " + section.Key);
            }
            Assert(Descendants(form).OfType<Button>().Any(button => button.Visible && button.Enabled
                && (button.Text == "Интерфейс" || Convert.ToString(button.Tag) == "interface")),
                "personal interface settings cannot be reached after hiding menu entries");
        }
        private static void AssertLedgerUnchanged(Store store, byte[] original, long revision, string reason)
        {
            Assert(store.Revision == revision && original.SequenceEqual(File.ReadAllBytes(store.DataPath)), reason);
        }
        private static void TestApplyAndRestart(Store store)
        {
            byte[] original = File.ReadAllBytes(store.DataPath); long revision = store.Revision;
            UiPreferences personal = ActsOnly(false);
            using (var form = new MainForm(store))
            {
                ShowHidden(form);
                Assert(Field<string>(form, "current") == "overview", "new user does not start in the default Overview");
                personal.Save(store.DataDir); form.ApplyInterfacePreferences(personal); Application.DoEvents();
                AssertNavigation(form, personal);
                Assert(Field<string>(form, "current") == "create-act", "hiding the active Overview did not select the first visible section");
                AssertLedgerUnchanged(store, original, revision, "saving or applying personal interface settings changed the ledger");
                personal.SetVisible("overview", true);
                Assert(!Field<UiPreferences>(form, "interfacePreferences").IsVisible("overview"),
                    "editing the supplied preferences object changed the live interface without applying it");
                form.Close();
            }
            using (var restarted = new MainForm(new Store(store.DataDir)))
            {
                ShowHidden(restarted);
                UiPreferences expected = ActsOnly(false);
                AssertNavigation(restarted, expected);
                Assert(Field<string>(restarted, "current") == "create-act", "saved hidden tabs were ignored during restart");
                Assert(!Field<UiPreferences>(restarted, "interfacePreferences").AnimationsEnabled, "animation preference was ignored during restart");
                AssertLedgerUnchanged(store, original, revision, "restarting after changing interface settings changed financial data");
                restarted.Close();
            }
        }
        private static void TestDraftAndHiddenActs(Store store, Record party)
        {
            byte[] original = File.ReadAllBytes(store.DataPath); long revision = store.Revision;
            using (var form = new MainForm(store))
            {
                ShowHidden(form); form.Navigate("create-act"); Application.DoEvents();
                HourlyActComposer composer = Field<HourlyActComposer>(form, "composer");
                composer.SelectCounterparty(party.Id);
                int row = composer.WorkGrid.Rows.Add("Проверка оборудования", "1,5", null, null);
                composer.WorkGrid.CurrentCell = composer.WorkGrid.Rows[row].Cells["description"];
                Assert(composer.WorkGrid.BeginEdit(true), "could not begin a work description edit");
                var editing = composer.WorkGrid.EditingControl as TextBox;
                Assert(editing != null, "work description did not use a text editor");
                editing.Text = "Проверка и настройка оборудования";
                UiPreferences hiddenCreate = ActsOnly(false); hiddenCreate.SetVisible("create-act", false);
                hiddenCreate.Save(store.DataDir); form.ApplyInterfacePreferences(hiddenCreate); Application.DoEvents();
                Assert(Field<string>(form, "current") == "party", "hiding Create Act did not select the first available section");
                Assert(Object.ReferenceEquals(composer, Field<HourlyActComposer>(form, "composer")) && !composer.IsDisposed,
                    "changing personal menu settings discarded an unfinished act");
                AssertNavigation(form, hiddenCreate);
                form.Navigate("create-act"); Application.DoEvents();
                Record draft = composer.BuildDraft();
                HourlyActLine work = Store.GetHourlyActLines(draft).Single();
                Assert(work.Description == "Проверка и настройка оборудования" && work.Hours == 1.5m && work.Rate == 35m,
                    "changing personal settings lost an active cell edit, entered hours or hourly rate");
                Assert(draft.Get("partyId") == party.Id && draft.Get("number") == "" && store.NextActNumber == 500,
                    "changing personal settings changed the selected counterparty or reserved an act number");
                AssertLedgerUnchanged(store, original, revision, "changing personal settings saved an unfinished act");
                UiPreferences hiddenActs = ActsOnly(false); hiddenActs.SetVisible("act", false);
                form.ApplyInterfacePreferences(hiddenActs); Application.DoEvents();
                Assert(Object.ReferenceEquals(composer, Field<HourlyActComposer>(form, "composer")), "applying preferences recreated the active composer");
                Record saved = composer.SaveDraft(); Application.DoEvents();
                Assert(saved.Get("number") == "500" && store.NextActNumber == 501 && store.Of("act").Count() == 1,
                    "saving after interface changes lost automatic act numbering or created duplicate acts");
                Assert(Field<string>(form, "current") == "act" && Field<TableData>(form, "table").Ids.Contains(saved.Id),
                    "saved act was not opened because its sidebar entry was hidden");
                AssertNavigation(form, hiddenActs);
                form.Close();
            }
            Store restarted = new Store(store.DataDir);
            Assert(restarted.Of("act").Count() == 1 && restarted.NextActNumber == 501,
                "restart lost an act saved with its sidebar section hidden");
            HourlyActLine savedWork = Store.GetHourlyActLines(restarted.Of("act").Single()).Single();
            Assert(savedWork.Description == "Проверка и настройка оборудования" && savedWork.Hours == 1.5m && savedWork.Amount == 52.5m,
                "saved work or financial calculation changed after interface settings");
        }
        private static void TestMotionDisable(Store store)
        {
            using (var form = new MainForm(store))
            {
                ShowHidden(form);
                var animated = new UiPreferences { AnimationsEnabled = true };
                form.ApplyInterfacePreferences(animated); form.Navigate("overview"); form.Navigate("act");
                object transition = Field<object>(form, "viewTransition");
                PropertyInfo running = transition.GetType().GetProperty("IsRunning");
                Assert(running != null && (bool)running.GetValue(transition, null), "enabled navigation did not start a view transition");
                animated.AnimationsEnabled = false;
                form.ApplyInterfacePreferences(animated); Application.DoEvents();
                Assert(!(bool)running.GetValue(transition, null), "disabling animations left an active view transition");
                form.Navigate("party"); form.Navigate("create-act"); form.Navigate("overview"); Application.DoEvents();
                Assert(!(bool)running.GetValue(transition, null), "disabled animations restarted on navigation");
                Assert(!Descendants(form).Any(control => control.GetType().Name == "TransitionOverlay"),
                    "disabling animations left an overlay covering the interface");
                form.Close();
                Assert(!(bool)running.GetValue(transition, null), "closing the form left a transition running");
            }
        }
        public static void Run(string tempdir)
        {
            string path = Path.Combine(tempdir, "interface-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            var store = new Store(Path.Combine(path, "data"));
            Add(store, "settings", "name", "Тестовый исполнитель", "currency", "BYN", "hourlyRate", "35.00", "unp", "123456789");
            Record party = Add(store, "party", "name", "Тестовый заказчик", "role", "client", "unp", "987654321");
            TestApplyAndRestart(store);
            TestDraftAndHiddenActs(store, party);
            TestMotionDisable(store);
            File.WriteAllText(Path.Combine(path, "PASS.txt"),
                "PASS: personal sidebar visibility and restart, emergency settings access, ledger isolation, active draft preservation, hidden Acts save flow, animation disable and cleanup.", Encoding.UTF8);
        }
    }
}