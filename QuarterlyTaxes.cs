using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace WorkLedger
{
    public sealed class TaxReceipt
    {
        public string PaymentId { get; private set; }
        public string Number { get; private set; }
        public string PartyName { get; private set; }
        public string Method { get; private set; }
        public DateTime Date { get; private set; }
        public decimal Amount { get; private set; }
        public bool Archived { get; private set; }
        public decimal TotalAmount { get; private set; }
        public decimal GreyAmount { get; private set; }
        internal TaxReceipt(string paymentId, string number, string partyName, string method, DateTime date, decimal amount, bool archived, decimal totalAmount, decimal greyAmount)
        {
            PaymentId = paymentId; Number = number; PartyName = partyName; Method = method; Date = date; Amount = amount; Archived = archived; TotalAmount = totalAmount; GreyAmount = greyAmount;
        }
    }

    public sealed class QuarterlyTaxResult
    {
        public bool AllTime { get; private set; }
        public bool CustomPeriod { get { return !AllTime && Quarter == 0; } }
        public int Year { get; private set; }
        public int Quarter { get; private set; }
        public string Currency { get; private set; }
        public DateTime FromDate { get; private set; }
        public DateTime ToDate { get; private set; }
        public decimal Rate { get; private set; }
        public decimal Receipts { get; private set; }
        public decimal PreliminaryTax { get; private set; }
        public decimal Tax { get; private set; }
        public decimal Remaining { get; private set; }
        public IList<TaxReceipt> Payments { get; private set; }
        internal QuarterlyTaxResult(int year, int quarter, string currency, DateTime fromDate, DateTime toDate, decimal receipts, List<TaxReceipt> payments, bool allTime)
        {
            AllTime = allTime; Year = year; Quarter = quarter; Currency = currency; FromDate = fromDate; ToDate = toDate;
            Rate = QuarterlyTaxes.TaxRate; Receipts = receipts;
            PreliminaryTax = Decimal.Round(receipts * Rate, 2, MidpointRounding.AwayFromZero);
            Remaining = receipts - PreliminaryTax;
            Tax = Decimal.Round(Remaining * Rate, 2, MidpointRounding.AwayFromZero);
            Payments = new ReadOnlyCollection<TaxReceipt>(payments);
        }
    }

    public static class QuarterlyTaxes
    {
        public const decimal TaxRate = 0.20m;
        public static QuarterlyTaxResult Calculate(Store store, int year, int quarter, string currency)
        {
            if (store == null) throw new ArgumentNullException("store", "Не указан источник оплат для расчёта.");
            if (year < 1 || year > 9999) throw new ArgumentOutOfRangeException("year", "Выберите год от 1 до 9999.");
            if (quarter < 1 || quarter > 4) throw new ArgumentOutOfRangeException("quarter", "Выберите квартал от 1 до 4.");
            int firstMonth = (quarter - 1) * 3 + 1, lastMonth = firstMonth + 2;
            DateTime fromDate = new DateTime(year, firstMonth, 1), toDate = new DateTime(year, lastMonth, DateTime.DaysInMonth(year, lastMonth));
            return CalculatePeriod(store, year, quarter, currency, fromDate, toDate, false);
        }
        public static QuarterlyTaxResult CalculateAllTime(Store store, string currency)
        {
            if (store == null) throw new ArgumentNullException("store", "Не указан источник оплат для расчёта.");
            return CalculatePeriod(store, 0, 0, currency, DateTime.MinValue, DateTime.MaxValue.Date, true);
        }
        public static QuarterlyTaxResult CalculateRange(Store store, DateTime fromDate, DateTime toDate, string currency)
        {
            if (store == null) throw new ArgumentNullException("store", "Не указан источник оплат для расчёта.");
            fromDate = fromDate.Date; toDate = toDate.Date;
            if (fromDate > toDate) throw new ArgumentException("Дата начала периода должна быть не позже даты окончания.", "fromDate");
            return CalculatePeriod(store, 0, 0, currency, fromDate, toDate, false);
        }
        private static QuarterlyTaxResult CalculatePeriod(Store store, int year, int quarter, string currency, DateTime fromDate, DateTime toDate, bool allTime)
        {
            if (!new[] { "BYN", "RUB", "USD", "EUR" }.Contains(currency, StringComparer.Ordinal))
                throw new ArgumentException("Выберите валюту BYN, RUB, USD или EUR.", "currency");
            // All payments and counterparty names are read from the same detached store snapshot.
            List<Record> snapshot = store.Records;
            PaymentAccounting accounting = PaymentAccounting.Build(snapshot);
            Dictionary<string, Record> parties = snapshot.Where(x => x.Kind == "party").ToDictionary(x => x.Id, StringComparer.Ordinal);
            var receipts = new List<TaxReceipt>();
            decimal total = 0m;
            foreach (Record payment in snapshot)
            {
                if (payment.Kind != "payment" || payment.Get("direction") != "income" || payment.Get("currency") != currency) continue;
                DateTime date;
                if (!DateTime.TryParseExact(payment.Get("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    throw new InvalidOperationException("У оплаты «" + PaymentLabel(payment) + "» некорректная дата. Исправьте оплату перед расчётом.");
                if (date < fromDate || date > toDate) continue;
                decimal amount = Store.ParseMoney(payment.Get("amount"));
                if (amount <= 0) throw new InvalidOperationException("У оплаты «" + PaymentLabel(payment) + "» сумма должна быть больше нуля.");
                PaymentSplit parts = accounting.For(payment);
                decimal taxable = parts.WhiteAmount;
                if (taxable == 0) continue;
                Record party;
                string partyName = parties.TryGetValue(payment.Get("partyId"), out party) ? party.Get("name") : "[контрагент не найден]";
                receipts.Add(new TaxReceipt(payment.Id, payment.Get("number"), partyName, payment.Get("method"), date, taxable, payment.Get("archived") == "true", amount, parts.GreyAmount));
                total += taxable;
            }
            receipts = receipts.OrderBy(x => x.Date).ThenBy(x => x.Number, StringComparer.Ordinal).ThenBy(x => x.PaymentId, StringComparer.Ordinal).ToList();
            return new QuarterlyTaxResult(year, quarter, currency, fromDate, toDate, total, receipts, allTime);
        }
        public static Tuple<int, int> PreviousQuarter(DateTime today)
        {
            int quarter = (today.Month - 1) / 3 + 1;
            if (quarter > 1) return Tuple.Create(today.Year, quarter - 1);
            if (today.Year == 1) throw new ArgumentOutOfRangeException("today", "Для этой даты предыдущий квартал находится за пределами допустимых лет.");
            return Tuple.Create(today.Year - 1, 4);
        }
        private static string PaymentLabel(Record payment) { return payment.Get("number") == "" ? payment.Id : payment.Get("number"); }
    }
}
