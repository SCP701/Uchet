using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
namespace WorkLedger
{
    public sealed class ServiceBalance
    {
        public Record Act,Party,Organization;
        public decimal Amount,Paid,Held,Due;
        public bool NeedsReview;
        public string Status { get {return NeedsReview?"Проверить привязку оплаты":Due<=0?(Paid>=Amount?"Оплачен":"Остаток удержан"):Paid>0?"Частично оплачен":"Не оплачен";} }
    }
    public static class ServiceReminderLedger
    {
        internal static DateTime Date(string value){return DateTime.ParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None);}
        public static List<ServiceBalance> Build(IEnumerable<Record> source,DateTime today)
        { return BuildBalances(source,today,false); }
        internal static ServiceBalance BalanceForAct(IEnumerable<Record> source,string actId,DateTime today)
        {
            var result=BuildBalances(source,today,true).FirstOrDefault(b=>b.Act.Id==actId);
            if(result==null)throw new InvalidOperationException("Выберите действующий акт с действующим контрагентом.");
            return result;
        }
        internal static List<ServiceBalance> BuildBalances(IEnumerable<Record> source,DateTime today,bool allActs)
        {
            var snapshot=source.Select(r=>r.Clone()).ToList();var accounting=PaymentAccounting.Build(snapshot);
            var result=new List<ServiceBalance>();
            var acts=snapshot.Where(r=>r.Kind=="act"&&(allActs||Store.IsTaxAct(r)&&r.Get("direction")=="income")&&r.Get("status")!="cancelled"&&r.Get("archived")!="true").ToList();
            var allocations=snapshot.Where(r=>r.Kind=="allocation"&&r.Get("reversed")!="true").ToList();
            var receipts=snapshot.Where(r=>r.Kind=="payment"&&(allActs||r.Get("direction")=="income")&&ServiceReminderLedger.Date(r.Get("date"))<=today.Date).ToList();
            Record organization=snapshot.FirstOrDefault(r=>r.Kind=="settings"&&r.Get("archived")!="true");
            var matchesByPayment=new Dictionary<string,string>();
            foreach(var payment in receipts.Where(p=>p.Get("actId")==""&&p.Get("method")=="bank"&&accounting.For(p).Unallocated>0)) {
                var matches=acts.Where(a=>a.Get("partyId")==payment.Get("partyId")&&a.Get("currency")==payment.Get("currency")&&a.Get("direction")==payment.Get("direction")&&(Store.IsExternalAct(a)?accounting.For(payment).GreyAmount:accounting.For(payment).WhiteAmount)>0&&PurposeMatches(String.IsNullOrWhiteSpace(payment.Get("bankPurpose"))?payment.Get("notes"):payment.Get("bankPurpose"),a.Get("number"))).Take(2).ToList();
                matchesByPayment[payment.Id]=matches.Count==1?matches[0].Id:"";
            }
            foreach(Record act in acts.Where(r=>allActs||r.Get("serviceGenerated")=="true")) {
                Record party=snapshot.FirstOrDefault(r=>r.Id==act.Get("partyId")&&r.Kind=="party"&&r.Get("archived")!="true");
                if(party==null)continue;
                var balance=new ServiceBalance{Act=act.Clone(),Party=party.Clone(),Organization=organization==null?null:organization.Clone(),Amount=Store.ParseMoney(act.Get("amount"))};
                balance.Paid=allocations.Where(a=>a.Get("actId")==act.Id&&receipts.Any(p=>p.Id==a.Get("paymentId"))).Sum(r=>Store.ParseMoney(r.Get("amount")));
                balance.Held=snapshot.Where(r=>r.Kind=="hold"&&r.Get("actId")==act.Id&&r.Get("status")=="active").Sum(r=>Store.ParseMoney(r.Get("amount")));
                foreach(Record payment in receipts.Where(p=>p.Get("partyId")==party.Id&&p.Get("currency")==act.Get("currency")&&p.Get("direction")==act.Get("direction"))) {
                    var split=accounting.For(payment);
                    if(split.Unallocated<=0||(Store.IsExternalAct(act)?split.GreyAmount:split.WhiteAmount)<=0)continue;
                    bool direct=payment.Get("actId")==act.Id;
                    if(payment.Get("actId")!=""&&!direct)continue;
                    if(direct) {balance.Paid+=split.Unallocated;continue;}
                    if(payment.Get("method")!="bank")continue;
                    // A bank purpose matches only an explicit act number and exactly one compatible act.
                    string matched;
                    if(matchesByPayment.TryGetValue(payment.Id,out matched)&&matched!="") {if(matched==act.Id)balance.Paid+=split.Unallocated;continue;}
                    if(ServiceReminderLedger.Date(payment.Get("date"))>=ServiceReminderLedger.Date(act.Get("date")))
                        balance.NeedsReview=true;
                }
                balance.Paid=Math.Min(balance.Amount,balance.Paid);
                balance.Due=Math.Max(0,balance.Amount-balance.Paid-balance.Held);
                if(balance.Due==0)balance.NeedsReview=false;
                result.Add(balance);
            }
            return result.OrderBy(b=>b.Act.Get("date")).ThenBy(b=>b.Act.Get("number")).ToList();
        }
        internal static bool PurposeMatches(string purpose,string number)
        {
            if(String.IsNullOrWhiteSpace(purpose)||String.IsNullOrWhiteSpace(number))return false;
            return Regex.IsMatch(purpose,@"(?i)\bакт(?:а|у|ом|ы)?\s*(?:№|N(?:o)?\.?|номер)?\s*"+Regex.Escape(number)+@"(?![\p{L}\p{N}_/-]|\.[\p{L}\p{N}])",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        }
        public static string ReplaceFields(string template,ServiceBalance balance)
        {
            var culture=CultureInfo.GetCultureInfo("ru-RU");Record act=balance.Act;
            DateTime date=ServiceReminderLedger.Date(act.Get("date"));
            Record own=balance.Organization;
            var fields=new Dictionary<string,string> {
                {"номер",act.Get("number")},{"дата",date.ToString("dd.MM.yyyy")},{"контрагент",balance.Party.Get("name")},
                {"период",date.ToString("MMMM yyyy",culture)},{"сумма",balance.Amount.ToString("N2",culture)},
                {"оплачено",balance.Paid.ToString("N2",culture)},{"остаток",balance.Due.ToString("N2",culture)},
                {"валюта",act.Get("currency")},{"организация",own==null?"":own.Get("name")},
                {"реквизиты",own==null?"":String.Join("\r\n",new[]{"УНП: "+own.Get("unp"),"Счёт: "+own.Get("bankAccount"),own.Get("bankName")}.Where(s=>s!="УНП: "&&s!="Счёт: "&&s!=""))}
            };
            // Replace once so counterparty text cannot become another template field.
            return Regex.Replace(template,@"\{([^{}]+)\}",m=>fields.ContainsKey(m.Groups[1].Value)?fields[m.Groups[1].Value]:m.Value);
        }
    }
}
