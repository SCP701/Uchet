using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace WorkLedger
{
    public static class ReconciliationImportTests
    {
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Reconciliation test: " + message); }
        private static void Reject(Action action, string message) { try { action(); } catch { return; } throw new InvalidOperationException("Reconciliation test: " + message); }
        private static Record Party(string id) { var r = new Record("party") { Id = id }; r.Set("name", "Тестовый заказчик"); r.Set("role", "client"); return r; }
        private static Record Act(string id, string partyId)
        {
            var r = new Record("act") { Id = id };
            r.Set("number", "TEST-01"); r.Set("date", "2026-10-12"); r.Set("partyId", partyId); r.Set("amount", "100.00"); r.Set("currency", "BYN");
            r.Set("direction", "income"); r.Set("status", "draft"); r.Set("signatureStatus", "unknown"); r.Set("description", "Исходное описание"); return r;
        }
        private static string Items(decimal price, string description)
        {
            return ActDocument.SerializeItems(new[] { new ActPrintLine { Description = description, Unit = "усл.", Quantity = 1m, Price = price } });
        }
        private static ReconciliationPackage Package(params ReconciliationEntry[] entries)
        {
            return new ReconciliationPackage { Format = "WorkLedger.Reconciliation", Version = 1, PackageId = "synthetic-review-test", Title = "Тест сверки",
                SourceHashes = new Dictionary<string, string> { { "synthetic-source", new string('a', 64) } }, Entries = entries.ToList() };
        }
        private static ReconciliationEntry Update(Record before, Record desired, params string[] fields)
        {
            return new ReconciliationEntry { Before = before.Clone(), Record = desired.Clone(), UpdateFields = fields };
        }
        private static string Json(object value) { return new JavaScriptSerializer { MaxJsonLength = 32000000 }.Serialize(value); }
        private static void SameFinancial(Record before, Record after)
        {
            foreach (string key in new[] { "number", "date", "partyId", "amount", "currency", "direction", "status", "signatureStatus", "notes", "archived", "actId", "method", "paymentScope" })
                Assert(before.Get(key) == after.Get(key), "protected field changed: " + key);
        }
        public static void Run(string tempdir)
        {
            string root = Path.Combine(tempdir, "reconciliation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Record party = Party("synthetic-party"), original = Act("synthetic-act", party.Id);
            original.Set("notes", "Примечание пользователя"); original.Set("unknownFutureField", "Сохранить");
            Record desired = original.Clone(); desired.Set("printItemsJson", Items(100m, "Подтверждённая операция"));
            desired.Set("reviewStatus", "matched"); desired.Set("reviewNotes", "Сверено с исходным документом.");
            var package = Package(Update(original, desired, "printItemsJson", "reviewStatus", "reviewNotes"));
            var snapshot = new List<Record> { party.Clone(), original.Clone() }; string untouched = Json(snapshot);
            ReconciliationPlan plan = ReconciliationImport.PlanReadOnly(snapshot, package);
            Assert(Json(snapshot) == untouched && original.Get("printItemsJson") == "", "planning mutated source records");
            Assert(plan.NewCount == 0 && plan.ExistingCount == 1 && plan.ConflictCount == 0, "safe update counts are incorrect");
            Record result = plan.Records.Single(); SameFinancial(original, result);
            Assert(result.Get("unknownFutureField") == "Сохранить" && result.Get("printItemsJson") == desired.Get("printItemsJson"), "positions or unknown fields lost");
            ReconciliationPlan repeated = ReconciliationImport.PlanReadOnly(new[] { party, result }, package);
            Assert(repeated.Records.Count == 0 && repeated.UnchangedCount == 1, "a repeated update is not idempotent");
            Record manuallyChecked = result.Clone(); manuallyChecked.Set("reviewStatus", "checked"); manuallyChecked.Set("reviewNotes", result.Get("reviewNotes") + "\r\nПроверено пользователем.");
            Assert(ReconciliationImport.PlanReadOnly(new[] { party, manuallyChecked }, package).Records.Count == 0, "repeat reset a manual checked status or notes");

            Record edited = original.Clone(); edited.Set("amount", "110.00"); edited.Set("status", "signed"); edited.Set("signatureStatus", "confirmed");
            edited.Set("archived", "true"); edited.Set("notes", "Дополнено пользователем");
            ReconciliationPlan changedFinance = ReconciliationImport.PlanReadOnly(new[] { party, edited }, package);
            Record financeResult = changedFinance.Records.Single(); SameFinancial(edited, financeResult);
            Assert(financeResult.Get("printItemsJson") == "" && financeResult.Get("reviewStatus") == "pending" && financeResult.Get("sourcePositionsJson").Contains("Подтверждённая операция"), "financial conflict lost original positions or pending flag");
            Assert(changedFinance.ConflictCount == 1, "financial conflict is not shown");
            Assert(ReconciliationImport.PlanReadOnly(new[] { party, financeResult }, package).Records.Count == 0, "financial conflict grows on repeated import");
            Record checkedConflict = financeResult.Clone(); checkedConflict.Set("reviewStatus", "checked");
            Assert(ReconciliationImport.PlanReadOnly(new[] { party, checkedConflict }, package).Records.Count == 0, "known conflict reset a manual checked status");

            Record editedPositions = original.Clone(); editedPositions.Set("printItemsJson", Items(100m, "Работа пользователя"));
            Record preserved = ReconciliationImport.PlanReadOnly(new[] { party, editedPositions }, package).Records.Single();
            Assert(preserved.Get("printItemsJson") == editedPositions.Get("printItemsJson") && preserved.Get("reviewStatus") == "pending", "user positions overwritten");
            Assert(preserved.Get("sourcePositionsJson").Contains("Подтверждённая операция"), "conflicting source positions were not preserved");
            Assert(ReconciliationImport.PlanReadOnly(new[] { party, preserved }, package).Records.Count == 0, "position conflict is not idempotent");
            Record userDescriptions = original.Clone(); userDescriptions.Set("descriptiveItemsJson", ActHtmlDocument.SerializeDescriptions(new[] { "Операция пользователя без цены" }));
            Record guardedFormats = ReconciliationImport.PlanReadOnly(new[] { party, userDescriptions }, package).Records.Single();
            Assert(guardedFormats.Get("printItemsJson") == "" && guardedFormats.Get("descriptiveItemsJson") == userDescriptions.Get("descriptiveItemsJson"), "a higher priority source format hid user positions");
            var partyOnly = Package(new ReconciliationEntry { Before = party.Clone(), Record = party.Clone(), UpdateFields = new string[0] });
            Assert(ReconciliationImport.PlanReadOnly(snapshot, partyOnly).Records.Count == 0, "unchanged party received invented review markers");

            Record invalidPositions = original.Clone(); invalidPositions.Set("printItemsJson", Items(40m, "Операция с конфликтом итога"));
            invalidPositions.Set("reviewStatus", "matched"); invalidPositions.Set("reviewNotes", "Исходный итог требует сверки.");
            var badPositionsPackage = Package(Update(original, invalidPositions, "printItemsJson", "reviewStatus", "reviewNotes"));
            ReconciliationPlan fallback = ReconciliationImport.PlanReadOnly(new[] { party, original }, badPositionsPackage);
            Record descriptive = fallback.Records.Single(); string summary;
            var rows = ActHtmlDocument.ReadDisplayRows(descriptive, out summary);
            SameFinancial(original, descriptive);
            Assert(descriptive.Get("reviewStatus") == "pending" && descriptive.Get("sourcePositionsJson") != "" && rows.Count == 1 && !rows[0].Price.HasValue, "invalid totals did not fall back to descriptive rows");
            Assert(ReconciliationImport.PlanReadOnly(new[] { party, descriptive }, badPositionsPackage).Records.Count == 0, "numeric fallback is not idempotent");

            Record hourly = original.Clone(); hourly.Set("workItems", "[{\"Description\":\"Почасовая работа\",\"Hours\":2,\"Rate\":50}]");
            hourly.Set("reviewStatus", "matched"); hourly.Set("reviewNotes", "Подтверждённые часы.");
            var hourlyPackage = Package(Update(original, hourly, "workItems", "reviewStatus", "reviewNotes"));
            Record hourlyResult = ReconciliationImport.PlanReadOnly(new[] { party, original }, hourlyPackage).Records.Single();
            Assert(hourlyResult.Get("hours") == "2" && hourlyResult.Get("hourlyRate") == "50.00" && hourlyResult.Get("amount") == "100.00", "hourly positions were not checked and normalized");
            Assert(ReconciliationImport.PlanReadOnly(new[] { party, hourlyResult }, hourlyPackage).Records.Count == 0, "hourly normalization is not idempotent");

            Record payment = new Record("payment") { Id = "synthetic-payment" };
            foreach (var field in original.Fields.Where(field => new[] { "date", "partyId", "amount", "currency", "direction" }.Contains(field.Key))) payment.Set(field.Key, field.Value);
            payment.Set("number", "PAY-01"); payment.Set("method", "bank"); payment.Set("notes", "Прежнее назначение");
            Record paymentDesired = payment.Clone(); paymentDesired.Set("candidateActIds", original.Id); paymentDesired.Set("reviewStatus", "pending"); paymentDesired.Set("reviewNotes", "Возможное совпадение.");
            Record paymentAfter = ReconciliationImport.PlanReadOnly(new[] { party, original, payment }, Package(Update(payment, paymentDesired, "candidateActIds", "reviewStatus", "reviewNotes"))).Records.Single();
            SameFinancial(payment, paymentAfter); Assert(paymentAfter.Get("actId") == "" && paymentAfter.Get("candidateActIds") == original.Id, "candidate match caused direct allocation");

            Record document = new Record("document") { Id = "synthetic-document" }; document.Set("title", "Документ для проверки"); document.Set("status", "draft"); document.Set("reviewNotes", "Не хватает даты акта.");
            var newPackage = Package(new ReconciliationEntry { Record = document });
            ReconciliationPlan newPlan = ReconciliationImport.PlanReadOnly(snapshot, newPackage);
            Assert(newPlan.NewCount == 1 && newPlan.Records.Single().Get("reviewStatus") == "pending", "incomplete source document is not retained for review");
            Assert(ReconciliationImport.PlanReadOnly(snapshot.Concat(newPlan.Records), newPackage).Records.Count == 0, "new records duplicate on second import");

            string path = Path.Combine(root, "package.json"); File.WriteAllText(path, Json(package));
            ReconciliationPackage loaded = ReconciliationImport.Read(path); Assert(loaded.Entries.Count == 1 && loaded.Entries[0].Record.Get("amount") == "100.00", "JSON read changed records");
            var forbidden = Package(Update(original, desired, "amount")); Reject(delegate { ReconciliationImport.PlanReadOnly(snapshot, forbidden); }, "financial UpdateFields accepted");
            Record malformedStatus = desired.Clone(); malformedStatus.Set("reviewStatus", "reviewed");
            Reject(delegate { ReconciliationImport.PlanReadOnly(snapshot, Package(Update(original, malformedStatus, "reviewStatus"))); }, "unsupported review status accepted");
            Assert(Json(snapshot) == untouched, "invalid review status mutated input records");
            var allocation = new Record("allocation") { Id = "synthetic-allocation" };
            Reject(delegate { ReconciliationImport.PlanReadOnly(snapshot, Package(new ReconciliationEntry { Record = allocation })); }, "allocation record accepted");
            File.WriteAllText(path, "{\"Format\":\"WorkLedger.Reconciliation\",\"Version\":1,\"PackageId\":\"test\",\"Title\":\"test\",\"SourceHashes\":{},\"Entries\":[{\"Record\":{\"Id\":\"x\",\"Kind\":\"party\",\"Fields\":{\"name\":123}}}]}");
            Reject(delegate { ReconciliationImport.Read(path); }, "non-string field accepted");
            Reject(delegate { ReconciliationImport.PlanReadOnly(snapshot, Package(package.Entries[0], package.Entries[0])); }, "duplicate package IDs accepted");
            Assert(Json(snapshot) == untouched, "rejected plans mutated their snapshot");

            Store store = new Store(Path.Combine(root, "database")); store.Upsert(party); store.Upsert(original);
            long revision = store.Revision; string beforeBytes = File.ReadAllText(store.DataPath); int audits = store.Audit.Count;
            ReconciliationPlan preview = ReconciliationImport.PlanReadOnly(store, package);
            Assert(store.Revision == revision && File.ReadAllText(store.DataPath) == beforeBytes && store.Audit.Count == audits, "cancelled preview touched the database");
            Record invalidNew = new Record("party") { Id = "invalid-party" }; invalidNew.Set("name", "Некорректная роль"); invalidNew.Set("role", "invalid");
            var mixedInvalid = Package(package.Entries[0], new ReconciliationEntry { Record = invalidNew });
            Reject(delegate { ReconciliationImport.Apply(store, mixedInvalid); }, "invalid mixed import accepted");
            Assert(store.Revision == revision && File.ReadAllText(store.DataPath) == beforeBytes && store.Audit.Count == audits && store.Find(original.Id).Get("printItemsJson") == "", "invalid import partially changed the database");
            ReconciliationImport.Apply(store, preview); Record saved = new Store(store.DataDir).Find(original.Id); SameFinancial(original, saved);
            Assert(saved.Get("unknownFutureField") == "Сохранить" && saved.Get("printItemsJson") != "", "applied data lost fields");
            string appliedBytes = File.ReadAllText(store.DataPath); long appliedRevision = store.Revision;
            ReconciliationImport.Apply(store, package);
            Assert(store.Revision == appliedRevision && File.ReadAllText(store.DataPath) == appliedBytes, "repeated apply changed database or audit");
            Record changedTarget = saved.Clone(); changedTarget.Set("reviewNotes", "Дополнительная сверка");
            var nextPackage = Package(Update(saved, changedTarget, "reviewNotes")); ReconciliationPlan stale = ReconciliationImport.PlanReadOnly(store, nextPackage);
            Record unrelated = Party("another-party"); store.Upsert(unrelated); string currentBytes = File.ReadAllText(store.DataPath);
            Reject(delegate { ReconciliationImport.Apply(store, stale); }, "stale preview accepted"); Assert(File.ReadAllText(store.DataPath) == currentBytes, "stale preview changed the database");
            Console.WriteLine("PASS: reconciliation package read-only preview, protected fields, pending conflicts, source positions, numeric and hourly rows, duplicate guards and atomic apply");
        }
    }
}