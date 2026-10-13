using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading;
namespace WorkLedger
{
    public sealed class ServiceMailEntry
    {
        public string ActId {get;set;} public int Stage {get;set;} public string State {get;set;}
        public string AttemptUtc {get;set;} public string SentUtc {get;set;} public string Recipient {get;set;}
        public string ActNumber {get;set;} public string Error {get;set;} public string MessageId {get;set;}
    }
    public sealed class ServiceMailHistory
    {
        public int FormatVersion {get;set;}
        public List<ServiceMailEntry> Entries {get;set;}
        public ServiceMailHistory(){FormatVersion=1;Entries=new List<ServiceMailEntry>();}
        internal static string HistoryPath(string dir){return Path.Combine(dir,"service-mail-history.json");}
        public static ServiceMailHistory Load(string dir)
        {
            string path=HistoryPath(dir);if(!File.Exists(path))return new ServiceMailHistory();
            try {
                var history=ServiceMailFiles.Read<ServiceMailHistory>(path);
                if(history==null||history.FormatVersion!=1||history.Entries==null)throw new InvalidDataException();
                var keys=new HashSet<string>();
                foreach(var e in history.Entries) {
                    DateTime time;
                    if(e==null||String.IsNullOrWhiteSpace(e.ActId)||(e.Stage!=1&&e.Stage!=2)||!keys.Add(e.ActId+"|"+e.Stage)
                        ||!new[]{"sending","sent","failed","uncertain","retry"}.Contains(e.State)||!DateTime.TryParse(e.AttemptUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out time))
                        throw new InvalidDataException();
                    if(e.State=="sent"&&!DateTime.TryParse(e.SentUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out time))throw new InvalidDataException();
                }
                return history;
            }catch(Exception e){throw new InvalidOperationException("История рассылки повреждена. Отправка остановлена, чтобы не повторять письма; файл сохранён.",e);}
        }
        internal void Save(string dir){ServiceMailFiles.Write(HistoryPath(dir),this);}
        public ServiceMailEntry Find(string id,int stage){return Entries.FirstOrDefault(e=>e.ActId==id&&e.Stage==stage);}
    }
    public sealed class ServiceMailPlan
    {
        public ServiceBalance Balance;
        public int Stage;
        public DateTime DueDate;
        public string Recipient,Subject,Body,Status;
    }
    public interface IServiceMailSender { void Send(ServiceMailSettings settings,ServiceMailPlan plan,string messageId); }
    public sealed class SmtpServiceMailSender : IServiceMailSender
    {
        public void Send(ServiceMailSettings settings,ServiceMailPlan plan,string messageId)
        {
            settings.Validate(true);
            if(settings.Tls)ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;
            using(var mail=new MailMessage()) {
                mail.From=new MailAddress(ServiceMailSettings.Address(settings.FromAddress),settings.FromName,Encoding.UTF8);
                mail.To.Add(new MailAddress(ServiceMailSettings.Address(plan.Recipient)));
                mail.Subject=plan.Subject.Replace("\r"," ").Replace("\n"," ");mail.SubjectEncoding=Encoding.UTF8;
                mail.Body=plan.Body;mail.BodyEncoding=Encoding.UTF8;mail.IsBodyHtml=false;
                mail.Headers.Add("Message-ID","<"+messageId+"@workledger.local>");
                using(var client=new SmtpClient(settings.Host,settings.Port)) {
                    client.EnableSsl=settings.Tls;client.UseDefaultCredentials=false;client.Timeout=20000;
                    if(!String.IsNullOrWhiteSpace(settings.Login))client.Credentials=new NetworkCredential(settings.Login,settings.Password());
                    client.Send(mail);
                }
            }
        }
    }
    public static class ServiceReminderRuntime
    {
        private static readonly object queueGate=new object();
        private static readonly HashSet<string> queued=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string,DateTime> nextChecks=new Dictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
        public static List<ServiceMailPlan> Plans(IEnumerable<Record> records,ServiceMailSettings settings,ServiceMailHistory history,DateTime nowUtc)
        {
            DateTime localNow=nowUtc.ToUniversalTime().AddHours(3);DateTime today=localNow.Date;
            var plans=new List<ServiceMailPlan>();
            foreach(var balance in ServiceReminderLedger.Build(records,today)) {
                Record act=balance.Act;DateTime date=ServiceReminderLedger.Date(act.Get("date"));
                if(date>today||balance.Party.Get("monthlyServiceEnabled")!="true")continue;
                if(String.CompareOrdinal(date.ToString("yyyy-MM"),settings.StartMonth)<0)continue;
                var first=history.Find(act.Id,1);var second=history.Find(act.Id,2);
                int stage=first!=null&&first.State=="sent"?2:1;
                var previous=stage==1?first:second;
                DateTime due=date.AddDays(7);
                if(stage==2) {
                    DateTime firstDate=DateTime.Parse(first.SentUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime().AddHours(3);
                    due=date.AddDays(10);if(firstDate.AddDays(3)>due)due=firstDate.AddDays(3);
                }
                if(previous!=null&&previous.State=="retry") {
                    DateTime nextTry=DateTime.Parse(previous.AttemptUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime().AddHours(3).AddMinutes(15);
                    if(nextTry>due)due=nextTry;
                    if(localNow>=due)previous=null;
                }
                string recipient=balance.Party.Get("email"),status;
                if(balance.Due<=0)status=balance.Status;
                else if(balance.NeedsReview)status=balance.Status;
                else if(previous!=null)status=previous.State=="sent"?"Два письма отправлены":previous.State=="retry"?"Ожидает подключения":previous.State=="failed"?"Ошибка отправки":"Результат отправки не подтверждён";
                else {
                    try{recipient=ServiceMailSettings.Address(recipient);status=localNow>=due?"Готово к отправке":"Ожидает срока";}
                    catch{status="Укажите email заказчика";}
                }
                if(!settings.Enabled&&status=="Готово к отправке")status="Рассылка выключена";
                plans.Add(new ServiceMailPlan{Balance=balance,Stage=stage,DueDate=due,Recipient=recipient,Subject=ServiceReminderLedger.ReplaceFields(settings.Subject,balance),
                    Body=ServiceReminderLedger.ReplaceFields(settings.Body,balance),Status=status});
            }
            return plans;
        }
        internal static string MailMutexName(string dir){return MonthlyServiceRuntime.MutexName(dir)+"-service-mail";}
        public static int Process(string dir,DateTime nowUtc,IServiceMailSender sender,bool force=false,Func<DateTime> utcClock=null)
        {
            using(var mutex=new Mutex(false,MailMutexName(dir))) {
                bool held;
                try{held=mutex.WaitOne(0);}catch(AbandonedMutexException){held=true;}
                if(!held)return 0;
                try{return ProcessLocked(dir,sender,force,utcClock??delegate{return nowUtc;});}
                finally{mutex.ReleaseMutex();}
            }
        }
        private static int ProcessLocked(string dir,IServiceMailSender sender,bool force,Func<DateTime> utcClock)
        {
            var settings=ServiceMailSettings.Load(dir);if(!settings.Enabled&&!force)return 0;
            settings.Validate(true);
            var history=ServiceMailHistory.Load(dir);
            if(history.Entries.Any(e=>e.State=="sending")) {
                foreach(var e in history.Entries.Where(e=>e.State=="sending")){e.State="uncertain";e.Error="Предыдущая отправка прервалась. Проверьте почту перед повтором.";}
                history.Save(dir);
            }
            int count=0;
            var initial=Plans(new Store(dir).Records,settings,history,utcClock()).Where(p=>p.Status=="Готово к отправке"||(force&&p.Status=="Рассылка выключена")).ToList();
            foreach(var planned in initial.Take(100)) {
                // Reread the bank imports and settings immediately before each send.
                settings=ServiceMailSettings.Load(dir);
                if(!settings.Enabled&&!force)break;
                var current=Plans(new Store(dir).Records,settings,history,utcClock()).FirstOrDefault(p=>p.Balance.Act.Id==planned.Balance.Act.Id&&p.Stage==planned.Stage);
                if(current==null||(current.Status!="Готово к отправке"&&!(force&&current.Status=="Рассылка выключена")))continue;
                string id=Guid.NewGuid().ToString("N");
                var entry=new ServiceMailEntry{ActId=current.Balance.Act.Id,ActNumber=current.Balance.Act.Get("number"),Stage=current.Stage,
                    State="sending",AttemptUtc=utcClock().ToUniversalTime().ToString("o"),Recipient=current.Recipient,Error="",MessageId=id,SentUtc=""};
                history.Entries.RemoveAll(e=>e.ActId==entry.ActId&&e.Stage==entry.Stage&&e.State=="retry");
                history.Entries.Add(entry);
                history.Save(dir); // Persist the claim before contacting the mail server.
                try {
                    sender.Send(settings,current,id);
                    entry.State="sent";entry.SentUtc=utcClock().ToUniversalTime().ToString("o");count++;
                    history.Save(dir);
                } catch(Exception error) {
                    entry.State=DefinitelyNotConnected(error)?"retry":error is SmtpFailedRecipientException?"failed":"uncertain";
                    entry.Error=entry.State=="retry"?"Не удалось подключиться к почте. Повторная попытка — не раньше чем через 15 минут.":error is SmtpFailedRecipientException?"Почтовый сервер отклонил адрес. Проверьте email.":"Отправка не подтверждена. Проверьте настройки и папку отправленных писем перед повтором.";
                    history.Save(dir);
                    if(!(error is SmtpFailedRecipientException))break;
                }
            }
            return count;
        }
        private static bool DefinitelyNotConnected(Exception error)
        {
            for(Exception cause=error;cause!=null;cause=cause.InnerException) {
                var web=cause as WebException;
                if(web!=null&&(web.Status==WebExceptionStatus.ConnectFailure||web.Status==WebExceptionStatus.NameResolutionFailure||web.Status==WebExceptionStatus.ProxyNameResolutionFailure))return true;
                var socket=cause as System.Net.Sockets.SocketException;
                if(socket!=null&&new[]{System.Net.Sockets.SocketError.ConnectionRefused,System.Net.Sockets.SocketError.HostNotFound,
                    System.Net.Sockets.SocketError.NetworkDown,System.Net.Sockets.SocketError.NetworkUnreachable,System.Net.Sockets.SocketError.HostUnreachable}.Contains(socket.SocketErrorCode))return true;
            }
            return false;
        }
        public static void AllowRetry(string dir,string actId,int stage)
        {
            using(var mutex=new Mutex(false,MailMutexName(dir))) {
                bool held;try{held=mutex.WaitOne(0);}catch(AbandonedMutexException){held=true;}
                if(!held)throw new InvalidOperationException("Сейчас выполняется отправка. Повторите позже.");
                try {
                    var history=ServiceMailHistory.Load(dir);var entry=history.Find(actId,stage);
                    if(entry==null||entry.State=="sent"||entry.State=="sending")throw new InvalidOperationException("Повтор доступен только для письма с ошибкой или неподтверждённой отправкой.");
                    history.Entries.Remove(entry);history.Save(dir);
                }finally{mutex.ReleaseMutex();}
            }
        }
        public static void Queue(string dir)
        {
            string key=Path.GetFullPath(dir);DateTime now=DateTime.UtcNow;
            lock(queueGate) {
                DateTime next;if(queued.Contains(key)||(nextChecks.TryGetValue(key,out next)&&next>now))return;
                queued.Add(key);nextChecks[key]=now.AddMinutes(5);
            }
            ThreadPool.QueueUserWorkItem(delegate {
                try {
                    if(!UpdateManager.IsActiveMarker(Path.GetDirectoryName(typeof(ServiceReminderRuntime).Assembly.Location))) {
                        int sent=Process(key,DateTime.UtcNow,new SmtpServiceMailSender(),false,delegate{return DateTime.UtcNow;});
                        if(sent>0)MonthlyServiceRuntime.Log(key,"Напоминания об оплате: отправлено писем "+sent+".");
                    }
                }catch(Exception e){MonthlyServiceRuntime.Log(key,"Рассылка обслуживания: "+e.Message);}
                finally{lock(queueGate)queued.Remove(key);}
            });
        }
        public static int RunBackground(string dir)
        {
            try {
                if(!File.Exists(Path.Combine(dir,"ledger.json"))||UpdateManager.IsActiveMarker(Path.GetDirectoryName(typeof(ServiceReminderRuntime).Assembly.Location)))return 0;
                int sent=Process(dir,DateTime.UtcNow,new SmtpServiceMailSender(),false,delegate{return DateTime.UtcNow;});
                if(sent>0)MonthlyServiceRuntime.Log(dir,"Напоминания об оплате: отправлено писем "+sent+".");
                return 0;
            }catch(Exception e){MonthlyServiceRuntime.Log(dir,"Рассылка обслуживания: "+e.Message);return 1;}
        }
    }
}
