using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class MonthlyServiceEditorTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception("Monthly service editor test failed: " + message);
        }

        private static Dictionary<string, Control> Inputs(RecordEditor editor)
        {
            return (Dictionary<string, Control>)typeof(RecordEditor).GetField("inputs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
        }

        private static void Save(RecordEditor editor)
        {
            typeof(RecordEditor).GetMethod("SaveClick", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(editor, new object[] { null, EventArgs.Empty });
        }

        private static string ChoiceValue(ComboBox combo)
        {
            object selected = combo.SelectedItem;
            return selected == null ? "" : (string)selected.GetType().GetField("Value").GetValue(selected);
        }

        private static void SelectChoice(ComboBox combo, string value)
        {
            for (int i = 0; i < combo.Items.Count; ++i)
            {
                object item = combo.Items[i];
                if ((string)item.GetType().GetField("Value").GetValue(item) == value)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            throw new Exception("Missing monthly currency choice: " + value);
        }

        private static DateTime DefaultStart()
        {
            DateTime today = DateTime.UtcNow.AddHours(3).Date;
            DateTime month = new DateTime(today.Year, today.Month, 1);
            return today.Day == 1 ? month : month.AddMonths(1);
        }

        public static void Run(string tempdir)
        {
            string dataDir = Path.Combine(tempdir, "monthly-editor-" + Guid.NewGuid().ToString("N"));
            Store store = new Store(dataDir);
            Record organization = new Record("settings");
            organization.Set("name", "Исполнитель"); organization.Set("currency", "USD");
            store.Upsert(organization);
            Record party = new Record("party");
            party.Set("name", "Клиент обслуживания"); party.Set("role", "client");
            party.Set("customField", "preserve-monthly-editor");
            store.Upsert(party);
            long initialRevision = store.Revision;
            DateTime selectedMonth = new DateTime(2030, 6, 1);
            using (RecordEditor editor = new RecordEditor(store, Catalog.Get("party"), store.Find(party.Id)))
            {
                Dictionary<string, Control> inputs = Inputs(editor);
                TabControl tabs = editor.Controls.OfType<TabControl>().Single();
                Assert(tabs.TabPages.Cast<TabPage>().Any(t => t.Text == "Обслуживание"), "service section missing");
                CheckBox enabled = inputs["monthlyServiceEnabled"] as CheckBox;
                Assert(enabled != null && !enabled.Checked, "monthly option is not an unchecked checkbox for legacy parties");
                foreach (string key in new[] { "monthlyServiceAmount", "monthlyServiceCurrency", "monthlyServiceDescription", "monthlyServiceStartMonth" })
                    Assert(!inputs[key].Enabled, "inactive monthly input is editable: " + key);
                DateTimePicker start = (DateTimePicker)inputs["monthlyServiceStartMonth"];
                Assert(start.Value.Date == DefaultStart() && !start.ShowCheckBox && start.ShowUpDown,
                    "first month should show upcoming first day and not require a second checkbox");
                Assert(ChoiceValue((ComboBox)inputs["monthlyServiceCurrency"]) == "USD", "organization currency not suggested");
                Assert(inputs["monthlyServiceDescription"].Text == "Ежемесячное обслуживание", "default monthly work description missing");
                Assert(store.Revision == initialRevision && store.Find(party.Id).Get("monthlyServiceEnabled") == "",
                    "opening editor changed legacy party");

                enabled.Checked = true;
                foreach (string key in new[] { "monthlyServiceAmount", "monthlyServiceCurrency", "monthlyServiceDescription", "monthlyServiceStartMonth" })
                    Assert(inputs[key].Enabled, "enabled monthly input is inactive: " + key);
                tabs.SelectedIndex = 0;
                Save(editor);
                Assert(editor.ResultRecord == null && store.Revision == initialRevision && tabs.SelectedTab.Text == "Обслуживание",
                    "missing fixed amount must focus service section and leave data unchanged");
                inputs["monthlyServiceAmount"].Text = "0";
                Save(editor);
                Assert(editor.ResultRecord == null && store.Revision == initialRevision, "zero monthly amount was accepted");
                inputs["monthlyServiceAmount"].Text = "1,234";
                Save(editor);
                Assert(editor.ResultRecord == null && store.Revision == initialRevision, "fractional cents monthly amount was accepted");
                inputs["monthlyServiceAmount"].Text = "2500,50";
                ((ComboBox)inputs["monthlyServiceCurrency"]).SelectedIndex = -1;
                Save(editor);
                Assert(editor.ResultRecord == null && store.Revision == initialRevision, "missing monthly currency was accepted");
                SelectChoice((ComboBox)inputs["monthlyServiceCurrency"], "EUR");
                Assert(((TextBox)inputs["monthlyServiceDescription"]).MaxLength == 2000, "monthly description limit is not exposed in editor");
                inputs["monthlyServiceDescription"].Text = new string('x', 2001);
                Save(editor);
                Assert(editor.ResultRecord == null && store.Revision == initialRevision, "overlong monthly description was not marked inline");
                inputs["monthlyServiceDescription"].Text = "";
                start.Value = new DateTime(selectedMonth.Year, selectedMonth.Month, 19);
                Save(editor);
                Assert(editor.DialogResult == DialogResult.OK && editor.ResultRecord != null, "valid monthly service settings were not saved");
            }

            Store reloaded = new Store(dataDir);
            Record saved = reloaded.Find(party.Id);
            Assert(saved.Get("monthlyServiceEnabled") == "true" && saved.Get("monthlyServiceAmount") == "2500.50"
                && saved.Get("monthlyServiceCurrency") == "EUR", "checkbox or fixed amount/currency did not survive reload");
            Assert(saved.Get("monthlyServiceStartMonth") == "2030-06-01" && saved.Get("monthlyServiceDescription") == "Ежемесячное обслуживание",
                "first month not normalized or blank description not defaulted");
            Assert(saved.Get("customField") == "preserve-monthly-editor", "unknown counterparty data lost");

            using (RecordEditor editor = new RecordEditor(reloaded, Catalog.Get("party"), saved))
            {
                Dictionary<string, Control> inputs = Inputs(editor);
                Assert(((CheckBox)inputs["monthlyServiceEnabled"]).Checked && inputs["monthlyServiceAmount"].Enabled,
                    "enabled service checkbox did not restore");
                Assert(((DateTimePicker)inputs["monthlyServiceStartMonth"]).Value.Date == selectedMonth
                    && ChoiceValue((ComboBox)inputs["monthlyServiceCurrency"]) == "EUR", "saved month or currency not shown");
                ((CheckBox)inputs["monthlyServiceEnabled"]).Checked = false;
                inputs["monthlyServiceAmount"].Text = "invalid stale amount";
                ((ComboBox)inputs["monthlyServiceCurrency"]).SelectedIndex = -1;
                ((DateTimePicker)inputs["monthlyServiceStartMonth"]).Checked = false;
                Save(editor);
                Assert(editor.ResultRecord != null && editor.DialogResult == DialogResult.OK,
                    "invalid stale inputs prevented disabling monthly service");
            }
            Record disabled = new Store(dataDir).Find(party.Id);
            Assert(disabled.Get("monthlyServiceEnabled") == "false" && disabled.Get("monthlyServiceAmount") == "2500.50"
                && disabled.Get("monthlyServiceCurrency") == "EUR" && disabled.Get("monthlyServiceStartMonth") == "2030-06-01",
                "disabling service destroyed saved configuration");

            reloaded = new Store(dataDir);
            using (RecordEditor editor = new RecordEditor(reloaded, Catalog.Get("party"), disabled))
            {
                Dictionary<string, Control> inputs = Inputs(editor);
                Assert(!((CheckBox)inputs["monthlyServiceEnabled"]).Checked && !inputs["monthlyServiceAmount"].Enabled,
                    "disabled checkbox did not restore");
                Assert(((DateTimePicker)inputs["monthlyServiceStartMonth"]).Value.Date == DefaultStart(),
                    "re-enabling must suggest a new first month instead of silently backfilling old months");
                ((CheckBox)inputs["monthlyServiceEnabled"]).Checked = true;
                ((DateTimePicker)inputs["monthlyServiceStartMonth"]).Value = new DateTime(2022, 5, 22);
                inputs["monthlyServiceDescription"].Text = "Обслуживание систем";
                Save(editor);
                Assert(editor.ResultRecord != null, "explicit past first month was not saved");
            }
            Record restarted = new Store(dataDir).Find(party.Id);
            Assert(restarted.Get("monthlyServiceStartMonth") == "2022-05-01" && restarted.Get("monthlyServiceDescription") == "Обслуживание систем",
                "explicit first month or work description changed");
            Assert(restarted.Get("customField") == "preserve-monthly-editor", "reload/re-enable lost unknown fields");

            reloaded = new Store(dataDir);
            Record legacyDisabled = new Record("party");
            legacyDisabled.Set("name", "Отключённый клиент"); legacyDisabled.Set("role", "client");
            legacyDisabled.Set("monthlyServiceEnabled", "false"); legacyDisabled.Set("monthlyServiceAmount", "old invalid amount");
            legacyDisabled.Set("monthlyServiceCurrency", "old invalid currency"); legacyDisabled.Set("monthlyServiceStartMonth", "old invalid date");
            reloaded.Upsert(legacyDisabled);
            using (RecordEditor editor = new RecordEditor(reloaded, Catalog.Get("party"), reloaded.Find(legacyDisabled.Id)))
            {
                Inputs(editor)["name"].Text = "Исправленное название";
                Save(editor);
                Assert(editor.ResultRecord != null, "disabled legacy configuration prevented unrelated party changes");
            }
            Record preserved = new Store(dataDir).Find(legacyDisabled.Id);
            Assert(preserved.Get("name") == "Исправленное название" && preserved.Get("monthlyServiceAmount") == "old invalid amount"
                && preserved.Get("monthlyServiceCurrency") == "old invalid currency" && preserved.Get("monthlyServiceStartMonth") == "old invalid date",
                "inactive legacy service configuration was rewritten");
            Console.WriteLine("PASS: monthly service checkbox, first-month suggestions, positive exact-cent validation, save/reload, disabling stale configuration and unknown-field preservation.");
        }
    }
}
