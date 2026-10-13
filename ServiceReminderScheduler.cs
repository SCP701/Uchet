using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
namespace WorkLedger
{
    public static class ServiceReminderScheduler
    {
        private const string Source="WorkLedger.ServiceMail.v1";
        private static readonly XNamespace Ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        internal static string TaskName(string dir,string sid){return MonthlyServiceScheduler.GetTaskName(dir,sid).Replace("AktiOplaty-Monthly-","AktiOplaty-ServiceMail-");}
        internal static string BuildTaskXml(string exe,string dir,string sid)
        {
            var xml=XDocument.Parse(MonthlyServiceScheduler.BuildTaskXml(exe,dir,sid));
            var root=xml.Root;
            root.Element(Ns+"RegistrationInfo").Element(Ns+"Source").Value=Source;
            root.Element(Ns+"RegistrationInfo").Element(Ns+"Description").Value="Два напоминания об оплате обслуживания: через 7 дней и через 3 дня после первого.";
            var trigger=root.Element(Ns+"Triggers").Element(Ns+"CalendarTrigger");
            trigger.SetAttributeValue("id","Daily");
            trigger.Element(Ns+"StartBoundary").Value="2000-01-01T09:00:00+03:00";
            trigger.Element(Ns+"ScheduleByMonth").Remove();
            trigger.Add(new XElement(Ns+"ScheduleByDay",new XElement(Ns+"DaysInterval","1")));
            root.Element(Ns+"Actions").Element(Ns+"Exec").Element(Ns+"Arguments").Value="--send-service-reminders --data-dir "+MonthlyServiceScheduler.QuoteArgument(Path.GetFullPath(dir));
            return xml.ToString();
        }
        private static bool Own(string xml,string sid)
        {
            try {
                var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1000000};
                using(var reader=XmlReader.Create(new StringReader(xml),settings)) {
                    var root=XDocument.Load(reader).Root;
                    return root!=null&&root.Name==Ns+"Task"&&
                        (string)root.Element(Ns+"RegistrationInfo").Element(Ns+"Source")==Source&&
                        (string)root.Element(Ns+"Principals").Element(Ns+"Principal").Element(Ns+"UserId")==sid;
                }
            }catch{return false;}
        }
        public static string Ensure(string exe,string dir,bool enabled)
        {
            try {
                using(var backend=new MonthlyServiceScheduler.ComTaskBackend())
                    return EnsureWithBackend(exe,dir,enabled,MonthlyServiceScheduler.CurrentUserSid(),backend);
            }catch(Exception e){return "Фоновая рассылка не настроена: "+e.Message;}
        }
        internal static string EnsureWithBackend(string exe,string dir,bool enabled,string sid,MonthlyServiceScheduler.ITaskBackend backend)
        {
            string name=TaskName(dir,sid),existing=backend.ReadXml(name);
            if(existing!=null&&!Own(existing,sid))throw new InvalidOperationException("Имя задачи рассылки занято другой задачей. Она сохранена.");
            if(!enabled){if(existing!=null)backend.Delete(name);return "Автоматическая рассылка выключена.";}
            string expected=BuildTaskXml(exe,dir,sid);
            if(existing==null||!MonthlyServiceScheduler.MatchesDefinition(existing,expected))
                backend.Register(name,expected,sid);
            string saved=backend.ReadXml(name);
            if(saved==null||!Own(saved,sid)||!MonthlyServiceScheduler.MatchesDefinition(saved,expected))
                throw new InvalidOperationException("Планировщик не подтвердил задачу рассылки.");
            return "Фоновая проверка включена: ежедневно в 09:00 по Минску и при входе в Windows.";
        }
    }
}
