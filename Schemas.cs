using System;
using System.Collections.Generic;
using System.Linq;

namespace WorkLedger
{
    public sealed class FieldDef
    {
        public string Key;
        public string Label;
        public string Type;
        public bool Required;
        public string ReferenceKind;
        public Dictionary<string, string> Options;
        public string Section;

        public FieldDef(string key, string label, string type, bool required)
        {
            Key = key;
            Label = label;
            Type = type;
            Required = required;
            Options = new Dictionary<string, string>();
        }
    }

    public sealed class Schema
    {
        public string Kind;
        public string SectionKey;
        public string Title;
        public string Singular;
        public List<FieldDef> Fields;

        public Schema(string kind, string title, string singular, params FieldDef[] fields)
        {
            Kind = kind; SectionKey = kind;
            Title = title;
            Singular = singular;
            Fields = new List<FieldDef>(fields);
        }
    }

    public static class Catalog
    {
        public const string ExternalActSection = "external-act";
        public const string GrayPaymentSection = "gray-payment";
        public static readonly List<Schema> All = Build();

        public static bool IsActSection(string kind) { return kind == "act" || kind == ExternalActSection; }
        public static bool IsPaymentSection(string kind) { return kind == "payment" || kind == GrayPaymentSection; }
        public static Schema Get(string kind)
        {
            if (kind == ExternalActSection)
            {
                Schema act = Get("act");
                return new Schema("act", "Акты вне", "Акт вне", act.Fields.ToArray()) { SectionKey = ExternalActSection };
            }
            if (kind == GrayPaymentSection)
            {
                Schema payment = Get("payment");
                return new Schema("payment", "Серые оплаты", "Серая оплата", payment.Fields.ToArray()) { SectionKey = GrayPaymentSection };
            }
            Schema schema = All.FirstOrDefault(delegate(Schema s) { return s.Kind == kind; });
            return schema;
        }

        public static string Display(Store store, Record record)
        {
            if (record == null) return "";
            string title;
            if (record.Kind == "party" || record.Kind == "project" || record.Kind == "settings")
                title = record.Get("name");
            else if (record.Kind == "document") title = record.Get("title");
            else if (record.Kind == "work") title = record.Get("description");
            else if (record.Kind == "hold")
            {
                Record act = store == null ? null : store.Find(record.Get("actId"));
                title = "Удержание" + (act == null ? "" : " · акт № " + act.Get("number"));
            }
            else if (record.Kind == "expense")
                title = "Расход " + record.Get("date") + " · " + record.Get("description");
            else
            {
                Schema schema = All.FirstOrDefault(delegate(Schema s) { return s.Kind == record.Kind; });
                string number = record.Get("number");
                title = (Store.IsExternalAct(record) ? "Акт вне" : record.Kind == "payment" && record.Get("paymentScope") == "external" ? "Серая оплата" : schema == null ? "Запись" : schema.Singular) +
                    (String.IsNullOrWhiteSpace(number) ? " от " + record.Get("date") : " № " + number);
            }
            if (String.IsNullOrWhiteSpace(title)) title = "Без названия";
            if (record.Get("archived") == "true") title += " (архив)";
            return title;
        }

        public static string ValueLabel(FieldDef field, string value)
        {
            if (field.Type == "bool") return String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ? "Да" : "Нет";
            string label;
            return field.Options != null && field.Options.TryGetValue(value ?? "", out label) ? label : value;
        }

        private static FieldDef F(string key, string label, string type, bool required)
        {
            return new FieldDef(key, label, type, required);
        }

        private static FieldDef R(string key, string label, string kind, bool required)
        {
            FieldDef field = F(key, label, "ref", required);
            field.ReferenceKind = kind;
            return field;
        }

        private static FieldDef S(string section, FieldDef field)
        {
            field.Section = section;
            return field;
        }
        private static FieldDef E(string key, string label, bool required, params string[] values)
        {
            FieldDef field = F(key, label, "enum", required);
            for (int i = 0; i < values.Length; i += 2) field.Options.Add(values[i], values[i + 1]);
            return field;
        }

        private static FieldDef Currency()
        {
            return E("currency", "Валюта", true, "BYN", "BYN — белорусский рубль", "RUB", "RUB — российский рубль", "USD", "USD — доллар США", "EUR", "EUR — евро");
        }

        private static FieldDef Direction()
        {
            return E("direction", "Направление", true, "income", "Заказчик платит нам", "expense", "Мы платим подрядчику");
        }

