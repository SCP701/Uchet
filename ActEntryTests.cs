using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class ActEntryTests
    {
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Act entry: " + message); }
        private static T Field<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static Record Add(Store store, string kind, params string[] fields)
        {
            var record = new Record(kind);
            for (int i = 0; i < fields.Length; i += 2) record.Set(fields[i], fields[i + 1]);
            store.Upsert(record); return record;
        }
        private static void Show(Form form)
        { form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents(); }
        private static void Render(Control control, string path)
        { using (var bitmap = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(bitmap, new Rectangle(0, 0, control.Width, control.Height)); bitmap.Save(path); } }
        public static void Run(string root)
        {
            Directory.CreateDirectory(root);
            foreach (bool external in new[] { false, true })
            {
                string path = Path.Combine(root, (external ? "outside-" : "normal-") + Guid.NewGuid().ToString("N").Substring(0, 8));
                var store = new Store(path);
                Add(store, "settings", "name", "ИП Тестовый исполнитель", "currency", "BYN", "hourlyRate", "50.00");
                Record party = Add(store, "party", "name", "ООО Тестовый заказчик", "role", "client");
                Record act;
                using (var host = new Form { Width = 1060, Height = 720 })
                using (var composer = new HourlyActComposer(store, null, external))
                {
                    host.Controls.Add(composer); Show(host); composer.SelectCounterparty(party.Id);
                    var number = Field<TextBox>(composer, "number");
                    Check(!number.ReadOnly && number.Text == "", "new composer number is editable and automatic by default");
                    number.Text = "  12/2026-А  "; composer.RefreshDefaults();
                    composer.WorkGrid.Rows.Add("Обслуживание", "2", "100", null);
                    Check(composer.BuildDraft().Get("number") == "12/2026-А" && store.NextActNumber == 500, "preview keeps custom number without reserving it");
                    Render(host, Path.Combine(root, external ? "create-external.png" : "create-act.png"));
                    act = composer.SaveDraft();
                    Check(act.Get("number") == "12/2026-А" && Store.IsExternalAct(act) == external, "manual number and scope saved");
                }
                Record automatic = store.CreateHourlyActDraft(party.Id, external);
                Store.SetHourlyActLines(automatic, new[] { new HourlyActLine { Description = "Работа", Hours = 1m, Rate = 50m } }); store.Upsert(automatic);
                using (var editor = new RecordEditor(store, Catalog.Get(external ? Catalog.ExternalActSection : "act"), automatic))
                {
                    Show(editor);
                    var number = (TextBox)Field<Dictionary<string, Control>>(editor, "inputs")["number"];
                    Check(!number.ReadOnly, "automatic number can be edited in the act card"); number.Text = "750";
                    store.Upsert(editor.PrepareRecord());
                }
                automatic = store.Find(automatic.Id);
                Check(automatic.Get("number") == "750" && automatic.Get("autoNumber") == "false" && store.NextActNumber == 751, "manual numeric edit advances counter");
                using (var editor = new RecordEditor(store, Catalog.Get(external ? Catalog.ExternalActSection : "act"), store.Find(automatic.Id)))
                {
                    Show(editor); ((TextBox)Field<Dictionary<string, Control>>(editor, "inputs")["number"]).Text = "750-ИСПР";
                    Field<TabControl>(editor, "sections").SelectedIndex = 1; Application.DoEvents();
                    Render(editor, Path.Combine(root, external ? "card-external.png" : "card.png"));
                    Field<Button>(editor, "save").PerformClick(); Application.DoEvents();
                    Check(editor.ResultRecord != null && editor.ResultRecord.Get("number") == "750-ИСПР", "card Save button persists custom number");
                }
                automatic.Set("number", "Р-1"); store.Upsert(automatic);
                Check(new Store(path).NextActNumber == 751, "renumbering down cannot lower persisted counter");
                var duplicate = store.Find(automatic.Id); duplicate.Set("number", act.Get("number"));
                byte[] before = File.ReadAllBytes(store.DataPath); bool rejected = false;
                try { store.Upsert(duplicate); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && before.SequenceEqual(File.ReadAllBytes(store.DataPath)), "duplicate manual number rejected atomically");
                foreach (bool signed in new[] { false, true })
                {
                    var current = store.Find(act.Id);
                    if (signed)
                    {
                        current.Set("status", "signed"); store.Upsert(current);
                    }
                    using (var dialog = new ManualPaymentDialog(store, act.Id))
                    {
                        Show(dialog); var amount = Field<TextBox>(dialog, "amount");
                        Check(amount.Text == "", "each new payment starts with an empty amount");
                        byte[] unchanged = File.ReadAllBytes(store.DataPath);
                        Check(!dialog.SaveConfirmation() && unchanged.SequenceEqual(File.ReadAllBytes(store.DataPath)), "empty payment cannot record the whole balance");
                        amount.Text = "100,00";
                        if (!signed)
                        {
                            Check(Field<Label>(dialog, "information").Text.Contains(100m.ToString("N2")) && Field<Label>(dialog, "information").Text.Contains("Частично оплачен"), "act 200 minus this receipt 100 previews remaining 100");
                            Render(dialog, Path.Combine(root, external ? "payment-external.png" : "payment.png"));
                        }
                        else Check(Field<Label>(dialog, "information").Text.Contains("Оплачен"), "second separate receipt of 100 completes payment");
                        Check(dialog.SaveConfirmation(), "payment form saved amount");
                    }
                    store = new Store(path);
                    var balance = store.GetManualPaymentView(act.Id).Balance;
                    Check(balance.Paid == (signed ? 200m : 100m) && balance.Due == (signed ? 0m : 100m), "200 minus 100 leaves 100; another 100 settles it after restart");
                    Check(balance.Status == (signed ? "Оплачен" : "Частично оплачен"), "payment status reflects actual payments");
                    using (var main = new MainForm(store, false))
                    {
                        Show(main); main.Navigate(external ? Catalog.ExternalActSection : "act"); Application.DoEvents();
                        var button = main.Controls.Find("recordPayment", true).Single(); Check(button.Visible, "direct payment action visible");
                        var grid = Field<DataGridView>(main, "grid");
                        var row = grid.Rows.Cast<DataGridViewRow>().Single(r => Convert.ToString(r.Tag) == act.Id);
                        var due = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.HeaderText == "К оплате");
                        Check(Store.ParseMoney(Convert.ToString(row.Cells[due.Index].Value).Replace("\u00a0", "")) == balance.Due, "list shows partial remainder even before signing");
                    }
                }
                var payments = store.Of("payment").ToList();
                Check(payments.Count == 2 && payments.All(p => Store.ParseMoney(p.Get("amount")) == 100m), "two equal receipts stay as separate payments of 100");
                Check(store.ActDue(act.Id) == 0 && store.Of("allocation").Count() == 2, "signing and final installment credit each payment once");
                act = store.Find(act.Id); act.Set("number", "ИТОГ-12/2026"); store.Upsert(act);
                Check(store.Of("payment").All(p => p.Get("actId") == act.Id) && store.ActDue(act.Id) == 0, "renumbering preserves payments and allocations by id");
                string doc = Path.Combine(root, external ? "external.docx" : "act.docx"); ActDocument.Export(store, act, doc);
                using (var archive = ZipFile.OpenRead(doc))
                using (var reader = new StreamReader(archive.GetEntry("word/document.xml").Open()))
                    Check(reader.ReadToEnd().Contains("ИТОГ-12/2026"), "Word contains user number");
                Check(QuarterlyTaxes.CalculateAllTime(store, "BYN").Receipts == (external ? 0m : 200m), "tax base counts each eligible payment once");
            }
            File.WriteAllText(Path.Combine(root, "PASS.txt"), "PASS: manual numbers, duplicate rollback, counter persistence, Word, partial and final installments, signature transition, list balances and taxes.");
        }
    }
}
