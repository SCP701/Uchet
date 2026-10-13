using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace WorkLedger
{
    public static class PaymentAccountingTests
    {
        public static void Run(string root)
        {
            string folder = Path.Combine(Path.GetFullPath(root), "payment-accounting-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            int passed = 0;
            Action<string, Action> test = delegate(string name, Action action)
            {
                try { action(); passed++; Console.WriteLine("PASS payment accounting: " + name); }
                catch (Exception error) { throw new Exception("Payment accounting group failed: " + name, error); }
            };

            test("legacy and explicit ordinary advances remain wholly white", delegate
            {
                Store s = Fresh(folder, "white-advances"); Record party = Party(s);
                Record legacy = Payment(s, party, 120m), marked = Payment(s, party, 35m, "standard");
                PaymentAccounting a = ReadOnlyBuild(s);
                Split(a, legacy, 120m, 0m, 0m, 0m, 120m); Split(a, marked, 35m, 0m, 0m, 0m, 35m);
                Eq(s.Find(legacy.Id).Get("paymentScope"), "", "legacy marker is not migrated");
            });
            test("direct ordinary link classifies an advance without booking an allocation", delegate
            {
                Store s = Fresh(folder, "direct-white"); Record party = Party(s);
                Record act = Act(s, party, "standard", 200m), p = Payment(s, party, 125m, "", act);
                Split(ReadOnlyBuild(s), p, 125m, 0m, 0m, 0m, 125m);
                Eq(s.AllocatedToAct(act.Id), 0m, "direct link is not an allocation"); Eq(s.ActDue(act.Id), 200m, "debt preserved");
            });
            test("direct external link makes the whole unallocated advance grey", delegate
            {
                Store s = Fresh(folder, "direct-grey"); Record party = Party(s);
                Record act = Act(s, party, "external", 200m), p = Payment(s, party, 125m, "", act);
                Split(ReadOnlyBuild(s), p, 0m, 125m, 0m, 0m, 125m);
                Eq(s.AllocatedToAct(act.Id), 0m, "grey link is not double counted"); Eq(s.ActDue(act.Id), 200m, "debt preserved");
            });
            test("allocation-only legacy mixed payment keeps its advance white", delegate
            {
                Store s = Fresh(folder, "allocation-mixed"); Record party = Party(s);
                Record white = Act(s, party, "", 200m), grey = Act(s, party, "external", 200m), p = Payment(s, party, 120m);
                s.Allocate(p.Id, white.Id, 40m); s.Allocate(p.Id, grey.Id, 60m);
                Split(ReadOnlyBuild(s), p, 60m, 60m, 40m, 60m, 20m);
                Eq(s.AllocatedFromPayment(p.Id), 100m, "actual allocation total"); Eq(s.PaymentUnallocated(p.Id), 20m, "advance conserved");
            });
            test("direct external partial mixed payment assigns only its advance to grey", delegate
            {
                Store s = Fresh(folder, "direct-grey-partial"); Record party = Party(s);
                Record white = Act(s, party, "standard", 200m), grey = Act(s, party, "external", 200m), p = Payment(s, party, 120m, "", grey);
                s.Allocate(p.Id, white.Id, 40m); s.Allocate(p.Id, grey.Id, 60m);
                Split(ReadOnlyBuild(s), p, 40m, 80m, 40m, 60m, 20m);
                Eq(s.AllocatedFromPayment(p.Id), 100m, "direct link adds no second allocation");
            });
            test("direct ordinary partial mixed payment keeps its advance white", delegate
            {
                Store s = Fresh(folder, "direct-white-partial"); Record party = Party(s);
                Record white = Act(s, party, "standard", 200m), grey = Act(s, party, "external", 200m), p = Payment(s, party, 120m, "", white);
                s.Allocate(p.Id, white.Id, 40m); s.Allocate(p.Id, grey.Id, 60m);
                Split(ReadOnlyBuild(s), p, 60m, 60m, 40m, 60m, 20m);
            });
            test("multiple allocations and successive reversals conserve all financial parts", delegate
            {
                Store s = Fresh(folder, "multiple-reversal"); Record party = Party(s);
                Record white = Act(s, party, "standard", 300m), grey = Act(s, party, "external", 300m), p = Payment(s, party, 200m);
                s.Allocate(p.Id, grey.Id, 40m); s.Allocate(p.Id, grey.Id, 25m); s.Allocate(p.Id, white.Id, 80m);
                Split(ReadOnlyBuild(s), p, 135m, 65m, 80m, 65m, 55m);
                s.ReverseAllocation(Allocation(s, p, grey, 40m).Id); Split(ReadOnlyBuild(s), p, 175m, 25m, 80m, 25m, 95m);
                s.ReverseAllocation(Allocation(s, p, white, 80m).Id); Split(ReadOnlyBuild(s), p, 175m, 25m, 0m, 25m, 175m);
                s.ReverseAllocation(Allocation(s, p, grey, 25m).Id); Split(ReadOnlyBuild(s), p, 200m, 0m, 0m, 0m, 200m);
                Eq(s.PaymentUnallocated(p.Id), 200m, "all reversals restore advance");
            });
            test("reversal of a direct external allocation preserves its grey advance", delegate
            {
                Store s = Fresh(folder, "direct-reversal"); Record party = Party(s);
                Record grey = Act(s, party, "external", 200m), p = Payment(s, party, 100m, "", grey);
                s.Allocate(p.Id, grey.Id, 60m); Split(ReadOnlyBuild(s), p, 0m, 100m, 0m, 60m, 40m);
                s.ReverseAllocation(Allocation(s, p, grey, 60m).Id); Split(ReadOnlyBuild(s), p, 0m, 100m, 0m, 0m, 100m);
            });
            test("explicit markers classify the whole payment with compatible partial allocations", delegate
            {
                Store s = Fresh(folder, "explicit-valid"); Record party = Party(s);
                Record white = Act(s, party, "standard", 300m), grey = Act(s, party, "external", 300m);
                Record gp = Payment(s, party, 150m, "external", grey), wp = Payment(s, party, 130m, "standard", white);
                s.Allocate(gp.Id, grey.Id, 60m); s.Allocate(wp.Id, white.Id, 45m);
                PaymentAccounting a = ReadOnlyBuild(s); Split(a, gp, 0m, 150m, 0m, 60m, 90m); Split(a, wp, 130m, 0m, 45m, 0m, 85m);
            });
            test("invalid markers and markers on other record kinds reject atomically", delegate
            {
                Store s = Fresh(folder, "invalid-markers"); Record party = Party(s);
                foreach (string marker in new[] { "grey", "EXTERNAL", "false", "tax-free" })
                {
                    Record p = NewPayment(party, 10m); p.Set("paymentScope", marker);
                    RejectUnchanged(s, delegate { s.Upsert(p); }, "invalid marker");
                }
                foreach (string marker in new[] { "standard", "external" })
                {
                    Record changed = s.Find(party.Id); changed.Set("paymentScope", marker);
                    RejectUnchanged(s, delegate { s.Upsert(changed); }, "marker on party");
                    Record act = NewAct(party, "", 30m); act.Set("paymentScope", marker);
                    RejectUnchanged(s, delegate { s.Upsert(act); }, "marker on act"); Eq(act.Get("number"), "", "failed act consumes no number");
                }
            });
            test("explicit grey payment requires a direct external act", delegate
            {
                Store s = Fresh(folder, "grey-link-required"); Record party = Party(s), ordinary = Act(s, party, "standard", 100m);
                Record missing = NewPayment(party, 10m, "external"), wrong = NewPayment(party, 10m, "external", ordinary);
                RejectUnchanged(s, delegate { s.Upsert(missing); }, "no direct act"); RejectUnchanged(s, delegate { s.Upsert(wrong); }, "ordinary direct act");
                Record broken = NewPayment(party, 10m, "external"); broken.Set("actId", "missing-synthetic-act");
                RejectUnchanged(s, delegate { s.Upsert(broken); }, "missing referenced act");
            });
            test("explicit ordinary payment rejects a direct external act but permits an unattached advance", delegate
            {
                Store s = Fresh(folder, "white-link-restriction"); Record party = Party(s), grey = Act(s, party, "external", 100m);
                Record bad = NewPayment(party, 20m, "standard", grey); RejectUnchanged(s, delegate { s.Upsert(bad); }, "ordinary direct external link");
                Record valid = Payment(s, party, 20m, "standard"); Split(ReadOnlyBuild(s), valid, 20m, 0m, 0m, 0m, 20m);
            });
            test("active allocations match explicit markers and failures preserve ledger and history", delegate
            {
                Store s = Fresh(folder, "explicit-allocation-integrity"); Record party = Party(s);
                Record white = Act(s, party, "standard", 200m), grey = Act(s, party, "external", 200m);
                Record wp = Payment(s, party, 100m, "standard"), gp = Payment(s, party, 100m, "external", grey);
                RejectUnchanged(s, delegate { s.Allocate(wp.Id, grey.Id, 10m); }, "white allocation to grey");
                RejectUnchanged(s, delegate { s.Allocate(gp.Id, white.Id, 10m); }, "grey allocation to white");
                s.Allocate(wp.Id, white.Id, 20m); s.Allocate(gp.Id, grey.Id, 30m);
                PaymentAccounting a = ReadOnlyBuild(s); Split(a, wp, 100m, 0m, 20m, 0m, 80m); Split(a, gp, 0m, 100m, 0m, 30m, 70m);
            });
            test("editing mixed legacy payment into an incompatible marker rolls back every field", delegate
            {
                Store s = Fresh(folder, "mixed-marker-edit"); Record party = Party(s);
                Record white = Act(s, party, "standard", 200m), grey = Act(s, party, "external", 200m), p = Payment(s, party, 100m);
                s.Allocate(p.Id, white.Id, 35m); s.Allocate(p.Id, grey.Id, 25m);
                Record toWhite = s.Find(p.Id); toWhite.Set("paymentScope", "standard"); toWhite.Set("notes", "must roll back");
                RejectUnchanged(s, delegate { s.Upsert(toWhite); }, "mixed to white");
                Record toGrey = s.Find(p.Id); toGrey.Set("paymentScope", "external"); toGrey.Set("actId", grey.Id); toGrey.Set("notes", "must roll back");
                RejectUnchanged(s, delegate { s.Upsert(toGrey); }, "mixed to grey");
                Split(ReadOnlyBuild(s), p, 75m, 25m, 35m, 25m, 40m); Eq(s.Find(p.Id).Get("paymentScope"), "", "legacy marker preserved");
            });
            test("act scope edits validate explicit direct links and allocations throughout the ledger", delegate
            {
                Store s = Fresh(folder, "act-scope-integrity"); Record party = Party(s);
                Record directGrey = Act(s, party, "external", 200m), allocatedGrey = Act(s, party, "external", 200m), allocatedWhite = Act(s, party, "standard", 200m);
                Record gp = Payment(s, party, 100m, "external", directGrey), wp = Payment(s, party, 100m, "standard");
                s.Allocate(gp.Id, allocatedGrey.Id, 30m); s.Allocate(wp.Id, allocatedWhite.Id, 20m);
                Record directEdit = s.Find(directGrey.Id); directEdit.Set("accountingScope", "standard");
                RejectUnchanged(s, delegate { s.Upsert(directEdit); }, "grey direct act moved to white");
                Record greyEdit = s.Find(allocatedGrey.Id); greyEdit.Set("accountingScope", "standard");
                RejectUnchanged(s, delegate { s.Upsert(greyEdit); }, "grey allocated act moved to white");
                Record whiteEdit = s.Find(allocatedWhite.Id); whiteEdit.Set("accountingScope", "external");
                RejectUnchanged(s, delegate { s.Upsert(whiteEdit); }, "white allocated act moved to grey");
                PaymentAccounting a = ReadOnlyBuild(s); Split(a, gp, 0m, 100m, 0m, 30m, 70m); Split(a, wp, 100m, 0m, 20m, 0m, 80m);
            });
            test("an ordinary direct explicit payment prevents silent act scope reclassification", delegate
            {
                Store s = Fresh(folder, "white-direct-scope-edit"); Record party = Party(s), white = Act(s, party, "standard", 200m);
                Record p = Payment(s, party, 70m, "standard", white), edit = s.Find(white.Id); edit.Set("accountingScope", "external");
                RejectUnchanged(s, delegate { s.Upsert(edit); }, "ordinary direct act moved to external");
                Split(ReadOnlyBuild(s), p, 70m, 0m, 0m, 0m, 70m);
            });
            test("legacy act reclassification recomputes parts without changing payment identity or allocations", delegate
            {
                Store s = Fresh(folder, "legacy-scope-edit"); Record party = Party(s);
                Record white = Act(s, party, "standard", 200m), grey = Act(s, party, "external", 200m), p = Payment(s, party, 100m), direct = Payment(s, party, 50m, "", white);
                s.Allocate(p.Id, white.Id, 30m); s.Allocate(p.Id, grey.Id, 40m);
                Split(ReadOnlyBuild(s), p, 60m, 40m, 30m, 40m, 30m); Split(ReadOnlyBuild(s), direct, 50m, 0m, 0m, 0m, 50m);
                long revision = s.Revision; int audits = s.Audit.Count; string number = white.Get("number");
                Record changed = s.Find(white.Id); changed.Set("accountingScope", "external"); s.Upsert(changed);
                Split(ReadOnlyBuild(s), p, 30m, 70m, 0m, 70m, 30m); Split(ReadOnlyBuild(s), direct, 0m, 50m, 0m, 0m, 50m);
                Eq(s.Revision, revision + 1, "one scope edit transaction"); Eq(s.Audit.Count, audits + 1, "one scope edit audit");
                Eq(s.Find(white.Id).Get("number"), number, "number preserved"); Eq(s.AllocatedFromPayment(p.Id), 70m, "booked total preserved");
                Eq(s.Find(p.Id).Get("paymentScope"), "", "legacy marker stays absent"); Eq(s.ActDue(white.Id), 170m, "debt preserved");
                changed = s.Find(white.Id); changed.Set("accountingScope", "standard"); s.Upsert(changed);
                Split(ReadOnlyBuild(s), p, 60m, 40m, 30m, 40m, 30m); Split(ReadOnlyBuild(s), direct, 50m, 0m, 0m, 0m, 50m);
            });
            test("invalid batch rolls back good rows and automatic numbering alongside the invalid payment", delegate
            {
                Store s = Fresh(folder, "batch-rollback"); Record party = Party(s), white = Act(s, party, "standard", 100m);
                Record pending = NewAct(party, "external", 100m), valid = NewPayment(party, 10m, "standard"), invalid = NewPayment(party, 20m, "external", white);
                RejectUnchanged(s, delegate { s.ImportBatch(new[] { pending, valid, invalid }); }, "invalid grey payment batch");
                True(s.Find(pending.Id) == null && s.Find(valid.Id) == null && s.Find(invalid.Id) == null, "all rows roll back");
                Eq(pending.Get("number"), "", "failed batch does not number caller's pending act");
            });
            test("valid batch resolves future direct and allocation references in one commit", delegate
            {
                Store s = Fresh(folder, "batch-valid"); Record party = Party(s), grey = NewAct(party, "external", 200m);
                Record p = NewPayment(party, 100m, "external", grey), allocation = R("allocation", "paymentId", p.Id, "actId", grey.Id, "date", "2026-10-13", "amount", "60.00");
                long revision = s.Revision; s.ImportBatch(new[] { allocation, p, grey }); Eq(s.Revision, revision + 1, "batch commits once");
                Split(ReadOnlyBuild(s), p, 0m, 100m, 0m, 60m, 40m); Eq(s.AllocatedToAct(grey.Id), 60m, "future reference counted once");
                Eq(s.ActDue(grey.Id), 140m, "future reference debt"); True(s.Find(grey.Id).Get("number") != "", "new act numbered");
            });
            test("reversed opposite-scope allocations are inactive for both integrity and financial parts", delegate
            {
                Store s = Fresh(folder, "reversed-incompatible"); Record party = Party(s);
                Record white = Act(s, party, "standard", 100m), grey = Act(s, party, "external", 100m);
                Record wp = Payment(s, party, 70m, "standard"), gp = Payment(s, party, 80m, "external", grey);
                Record oldGrey = R("allocation", "paymentId", wp.Id, "actId", grey.Id, "date", "2026-10-13", "amount", "30.00", "reversed", "true", "reversedDate", "2026-10-13");
                Record oldWhite = R("allocation", "paymentId", gp.Id, "actId", white.Id, "date", "2026-10-13", "amount", "40.00", "reversed", "true", "reversedDate", "2026-10-13");
                s.ImportBatch(new[] { oldGrey, oldWhite }); PaymentAccounting a = ReadOnlyBuild(s);
                Split(a, wp, 70m, 0m, 0m, 0m, 70m); Split(a, gp, 0m, 80m, 0m, 0m, 80m);
                Eq(s.AllocatedFromPayment(wp.Id), 0m, "reversed grey inactive"); Eq(s.AllocatedFromPayment(gp.Id), 0m, "reversed white inactive");
            });
            test("archived legacy references survive reload and existing edits without read migration", delegate
            {
                Store s = Fresh(folder, "archives-legacy"); Record party = Party(s);
                Record white = Act(s, party, "standard", 200m), grey = Act(s, party, "external", 200m), mixed = Payment(s, party, 120m), direct = Payment(s, party, 50m, "", grey);
                s.Allocate(mixed.Id, white.Id, 40m); s.Allocate(mixed.Id, grey.Id, 60m);
                // Archive() blocks referenced records; seed persisted historical archive flags instead.
                foreach (Record reference in new[] { white, grey, mixed, direct, party })
                {
                    Record historical = s.Find(reference.Id); historical.Set("archived", "true"); s.Upsert(historical);
                }
                SavedState state = Capture(s); Store loaded = new Store(s.DataDir); state.Unchanged(loaded, "legacy archived reload");
                PaymentAccounting a = ReadOnlyBuild(loaded); Split(a, mixed, 60m, 60m, 40m, 60m, 20m); Split(a, direct, 0m, 50m, 0m, 0m, 50m);
                Eq(loaded.Of("payment").Count(), 0, "archived payments absent from active catalog");
                True(!loaded.Find(mixed.Id).Fields.ContainsKey("paymentScope") && !loaded.Find(direct.Id).Fields.ContainsKey("paymentScope"), "read adds no legacy markers");
                Record edited = loaded.Find(direct.Id); edited.Set("notes", "Synthetic archive note"); loaded.Upsert(edited);
                Split(ReadOnlyBuild(loaded), edited, 0m, 50m, 0m, 0m, 50m); Eq(loaded.Find(edited.Id).Get("actId"), grey.Id, "old archived link preserved");
                Eq(loaded.Find(edited.Id).Get("archived"), "true", "edit keeps payment archived");
            });
            test("cent precision and currency integrity survive classification and rejected edits", delegate
            {
                foreach (string currency in new[] { "BYN", "RUB", "USD", "EUR" })
                {
                    Store s = Fresh(folder, "cents-" + currency); Record party = Party(s);
                    Record white = Act(s, party, "standard", 200m, currency), grey = Act(s, party, "external", 200m, currency), p = Payment(s, party, 100.05m, "", grey, currency);
                    s.Allocate(p.Id, white.Id, 20.01m); s.Allocate(p.Id, grey.Id, 10.02m);
                    Split(ReadOnlyBuild(s), p, 20.01m, 80.04m, 20.01m, 10.02m, 70.02m);
                    RejectUnchanged(s, delegate { s.Allocate(p.Id, grey.Id, 70.03m); }, "one-cent overallocation");
                    Record changed = s.Find(p.Id); changed.Set("currency", currency == "BYN" ? "USD" : "BYN");
                    RejectUnchanged(s, delegate { s.Upsert(changed); }, "incompatible currency edit"); Eq(s.Find(p.Id).Get("currency"), currency, "currency preserved");
                }
            });
            test("outgoing payment parts remain independent of incoming receipt filtering", delegate
            {
                Store s = Fresh(folder, "outgoing"); Record party = Party(s), grey = Act(s, party, "external", 120m, "BYN", "expense");
                Record p = Payment(s, party, 100m, "external", grey, "BYN", "expense"); s.Allocate(p.Id, grey.Id, 60m);
                Split(ReadOnlyBuild(s), p, 0m, 100m, 0m, 60m, 40m);
                Record incompatible = NewPayment(party, 20m, "external", grey, "BYN", "income"); RejectUnchanged(s, delegate { s.Upsert(incompatible); }, "direction mismatch");
            });
            test("snapshot clones records in one enumeration and For uses only caller identity", delegate
            {
                Store s = Fresh(folder, "immutable-snapshot"); Record party = Party(s);
                Record white = Act(s, party, "standard", 200m), grey = Act(s, party, "external", 200m), p = Payment(s, party, 120m, "", grey);
                s.Allocate(p.Id, white.Id, 40m); s.Allocate(p.Id, grey.Id, 60m);
                List<Record> source = s.Records; SinglePass sequence = new SinglePass(source); SavedState state = Capture(s);
                PaymentAccounting a = PaymentAccounting.Build(sequence); Eq(sequence.Enumerations, 1, "one enumeration");
                source.Single(r => r.Id == p.Id).Set("amount", "999.00"); source.Single(r => r.Id == p.Id).Set("paymentScope", "standard");
                source.Single(r => r.Id == grey.Id).Set("accountingScope", "standard"); source.First(r => r.Kind == "allocation").Set("amount", "1.00"); source.Clear();
                Record caller = p.Clone(); caller.Set("amount", "999.00"); caller.Set("actId", white.Id); caller.Set("paymentScope", "standard"); caller.Set("currency", "USD");
                Split(a, caller, 40m, 80m, 40m, 60m, 20m); state.Unchanged(s, "caller mutations preserve store");
                PaymentSplit saved = a.ForId(p.Id); True(typeof(PaymentSplit).IsSealed && typeof(PaymentSplit).GetProperties().All(prop => prop.GetSetMethod() == null), "split has no public setters");
                Record changed = s.Find(p.Id); changed.Set("amount", "130.00"); s.Upsert(changed);
                Split(a, caller, 40m, 80m, 40m, 60m, 20m); Eq(saved.Total, 120m, "previous result survives store edit");
                Split(ReadOnlyBuild(s), changed, 40m, 90m, 40m, 60m, 30m);
            });
            test("duplicate IDs reject even if duplicate acts disagree on accounting scope", delegate
            {
                Record p = R("payment", "amount", "10.00"), duplicate = p.Clone(); duplicate.Set("amount", "20.00");
                Reject(delegate { PaymentAccounting.Build(new[] { p, duplicate }); });
                Record ordinary = R("act", "accountingScope", "standard"), external = ordinary.Clone(); external.Set("accountingScope", "external");
                Reject(delegate { PaymentAccounting.Build(new[] { ordinary, external }); });
            });
            test("same document number never merges distinct payment identities", delegate
            {
                Store s = Fresh(folder, "duplicate-numbers"); Record party = Party(s), grey = Act(s, party, "external", 100m);
                Record wp = NewPayment(party, 35m), gp = NewPayment(party, 65m, "external", grey); wp.Set("number", "DUP"); gp.Set("number", "DUP"); s.Upsert(wp); s.Upsert(gp);
                PaymentAccounting a = ReadOnlyBuild(s); Split(a, wp, 35m, 0m, 0m, 0m, 35m); Split(a, gp, 0m, 65m, 0m, 0m, 65m);
                Eq(a.ForId(wp.Id).Total + a.ForId(gp.Id).Total, 100m, "full received total conserved");
            });
            test("public snapshot rejects dangling references and invalid financial cents", delegate
            {
                Record grey = R("act", "accountingScope", "external"), p = R("payment", "amount", "100.00", "actId", grey.Id);
                Record allocation = R("allocation", "paymentId", p.Id, "actId", grey.Id, "amount", "60.00");
                foreach (string amount in new[] { "0.00", "-1.00", "0.001" })
                {
                    Record invalid = p.Clone(); invalid.Set("amount", amount); Reject(delegate { PaymentAccounting.Build(new[] { grey, invalid }); });
                    Record invalidAllocation = allocation.Clone(); invalidAllocation.Set("amount", amount); Reject(delegate { PaymentAccounting.Build(new[] { grey, p, invalidAllocation }); });
                }
                Record excessive = allocation.Clone(); excessive.Set("amount", "100.01"); Reject(delegate { PaymentAccounting.Build(new[] { grey, p, excessive }); });
                Record dangling = allocation.Clone(); dangling.Set("actId", "missing-synthetic-act"); Reject(delegate { PaymentAccounting.Build(new[] { grey, p, dangling }); });
                Record absentPayment = allocation.Clone(); absentPayment.Set("paymentId", "missing-synthetic-payment"); Reject(delegate { PaymentAccounting.Build(new[] { grey, p, absentPayment }); });
                Record wrongKind = grey.Clone(); wrongKind.Kind = "party"; Reject(delegate { PaymentAccounting.Build(new[] { wrongKind, p }); });
                Record invalidScope = p.Clone(); invalidScope.Set("paymentScope", "EXTERNAL"); Reject(delegate { PaymentAccounting.Build(new[] { grey, invalidScope }); });
                Record badAct = grey.Clone(); badAct.Set("accountingScope", "typo"); Reject(delegate { PaymentAccounting.Build(new[] { badAct, p }); });
                Split(PaymentAccounting.Build(new[] { grey, p, allocation }), p, 0m, 100m, 0m, 60m, 40m);
            });
            test("empty calculations create no ledger and invalid lookup identities reject", delegate
            {
                Store s = Fresh(folder, "empty"); True(!File.Exists(s.DataPath), "no initial ledger"); PaymentAccounting a = ReadOnlyBuild(s);
                True(!File.Exists(s.DataPath), "snapshot does not create ledger"); Reject(delegate { a.ForId("missing-synthetic-payment"); }); Reject(delegate { a.ForId(""); });
                Reject(delegate { a.For(null); }); Reject(delegate { a.For(R("act")); }); Reject(delegate { PaymentAccounting.Build(null); });
            });
            File.WriteAllText(Path.Combine(folder, "PASS.txt"), "PASS: " + passed.ToString(CultureInfo.InvariantCulture) + " groups; mixed and direct advances, explicit integrity, atomic storage and imports, reversals, archived references, no migration, cents/currencies, single-pass immutable snapshots.", new UTF8Encoding(false));
            Console.WriteLine("Payment accounting: " + passed.ToString(CultureInfo.InvariantCulture) + " groups passed. Synthetic artifacts: " + folder);
        }

        private static PaymentAccounting ReadOnlyBuild(Store store)
        {
            SavedState state = Capture(store); PaymentAccounting accounting = PaymentAccounting.Build(store.Records);
            state.Unchanged(store, "snapshot calculation is read-only"); return accounting;
        }
        private static void Split(PaymentAccounting accounting, Record payment, decimal white, decimal grey, decimal allocatedWhite, decimal allocatedGrey, decimal unallocated)
        {
            PaymentSplit split = accounting.For(payment), byId = accounting.ForId(payment.Id);
            Eq(split.PaymentId, payment.Id, "identity"); Eq(split.Total, white + grey, "actual payment amount");
            Eq(split.WhiteAmount, white, "white part"); Eq(split.GreyAmount, grey, "grey part");
            Eq(split.AllocatedWhite, allocatedWhite, "allocated white"); Eq(split.AllocatedGrey, allocatedGrey, "allocated grey"); Eq(split.Unallocated, unallocated, "unallocated advance");
            Eq(split.IsMixed, white > 0m && grey > 0m, "mixed classification");
            Eq(split.WhiteAmount + split.GreyAmount, split.Total, "white plus grey conserves total");
            Eq(split.AllocatedWhite + split.AllocatedGrey + split.Unallocated, split.Total, "allocation plus advance conserves total");
            Eq(byId.Total, split.Total, "ID lookup amount"); Eq(byId.WhiteAmount, split.WhiteAmount, "ID lookup white"); Eq(byId.GreyAmount, split.GreyAmount, "ID lookup grey");
        }
        private static void RejectUnchanged(Store store, Action action, string reason)
        {
            SavedState state = Capture(store); Reject(action); state.Unchanged(store, reason);
        }
        private sealed class SavedState
        {
            internal bool Existed; internal byte[] Bytes; internal long Revision, Counter; internal string Records, Audit;
            internal void Unchanged(Store store, string reason)
            {
                Eq(store.Revision, Revision, reason + " revision"); Eq(store.NextActNumber, Counter, reason + " next act number");
                Eq(File.Exists(store.DataPath), Existed, reason + " ledger existence");
                if (Existed) True(Bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), reason + " ledger bytes");
                Eq(Json(store.Records), Records, reason + " records"); Eq(Json(store.Audit), Audit, reason + " audit history");
            }
        }
        private static SavedState Capture(Store store)
        {
            bool exists = File.Exists(store.DataPath);
            return new SavedState { Existed = exists, Bytes = exists ? File.ReadAllBytes(store.DataPath) : null, Revision = store.Revision, Counter = store.NextActNumber, Records = Json(store.Records), Audit = Json(store.Audit) };
        }
        private static string Json(object value) { return new JavaScriptSerializer { MaxJsonLength = 32000000, RecursionLimit = 100 }.Serialize(value); }
        private sealed class SinglePass : IEnumerable<Record>
        {
            private readonly IEnumerable<Record> source; internal int Enumerations;
            internal SinglePass(IEnumerable<Record> source) { this.source = source; }
            public IEnumerator<Record> GetEnumerator()
            {
                Enumerations++; if (Enumerations != 1) throw new InvalidOperationException("Synthetic snapshot must be enumerated exactly once."); return source.GetEnumerator();
            }
            IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        }
        private static Record Allocation(Store store, Record payment, Record act, decimal amount)
        {
            return store.Records.Single(r => r.Kind == "allocation" && r.Get("paymentId") == payment.Id && r.Get("actId") == act.Id && r.Get("reversed") != "true" && Store.ParseMoney(r.Get("amount")) == amount);
        }
        private static Store Fresh(string root, string name) { return new Store(Path.Combine(root, name)); }
        private static Record Party(Store store) { Record party = R("party", "name", "Synthetic payer", "role", "both"); store.Upsert(party); return party; }
        private static Record NewAct(Record party, string scope, decimal amount, string currency = "BYN", string direction = "income")
        {
            return R("act", "partyId", party.Id, "accountingScope", scope, "date", "2026-10-13", "amount", Store.MoneyString(amount), "currency", currency, "direction", direction, "status", "signed");
        }
        private static Record Act(Store store, Record party, string scope, decimal amount, string currency = "BYN", string direction = "income")
        {
            Record act = NewAct(party, scope, amount, currency, direction); store.Upsert(act); return act;
        }
        private static Record NewPayment(Record party, decimal amount, string scope = "", Record act = null, string currency = "BYN", string direction = "income")
        {
            Record payment = R("payment", "partyId", party.Id, "date", "2026-10-13", "amount", Store.MoneyString(amount), "currency", currency, "direction", direction, "method", "bank");
            if (scope != "") payment.Set("paymentScope", scope); if (act != null) payment.Set("actId", act.Id); return payment;
        }
        private static Record Payment(Store store, Record party, decimal amount, string scope = "", Record act = null, string currency = "BYN", string direction = "income")
        {
            Record payment = NewPayment(party, amount, scope, act, currency, direction); store.Upsert(payment); return payment;
        }
        private static Record R(string kind, params string[] fields)
        {
            Record record = new Record(kind); for (int i = 0; i < fields.Length; i += 2) record.Set(fields[i], fields[i + 1]); return record;
        }
        private static void True(bool value, string reason) { if (!value) throw new Exception("Payment accounting assertion failed: " + reason); }
        private static void Eq<T>(T actual, T expected, string reason)
        {
            if (!Object.Equals(actual, expected)) throw new Exception("Payment accounting " + reason + ": expected " + expected + ", got " + actual);
        }
        private static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; }
            throw new Exception("Payment accounting expected a rejected operation.");
        }
    }
}
