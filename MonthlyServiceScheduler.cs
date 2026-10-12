using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WorkLedger
{
    // One task per Windows user and ledger folder. No password, elevation or shell command is used.
    public static class MonthlyServiceScheduler
    {
        private const string TaskSource = "WorkLedger.MonthlyService.v1";
        private const string TaskPrefix = "AktiOplaty-Monthly-";
        private const string Boundary = "2000-01-01T00:05:00+03:00";
        private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        private static readonly object Gate = new object();

        public sealed class SchedulerResult
        {
            public bool Success { get; internal set; }
            public bool Changed { get; internal set; }
            public string Message { get; internal set; }
            public string TaskName { get; internal set; }
            public string ErrorDetails { get; internal set; }
        }

        public static SchedulerResult Ensure(string executablePath, string dataDir, bool enabled)
        {
            lock (Gate)
            {
                string taskName = "";
                try
                {
                    string sid = CurrentUserSid();
                    string directory = AbsolutePath(dataDir, "папка базы");
                    taskName = GetTaskName(directory, sid);
                    if (enabled) ValidateExecutable(executablePath);
                    using (ITaskBackend backend = new ComTaskBackend())
                        return EnsureWithBackend(executablePath, directory, enabled, sid, backend);
                }
                catch (Exception error) { return Failure(error, taskName); }
            }
        }

        internal static SchedulerResult EnsureWithBackend(string executablePath, string dataDir, bool enabled, string sid, ITaskBackend backend)
        {
            string taskName = "";
            try
            {
                string directory = AbsolutePath(dataDir, "папка базы");
                taskName = GetTaskName(directory, sid);
                string executable = enabled ? ValidateExecutable(executablePath) : "";
                string existing = backend.ReadXml(taskName);
                if (existing != null && !IsOwnTask(existing, sid))
                    throw new InvalidOperationException("Имя фоновой задачи занято другой задачей Windows. Существующая задача не изменена.");
                if (!enabled)
                {
                    if (existing != null) backend.Delete(taskName);
                    return Success(taskName, existing != null, "Фоновое создание актов отключено.");
                }
                string expected = BuildTaskXml(executable, directory, sid);
                if (existing != null && MatchesDefinition(existing, expected))
                    return Success(taskName, false, "Фоновое создание актов включено: 1-го числа в 00:05 по времени Минска.");
                backend.Register(taskName, expected, sid);
                // Only report enabled after the registered definition has actually been read back.
                string registered = backend.ReadXml(taskName);
                if (registered == null || !IsOwnTask(registered, sid) || !MatchesDefinition(registered, expected))
                    throw new InvalidOperationException("Планировщик Windows не подтвердил сохранение фоновой задачи.");
                return Success(taskName, true, "Фоновое создание актов включено: 1-го числа в 00:05 по времени Минска.");
            }
            catch (Exception error) { return Failure(error, taskName); }
        }

        private static SchedulerResult Success(string taskName, bool changed, string message)
        {
            return new SchedulerResult { Success = true, Changed = changed, TaskName = taskName, Message = message, ErrorDetails = "" };
        }
        private static SchedulerResult Failure(Exception error, string taskName)
        {
            Exception cause = Unwrap(error);
            COMException comError = cause as COMException;
            string reason = comError == null ? cause.Message : "Планировщик Windows вернул ошибку 0x" + unchecked((uint)comError.ErrorCode).ToString("X8", CultureInfo.InvariantCulture) + ".";
            return new SchedulerResult { Success = false, Changed = false, TaskName = taskName, Message = "Не удалось настроить фоновое создание актов. " + reason, ErrorDetails = cause.ToString() };
        }

        internal static string CurrentUserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User == null) throw new InvalidOperationException("Не удалось определить пользователя Windows.");
                return identity.User.Value;
            }
        }
        internal static string GetTaskName(string dataDir, string sid)
        {
            // SID validation also prevents an accidentally empty/current-global task identity.
            new SecurityIdentifier(sid);
            string directory = AbsolutePath(dataDir, "папка базы").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
            using (SHA256 hash = SHA256.Create())
                return TaskPrefix + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(sid + "\n" + directory))).Replace("-", "").ToLowerInvariant();
        }
        private static string AbsolutePath(string value, string label)
        {
            if (String.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) || Path.GetPathRoot(value).Length <= 2)
                throw new ArgumentException("Не указан полный путь: " + label + ".");
            string result = Path.GetFullPath(value);
            if (result.IndexOf('%') >= 0)
                throw new ArgumentException("Для фонового создания актов путь не должен содержать знак %. Выберите другую папку: " + label + ".");
            return result;
        }
        private static string ValidateExecutable(string path)
        {
            string result = AbsolutePath(path, "программа");
            if (!String.Equals(Path.GetExtension(result), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(result))
                throw new InvalidOperationException("Не найден исполняемый файл программы для фонового создания актов.");
            if (result.Length > 260) throw new InvalidOperationException("Путь к программе слишком длинный для Планировщика Windows.");
            return result;
        }

        internal static string QuoteArgument(string value)
        {
            // Windows CommandLineToArgvW / CLR rules, including trailing backslashes.
            StringBuilder quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') { quoted.Append('\\', slashes * 2 + 1); quoted.Append('"'); }
                else { quoted.Append('\\', slashes); quoted.Append(character); }
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2); quoted.Append('"');
            return quoted.ToString();
        }
        private static XElement Element(string name, object value) { return new XElement(TaskNamespace + name, value); }
        internal static string BuildTaskXml(string executablePath, string dataDir, string sid)
        {
            string executable = AbsolutePath(executablePath, "программа"), directory = AbsolutePath(dataDir, "папка базы");
            new SecurityIdentifier(sid);
            string[] months = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
            XDocument document = new XDocument(new XElement(TaskNamespace + "Task", new XAttribute("version", "1.2"),
                Element("RegistrationInfo", new object[] { Element("Source", TaskSource), Element("Author", sid), Element("Description", "Автоматическое создание актов ежемесячного обслуживания в программе «Акты и оплаты».") }),
                Element("Triggers", new object[] {
                    new XElement(TaskNamespace + "CalendarTrigger", new XAttribute("id", "Monthly"), Element("Enabled", "true"), Element("StartBoundary", Boundary),
                        Element("ScheduleByMonth", new object[] { Element("DaysOfMonth", Element("Day", "1")), Element("Months", months.Select(month => Element(month, null))) })),
                    new XElement(TaskNamespace + "LogonTrigger", new XAttribute("id", "Logon"), Element("Enabled", "true"), Element("UserId", sid), Element("Delay", "PT20S")) }),
                Element("Principals", new XElement(TaskNamespace + "Principal", new XAttribute("id", "Owner"), Element("UserId", sid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
                Element("Settings", new object[] { Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", "false"), Element("StopIfGoingOnBatteries", "false"),
                    Element("StartWhenAvailable", "true"), Element("RunOnlyIfNetworkAvailable", "false"), Element("RunOnlyIfIdle", "false"), Element("WakeToRun", "false"),
                    Element("Enabled", "true"), Element("Hidden", "false"), Element("ExecutionTimeLimit", "PT10M"),
                    Element("RestartOnFailure", new object[] { Element("Interval", "PT1M"), Element("Count", "3") }) }),
                new XElement(TaskNamespace + "Actions", new XAttribute("Context", "Owner"), Element("Exec", new object[] {
                    Element("Command", executable), Element("Arguments", "--generate-monthly-acts --data-dir " + QuoteArgument(directory)), Element("WorkingDirectory", Path.GetDirectoryName(executable)) }))));
            return document.ToString();
        }

        private static XDocument ReadDefinition(string xml)
        {
            // Task XML is normally local OS output; prohibit DTDs even if a task was manually replaced.
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1000000 };
            using (StringReader source = new StringReader(xml))
            using (XmlReader reader = XmlReader.Create(source, settings)) return XDocument.Load(reader);
        }
        private static bool IsOwnTask(string xml, string sid)
        {
            try
            {
                XDocument document = ReadDefinition(xml);
                XElement root = document.Root, info = root == null ? null : root.Element(TaskNamespace + "RegistrationInfo"), principals = root == null ? null : root.Element(TaskNamespace + "Principals");
                return root != null && root.Name == TaskNamespace + "Task" && info != null && (string)info.Element(TaskNamespace + "Source") == TaskSource
                    && principals != null && principals.Elements().Count() == 1 && (string)principals.Element(TaskNamespace + "Principal").Element(TaskNamespace + "UserId") == sid;
            }
            catch (Exception error)
            {
                if (error is XmlException || error is InvalidOperationException || error is NullReferenceException) return false;
                throw;
            }
        }
        internal static bool MatchesDefinition(string existingXml, string expectedXml)
        {
            try { return MatchesElement(ReadDefinition(existingXml).Root, ReadDefinition(expectedXml).Root); }
            catch (Exception error)
            {
                if (error is XmlException || error is FormatException || error is ArgumentException || error is OverflowException) return false;
                throw;
            }
        }
        private static bool MatchesElement(XElement actual, XElement expected)
        {
            if (actual == null || actual.Name != expected.Name) return false;
            foreach (XAttribute attribute in expected.Attributes())
            {
                // The OS may export a newer compatible schema version.
                if (attribute.Name.LocalName == "version" && expected.Name.LocalName == "Task") continue;
                if ((string)actual.Attribute(attribute.Name) != attribute.Value) return false;
            }
            if (!expected.HasElements)
            {
                if (actual.HasElements) return false;
                if (expected.Name.LocalName == "ExecutionTimeLimit" || expected.Name.LocalName == "Delay" || expected.Name.LocalName == "Interval")
                    return XmlConvert.ToTimeSpan(actual.Value) == XmlConvert.ToTimeSpan(expected.Value);
                if (expected.Value == "true" || expected.Value == "false") return XmlConvert.ToBoolean(actual.Value) == XmlConvert.ToBoolean(expected.Value);
                return actual.Value == expected.Value;
            }
            string name = expected.Name.LocalName;
            // Extra scheduler defaults are fine; extra triggers/actions/month days are not.
            if ((name == "Triggers" || name == "Actions" || name == "Principals" || name == "DaysOfMonth" || name == "Months" || name == "ScheduleByMonth")
                && actual.Elements().Count() != expected.Elements().Count()) return false;
            foreach (XElement child in expected.Elements())
            {
                XElement[] matches = actual.Elements(child.Name).ToArray();
                if (matches.Length != 1 || !MatchesElement(matches[0], child)) return false;
            }
            // An end boundary or repetition could silently stop or multiply the monthly run.
            if ((name == "CalendarTrigger" || name == "LogonTrigger")
                && actual.Elements().Any(child => child.Name.LocalName == "EndBoundary" || child.Name.LocalName == "Repetition")) return false;
            return true;
        }

        internal interface ITaskBackend : IDisposable
        {
            string ReadXml(string taskName);
            void Register(string taskName, string xml, string sid);
            void Delete(string taskName);
        }
        private sealed class ComTaskBackend : ITaskBackend
        {
            private object service, folder;
            public ComTaskBackend()
            {
                try
                {
                    Type type = Type.GetTypeFromProgID("Schedule.Service");
                    if (type == null) throw new InvalidOperationException("Планировщик Windows недоступен.");
                    service = Activator.CreateInstance(type);
                    Invoke(service, "Connect", Type.Missing, Type.Missing, Type.Missing, Type.Missing);
                    folder = Invoke(service, "GetFolder", "\\");
                }
                catch { Dispose(); throw; }
            }
            public string ReadXml(string taskName)
            {
                object task = null;
                try { task = Invoke(folder, "GetTask", taskName); return (string)Get(task, "Xml"); }
                catch (Exception error)
                {
                    COMException cause = Unwrap(error) as COMException;
                    if (cause != null && (cause.ErrorCode == unchecked((int)0x80070002) || cause.ErrorCode == unchecked((int)0x80070003))) return null;
                    throw;
                }
                finally { Release(task); }
            }
            public void Register(string taskName, string xml, string sid)
            {
                object task = null;
                try { task = Invoke(folder, "RegisterTask", taskName, xml, 6, sid, null, 3, Type.Missing); }
                finally { Release(task); }
            }
            public void Delete(string taskName) { Invoke(folder, "DeleteTask", taskName, 0); }
            public void Dispose() { Release(folder); folder = null; Release(service); service = null; }
        }
        private static object Invoke(object target, string member, params object[] args)
        {
            return target.GetType().InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding, null, target, args, CultureInfo.InvariantCulture);
        }
        private static object Get(object target, string member)
        {
            return target.GetType().InvokeMember(member, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);
        }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        private static Exception Unwrap(Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            return error;
        }

        // Checks the installed Windows XML parser without saving, enabling or running any task.
        internal static bool ValidateTaskXmlReadOnly(string xml, out string unavailableReason)
        {
            object service = null, definition = null;
            unavailableReason = "";
            try
            {
                Type type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) { unavailableReason = "Планировщик Windows не установлен."; return false; }
                try
                {
                    service = Activator.CreateInstance(type);
                    Invoke(service, "Connect", Type.Missing, Type.Missing, Type.Missing, Type.Missing);
                }
                catch (Exception error)
                {
                    COMException cause = Unwrap(error) as COMException;
                    if (cause != null && (cause.ErrorCode == unchecked((int)0x80040154) || cause.ErrorCode == unchecked((int)0x80041315) || cause.ErrorCode == unchecked((int)0x80070005)))
                    { unavailableReason = cause.Message; return false; }
                    throw;
                }
                definition = Invoke(service, "NewTask", 0);
                definition.GetType().InvokeMember("XmlText", BindingFlags.SetProperty, null, definition, new object[] { xml }, CultureInfo.InvariantCulture);
                if (!MatchesDefinition((string)Get(definition, "XmlText"), xml))
                    throw new InvalidOperationException("Нормализация XML Планировщиком Windows изменила параметры фоновой задачи.");
                return true;
            }
            finally { Release(definition); Release(service); }
        }
    }
}
