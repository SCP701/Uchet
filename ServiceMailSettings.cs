using System;
using System.IO;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
namespace WorkLedger
{
    public sealed class ServiceMailSettings
    {
        public int FormatVersion { get; set; }
        public bool Enabled { get; set; }
        public string StartMonth { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public bool Tls { get; set; }
        public string Login { get; set; }
        public string FromAddress { get; set; }
        public string FromName { get; set; }
        public string ProtectedPassword { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public ServiceMailSettings()
        {
            FormatVersion=1;Port=587;Tls=true;StartMonth=MonthlyServiceRuntime.Today.ToString("yyyy-MM");
            Host=Login=FromAddress=FromName=ProtectedPassword="";
            Subject="Напоминание об оплате обслуживания — акт № {номер}";
            Body="Здравствуйте, {контрагент}!\r\n\r\nПо нашим данным, оплата обслуживания за {период} по акту № {номер} от {дата} отражена не полностью.\r\nСумма акта: {сумма} {валюта}.\r\nУчтено оплат: {оплачено} {валюта}.\r\nОсталось оплатить: {остаток} {валюта}.\r\n\r\nПросим оплатить оставшуюся сумму. Если оплата уже выполнена, пожалуйста, пришлите платёжный документ для сверки.\r\n\r\n{организация}\r\n{реквизиты}";
        }
        internal static string SettingsPath(string dir){return Path.Combine(dir,"service-mail-settings.json");}
        public static ServiceMailSettings Load(string dir)
        {
            string path=SettingsPath(dir);if(!File.Exists(path))return new ServiceMailSettings();
            try {
                var result=ServiceMailFiles.Read<ServiceMailSettings>(path);
                if(result==null||result.FormatVersion!=1)throw new InvalidDataException();
                result.Validate(false);return result;
            }catch(Exception e){throw new InvalidOperationException("Не удалось прочитать настройки рассылки. Отправка остановлена; файл сохранён.",e);}
        }
        public static string Address(string value)
        {
            string text=(value??"").Trim();
            if(text.Length==0||text.Length>254||text.IndexOfAny(new[]{'\r','\n',',',';'})>=0)throw new InvalidOperationException("Укажите один корректный email.");
            MailAddress address;
            try{address=new MailAddress(text);}catch{throw new InvalidOperationException("Укажите корректный email.");}
            if(address.Address!=text||address.DisplayName!=""||!text.Contains("@"))throw new InvalidOperationException("Укажите email без имени и других адресов.");
            return address.Address;
        }
        public void Validate(bool requireReady)
        {
            DateTime month;
            if(!DateTime.TryParseExact((StartMonth??"")+"-01","yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out month))
                throw new InvalidOperationException("Выберите первый месяц рассылки.");
            if(Port<1||Port>65535)throw new InvalidOperationException("Некорректный порт почты.");
            if((Host??"").Length>253||(Login??"").Length>320||(FromName??"").Length>200||(ProtectedPassword??"").Length>16000)throw new InvalidOperationException("Слишком длинные настройки почты.");
            if((Host??"").IndexOfAny(new[]{'\r','\n','/','\\',' '})>=0||(FromName??"").IndexOfAny(new[]{'\r','\n'})>=0)throw new InvalidOperationException("Проверьте адрес сервера и имя отправителя.");
            if(String.IsNullOrWhiteSpace(Subject)||Subject.Length>250||Subject.IndexOfAny(new[]{'\r','\n'})>=0||String.IsNullOrWhiteSpace(Body)||Body.Length>15000)
                throw new InvalidOperationException("Проверьте тему и текст письма.");
            string[] fields={"номер","дата","контрагент","период","сумма","оплачено","остаток","валюта","организация","реквизиты"};
            foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(Subject+"\n"+Body,@"\{([^{}]+)\}"))
                if(Array.IndexOf(fields,match.Groups[1].Value)<0)throw new InvalidOperationException("Неизвестное поле письма: "+match.Value);
            if(requireReady) {
                if(String.IsNullOrWhiteSpace(Host))throw new InvalidOperationException("Укажите сервер исходящей почты.");
                Address(FromAddress);
                if(Port==465)throw new InvalidOperationException("Выберите порт почты с STARTTLS, обычно 587. Подключение через порт 465 не поддерживается.");
                if(!String.IsNullOrWhiteSpace(Login)) {
                    if(!Tls)throw new InvalidOperationException("Для входа с паролем включите защищённое соединение.");
                    if(String.IsNullOrEmpty(ProtectedPassword))throw new InvalidOperationException("Введите пароль приложения для почты.");
                    Password();
                }
            }
        }
        public string Password()
        {
            if(String.IsNullOrEmpty(ProtectedPassword))return "";
            try{return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword),Encoding.UTF8.GetBytes("WorkLedger.ServiceMail.v1"),DataProtectionScope.CurrentUser));}
            catch{throw new InvalidOperationException("Сохранённый пароль недоступен этому пользователю Windows. Введите его заново.");}
        }
        public void SetPassword(string newPassword)
        {
            if(!String.IsNullOrEmpty(newPassword)) {
                if(String.IsNullOrWhiteSpace(Login)&&!String.IsNullOrWhiteSpace(FromAddress))Login=FromAddress.Trim();
                ProtectedPassword=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(newPassword),Encoding.UTF8.GetBytes("WorkLedger.ServiceMail.v1"),DataProtectionScope.CurrentUser));
            }
        }
        public void Save(string dir,string newPassword)
        {
            SetPassword(newPassword);
            Validate(Enabled);ServiceMailFiles.Write(SettingsPath(dir),this);
        }
    }
    internal static class ServiceMailFiles
    {
        internal static T Read<T>(string path)
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Файл рассылки не должен быть ссылкой.");
            if(new FileInfo(path).Length>16*1024*1024)throw new InvalidDataException("Файл слишком большой.");
            return new JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=20}.Deserialize<T>(File.ReadAllText(path,Encoding.UTF8));
        }
        internal static void Write(string path,object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if((File.GetAttributes(Path.GetDirectoryName(path))&FileAttributes.ReparsePoint)!=0||(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0))
                throw new InvalidDataException("Папка и файлы рассылки не должны быть ссылками.");
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                File.WriteAllText(temp,new JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=20}.Serialize(value),new UTF8Encoding(false));
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