        private static FieldDef Notes() { return F("notes", "Примечание", "memo", false); }
        private static FieldDef ReviewStatus()
        {
            return E("reviewStatus", "Проверка сверки", false, "pending", "Требует проверки", "matched", "Найдена связь", "checked", "Проверено");
        }
        private static FieldDef ReviewNotes() { return F("reviewNotes", "Причина проверки / связь с выпиской", "memo", false); }

        private static FieldDef Date() { return F("date", "Дата", "date", true); }
        private static FieldDef Number() { return F("number", "Номер", "text", true); }
        private static FieldDef Amount() { return F("amount", "Сумма", "money", true); }
        private static FieldDef Party(bool required) { return R("partyId", "Контрагент", "party", required); }
        private static FieldDef Project(bool required) { return R("projectId", "Объект", "project", required); }
        private static FieldDef Contract(bool required) { return R("contractId", "Договор", "contract", required); }
        private static FieldDef DueDate() { return F("dueDate", "Срок оплаты", "date", false); }

        private static List<Schema> Build()
        {
            return new List<Schema>
            {
                new Schema("party", "Контрагенты", "Контрагент",
                    S("Основные данные", F("name", "Название / ФИО", "text", true)),
                    S("Основные данные", E("role", "Роль", true, "client", "Заказчик", "vendor", "Подрядчик / поставщик", "both", "Заказчик и подрядчик")),
                    S("Основные данные", F("unp", "УНП", "text", false)),
                    S("Адреса", F("registrationAddress", "Адрес регистрации / юридический адрес", "memo", false)),
                    S("Адреса", F("postalAddress", "Почтовый адрес", "memo", false)),
                    S("Адреса", F("actualAddress", "Фактический адрес", "memo", false)),
                    S("Банк", F("bankName", "Название банка", "text", false)),
                    S("Банк", F("bankAccount", "Расчётный счёт (IBAN)", "text", false)),
                    S("Банк", F("bankCode", "БИК / SWIFT", "text", false)),
                    S("Контакты", F("contact", "Контактное лицо", "text", false)),
                    S("Контакты", F("phone", "Телефон", "text", false)),
                    S("Контакты", F("email", "Электронная почта", "text", false)),
                    S("Контакты", F("website", "Сайт", "text", false)),
                    S("Контакты", F("chiefAccountantName", "Главный бухгалтер", "text", false)),
                    S("Контакты", F("chiefAccountantPhone", "Телефон главного бухгалтера", "text", false)),
                    S("Обслуживание", F("monthlyServiceEnabled", "Ежемесячное обслуживание", "bool", false)),
                    S("Обслуживание", F("monthlyServiceAmount", "Сумма за месяц", "money", false)),
                    S("Обслуживание", E("monthlyServiceCurrency", "Валюта", false, "BYN", "BYN — белорусский рубль", "RUB", "RUB — российский рубль", "USD", "USD — доллар США", "EUR", "EUR — евро")),
                    S("Обслуживание", F("monthlyServiceDescription", "Наименование работ", "memo", false)),
                    S("Обслуживание", F("monthlyServiceStartMonth", "Первый акт", "date", false)),
                    S("Прочее", F("requisites", "Реквизиты", "memo", false)),
                    S("Прочее", Notes())),
                new Schema("project", "Объекты", "Объект",
                    F("name", "Название", "text", true), Party(false), F("location", "Адрес / расположение", "text", false),
                    E("status", "Статус", true, "active", "В работе", "complete", "Завершён"), Notes()),
                new Schema("contract", "Договоры", "Договор",
                    Number(), Date(), Party(true), Project(false), Direction(), Amount(), Currency(), DueDate(),
                    E("status", "Статус", true, "active", "Действует", "complete", "Завершён", "cancelled", "Отменён"), Notes()),
                new Schema("amendment", "Допсоглашения", "Допсоглашение",
                    Number(), Date(), Contract(true), F("amount", "Изменение суммы (+ / −)", "money", true),
                    E("status", "Статус", true, "draft", "Черновик", "signed", "Подписано", "cancelled", "Отменено"), Notes()),
                new Schema("estimate", "Сметы", "Смета",
                    Number(), Date(), Party(true), Project(false), Contract(false), Direction(), Currency(),
                    E("status", "Статус", true, "draft", "Черновик", "approved", "Согласована", "cancelled", "Отменена"), Notes()),
                new Schema("work", "Работы в сметах", "Работа",
                    R("estimateId", "Смета", "estimate", true), F("description", "Наименование работы", "memo", true),
                    F("unit", "Единица измерения", "text", false), F("qty", "Количество", "number", true),
                    F("price", "Цена за единицу", "money", true)),
                new Schema("act", "Акты", "Акт",
                    F("number", "Номер", "text", false), Date(), Party(true), Project(false), Contract(false), Direction(), Amount(), Currency(), DueDate(),
                    F("hours", "Всего часов", "number", false),
                    E("status", "Статус", true, "draft", "Черновик", "sent", "Отправлен", "signed", "Подписан", "cancelled", "Отменён"),
                    F("description", "Выполненные работы", "memo", false), F("signedDate", "Дата подписания", "date", false), Notes(), ReviewStatus(), ReviewNotes()),
                new Schema("invoice", "Счета", "Счёт",
                    Number(), Date(), Party(true), Project(false), Contract(false), R("actId", "Акт", "act", false),
                    Amount(), Currency(), Direction(), DueDate(),
                    E("status", "Статус", true, "draft", "Черновик", "sent", "Выставлен", "cancelled", "Отменён"), Notes()),
                new Schema("payment", "Оплаты и авансы", "Платёж",
                    F("number", "Номер документа", "text", false), Date(),
                    E("paymentScope", "Вид оплаты", false, "standard", "Белая", "external", "Серая (Акты вне)", "", "По актам и распределениям"),
                    R("actId", "Акт", "act", false), Party(true), Project(false), Contract(false),
                    Direction(), Amount(), Currency(), E("method", "Способ оплаты", true, "bank", "Банк", "cash", "Наличные", "other", "Другое"), Notes(), ReviewStatus(), ReviewNotes()),
                new Schema("expense", "Расходы", "Расход",
                    Date(), Party(false), Project(true),
                    E("category", "Категория", true, "materials", "Материалы", "labor", "Рабочие", "transport", "Транспорт", "rent", "Аренда", "other", "Прочее"),
                    Amount(), Currency(), F("description", "Описание", "memo", false), Notes()),
                new Schema("hold", "Удержания", "Удержание",
                    R("actId", "Акт", "act", true), Date(), Amount(),
                    E("status", "Статус", true, "active", "Удерживается", "released", "Освобождено"),
                    E("type", "Причина", true, "warranty", "Гарантийное удержание", "penalty", "Штраф", "other", "Другое"), Notes()),
                new Schema("document", "Документы", "Документ",
                    F("title", "Название", "text", true), Date(), Party(false), Project(false), Contract(false), R("actId", "Акт", "act", false),
                    E("status", "Статус", true, "missing", "Не получен", "draft", "Черновик", "sent", "Отправлен", "signed", "Подписан"), Notes(), ReviewStatus(), ReviewNotes()),
                new Schema("settings", "Моя организация", "Организация",
                    S("Создание актов", F("hourlyRate", "Стоимость часа", "money", false)),
                    S("Создание актов", F("nextActNumber", "Следующий номер акта (с 500)", "number", false)),
                    S("Основные данные", F("name", "Название / ФИО ИП", "text", false)),
                    S("Основные данные", F("unp", "УНП", "text", false)),
                    S("Основные данные", Currency()),
                    S("Основные данные", F("registrationNumber", "Номер свидетельства / регистрации", "text", false)),
                    S("Основные данные", F("registrationDate", "Дата регистрации", "date", false)),
                    S("Основные данные", F("registrationAuthority", "Регистрирующий орган", "text", false)),
                    S("Адреса", F("registrationAddress", "Адрес регистрации", "memo", false)),
                    S("Адреса", F("postalAddress", "Почтовый адрес", "memo", false)),
                    S("Адреса", F("actualAddress", "Фактический адрес", "memo", false)),
                    S("Банк", F("bankName", "Название банка", "text", false)),
                    S("Банк", F("bankAccount", "Расчётный счёт (IBAN)", "text", false)),
                    S("Банк", F("bankCode", "БИК / SWIFT", "text", false)),
                    S("Контакты", F("phone", "Телефон", "text", false)),
                    S("Контакты", F("email", "Электронная почта", "text", false)),
                    S("Контакты", F("website", "Сайт", "text", false)),
                    S("Контакты", F("chiefAccountantName", "Главный бухгалтер", "text", false)),
                    S("Контакты", F("chiefAccountantPhone", "Телефон главного бухгалтера", "text", false)),
                    S("Контакты", F("contact", "Дополнительные контакты", "memo", false)),
                    S("Подпись и прочее", F("signatoryName", "ФИО подписанта", "text", false)),
                    S("Подпись и прочее", F("signatoryPosition", "Статус / должность подписанта", "text", false)),
                    S("Подпись и прочее", F("signatoryBasis", "Основание полномочий", "memo", false)),
                    S("Подпись и прочее", F("vatNote", "Налоги / НДС (для документов)", "memo", false)),
                    S("Подпись и прочее", F("requisites", "Дополнительные реквизиты", "memo", false)))
            };
        }
    }
}

