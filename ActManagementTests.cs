using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class ActManagementTests
    {
        private static void Check(bool ok, string reason) { if (!ok) throw new Exception("Act management: " + reason); }
        private static Record Add(Store store, string kind, params string[] fields)
        {
            var r = new Record(kind); for (int i=0;i<fields.Length;i+=2) r.Set(fields[i], fields[i+1]);
            store.Upsert(r); return store.Find(r.Id);
        }
        private static Record Act(Store store, Record party)
        {
            Record act = store.CreateHourlyActDraft(party.Id);
            Store.SetHourlyActLines(act, new[]{new HourlyActLine { Description="Тестовая работа", Hours=2, Rate=50 }});
            store.Upsert(act); return store.Find(act.Id);
        }
        private static void RejectUnchanged(Store store, Action action)
        {
            byte[] before=File.ReadAllBytes(store.DataPath); long revision=store.Revision; bool rejected=false;
            try { action(); } catch (InvalidOperationException) { rejected=true; }
            Check(rejected && store.Revision==revision && before.SequenceEqual(File.ReadAllBytes(store.DataPath)), "rejected operation must not change ledger");
        }
        public static void Run(string root)
        {
            string dir=Path.Combine(root,Guid.NewGuid().ToString("N")); var store=new Store(Path.Combine(dir,"data"));
            Record own=Add(store,"settings","name","Прежний ИП","currency","BYN","bankAccount","OLD-OWN");
            Record party=Add(store,"party","name","Прежний заказчик","role","client","bankAccount","OLD-PARTY");
            Record first=Act(store,party), second=Act(store,party);
            own.Set("name","Новый ИП");own.Set("bankAccount","NEW-OWN");store.Upsert(own);
            party.Set("name","Новый заказчик");party.Set("bankAccount","NEW-PARTY");store.Upsert(party);
            Record updated=store.RefreshActRequisites(first.Id,store.Revision);
            Check(Store.GetActSnapshot(updated,"organizationSnapshot").Get("bankAccount")=="NEW-OWN" && Store.GetActSnapshot(updated,"partySnapshot").Get("bankAccount")=="NEW-PARTY","explicit refresh updates both snapshots");
            Check(first.Fields.Where(f=>f.Key!="organizationSnapshot"&&f.Key!="partySnapshot").All(f=>updated.Get(f.Key)==f.Value),"refresh changes no other act field");
            Check(Store.GetActSnapshot(store.Find(second.Id),"organizationSnapshot").Get("name")=="Прежний ИП","refresh leaves other acts historical");
            Check(store.Audit.Last().Action=="refresh-requisites" && store.Audit.Last().Before.Contains("OLD-OWN"),"refresh is audited");
            long revision=store.Revision; store.RefreshActRequisites(first.Id,revision); Check(store.Revision==revision,"identical refresh is a no-op");
            own.Set("name","");store.Upsert(own);RejectUnchanged(store,()=>store.RefreshActRequisites(first.Id,store.Revision));
            own.Set("name","Новый ИП");store.Upsert(own);
            RejectUnchanged(store,()=>store.RefreshActRequisites(first.Id,store.Revision-1));
            foreach (string kind in new[]{"invoice","payment","document"})
            {
                var isolated=new Store(Path.Combine(dir,"linked-"+kind));Record client=Add(isolated,"party","name","Заказчик","role","client");Record linked=Act(isolated,client);
                var record=new Record(kind);record.Set("actId",linked.Id);record.Set("partyId",client.Id);record.Set("date","2026-11-01");record.Set("direction","income");record.Set("currency","BYN");record.Set("amount","100");
                record.Set("number","LINK-1");record.Set("title","Вложение");record.Set("method","bank");record.Set("status","draft");isolated.Upsert(record);
                RejectUnchanged(isolated,()=>isolated.DeleteAct(linked.Id,isolated.Revision));
            }
            Record signed=store.Find(second.Id);signed.Set("status","signed");store.Upsert(signed);RejectUnchanged(store,()=>store.DeleteAct(signed.Id,store.Revision));
            long next=store.NextActNumber;
            string backup=ActActions.DeleteWithBackup(store,first.Id,store.Revision);
            Check(File.Exists(backup)&&store.Find(first.Id)==null&&store.NextActNumber==next,"delete creates backup and preserves next number");
            Check(store.Audit.Last().Action=="delete"&&store.Audit.Last().After==""&&store.Audit.Last().Before.Contains("100.00"),"deletion keeps original fields in audit");
            string restored=Path.Combine(dir,"restored");BackupArchive.Restore(backup,restored,MainForm.ValidateSnapshot);
            Check(new Store(restored).Find(first.Id)!=null,"deletion backup restores act");
            Record manual=Add(store,"act","number","900","date","2026-11-01","partyId",party.Id,"direction","income","currency","BYN","amount","1","status","cancelled");
            store.DeleteAct(manual.Id,store.Revision);Check(store.NextActNumber==901,"deleting a manual high number does not reuse it");
            Record generated=Add(store,"act","number","AUTO-1","date","2026-11-01","partyId",party.Id,"direction","income","currency","BYN","amount","1","status","draft","serviceGenerated","true","recurringPartyId",party.Id,"monthlyServiceMonth","2026-11");
            store.RefreshActRequisites(generated.Id,store.Revision);RejectUnchanged(store,()=>store.DeleteAct(generated.Id,store.Revision));
            Record external=store.CreateHourlyActDraft(party.Id,true);Store.SetHourlyActLines(external,new[]{new HourlyActLine {Description="Вне",Hours=1,Rate=10}});store.Upsert(external);
            var stale=new Store(store.DataDir);own.Set("phone","TEST");store.Upsert(own);
            RejectUnchanged(stale,()=>stale.DeleteAct(external.Id,stale.Revision));
            RejectUnchanged(stale,()=>stale.RefreshActRequisites(external.Id,stale.Revision));
            store.DeleteAct(external.Id,store.Revision);Check(new Store(store.DataDir).Find(external.Id)==null,"external act deletion persists");
            using(var editor=new RecordEditor(store,Catalog.Get("act"),store.Find(second.Id)))
            {
                Check(editor.Controls.Find("refreshActRequisites",true).Length==1 && editor.Controls.Find("deleteAct",true).Length==1,"saved act card exposes both actions");
            }
            File.WriteAllText(Path.Combine(dir,"PASS.txt"),"PASS: explicit requisites, history, isolation, deletion restrictions, backup restore, numbering, concurrency and card actions.");
            Console.WriteLine("PASS: act management, backups, historical requisites, numbering and concurrency.");
        }
    }
}
