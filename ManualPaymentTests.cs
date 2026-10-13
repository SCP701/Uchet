using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
namespace WorkLedger
{
    public static class ManualPaymentTests
    {
        private static int checks;
        private static readonly DateTime Today=new DateTime(2026,10,13);
        private static void Check(bool ok,string reason){if(!ok)throw new Exception("Manual payment: "+reason);checks++;}
        private static Record Add(Store s,string kind,params string[] fields){var r=new Record(kind);for(int i=0;i<fields.Length;i+=2)r.Set(fields[i],fields[i+1]);s.Upsert(r);return s.Find(r.Id);}
        private sealed class Fixture
        {
            public Store Store;public string Dir;public Record Party,Act;
            public Fixture(string root,string name,bool service=false,bool signed=false,bool external=false,string direction="income")
            {
                Dir=Path.Combine(root,name);Directory.CreateDirectory(Dir);Store=new Store(Dir);
                Add(Store,"settings","currency","BYN","name","Тестовый исполнитель");
                Party=Add(Store,"party","name","Тестовый заказчик","role","both","email","customer@example.test");
                if(service)
                {
                    Party.Set("monthlyServiceEnabled","true");Party.Set("monthlyServiceAmount","120.00");Party.Set("monthlyServiceCurrency","BYN");Party.Set("monthlyServiceStartMonth","2026-10-01");Store.Upsert(Party);
                    Act=Store.GenerateMonthlyServiceActs(new DateTime(2026,10,1)).Single();
                }
                else Act=Add(Store,"act","number","MANUAL-500","date","2026-10-01","partyId",Party.Id,"amount","100.00","currency","BYN","direction",direction,"status",signed?"signed":"draft","signatureStatus","unknown","accountingScope",external?"external":"standard");
            }
            public Record Receipt(decimal amount,string currency="BYN",string direction="income",string scope="standard",string method="bank")
            {return Add(Store,"payment","date","2026-10-09","partyId",Party.Id,"amount",Store.MoneyString(amount),"currency",currency,"direction",direction,"method",method,"paymentScope",scope,"number","BANK-900","notes","Платёж из банковской выписки");}
            public Record Confirm(decimal amount,string existing=null,string method="cash",string key=null)
            {
                var view=Store.GetManualPaymentView(Act.Id,Today);
                return Store.ConfirmActPayment(Act.Id,existing,amount,new DateTime(2026,10,9),method,"MANUAL-REF","Подтверждено по платёжному документу",view.Revision,key??Guid.NewGuid().ToString("N"),Today);
            }
            public ServiceBalance Balance(){return ServiceReminderLedger.BalanceForAct(Store.Records,Act.Id,Today);}
            public decimal Received(){return Store.Records.Where(r=>r.Kind=="payment"&&r.Get("direction")=="income").Sum(r=>Store.ParseMoney(r.Get("amount")));}
        }
        private sealed class Sender:IServiceMailSender
        {
            public List<ServiceMailPlan> Sent=new List<ServiceMailPlan>();
            public void Send(ServiceMailSettings settings,ServiceMailPlan plan,string id){Sent.Add(plan);}
        }
        private static void RejectUnchanged(Store store,Action action,string reason)
        {
            var before=File.ReadAllBytes(store.DataPath);long revision=store.Revision;bool rejected=false;
            try{action();}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&revision==store.Revision&&before.SequenceEqual(File.ReadAllBytes(store.DataPath)),reason);
        }
        private static IEnumerable<Control> All(Control c){yield return c;foreach(Control child in c.Controls)foreach(Control item in All(child))yield return item;}
        private static T Field<T>(object target,string name){return (T)target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(target);}
        public static void Run(string root)
        {
            checks=0;Directory.CreateDirectory(root);var f=new Fixture(root,"partial-service",true);long revision=f.Store.Revision;
            var partial=f.Confirm(40m);
            Check(f.Store.Revision==revision+1&&f.Store.Records.Count(r=>r.Kind=="payment")==1,"one atomic receipt for partial service payment");
            Check(f.Balance().Paid==40m&&f.Balance().Due==80m&&f.Balance().Status=="Частично оплачен","partial confirmation reduces service balance");
            Check(f.Store.Find(f.Act.Id).Get("status")=="draft"&&f.Store.Find(f.Act.Id).Get("signatureStatus")==f.Act.Get("signatureStatus")&&f.Store.Records.All(r=>r.Kind!="allocation"),"unknown signature is preserved and not silently signed");
            Check(QuarterlyTaxes.Calculate(f.Store,2026,4,"BYN").Receipts==40m,"manual ordinary receipt included in actual receipt tax base");
            var settings=new ServiceMailSettings{Enabled=true,StartMonth="2026-10",Host="smtp.example.test",FromAddress="sender@example.test"};settings.Save(f.Dir,"");
            var sender=new Sender();ServiceReminderRuntime.Process(f.Dir,new DateTime(2026,10,10,6,0,0,DateTimeKind.Utc),sender);
            Check(sender.Sent.Count==1&&sender.Sent[0].Balance.Due==80m&&sender.Sent[0].Body.Contains("80"),"reminder uses partial remaining amount");
            f.Confirm(80m);ServiceReminderRuntime.Process(f.Dir,new DateTime(2026,10,13,6,0,0,DateTimeKind.Utc),sender);
            Check(f.Balance().Status=="Оплачен"&&sender.Sent.Count==1,"manual full settlement prevents follow-up email");
            Check(ServiceMailHistory.Load(f.Dir).Entries.Single().State=="sent","previous sent mail history remains intact");
            Check(f.Store.Audit.Count(a=>a.Summary.Contains("Ручное подтверждение оплаты"))==2&&partial.Get("manualConfirmedBy")!=""&&partial.Get("manualConfirmedUtc")!="","manual confirmations record amount user and time in audit");
            RejectUnchanged(f.Store,delegate{f.Confirm(120m);},"fresh repeat cannot create another fully paid receipt");
            var signed=f.Store.Find(f.Act.Id);signed.Set("status","signed");f.Store.Upsert(signed);
            Check(f.Store.ActDue(f.Act.Id)==0m&&f.Store.Records.Count(r=>r.Kind=="allocation")==2,"confirming later signature credits pending manual receipts once");
            f.Store.Upsert(signed);Check(f.Store.Records.Count(r=>r.Kind=="allocation")==2,"resaving signed act does not repeat pending credit");
            var pending=new Fixture(root,"later-partial-signature");pending.Confirm(40m);signed=pending.Store.Find(pending.Act.Id);signed.Set("status","signed");pending.Store.Upsert(signed);
            Check(pending.Store.ActDue(pending.Act.Id)==60m&&pending.Balance().Paid==40m,"later signature credits only actual partial amount");
            f=new Fixture(root,"idempotent-signed",false,true);var initial=f.Store.GetManualPaymentView(f.Act.Id,Today);string key=Guid.NewGuid().ToString("N");
            var receipt=f.Store.ConfirmActPayment(f.Act.Id,null,60m,new DateTime(2026,10,9),"cash","REF","",initial.Revision,key,Today);revision=f.Store.Revision;
            Check(f.Store.Records.Count(r=>r.Kind=="allocation")==1&&f.Store.ActDue(f.Act.Id)==40m&&f.Balance().Paid==60m,"signed manual receipt is atomically credited to act debt");
            var bytes=File.ReadAllBytes(f.Store.DataPath);var same=f.Store.ConfirmActPayment(f.Act.Id,null,60m,new DateTime(2026,10,9),"cash","REF","",initial.Revision,key,Today);
            Check(same.Id==receipt.Id&&revision==f.Store.Revision&&bytes.SequenceEqual(File.ReadAllBytes(f.Store.DataPath)),"double submission returns original confirmation without duplicates");
            RejectUnchanged(f.Store,delegate{f.Store.ConfirmActPayment(f.Act.Id,null,50m,Today,"cash","","",initial.Revision,key,Today);},"same operation cannot be reused for a different amount");
            f=new Fixture(root,"existing-signed",false,true);var bank=f.Receipt(100m);decimal funds=f.Received();
            RejectUnchanged(f.Store,delegate{f.Confirm(100m);},"unassigned bank receipt requires choosing existing payment");
            f.Confirm(40m,bank.Id);
            Check(f.Store.Records.Count(r=>r.Kind=="payment")==1&&f.Received()==funds&&QuarterlyTaxes.Calculate(f.Store,2026,4,"BYN").Receipts==100m,"existing bank confirmation does not duplicate money or tax base");
            Check(f.Store.ActDue(f.Act.Id)==60m&&f.Store.PaymentUnallocated(bank.Id)==60m,"partial existing receipt credits only confirmed amount");
            Check(f.Store.Find(bank.Id).Get("notes")==bank.Get("notes")&&f.Store.Find(bank.Id).Get("date")==bank.Get("date")&&f.Store.Find(bank.Id).Get("amount")==bank.Get("amount"),"bank details and purpose remain unchanged");
            f=new Fixture(root,"existing-draft-service",true);bank=f.Receipt(120m);f.Confirm(120m,bank.Id);
            Check(f.Store.Find(bank.Id).Get("actId")==f.Act.Id&&f.Balance().Due==0&&f.Received()==120m&&f.Store.Records.Count(r=>r.Kind=="payment")==1,"existing receipt links to unknown-signature service act without another receipt");
            Check(f.Store.Find(f.Act.Id).Get("status")=="draft"&&f.Store.Records.All(r=>r.Kind!="allocation"),"manual bank linking preserves service signature");
            f=new Fixture(root,"existing-draft-partial");bank=f.Receipt(100m);
            RejectUnchanged(f.Store,delegate{f.Confirm(40m,bank.Id);},"unsupported partial direct link cannot consume whole existing receipt by mistake");
            f=new Fixture(root,"grey",false,true,true);receipt=f.Confirm(100m);var accounting=PaymentAccounting.Build(f.Store.Records).For(receipt);
            Check(receipt.Get("paymentScope")=="external"&&accounting.GreyAmount==100m&&accounting.WhiteAmount==0m,"act outside creates only grey receipt");
            Check(QuarterlyTaxes.Calculate(f.Store,2026,4,"BYN").Receipts==0m&&f.Received()==100m&&f.Store.ActDue(f.Act.Id)==0m,"grey manual payment settles debt and is excluded from tax receipts");
            f=new Fixture(root,"expense",false,true,false,"expense");receipt=f.Confirm(100m);
            Check(receipt.Get("direction")=="expense"&&f.Received()==0m&&QuarterlyTaxes.Calculate(f.Store,2026,4,"BYN").Receipts==0m&&f.Store.ActDue(f.Act.Id)==0m,"outgoing act confirmation is not counted as income");
            f=new Fixture(root,"wrong-receipts",false,true);bank=f.Receipt(100m,"USD");var outgoing=f.Receipt(100m,"BYN","expense");
            Check(!f.Store.GetManualPaymentView(f.Act.Id,Today).Payments.Any(),"wrong currency and direction are excluded from candidates");
            RejectUnchanged(f.Store,delegate{f.Confirm(100m,bank.Id);},"wrong currency cannot be confirmed for act");
            RejectUnchanged(f.Store,delegate{f.Confirm(100m,outgoing.Id);},"outgoing receipt cannot settle incoming act");
            f=new Fixture(root,"invalid-new",false,true);
            foreach(decimal bad in new[]{0m,-1m,100.01m,0.001m}){decimal amount=bad;RejectUnchanged(f.Store,delegate{f.Confirm(amount);},"invalid or excessive amount rejected atomically "+bad);}
            var view=f.Store.GetManualPaymentView(f.Act.Id,Today);
            RejectUnchanged(f.Store,delegate{f.Store.ConfirmActPayment(f.Act.Id,null,100m,Today.AddDays(1),"cash","","",view.Revision,Guid.NewGuid().ToString("N"),Today);},"future received payment rejected");
            RejectUnchanged(f.Store,delegate{f.Store.ConfirmActPayment(f.Act.Id,null,100m,Today,"invalid","","",view.Revision,Guid.NewGuid().ToString("N"),Today);},"invalid payment method rejected");
            view=f.Store.GetManualPaymentView(f.Act.Id,Today);var party=f.Store.Find(f.Party.Id);party.Set("notes","Изменено после открытия формы");f.Store.Upsert(party);
            RejectUnchanged(f.Store,delegate{f.Store.ConfirmActPayment(f.Act.Id,null,100m,Today,"cash","","",view.Revision,Guid.NewGuid().ToString("N"),Today);},"stale confirmation refuses changed ledger");
            f=new Fixture(root,"parallel-disk",false,true);view=f.Store.GetManualPaymentView(f.Act.Id,Today);var other=new Store(f.Dir);party=other.Find(f.Party.Id);party.Set("notes","Из другого процесса");other.Upsert(party);bytes=File.ReadAllBytes(f.Store.DataPath);revision=f.Store.Revision;
            bool refused=false;try{f.Store.ConfirmActPayment(f.Act.Id,null,100m,Today,"cash","","",view.Revision,Guid.NewGuid().ToString("N"),Today);}catch(InvalidOperationException){refused=true;}
            Check(refused&&f.Store.Revision==revision&&f.Store.Records.All(r=>r.Kind!="payment")&&bytes.SequenceEqual(File.ReadAllBytes(f.Store.DataPath)),"disk concurrency keeps receipt and allocation uncommitted");
            f=new Fixture(root,"mail-mutex",true);view=f.Store.GetManualPaymentView(f.Act.Id,Today);var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
            var thread=new Thread(delegate(){using(var mutex=new Mutex(false,ServiceReminderRuntime.MailMutexName(f.Dir))){mutex.WaitOne();entered.Set();release.WaitOne(10000);mutex.ReleaseMutex();}});thread.Start();
            try{Check(entered.WaitOne(3000),"send-lock fixture acquired");RejectUnchanged(f.Store,delegate{f.Confirm(120m);},"payment confirmation coordinates with in-flight reminder batch");}
            finally{release.Set();thread.Join(3000);entered.Dispose();release.Dispose();}
            f=new Fixture(root,"cancelled");var cancelled=f.Store.Find(f.Act.Id);cancelled.Set("status","cancelled");f.Store.Upsert(cancelled);
            RejectUnchanged(f.Store,delegate{f.Confirm(100m);},"cancelled act cannot accept confirmation");
            f=new Fixture(root,"backup-manual",true);f.Confirm(120m);string backup=Path.Combine(root,"manual-backup.zip"),restored=Path.Combine(root,"restored");BackupArchive.Create(f.Dir,backup);BackupArchive.Restore(backup,restored,MainForm.ValidateSnapshot);
            Check(ServiceReminderLedger.Build(new Store(restored).Records,Today).Single().Due==0&&new Store(restored).Audit.Any(a=>a.Summary.Contains("Ручное подтверждение оплаты")),"backup transfer preserves manual receipt and audit");
            UiChecks(root);
            File.WriteAllText(Path.Combine(root,"PASS.txt"),"PASS: "+checks+" manual payment checks; fixtures only, no real email sent.",Encoding.UTF8);
        }
        private static void UiChecks(string root)
        {
            var f=new Fixture(root,"ui",true);byte[] before=File.ReadAllBytes(f.Store.DataPath);
            using(var dialog=new ManualPaymentDialog(f.Store,f.Act.Id))
            {
                dialog.ShowInTaskbar=false;dialog.Opacity=0;dialog.Show();Application.DoEvents();
                Field<NumericUpDown>(dialog,"amount").Value=999m;
                Check(!dialog.SaveConfirmation()&&Field<Label>(dialog,"error").Text!=""&&before.SequenceEqual(File.ReadAllBytes(f.Store.DataPath)),"form reports invalid amount without altering ledger");
                Field<NumericUpDown>(dialog,"amount").Value=120m;Field<DateTimePicker>(dialog,"date").Value=new DateTime(2026,10,9);
                Check(dialog.SaveConfirmation()&&dialog.Payment!=null&&f.Balance().Due==0,"actual confirmation button workflow saves receipt");
            }
            using(var main=new MainForm(f.Store,false))
            {
                main.ShowInTaskbar=false;main.Opacity=0;main.Show();main.Navigate("act");Application.DoEvents();
                foreach(var size in new[]{new Size(1440,900),new Size(1120,680)})
                {
                    main.Size=size;Application.DoEvents();
                    Check(CompactToolbarTests.CanReachCommand(main,"confirm-payment"),"confirm action is reachable in compact act menu at "+size.Width);
                    Check(All(main).OfType<DataGridView>().Any(g=>g.Rows.Cast<DataGridViewRow>().Any(row=>row.Cells.Cast<DataGridViewCell>().Any(c=>Convert.ToString(c.Value)=="Оплачен"))),"act shows manually paid at "+size.Width);
                }
            }
            using(var window=new ServiceReminderWindow(f.Store,null))
            {
                window.ShowInTaskbar=false;window.Opacity=0;window.Size=new Size(980,660);window.Show();Application.DoEvents();
                var button=All(window).OfType<Button>().Single(b=>b.Name=="confirmServicePayment");
                Check(button.Visible&&!button.Enabled&&button.Right<=button.Parent.ClientSize.Width,"paid service row disables duplicate confirmation and command fits narrow window");
            }
            f=new Fixture(root,"ui-existing",true);var receipt=f.Receipt(120m);
            using(var dialog=new ManualPaymentDialog(f.Store,f.Act.Id))
            {
                dialog.ShowInTaskbar=false;dialog.Opacity=0;dialog.Show();Application.DoEvents();
                Check(Field<ComboBox>(dialog,"source").SelectedIndex==1&&!Field<NumericUpDown>(dialog,"amount").Enabled,"form defaults to existing receipt and prevents partial direct-link mistake");
                Check(dialog.SaveConfirmation()&&dialog.Payment.Id==receipt.Id&&f.Received()==120m,"existing-payment form workflow creates no duplicate funds");
            }
            f=new Fixture(root,"ui-commands",true);
            using(var window=new ServiceReminderWindow(f.Store,null))
            {
                window.ShowInTaskbar=false;window.Opacity=0;window.Show();Application.DoEvents();
                var button=All(window).OfType<Button>().Single(b=>b.Name=="confirmServicePayment");
                Check(button.Enabled,"unpaid service row enables confirmation after initial refresh");
                foreach(var size in new[]{new Size(1080,750),new Size(980,660)})
                {
                    window.Size=size;Application.DoEvents();
                    Check(button.Visible&&button.Enabled&&button.Parent.Controls.Cast<Control>().All(c=>c.Left>=0&&c.Right<=button.Parent.ClientSize.Width),"enabled service commands fit after resizing to "+size.Width);
                    Check(CompactToolbarTests.CanReachCommand(window,"preview-service-mail")&&CompactToolbarTests.CanReachCommand(window,"open-service-act"),"grouped preview and act actions remain reachable at "+size.Width);
                }
            }
            using(var dialog=new ManualPaymentDialog(f.Store,f.Act.Id))
            {
                dialog.ShowInTaskbar=false;dialog.Opacity=0;dialog.Show();Application.DoEvents();
                foreach(Size size in new[]{new Size(760,620),new Size(700,560)})
                {
                    dialog.Size=size;Application.DoEvents();
                    var button=All(dialog).OfType<Button>().Single(b=>b.Name=="confirmPayment");
                    Check(button.Visible&&button.Enabled&&button.Right<=button.Parent.ClientSize.Width&&button.Bottom<=button.Parent.ClientSize.Height,"confirmation controls fit dialog at "+size.Height);
                    using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(root,"manual-dialog-"+size.Height+".png"));}
                }
                dialog.Close();
            }
        }
    }
}
