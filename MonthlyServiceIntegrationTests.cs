using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
namespace WorkLedger
{
    public static class MonthlyServiceIntegrationTests
    {
        private static void Assert(bool condition, string reason) { if (!condition) throw new Exception("Monthly integration: " + reason); }
        private static Record Add(Store store, string kind, params string[] values)
        {
            var record = new Record(kind);
            for (int i = 0; i < values.Length; i += 2) record.Set(values[i], values[i + 1]);
            store.Upsert(record); return record;
        }
        private static XDocument Sheet(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            using (var stream = archive.GetEntry("xl/worksheets/sheet1.xml").Open()) return XDocument.Load(stream);
        }
        private static void TestUiAndDocuments(string path)
        {
            var store = new Store(Path.Combine(path, "data"));
            Record own = Add(store, "settings", "name", "Исполнитель по обслуживанию", "currency", "BYN");
            Record party = Add(store, "party", "name", "Заказчик по обслуживанию", "role", "client", "monthlyServiceEnabled", "true",
                "monthlyServiceAmount", "275.55", "monthlyServiceCurrency", "BYN", "monthlyServiceStartMonth", "2026-11-01");
            long revision = store.Revision;
            using (var form = new MainForm(store))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
                Assert(store.Revision == revision && store.Of("act").Count() == 0, "test-only form caused background scheduling or generation");
                Assert(form.ProcessMonthlyServiceActs(new DateTime(2026, 12, 1)) == 2, "missing periods were not created from the live interface");
                long generatedRevision = store.Revision;
                Assert(form.ProcessMonthlyServiceActs(new DateTime(2026, 12, 20)) == 0 && store.Revision == generatedRevision, "UI polling wrote duplicate acts or changed the ledger");
                Record act = store.Of("act").First(a => a.Get("monthlyServiceMonth") == "2026-11");
                Assert(act.Get("number") == "500" && act.Get("date") == "2026-11-01" && act.Get("amount") == "275.55" && act.Get("hours") == "" && act.Get("workItems") == "", "fixed-price act date, number, amount or hours changed");
                Assert(store.ActGrossDue(act.Id) == 0, "generated drafts were included in debt");
                own.Set("name", "Изменённый исполнитель"); store.Upsert(own);
                party.Set("name", "Изменённый заказчик"); party.Set("monthlyServiceAmount", "900.00"); store.Upsert(party);
                var details = form.BuildDocumentDetails(act);
                Assert(details.Values.Any(v => v.Contains("Исполнитель по обслуживанию")) && details.Values.Any(v => v.Contains("Заказчик по обслуживанию")), "monthly print uses current requisites instead of snapshots");
                string html = Path.Combine(path, "monthly-act.html"), excel = Path.Combine(path, "monthly-act.xlsx");
                HourlyActDocuments.WriteHtml(html, store, act); HourlyActDocuments.WriteXlsx(excel, store, act);
                string printed = File.ReadAllText(html, Encoding.UTF8), xml = Sheet(excel).ToString();
                Assert(printed.Contains("Исполнитель по обслуживанию") && printed.Contains("Заказчик по обслуживанию") && printed.Contains("275.55"), "monthly HTML has wrong requisites or amount");
                Assert(xml.Contains("275.55") && xml.Contains("Исполнитель по обслуживанию") && xml.Contains("Заказчик по обслуживанию"), "monthly Excel has wrong requisites or amount");
                var ns = XNamespace.Get("http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                Assert(Sheet(excel).Descendants(ns + "c").Any(c => c.Attribute("t") == null && (string)c.Element(ns + "v") == "275.55"), "fixed price is not a numeric Excel cell");
                string word = Path.Combine(path, "monthly-act.docx"); ActDocument.Export(store, act, word);
                using (var zip = ZipFile.OpenRead(word))
                using (var stream = zip.GetEntry("word/document.xml").Open())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string content = reader.ReadToEnd();
                    Assert(content.Contains("Исполнитель по обслуживанию") && content.Contains("Заказчик по обслуживанию"), "monthly Word changed stored requisites");
                }
                form.Close();
            }
        }
        private static void TestBackgroundAndMutex(string path)
        {
            var store = new Store(Path.Combine(path, "data"));
            Add(store, "settings", "name", "Фоновый исполнитель", "currency", "BYN");
            DateTime month = new DateTime(MonthlyServiceRuntime.Today.Year, MonthlyServiceRuntime.Today.Month, 1);
            Add(store, "party", "name", "Фоновый заказчик", "role", "client", "monthlyServiceEnabled", "true",
                "monthlyServiceAmount", "100.00", "monthlyServiceCurrency", "BYN", "monthlyServiceStartMonth", Store.DateString(month));
            bool first;
            using (var mutex = new Mutex(true, MonthlyServiceRuntime.MutexName(store.DataDir), out first))
            {
                Assert(first, "fixture mutex already existed");
                try { Assert(MonthlyServiceRuntime.RunBackground(store.DataDir) == 0 && new Store(store.DataDir).Of("act").Count() == 0, "background generator wrote while another app held the database"); }
                finally { mutex.ReleaseMutex(); }
            }
            Assert(MonthlyServiceRuntime.RunBackground(store.DataDir) == 0, "headless generator failed");
            var generated = new Store(store.DataDir);
            Assert(generated.Of("act").Count() == 1 && generated.Of("act").Single().Get("number") == "500", "headless generator did not create the due act");
            long revision = generated.Revision;
            Assert(MonthlyServiceRuntime.RunBackground(store.DataDir) == 0 && new Store(store.DataDir).Revision == revision, "repeated headless run changed the database");
            Assert(MonthlyServiceRuntime.RunBackground(Path.Combine(path, "absent")) == 0 && !Directory.Exists(Path.Combine(path, "absent")), "missing database caused a new empty database");
        }
        private static void TestExportImport(string path)
        {
            var store = new Store(Path.Combine(path, "data"));
            Add(store, "settings", "currency", "BYN");
            var rows = new List<Dictionary<string, string>> {
                new Dictionary<string, string> {
                    { "Название / ФИО", "Импорт ежемесячного обслуживания" }, { "Роль", "Заказчик" },
                    { "Ежемесячное обслуживание", "Да" }, { "Сумма за месяц", "200,50" },
                    { "Валюта", "BYN" }, { "Первый акт", "01.11.2026" }
                }
            };
            using (var dialog = new ImportDialog(store, Catalog.Get("party"), rows, "BYN"))
            {
                List<Record> prepared = dialog.PrepareImport();
                Assert(prepared.Single().Get("monthlyServiceEnabled") == "true" && prepared.Single().Get("monthlyServiceAmount") == "200.50", "exported bool or amount cannot be imported");
                store.ImportBatch(prepared);
            }
        }
        public static void Run(string root)
        {
            string path = Path.Combine(root, "monthly-integration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            TestUiAndDocuments(Path.Combine(path, "ui-documents"));
            TestBackgroundAndMutex(Path.Combine(path, "headless"));
            TestExportImport(Path.Combine(path, "import"));
            File.WriteAllText(Path.Combine(path, "PASS.txt"), "PASS: monthly UI polling, fixed-price docs and immutable snapshots, headless generation, database mutex, duplicate protection and import.");
        }
    }
}
