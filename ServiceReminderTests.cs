using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;
namespace WorkLedger
{
    public static class ServiceReminderTests
    {
        private static int checks;
        private static void Check(bool ok,string name){if(!ok)throw new Exception("Service mail: "+name);checks++;Console.WriteLine("PASS service mail "+name);}
        private static DateTime Now(int day){return new DateTime(2026,10,day,6,0,0,DateTimeKind.Utc);}
        private static Record Add(Store s,string kind,params string[] values){var r=new Record(kind);for(int i=0;i<values.Length;i+=2)r.Set(values[i],values[i+1]);s.Upsert(r);return s.Find(r.Id);}
        private sealed class Fixture
        {
            public string Dir;public Store Store;public Record Party,Act;public ServiceMailSettings Settings;
            public Fixture(string root,string name){
                Dir=Path.Combine(root,name);Directory.CreateDirectory(Dir);Store=new Store(Dir);
                Add(Store,"settings","currency","BYN","name","Тестовый исполнитель","bankAccount","TEST-ACCOUNT");
                Party=Add(Store,"party","name","Тестовый клиент","role","client","email","client@example.test","monthlyServiceEnabled","true",
                    "monthlyServiceAmount","120.00","monthlyServiceCurrency","BYN","monthlyServiceStartMonth","2026-10-01");
                Act=Store.GenerateMonthlyServiceActs(new DateTime(2026,10,1)).Single();
                Settings=new ServiceMailSettings{Enabled=true,StartMonth="2026-10",Host="smtp.example.test",FromAddress="sender@example.test",FromName="Тестовая почта"};
                Settings.Save(Dir,"");
            }
            public Record Payment(decimal amount,string direct,string notes=""){
                var r=new Record("payment");r.Set("date","2026-10-09");r.Set("partyId",Party.Id);r.Set("currency","BYN");r.Set("direction","income");
                r.Set("method","bank");r.Set("amount",Store.MoneyString(amount));r.Set("paymentScope","standard");r.Set("actId",direct);r.Set("notes",notes);
                Store.ImportBatch(new[]{r});return Store.Find(r.Id);
            }
            public ServiceBalance Balance(){return ServiceReminderLedger.Build(Store.Records,new DateTime(2026,10,30)).Single(b=>b.Act.Id==Act.Id);}
        }
        private sealed class Sender:IServiceMailSender
        {
            public List<ServiceMailPlan> Sent=new List<ServiceMailPlan>();public Action<ServiceMailPlan> After;
            public bool Throw;public Exception Failure;
            public void Send(ServiceMailSettings settings,ServiceMailPlan plan,string id){Sent.Add(plan);if(After!=null)After(plan);if(Failure!=null)throw Failure;if(Throw)throw new IOException("Simulated uncertain SMTP acknowledgement");}
        }
        private sealed class Backend:MonthlyServiceScheduler.ITaskBackend
        {
            public string Xml;public int Writes,Deletes;
            public string ReadXml(string name){return Xml;}
            public void Register(string name,string xml,string sid){Xml=xml;Writes++;}
            public void Delete(string name){Xml=null;Deletes++;}
            public void Dispose(){}
        }
        private static IEnumerable<Control> All(Control c){yield return c;foreach(Control child in c.Controls)foreach(var item in All(child))yield return item;}
        private static void TransportChecks(string root)
        {
            var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
            int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;string captured="";Exception serverError=null;
            var thread=new System.Threading.Thread(delegate() {
                try {
                    using(var client=listener.AcceptTcpClient()) {
                        client.ReceiveTimeout=10000;client.SendTimeout=10000;
                        using(var reader=new StreamReader(client.GetStream(),Encoding.ASCII))
                        using(var writer=new StreamWriter(client.GetStream(),Encoding.ASCII){AutoFlush=true,NewLine="\r\n"}) {
                            writer.WriteLine("220 localhost test capture");
                            string line;bool data=false;
                            while((line=reader.ReadLine())!=null) {
                                if(data) {if(line=="."){data=false;writer.WriteLine("250 captured");}else captured+=line+"\n";continue;}
                                if(line.StartsWith("EHLO")||line.StartsWith("HELO"))writer.WriteLine("250 localhost");
                                else if(line=="DATA"){data=true;writer.WriteLine("354 send data");}
                                else if(line=="QUIT"){writer.WriteLine("221 done");break;}
                                else writer.WriteLine("250 OK");
                            }
                        }
                    }
                }catch(Exception e){serverError=e;}
            });
            thread.IsBackground=true;thread.Start();
            var settings=new ServiceMailSettings{Host="127.0.0.1",Port=port,Tls=false,FromAddress="sender@example.test",FromName="Тестовый отправитель"};
            var plan=new ServiceMailPlan{Recipient="capture@example.test",Subject="Проверка оплаты",Body="Осталось оплатить: 85,00 BYN."};
            try{new SmtpServiceMailSender().Send(settings,plan,"test-capture");}
            finally{thread.Join(10000);listener.Stop();}
            Check(serverError==null&&captured.Contains("capture@example.test")&&captured.Contains("utf-8")&&captured.Contains("test-capture@workledger.local"),"real SMTP transport emits recipient UTF8 and stable message id to loopback capture only");
        }
        public static void Run(string root)
        {
            checks=0;Directory.CreateDirectory(root);
            var f=new Fixture(root,"schedule");var sender=new Sender();byte[] original=File.ReadAllBytes(f.Store.DataPath);
            Check(ServiceReminderRuntime.Process(f.Dir,Now(7),sender)==0,"no reminder before seven complete days");
            Check(ServiceReminderRuntime.Process(f.Dir,Now(8),sender)==1&&sender.Sent[0].Stage==1,"first reminder after seven days");
            Check(ServiceReminderRuntime.Process(f.Dir,Now(8),sender)==0,"no duplicate after restart on same day");
            Check(ServiceReminderRuntime.Process(f.Dir,Now(10),sender)==0,"three-day gap before second");
            Check(ServiceReminderRuntime.Process(f.Dir,Now(11),sender)==1&&sender.Sent.Last().Stage==2,"second reminder after three days");
            Check(ServiceReminderRuntime.Process(f.Dir,Now(30),sender)==0&&sender.Sent.Count==2,"no third reminder");
            Check(original.SequenceEqual(File.ReadAllBytes(f.Store.DataPath))&&f.Store.Find(f.Act.Id).Get("status")=="draft","sending preserves ledger and unknown signature");
            f=new Fixture(root,"late");sender=new Sender();
            ServiceReminderRuntime.Process(f.Dir,Now(14),sender);
            Check(ServiceReminderRuntime.Process(f.Dir,Now(16),sender)==0&&ServiceReminderRuntime.Process(f.Dir,Now(17),sender)==1,"late startup spaces emails three days apart");
            f=new Fixture(root,"exact-gap");sender=new Sender();ServiceReminderRuntime.Process(f.Dir,Now(8).AddHours(12),sender);
            Check(ServiceReminderRuntime.Process(f.Dir,Now(11).AddHours(11),sender)==0&&ServiceReminderRuntime.Process(f.Dir,Now(11).AddHours(12),sender)==1,"second email waits all seventy-two hours after a late manual first send");
            f=new Fixture(root,"accepted-clock");DateTime acceptedClock=Now(8);
            sender=new Sender{After=delegate{acceptedClock=acceptedClock.AddHours(2);}};
            ServiceReminderRuntime.Process(f.Dir,Now(8),sender,false,delegate{return acceptedClock;});
            sender.After=null;
            Check(DateTime.Parse(ServiceMailHistory.Load(f.Dir).Entries.Single().SentUtc).ToUniversalTime()==Now(8).AddHours(2)
                &&ServiceReminderRuntime.Process(f.Dir,Now(11).AddHours(1),sender)==0,"three-day interval starts from successful acceptance rather than batch startup");
            f=new Fixture(root,"full-payment");sender=new Sender();ServiceReminderRuntime.Process(f.Dir,Now(8),sender);
            f.Payment(120m,f.Act.Id);original=File.ReadAllBytes(f.Store.DataPath);
            Check(f.Balance().Status=="Оплачен"&&ServiceReminderRuntime.Process(f.Dir,Now(11),sender)==0&&sender.Sent.Count==1,"imported full bank receipt stops second email");
            Check(original.SequenceEqual(File.ReadAllBytes(f.Store.DataPath))&&f.Store.ActDue(f.Act.Id)==0&&f.Store.Find(f.Act.Id).Get("status")=="draft","bank recognition does not book allocations or change signature");
            f=new Fixture(root,"partial");f.Payment(35m,f.Act.Id);sender=new Sender();ServiceReminderRuntime.Process(f.Dir,Now(11),sender);
            Check(f.Balance().Paid==35m&&sender.Sent.Single().Balance.Due==85m&&sender.Sent[0].Body.Contains("85,00"),"partial bank receipt sends only remaining amount");
            f=new Fixture(root,"allocation");var signed=f.Store.Find(f.Act.Id);signed.Set("status","signed");f.Store.Upsert(signed);
            var payment=f.Payment(40m,f.Act.Id);f.Store.Allocate(payment.Id,f.Act.Id,40m);
            Check(f.Balance().Paid==40m&&f.Balance().Due==80m,"direct link and allocation count money once");
            f=new Fixture(root,"purpose");f.Payment(120m,"","Оплата обслуживания по акту № "+f.Act.Get("number")+" от 01.10.2026");
            Check(f.Balance().Due==0&&f.Balance().Status=="Оплачен","exact bank purpose number recognizes full payment");
            f=new Fixture(root,"bank-purpose-field");payment=f.Payment(120m,"","Импорт из банковской выписки");
            payment.Set("bankPurpose","Оплата обслуживания по акту № "+f.Act.Get("number"));f.Store.Upsert(payment);
            Check(f.Balance().Due==0&&f.Balance().Status=="Оплачен","bank purpose from reconciliation package settles exact service act");
            Check(!ServiceReminderLedger.PurposeMatches("Оплата по акту № 5000","500")&&!ServiceReminderLedger.PurposeMatches("платёж 500","500")&&!ServiceReminderLedger.PurposeMatches("по акту №500/1","500")&&!ServiceReminderLedger.PurposeMatches("по акту №500.1","500"),"neighboring number and bare amount never match");
            f=new Fixture(root,"unmatched");f.Payment(120m,"","Оплата услуг");sender=new Sender();
            Check(f.Balance().NeedsReview&&ServiceReminderRuntime.Process(f.Dir,Now(11),sender)==0,"unassigned bank receipt pauses mail for reconciliation");
            f=new Fixture(root,"other-currency");
            Add(f.Store,"payment","date","2026-10-09","partyId",f.Party.Id,"currency","USD","direction","income","method","bank","amount","120.00","notes","по акту № "+f.Act.Get("number"));
            Add(f.Store,"payment","date","2026-10-09","partyId",f.Party.Id,"currency","BYN","direction","expense","method","bank","amount","120.00","notes","по акту № "+f.Act.Get("number"));
            Check(f.Balance().Due==120m&&!f.Balance().NeedsReview,"foreign currency and outgoing payment do not settle act");
            f=new Fixture(root,"future");payment=f.Payment(120m,f.Act.Id);payment.Set("date","2026-10-20");f.Store.Upsert(payment);
            Check(ServiceReminderLedger.Build(f.Store.Records,new DateTime(2026,10,11)).Single().Due==120m,"future bank date is not a received payment");
            f=new Fixture(root,"cancelled");var cancelled=f.Store.Find(f.Act.Id);cancelled.Set("status","cancelled");f.Store.Upsert(cancelled);
            Check(ServiceReminderRuntime.Plans(f.Store.Records,f.Settings,new ServiceMailHistory(),Now(11)).Count==0,"cancelled act has no reminders");
            f=new Fixture(root,"disabled-party");var disabled=f.Store.Find(f.Party.Id);disabled.Set("monthlyServiceEnabled","false");f.Store.Upsert(disabled);
            Check(ServiceReminderRuntime.Plans(f.Store.Records,f.Settings,new ServiceMailHistory(),Now(11)).Count==0,"disabled maintenance has no reminders");
            f=new Fixture(root,"missing-email");var missing=f.Store.Find(f.Party.Id);missing.Set("email","");f.Store.Upsert(missing);sender=new Sender();
            Check(ServiceReminderRuntime.Process(f.Dir,Now(11),sender)==0&&ServiceReminderRuntime.Plans(f.Store.Records,f.Settings,new ServiceMailHistory(),Now(11)).Single().Status=="Укажите email заказчика","missing recipient is shown without sending");
            f=new Fixture(root,"uncertain");sender=new Sender{Throw=true};ServiceReminderRuntime.Process(f.Dir,Now(8),sender);
            sender.Throw=false;ServiceReminderRuntime.Process(f.Dir,Now(10),sender);
            Check(sender.Sent.Count==1&&ServiceMailHistory.Load(f.Dir).Entries.Single().State=="uncertain","lost SMTP acknowledgement is not automatically repeated");
            ServiceReminderRuntime.AllowRetry(f.Dir,f.Act.Id,1);ServiceReminderRuntime.Process(f.Dir,Now(10),sender);
            Check(sender.Sent.Count==2&&ServiceMailHistory.Load(f.Dir).Entries.Single().State=="sent","explicit retry rechecks current payment");
            f=new Fixture(root,"offline-retry");
            sender=new Sender{Failure=new System.Net.Mail.SmtpException("Offline",new System.Net.WebException("No connection",null,System.Net.WebExceptionStatus.ConnectFailure,null))};
            ServiceReminderRuntime.Process(f.Dir,Now(8),sender);sender.Failure=null;
            Check(ServiceMailHistory.Load(f.Dir).Entries.Single().State=="retry"&&ServiceReminderRuntime.Process(f.Dir,Now(8).AddMinutes(14),sender)==0
                &&ServiceReminderRuntime.Process(f.Dir,Now(8).AddMinutes(16),sender)==1&&sender.Sent.Count==2,"known connection failure retries automatically without counting a delivered letter");
            f=new Fixture(root,"interrupted");var history=new ServiceMailHistory();
            history.Entries.Add(new ServiceMailEntry{ActId=f.Act.Id,ActNumber=f.Act.Get("number"),Stage=1,State="sending",AttemptUtc=Now(8).ToString("o"),SentUtc="",Recipient="client@example.test"});history.Save(f.Dir);
            sender=new Sender();ServiceReminderRuntime.Process(f.Dir,Now(10),sender);
            Check(sender.Sent.Count==0&&ServiceMailHistory.Load(f.Dir).Entries[0].State=="uncertain","interrupted send recovered without duplicate");
            f=new Fixture(root,"corrupt-history");string hp=ServiceMailHistory.HistoryPath(f.Dir);File.WriteAllText(hp,"{ broken");sender=new Sender();bool rejected=false;
            try{ServiceReminderRuntime.Process(f.Dir,Now(10),sender);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&sender.Sent.Count==0&&File.ReadAllText(hp)=="{ broken","corrupt history stops sending and is preserved");
            f=new Fixture(root,"recheck");
            var party2=Add(f.Store,"party","name","Другой тестовый клиент","role","client","email","second@example.test","monthlyServiceEnabled","true","monthlyServiceAmount","120.00","monthlyServiceCurrency","BYN","monthlyServiceStartMonth","2026-10-01");
            var act2=f.Store.GenerateMonthlyServiceActs(new DateTime(2026,10,1)).Single();
            sender=new Sender{After=delegate(ServiceMailPlan plan){
                string other=plan.Balance.Act.Id==f.Act.Id?act2.Id:f.Act.Id;var otherAct=f.Store.Find(other);
                Add(f.Store,"payment","date","2026-10-08","partyId",otherAct.Get("partyId"),"currency","BYN","direction","income","method","bank","amount","120.00","actId",other,"paymentScope","standard");
            }};
            Check(ServiceReminderRuntime.Process(f.Dir,Now(8),sender)==1&&sender.Sent.Count==1,"bank data reread before each recipient");
            f=new Fixture(root,"credentials");const string secret="test-only-app-password";
            f.Settings.Login="sender@example.test";f.Settings.Save(f.Dir,secret);
            Check(!File.ReadAllText(ServiceMailSettings.SettingsPath(f.Dir)).Contains(secret)&&ServiceMailSettings.Load(f.Dir).Password()==secret,"password stored encrypted for Windows user");
            bool badTls=false;f.Settings.Tls=false;try{f.Settings.Save(f.Dir,"");}catch(InvalidOperationException){badTls=true;}Check(badTls,"password cannot be sent over unprotected connection");
            bool badAddress=false;try{ServiceMailSettings.Address("a@example.test,b@example.test");}catch{badAddress=true;}Check(badAddress,"recipient header cannot inject another address");
            f=new Fixture(root,"backup");sender=new Sender();ServiceReminderRuntime.Process(f.Dir,Now(8),sender);
            string backup=Path.Combine(root,"with-mail.zip"),restored=Path.Combine(root,"restored");
            BackupArchive.Create(f.Dir,backup);BackupArchive.Restore(backup,restored,MainForm.ValidateSnapshot);
            Check(ServiceMailHistory.Load(restored).Entries.Single().State=="sent"&&ServiceMailSettings.Load(restored).Host=="smtp.example.test","backup transfer preserves mail settings and sent history");
            Check(ServiceReminderRuntime.Process(restored,Now(8),sender)==0,"restored backup does not repeat first email");
            string sid="S-1-5-21-1-2-3-1001",exe=typeof(MainForm).Assembly.Location;var backend=new Backend();
            string xml=ServiceReminderScheduler.BuildTaskXml(exe,f.Dir,sid);
            var doc=XDocument.Parse(xml);XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
            Check(doc.Descendants(ns+"ScheduleByDay").Any()&&!doc.Descendants(ns+"ScheduleByMonth").Any()&&doc.Descendants(ns+"StartBoundary").First().Value.EndsWith("09:00:00+03:00"),"daily background schedule at Minsk nine");
            ServiceReminderScheduler.EnsureWithBackend(exe,f.Dir,true,sid,backend);ServiceReminderScheduler.EnsureWithBackend(exe,f.Dir,true,sid,backend);
            Check(backend.Writes==1,"scheduler does not reregister unchanged task");
            ServiceReminderScheduler.EnsureWithBackend(exe,f.Dir,false,sid,backend);
            Check(backend.Deletes==1,"disabling mail removes only its task");
            backend.Xml=MonthlyServiceScheduler.BuildTaskXml(exe,f.Dir,sid);rejected=false;
            try{ServiceReminderScheduler.EnsureWithBackend(exe,f.Dir,false,sid,backend);}catch{rejected=true;}
            Check(rejected&&backend.Deletes==1,"foreign scheduler task is preserved");
            f=new Fixture(root,"ui");f.Payment(120m,f.Act.Id);
            using(var form=new MainForm(f.Store)){
                form.ShowInTaskbar=false;form.Opacity=0;form.Show();form.Navigate("act");Application.DoEvents();
                Check(CompactToolbarTests.CanReachCommand(form,"service-mail"),"mail settings accessible in compact global menu");
                Check(All(form).OfType<DataGridView>().Any(g=>g.Rows.Cast<DataGridViewRow>().Any(row=>row.Cells.Cast<DataGridViewCell>().Any(c=>Convert.ToString(c.Value)=="Оплачен"))),"act shows paid from imported bank receipt without signature change");
            }
            using(var window=new ServiceReminderWindow(f.Store,null)){
                window.ShowInTaskbar=false;window.Opacity=0;window.Size=new Size(980,660);window.Show();Application.DoEvents();
                Check(All(window).OfType<TabControl>().Single().TabPages.Count==3&&All(window).OfType<DataGridView>().First().Rows.Count==1,"mail window has payment list settings and history");
            }
            TransportChecks(root);
            File.WriteAllText(Path.Combine(root,"PASS.txt"),"PASS: "+checks+" service mail checks; no real email sent.",Encoding.UTF8);
        }
    }
}
