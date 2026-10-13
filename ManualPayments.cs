using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
namespace WorkLedger
{
    public sealed class ManualPaymentView
    {
        public ServiceBalance Balance { get; internal set; }
        public List<Record> Payments { get; internal set; }
        public Dictionary<string,decimal> Available { get; internal set; }
        public long Revision { get; internal set; }
    }
    public sealed partial class Store
    {
        private static bool CanConfirmExistingPayment(Record payment,Record act,DateTime today)
        {
            if(payment.Kind!="payment"||payment.Get("archived")=="true"||payment.Get("actId")!=""
                ||payment.Get("partyId")!=act.Get("partyId")||payment.Get("currency")!=act.Get("currency")
                ||payment.Get("direction")!=act.Get("direction")||ServiceReminderLedger.Date(payment.Get("date"))>today.Date)return false;
            string scope=payment.Get("paymentScope");
            if(scope!=""&&(scope=="external")!=IsExternalAct(act))return false;
            return new[]{"projectId","contractId"}.All(k=>payment.Get(k)==""||act.Get(k)==""||payment.Get(k)==act.Get(k));
        }
        public ManualPaymentView GetManualPaymentView(string actId)
        { return GetManualPaymentView(actId,MonthlyServiceRuntime.Today); }
        internal ManualPaymentView GetManualPaymentView(string actId,DateTime today)
        {
            lock(gate)
            {
                var balance=ServiceReminderLedger.BalanceForAct(records,actId,today);
                var accounting=PaymentAccounting.Build(records);
                var payments=records.Where(p=>CanConfirmExistingPayment(p,balance.Act,today)&&accounting.For(p).Unallocated>0)
                    .OrderByDescending(p=>p.Get("date")).ThenBy(p=>p.Get("number")).Select(p=>p.Clone()).ToList();
                return new ManualPaymentView { Balance=balance,Payments=payments,Revision=Revision,
                    Available=payments.ToDictionary(p=>p.Id,p=>accounting.For(p).Unallocated) };
            }
        }
        public Record ConfirmActPayment(string actId,string paymentId,decimal amount,DateTime paidDate,string method,string number,string notes,long expectedRevision,string operationId)
        {
            return ConfirmActPayment(actId,paymentId,amount,paidDate,method,number,notes,expectedRevision,operationId,MonthlyServiceRuntime.Today);
        }
        internal Record ConfirmActPayment(string actId,string paymentId,decimal amount,DateTime paidDate,string method,string number,string notes,long expectedRevision,string operationId,DateTime today)
        {
            Guid operation;
            if(!Guid.TryParseExact(operationId,"N",out operation))throw new InvalidOperationException("Некорректное подтверждение оплаты.");
            using(var mutex=new Mutex(false,ServiceReminderRuntime.MailMutexName(DataDir)))
            {
                bool held;try{held=mutex.WaitOne(0);}catch(AbandonedMutexException){held=true;}
                if(!held)throw new InvalidOperationException("Сейчас отправляются письма. Повторите подтверждение оплаты после завершения отправки.");
                try
                {
                    lock(gate)
                    {
                        var previous=records.FirstOrDefault(r=>r.Get("manualConfirmationKey")==operationId);
                        if(previous!=null)
                        {
                            if(previous.Get("manualConfirmedActId")!=actId||previous.Get("manualConfirmedAmount")!=MoneyString(amount))
                                throw new InvalidOperationException("Это подтверждение уже сохранено с другой суммой или актом.");
                            return records.First(r=>r.Id==(previous.Kind=="payment"?previous.Id:previous.Get("paymentId"))).Clone();
                        }
                        if(Revision!=expectedRevision)throw new InvalidOperationException("Данные изменились. Закройте это окно и откройте подтверждение оплаты заново.");
                        if(amount<=0||amount>1000000000000m||Decimal.Round(amount,2)!=amount)throw new InvalidOperationException("Сумма должна быть больше нуля и содержать не более двух знаков после запятой.");
                        var view=GetManualPaymentView(actId,today);var act=view.Balance.Act;
                        if(view.Balance.Due<=0)throw new InvalidOperationException("Оплата этого акта уже учтена. Повторное подтверждение не требуется.");
                        var staged=records.Select(r=>r.Clone()).ToList();var entries=new List<AuditEntry>();
                        Record old=null,payment;
                        if(String.IsNullOrEmpty(paymentId))
                        {
                            if(view.Balance.NeedsReview)throw new InvalidOperationException("В программе есть банковская оплата без привязки. Выберите существующий платёж, чтобы не учитывать одну оплату дважды.");
                            if(amount>view.Balance.Due)throw new InvalidOperationException("Сумма подтверждения превышает остаток по акту.");
                            if(paidDate.Date>today.Date)throw new InvalidOperationException("Дата полученной оплаты не может быть в будущем.");
                            if(!new[]{"bank","cash","other"}.Contains(method))throw new InvalidOperationException("Выберите способ оплаты.");
                            payment=new Record("payment"){Id=operationId};
                            payment.Set("manualPaymentOrigin","true");payment.Set("date",DateString(paidDate.Date));
                            foreach(string key in new[]{"partyId","projectId","contractId","currency","direction"})payment.Set(key,act.Get(key));
                            payment.Set("actId",act.Id);payment.Set("paymentScope",IsExternalAct(act)?"external":"standard");
                            payment.Set("amount",MoneyString(amount));payment.Set("method",method);payment.Set("number",(number??"").Trim());payment.Set("notes",(notes??"").Trim());
                            staged.Add(payment);
                        }
                        else
                        {
                            old=view.Payments.FirstOrDefault(p=>p.Id==paymentId);
                            if(old==null)throw new InvalidOperationException("Выбранный платёж недоступен для этого акта.");
                            decimal free=view.Available[paymentId];
                            if(amount>free)throw new InvalidOperationException("Сумма превышает свободный остаток платежа.");
                            payment=staged.First(p=>p.Id==old.Id);
                            if(act.Get("status")!="signed")
                            {
                                if(amount!=free)throw new InvalidOperationException("Для акта с неизвестной подписью выберите всю свободную сумму существующего платежа. Частичный зачёт существующего платежа доступен после подтверждения подписи акта.");
                                payment.Set("actId",act.Id);
                            }
                            else if(amount>view.Balance.Due)throw new InvalidOperationException("Сумма превышает остаток по акту.");
                        }
                        payment.Set("manualPaymentConfirmed","true");payment.Set("manualConfirmedActId",act.Id);
                        payment.Set("manualAwaitingSignature",act.Get("status")=="signed"?"false":"true");
                        payment.Set("manualConfirmedBy",Environment.UserName);payment.Set("manualConfirmedUtc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture));
                        payment.Set("manualConfirmedAmount",MoneyString(amount));payment.Set("manualConfirmationKey",operationId);
                        Normalize(payment);
                        var auditEntry=MakeAudit(old==null?"create":"update",old,payment);
                        auditEntry.Summary="Ручное подтверждение оплаты акта № "+act.Get("number")+": "+MoneyString(amount)+" "+act.Get("currency")+(old==null?" — новый платёж":" — существующий платёж");
                        entries.Add(auditEntry);
                        if(act.Get("status")=="signed")
                        {
                            var allocation=new Record("allocation");allocation.Set("paymentId",payment.Id);allocation.Set("actId",act.Id);
                            allocation.Set("amount",MoneyString(amount));allocation.Set("date",DateString(today));
                            allocation.Set("manualConfirmationKey",operationId);allocation.Set("manualConfirmedActId",act.Id);allocation.Set("manualConfirmedAmount",MoneyString(amount));
                            staged.Add(allocation);entries.Add(MakeAudit("create",null,allocation));
                        }
                        Validate(staged);Commit(staged,entries);return payment.Clone();
                    }
                }
                finally{mutex.ReleaseMutex();}
            }
        }
    }
}
