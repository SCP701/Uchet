using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
namespace WorkLedger
{
    public static class QuarterlyTaxesIntegrationTests
    {
        static void Assert(bool value, string why) { if (!value) throw new Exception("Tax integration: " + why); }
        static Record Add(Store store, string kind, params string[] values) {
            var r = new Record(kind); for (int i=0; i<values.Length; i+=2) r.Set(values[i],values[i+1]); store.Upsert(r); return r;
        }
        static T Field<T>(object instance,string name) { return (T)instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(instance); }
        public static void Run(string root)
        {
            string folder=Path.Combine(root,"tax-navigation-"+Guid.NewGuid().ToString("N"));
            var store=new Store(Path.Combine(folder,"data"));
            Add(store,"settings","name","Тестовый исполнитель","currency","BYN");
            var cp=Add(store,"party","name","Тестовый плательщик","role","both");
            var first=Add(store,"payment","number","T-1","date","2026-07-01","partyId",cp.Id,"direction","income","amount","4000","currency","BYN","method","bank");
            first.Set("archived","true"); store.Upsert(first);
            var second=Add(store,"payment","number","T-2","date","2026-09-30","partyId",cp.Id,"direction","income","amount","6000","currency","BYN","method","cash");
            Add(store,"payment","number","T-USD","date","2026-08-15","partyId",cp.Id,"direction","income","amount","100","currency","USD","method","other");
            Add(store,"payment","date","2026-08-15","partyId",cp.Id,"direction","expense","amount","500","currency","BYN","method","bank");
            Add(store,"payment","date","2026-06-30","partyId",cp.Id,"direction","income","amount","150","currency","BYN","method","bank");
            long revision=store.Revision; int audits=store.Audit.Count; byte[] bytes=File.ReadAllBytes(store.DataPath);
            using(var form=new MainForm(store))
            {
                form.ShowInTaskbar=false; form.Opacity=0;
                form.ApplyInterfacePreferences(new UiPreferences { AnimationsEnabled=false });
                form.Show(); Application.DoEvents(); form.Navigate("payment"); Application.DoEvents();
                var previousGrid=Field<DataGridView>(form,"grid");
                var selectedRow=previousGrid.Rows.Cast<DataGridViewRow>().Single(r=>Convert.ToString(r.Tag)==second.Id);
                previousGrid.ClearSelection(); previousGrid.CurrentCell=selectedRow.Cells[0]; selectedRow.Selected=true;
                Assert(previousGrid.SelectedRows.Count==1 && Convert.ToString(previousGrid.SelectedRows[0].Tag)==second.Id,"test starts with a genuinely selected payment");
                form.Navigate("taxes"); Application.DoEvents();
                Assert(!previousGrid.Visible && previousGrid.Rows.Count==0 && previousGrid.SelectedRows.Count==0,"tax navigation clears a real previous payment selection");
                form.Navigate("payment"); Application.DoEvents();
                Field<TextBox>(form,"search").Text="совершенно нет такого плательщика";
                Field<DateTimePicker>(form,"from").Value=new DateTime(2026,12,31);
                Field<DateTimePicker>(form,"to").Value=new DateTime(2026,1,1);
                Field<DateTimePicker>(form,"from").Checked=true; Field<DateTimePicker>(form,"to").Checked=true;
                Field<ComboBox>(form,"currency").SelectedItem="USD";
                form.RefreshView(); form.Navigate("taxes"); Application.DoEvents();
                var page=Field<QuarterlyTaxesPage>(form,"taxesPage");
                page.SetPeriod(2026,3,"BYN"); Application.DoEvents();
                Assert(Field<Label>(form,"title").Text=="Налоги" && page.Visible,"new navigation opens tax page");
                Assert(!Field<DataGridView>(form,"grid").Visible && Field<DataGridView>(form,"grid").Rows.Count==0,"hidden previous grid cannot retain a stale financial selection");
                Assert(!Field<FlowLayoutPanel>(form,"filters").Visible && !Field<FlowLayoutPanel>(form,"actions").Visible,"quarter page owns its selectors without general record actions");
                Assert(page.CurrentResult.Receipts==10000m && page.CurrentResult.Tax==2000m && page.CurrentResult.Remaining==8000m,"unrelated search, currency and invalid general date filters do not affect taxes");
                Assert(page.CurrentResult.Payments.Count==2,"archived received payment included once");
                page.SetPeriod(2026,3,"USD"); Assert(page.CurrentResult.Receipts==100m && page.CurrentResult.Tax==20m,"tax page currency changes independently");
                form.Navigate("payment"); Application.DoEvents();
                Assert(!page.Visible && Field<DataGridView>(form,"grid").Visible && Field<FlowLayoutPanel>(form,"filters").Visible && Field<FlowLayoutPanel>(form,"actions").Visible,"leaving restores payment view");
                form.Navigate("taxes"); Application.DoEvents();
                Assert(Field<QuarterlyTaxesPage>(form,"taxesPage")==page && page.SelectedYear==2026 && page.SelectedQuarter==3 && page.SelectedCurrency=="USD","tax selection and existing control survive navigation");
                var replacement=new Store(Path.Combine(folder,"replacement-data"));
                Add(replacement,"settings","currency","RUB");
                var replacementParty=Add(replacement,"party","name","Другой тестовый плательщик","role","both");
                var replacementPayment=Add(replacement,"payment","number","RESTORED","date","2026-08-10","partyId",replacementParty.Id,"direction","income","amount","325","currency","USD","method","bank");
                byte[] replacementBytes=File.ReadAllBytes(replacement.DataPath); long replacementRevision=replacement.Revision; int replacementAudits=replacement.Audit.Count;
                var previousPage=page;
                form.ReplaceStore(replacement); Application.DoEvents(); page=Field<QuarterlyTaxesPage>(form,"taxesPage");
                Assert(previousPage.IsDisposed && page!=previousPage && page.Visible && Field<string>(form,"current")=="taxes","store replacement disposes the old tax page and preserves the active section");
                Assert(page.SelectedYear==2026 && page.SelectedQuarter==3 && page.SelectedCurrency=="USD","restoring data preserves the selected tax period and currency");
                Assert(page.CurrentResult.Receipts==325m && page.CurrentResult.Tax==65m && page.CurrentResult.Remaining==260m && page.CurrentResult.Payments.Single().PaymentId==replacementPayment.Id,"tax calculation after restore uses only the replacement ledger");
                form.Navigate("payment"); Application.DoEvents();
                var anotherStore=new Store(Path.Combine(folder,"another-restored-data"));
                Add(anotherStore,"settings","currency","EUR");
                var anotherParty=Add(anotherStore,"party","name","Третий тестовый плательщик","role","both");
                Add(anotherStore,"payment","date","2026-09-01","partyId",anotherParty.Id,"direction","income","amount","50","currency","USD","method","cash");
                byte[] anotherBytes=File.ReadAllBytes(anotherStore.DataPath); long anotherRevision=anotherStore.Revision; int anotherAudits=anotherStore.Audit.Count;
                previousPage=page; form.ReplaceStore(anotherStore); Application.DoEvents(); page=Field<QuarterlyTaxesPage>(form,"taxesPage");
                Assert(previousPage.IsDisposed && Field<string>(form,"current")=="payment" && !page.Visible && Field<DataGridView>(form,"grid").Visible && Field<FlowLayoutPanel>(form,"actions").Visible && Field<FlowLayoutPanel>(form,"filters").Visible,"replacing a ledger while taxes are hidden preserves the payment view");
                form.Navigate("taxes"); Application.DoEvents();
                Assert(page.SelectedYear==2026 && page.SelectedQuarter==3 && page.SelectedCurrency=="USD" && page.CurrentResult.Receipts==50m && page.CurrentResult.Tax==10m && page.CurrentResult.Remaining==40m,"a cached hidden tax page also rebinds to restored data");
                var onlyTaxes=new UiPreferences { AnimationsEnabled=false };
                foreach(var section in UiPreferences.Sections) onlyTaxes.SetVisible(section.Key,section.Key=="taxes");
                form.ApplyInterfacePreferences(onlyTaxes); Application.DoEvents();
                Assert(Field<string>(form,"current")=="taxes" && page.Visible,"taxes can be the only visible main section");
                onlyTaxes.SetVisible("overview",true); onlyTaxes.SetVisible("taxes",false);
                form.ApplyInterfacePreferences(onlyTaxes); Application.DoEvents();
                Assert(Field<string>(form,"current")=="overview" && !page.Visible,"hiding active tax section returns to a visible section");
                Assert(replacement.Revision==replacementRevision && replacement.Audit.Count==replacementAudits && replacementBytes.SequenceEqual(File.ReadAllBytes(replacement.DataPath)),"replacing a store preserves the first restored ledger");
                Assert(anotherStore.Revision==anotherRevision && anotherStore.Audit.Count==anotherAudits && anotherBytes.SequenceEqual(File.ReadAllBytes(anotherStore.DataPath)),"replacing and navigating a store preserves the current restored ledger");
                form.Close();
            }
            Assert(store.Revision==revision && store.Audit.Count==audits && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)),"navigation and recalculation preserve ledger and history");
            Console.WriteLine("PASS: tax navigation, personal visibility, filter isolation, real selection clearing, restored store rebinding, quarter persistence and unchanged ledgers");
        }
    }
}
