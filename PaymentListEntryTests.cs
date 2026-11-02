using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class PaymentListEntryTests
    {
        private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException("Payment list entry: " + reason); }
        private static T Field<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static Record Add(Store store, string kind, params string[] fields)
        {
            var record = new Record(kind);
            for (int i = 0; i < fields.Length; i += 2) record.Set(fields[i], fields[i + 1]);
            store.Upsert(record); return record;
        }
        private static Record Act(Store store, Record party, string number, bool external)
        { return Add(store, "act", "number", number, "partyId", party.Id, "date", "2026-10-01", "amount", "200.00", "currency", "BYN", "direction", "income", "status", "draft", "accountingScope", external ? "external" : "standard"); }
        private static void Show(Form form)
        { form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents(); }
        private static void EnterFromList(MainForm form, string actNumber, bool cancel)
        {
            int phase = 0; Exception failure = null; DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            using (var timer = new Timer { Interval = 50 })
            {
                timer.Tick += delegate
                {
                    try
                    {
                        if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Payment workflow timed out");
                        var picker = Application.OpenForms.OfType<ReferencePickerDialog>().FirstOrDefault();
                        if (phase == 0 && picker != null)
                        {
                            picker.Opacity = 0; picker.SearchBox.Text = actNumber; picker.ApplyQueryNow();
                            Check(picker.ResultIds.Length == 1, "picker finds the requested act"); phase = 1;
                            ((Button)picker.Controls.Find("picker-select", true).Single()).PerformClick();
                        }
                        var payment = Application.OpenForms.OfType<ManualPaymentDialog>().FirstOrDefault();
                        if (phase == 1 && payment != null)
                        {
                            payment.Opacity = 0; phase = 2;
                            if (cancel) { payment.DialogResult = DialogResult.Cancel; payment.Close(); return; }
                            var amount = Field<TextBox>(payment, "amount"); Check(amount.Text == "", "new installment amount starts empty");
                            amount.Text = "100,00";
                            Check(payment.SaveConfirmation(), "individual payment saves from payment section");
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex; timer.Stop();
                        foreach (Form open in Application.OpenForms.Cast<Form>().Where(f => f != form).ToList()) { open.DialogResult = DialogResult.Cancel; open.Close(); }
                    }
                };
                timer.Start(); ((Button)form.Controls.Find("recordPayment", true).Single()).PerformClick(); timer.Stop();
            }
            if (failure != null) throw failure;
            Check(phase == 2, "button opens act picker and payment form"); Application.DoEvents();
        }
        public static void Run(string root)
        {
            Directory.CreateDirectory(root);
            foreach (bool external in new[] { false, true })
            {
                string path = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 8)); var store = new Store(path);
                Add(store, "settings", "name", "ИП Тестовый", "currency", "BYN");
                var party = Add(store, "party", "name", "ООО Тестовый заказчик", "role", "client");
                var act = Act(store, party, "ОПЛ-200", external); var cancelled = Act(store, party, "ОТМЕНЕН", external);
                cancelled.Set("status", "cancelled"); store.Upsert(cancelled);
                var archived = Act(store, party, "АРХИВ", external); store.Archive(archived.Id);
                Act(store, party, "ДРУГОЙ-РАЗДЕЛ", !external);
                string section = external ? Catalog.GrayPaymentSection : "payment";
                using (var main = new MainForm(store, false))
                {
                    main.Size = new Size(1120, 700); Show(main); main.Navigate(section); main.ResetFilters(); Application.DoEvents();
                    using (var picker = main.CreatePaymentActPicker()) Check(picker.ResultIds.SequenceEqual(new[] { act.Id }), "picker excludes cancelled, archived and opposite-scope acts");
                    byte[] before = File.ReadAllBytes(store.DataPath); EnterFromList(main, act.Get("number"), true);
                    Check(before.SequenceEqual(File.ReadAllBytes(store.DataPath)), "cancelling from payments creates nothing");
                    for (int i = 1; i <= 2; ++i)
                    {
                        Field<TextBox>(main, "search").Text = "скрыть все платежи"; Application.DoEvents();
                        EnterFromList(main, act.Get("number"), false);
                        var grid = Field<DataGridView>(main, "grid"); var payments = store.Of("payment").ToList();
                        Check(payments.Count == i && grid.Rows.Count == i, "each installment appears as one separate payment row despite previous filters");
                        var amountColumn = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.HeaderText == "Сумма");
                        var actColumn = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.HeaderText == "Акт");
                        Check(actColumn.Visible, "linked act column is visible in compact layout");
                        Check(grid.Rows.Cast<DataGridViewRow>().All(r => Store.ParseMoney(Convert.ToString(r.Cells[amountColumn.Index].Value)) == 100m
                            && Convert.ToString(r.Cells[actColumn.Index].Value).Contains("ОПЛ-200")), "payment rows show individual amount and linked act");
                        var balance = new Store(path).GetManualPaymentView(act.Id).Balance;
                        Check(balance.Paid == i * 100m && balance.Due == 200m - i * 100m, "newly entered payments reduce the act balance and survive reload");
                    }
                    bool noUnpaid = false; try { using (main.CreatePaymentActPicker()) { } } catch (InvalidOperationException) { noUnpaid = true; }
                    Check(noUnpaid, "fully paid act cannot be selected for another receipt");
                    using (var image = new Bitmap(main.Width, main.Height))
                    { main.DrawToBitmap(image, new Rectangle(0, 0, main.Width, main.Height)); image.Save(Path.Combine(root, external ? "gray-payments.png" : "payments.png")); }
                }
                using (var reopened = new MainForm(new Store(path), false))
                {
                    Show(reopened); reopened.Navigate(section); reopened.ResetFilters(); Application.DoEvents();
                    Check(Field<DataGridView>(reopened, "grid").Rows.Count == 2, "both payments remain listed after reopening");
                }
            }
            File.WriteAllText(Path.Combine(root, "PASS.txt"), "PASS: payment-list entry, act selection scope, cancel, two installments, visible act/amount, filter reset, paid-act exclusion and restart.");
        }
    }
}
