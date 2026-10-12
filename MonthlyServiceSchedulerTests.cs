using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace WorkLedger
{
    public static class MonthlyServiceSchedulerTests
    {
        private const string Sid = "S-1-5-21-101-202-303-1001";
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Monthly scheduler test: " + message);
        }
        private sealed class FakeBackend : MonthlyServiceScheduler.ITaskBackend
        {
            internal readonly Dictionary<string, string> Tasks = new Dictionary<string, string>(StringComparer.Ordinal);
            internal int Registrations, Deletions;
            internal bool FailRegistration, FailReadback;
            internal string LastSid;
            public string ReadXml(string name) { string xml; return FailReadback && Registrations > 0 ? null : Tasks.TryGetValue(name, out xml) ? xml : null; }
            public void Register(string name, string xml, string sid)
            {
                if (FailRegistration) throw new COMException("Access denied", unchecked((int)0x80070005));
                Registrations++; LastSid = sid; Tasks[name] = xml;
            }
            public void Delete(string name) { Deletions++; Tasks.Remove(name); }
            public void Dispose() { }
        }
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
        private static void AssertArgument(string value)
        {
            int count;
            IntPtr arguments = CommandLineToArgvW("runner.exe --data-dir " + MonthlyServiceScheduler.QuoteArgument(value), out count);
            if (arguments == IntPtr.Zero) throw new InvalidOperationException("Native command line parser unavailable.");
            try { Assert(count == 3 && Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, 2 * IntPtr.Size)) == value, "quoted argument did not round-trip: " + value); }
            finally { LocalFree(arguments); }
        }
        public static void Run(string rootPath)
        {
            // These files are inert placeholders: no executable is started and no scheduled task is registered.
            string directory = Path.Combine(Path.GetFullPath(rootPath), "monthly-scheduler-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, "Акты и оплаты & тест.exe"), moved = Path.Combine(directory, "Moved.exe");
            File.WriteAllText(executable, "test placeholder"); File.WriteAllText(moved, "test placeholder");
            string dataDir = Path.Combine(directory, "База актов & организации"), otherDir = Path.Combine(directory, "other-ledger");
            string taskName = MonthlyServiceScheduler.GetTaskName(dataDir, Sid);
            Assert(taskName == MonthlyServiceScheduler.GetTaskName(dataDir.ToUpperInvariant() + "\\", Sid), "same Windows folder produced different task names");
            Assert(taskName != MonthlyServiceScheduler.GetTaskName(otherDir, Sid), "separate ledgers shared a task");
            Assert(taskName != MonthlyServiceScheduler.GetTaskName(dataDir, "S-1-5-21-101-202-303-1002"), "separate Windows users shared a task");
            AssertArgument(dataDir); AssertArgument("C:\\папка с пробелами\\"); AssertArgument("C:\\"); AssertArgument("a\\\"quoted\\\" & $'value"); AssertArgument("");

            string xml = MonthlyServiceScheduler.BuildTaskXml(executable, dataDir, Sid);
            XDocument definition = XDocument.Parse(xml);
            XElement root = definition.Root, settings = root.Element(Ns + "Settings"), triggers = root.Element(Ns + "Triggers");
            Assert(triggers.Elements().Count() == 2, "unexpected recurrence or triggers");
            XElement monthly = triggers.Element(Ns + "CalendarTrigger"), logon = triggers.Element(Ns + "LogonTrigger");
            Assert((string)monthly.Element(Ns + "StartBoundary") == "2000-01-01T00:05:00+03:00", "monthly start did not preserve Minsk UTC offset");
            Assert(monthly.Elements().First().Name.LocalName == "Enabled", "trigger base XML does not follow the Windows schema sequence");
            XElement schedule = monthly.Element(Ns + "ScheduleByMonth");
            Assert(schedule.Element(Ns + "DaysOfMonth").Elements().Single().Value == "1" && schedule.Element(Ns + "Months").Elements().Count() == 12, "not scheduled on first day of all months");
            Assert((string)logon.Element(Ns + "UserId") == Sid, "logon catch-up belongs to another user");
            XElement principal = root.Element(Ns + "Principals").Element(Ns + "Principal");
            Assert((string)principal.Element(Ns + "UserId") == Sid && (string)principal.Element(Ns + "RunLevel") == "LeastPrivilege" && (string)principal.Element(Ns + "LogonType") == "InteractiveToken", "task requested elevation/password or changed Windows user");
            Assert((string)settings.Element(Ns + "MultipleInstancesPolicy") == "IgnoreNew" && (string)settings.Element(Ns + "StartWhenAvailable") == "true", "missed/parallel execution policy is wrong");
            Assert((string)settings.Element(Ns + "WakeToRun") == "false" && (string)settings.Element(Ns + "RunOnlyIfNetworkAvailable") == "false" && (string)settings.Element(Ns + "RunOnlyIfIdle") == "false", "task added unnecessary wake/network/idle requirement");
            Assert((string)settings.Element(Ns + "DisallowStartIfOnBatteries") == "false" && (string)settings.Element(Ns + "StopIfGoingOnBatteries") == "false", "battery use prevents or stops generation");
            XElement action = root.Element(Ns + "Actions").Elements().Single();
            Assert(action.Name == Ns + "Exec" && (string)action.Element(Ns + "Command") == executable && (string)action.Element(Ns + "Arguments") == "--generate-monthly-acts --data-dir " + MonthlyServiceScheduler.QuoteArgument(dataDir), "action did not use the direct absolute executable and isolated ledger");
            Assert(!xml.Contains("powershell") && !xml.Contains("cmd.exe"), "scheduler invoked a shell");

            FakeBackend backend = new FakeBackend();
            backend.Tasks["unrelated-task"] = "untouched";
            MonthlyServiceScheduler.SchedulerResult result = MonthlyServiceScheduler.EnsureWithBackend(executable, dataDir, true, Sid, backend);
            Assert(result.Success && result.Changed && result.TaskName == taskName && backend.Registrations == 1 && backend.LastSid == Sid, "initial registration failed");
            result = MonthlyServiceScheduler.EnsureWithBackend(executable, dataDir, true, Sid, backend);
            Assert(result.Success && !result.Changed && backend.Registrations == 1, "unchanged startup rewrote task");
            XDocument normalized = XDocument.Parse(backend.Tasks[taskName]);
            normalized.Root.SetAttributeValue("version", "1.4");
            normalized.Root.Element(Ns + "Settings").Add(new XElement(Ns + "Priority", "7"));
            normalized.Root.Element(Ns + "Settings").Element(Ns + "ExecutionTimeLimit").Value = "PT600S";
            backend.Tasks[taskName] = normalized.ToString();
            result = MonthlyServiceScheduler.EnsureWithBackend(executable, dataDir, true, Sid, backend);
            Assert(result.Success && !result.Changed && backend.Registrations == 1, "Windows XML normalization caused a redundant registration");
            result = MonthlyServiceScheduler.EnsureWithBackend(moved, dataDir, true, Sid, backend);
            Assert(result.Success && result.Changed && backend.Registrations == 2 && backend.Tasks[taskName].Contains(moved), "moving application did not repair its command");
            result = MonthlyServiceScheduler.EnsureWithBackend("", dataDir, false, Sid, backend);
            Assert(result.Success && result.Changed && backend.Deletions == 1 && !backend.Tasks.ContainsKey(taskName) && backend.Tasks["unrelated-task"] == "untouched", "disabling removed another task or required an existing executable");
            result = MonthlyServiceScheduler.EnsureWithBackend("", dataDir, false, Sid, backend);
            Assert(result.Success && !result.Changed && backend.Deletions == 1, "disabled missing task was not idempotent");

            backend.FailRegistration = true;
            result = MonthlyServiceScheduler.EnsureWithBackend(executable, dataDir, true, Sid, backend);
            Assert(!result.Success && !result.Changed && result.Message.Contains("80070005"), "registration access denial was reported as successful");
            backend.FailRegistration = false; backend.FailReadback = true;
            result = MonthlyServiceScheduler.EnsureWithBackend(executable, dataDir, true, Sid, backend);
            Assert(!result.Success && result.Message.Contains("не подтвердил"), "unverified registration was reported as enabled");
            backend.FailReadback = false;
            XDocument foreign = XDocument.Parse(xml);
            foreign.Root.Element(Ns + "RegistrationInfo").Element(Ns + "Source").Value = "SomeoneElse";
            backend.Tasks[taskName] = foreign.ToString();
            int writesBefore = backend.Registrations, deletesBefore = backend.Deletions;
            Assert(!MonthlyServiceScheduler.EnsureWithBackend(executable, dataDir, true, Sid, backend).Success && !MonthlyServiceScheduler.EnsureWithBackend("", dataDir, false, Sid, backend).Success
                && writesBefore == backend.Registrations && deletesBefore == backend.Deletions, "foreign task occupying the exact name was overwritten/deleted");
            Assert(!MonthlyServiceScheduler.EnsureWithBackend(Path.Combine(directory, "missing.exe"), dataDir, true, Sid, backend).Success && writesBefore == backend.Registrations, "missing executable was registered");
            Assert(!MonthlyServiceScheduler.EnsureWithBackend(executable, "relative-database", true, Sid, backend).Success, "relative database path was accepted");
            Assert(!MonthlyServiceScheduler.EnsureWithBackend(executable, "C:relative-database", true, Sid, backend).Success, "drive-relative database path was accepted");
            Assert(!MonthlyServiceScheduler.MatchesDefinition(xml.Replace("<Day>1</Day>", "<Day>2</Day>"), xml), "wrong day was accepted");
            Assert(!MonthlyServiceScheduler.MatchesDefinition(xml.Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>"), xml), "disabled definition was accepted");

            string unavailable;
            bool nativeValidated = MonthlyServiceScheduler.ValidateTaskXmlReadOnly(MonthlyServiceScheduler.BuildTaskXml(executable, dataDir, MonthlyServiceScheduler.CurrentUserSid()), out unavailable);
            File.WriteAllText(Path.Combine(directory, "native-xml-validation.txt"), nativeValidated ? "Windows Task Scheduler accepted XML; no task registered." : "Unavailable: " + unavailable);
        }
    }
}
