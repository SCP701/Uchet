using System;
using System.Collections.Generic;
using System.Globalization;

namespace WorkLedger
{
    public sealed class PaymentSplit
    {
        public string PaymentId { get; private set; }
        public decimal Total { get; private set; }
        public decimal WhiteAmount { get; private set; }
        public decimal GreyAmount { get; private set; }
        public decimal AllocatedWhite { get; private set; }
        public decimal AllocatedGrey { get; private set; }
        public decimal Unallocated { get; private set; }
        public bool IsMixed { get { return WhiteAmount > 0m && GreyAmount > 0m; } }

        internal PaymentSplit(string paymentId, decimal total, decimal white, decimal grey, decimal allocatedWhite, decimal allocatedGrey, decimal unallocated)
        {
            PaymentId = paymentId; Total = total; WhiteAmount = white; GreyAmount = grey;
            AllocatedWhite = allocatedWhite; AllocatedGrey = allocatedGrey; Unallocated = unallocated;
        }
    }

    public sealed class PaymentAccounting
    {
        private readonly Dictionary<string, PaymentSplit> payments;
        private PaymentAccounting(Dictionary<string, PaymentSplit> payments) { this.payments = payments; }

        public static PaymentAccounting Build(IEnumerable<Record> snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            var records = new Dictionary<string, Record>(StringComparer.Ordinal);
            foreach (Record record in snapshot)
            {
                if (record == null || String.IsNullOrWhiteSpace(record.Id) || record.Fields == null)
                    throw new InvalidOperationException("Снимок базы содержит некорректную запись.");
                if (records.ContainsKey(record.Id)) throw new InvalidOperationException("Снимок базы содержит повторяющийся идентификатор.");
                records.Add(record.Id, record.Clone());
            }
            var allocatedWhite = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var allocatedGrey = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (Record allocation in records.Values)
            {
                if (allocation.Kind != "allocation" || allocation.Get("reversed") == "true") continue;
                Record payment = Linked(records, allocation.Get("paymentId"), "payment");
                Record act = Linked(records, allocation.Get("actId"), "act");
                bool external = ExternalAct(act);
                string scope = Scope(payment);
                if (scope != "" && (scope == "external") != external)
                    throw new InvalidOperationException("Вид оплаты не совпадает с разделом акта в распределении.");
                decimal amount = Money(allocation);
                var totals = external ? allocatedGrey : allocatedWhite;
                decimal previous; totals.TryGetValue(payment.Id, out previous); totals[payment.Id] = previous + amount;
            }
            var result = new Dictionary<string, PaymentSplit>(StringComparer.Ordinal);
            foreach (Record payment in records.Values)
            {
                if (payment.Kind != "payment") continue;
                string scope = Scope(payment);
                Record directAct = payment.Get("actId") == "" ? null : Linked(records, payment.Get("actId"), "act");
                bool directExternal = directAct != null && ExternalAct(directAct);
                if (scope == "external" && !directExternal)
                    throw new InvalidOperationException("Для серой оплаты требуется связанный акт из раздела «Акты вне».");
                if (scope == "standard" && directExternal)
                    throw new InvalidOperationException("Обычная оплата не может быть связана с актом из раздела «Акты вне».");
                decimal total = Money(payment), whiteAllocated, greyAllocated;
                allocatedWhite.TryGetValue(payment.Id, out whiteAllocated); allocatedGrey.TryGetValue(payment.Id, out greyAllocated);
                decimal unallocated = total - whiteAllocated - greyAllocated;
                if (unallocated < 0m) throw new InvalidOperationException("Распределение превышает сумму платежа.");
                decimal grey;
                if (scope == "external") grey = total;
                else if (scope == "standard") grey = 0m;
                else grey = greyAllocated + (directExternal ? unallocated : 0m);
                decimal white = total - grey;
                if (grey < 0m || white < 0m || white + grey != total)
                    throw new InvalidOperationException("Белая и серая части оплаты должны равняться её полной сумме.");
                result.Add(payment.Id, new PaymentSplit(payment.Id, total, white, grey, whiteAllocated, greyAllocated, unallocated));
            }
            return new PaymentAccounting(result);
        }

        public PaymentSplit For(Record payment)
        {
            if (payment == null) throw new ArgumentNullException("payment");
            if (payment.Kind != "payment") throw new ArgumentException("Выберите запись оплаты.", "payment");
            // Only identity is read from the caller; all financial values belong to the captured snapshot.
            return ForId(payment.Id);
        }
        public PaymentSplit ForId(string paymentId)
        {
            if (String.IsNullOrWhiteSpace(paymentId)) throw new ArgumentException("Не указан идентификатор оплаты.", "paymentId");
            PaymentSplit result;
            if (!payments.TryGetValue(paymentId, out result)) throw new InvalidOperationException("Оплата не найдена в снимке базы.");
            return result;
        }
        private static Record Linked(Dictionary<string, Record> records, string id, string kind)
        {
            Record result;
            if (String.IsNullOrWhiteSpace(id) || !records.TryGetValue(id, out result) || result.Kind != kind)
                throw new InvalidOperationException("В снимке базы не найдена связанная запись «" + Store.KindLabel(kind) + "».");
            return result;
        }
        private static bool ExternalAct(Record act)
        {
            string scope = act.Get("accountingScope");
            if (scope != "" && scope != "standard" && scope != "external")
                throw new InvalidOperationException("Некорректный раздел связанного акта.");
            return scope == "external";
        }
        private static string Scope(Record payment)
        {
            string scope = payment.Get("paymentScope");
            if (scope != "" && scope != "standard" && scope != "external")
                throw new InvalidOperationException("Некорректный вид оплаты.");
            return scope;
        }
        private static decimal Money(Record record)
        {
            string value = record.Get("amount");
            decimal amount = Store.ParseMoney(value), exact;
            if (!Decimal.TryParse(value.Trim().Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out exact)
                || exact != amount || amount <= 0m)
                throw new InvalidOperationException("Сумма оплаты или распределения должна быть положительной и содержать не более двух знаков после запятой.");
            return amount;
        }
    }
}
