using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    internal static class ActActions
    {
        internal static Record RefreshRequisites(IWin32Window owner, Store store, string id)
        {
            Record act = store.Find(id);
            if (act == null || act.Kind != "act") throw new InvalidOperationException("Выберите сохранённый акт.");
            Record own = store.Of("settings").FirstOrDefault(), party = store.Find(act.Get("partyId"));
            if (own == null || String.IsNullOrWhiteSpace(own.Get("name"))) throw new InvalidOperationException("Заполните название своей организации: «Меню → Настройки → Основные данные».");
            if (party == null) throw new InvalidOperationException("Контрагент акта не найден.");
            long revision = store.Revision;
            string text = "Обновить реквизиты акта № " + act.Get("number") + "?\n\nМоя организация: " + own.Get("name") + "\nКонтрагент: " + party.Get("name")
                + "\n\nВ акт будут сохранены текущие реквизиты обеих сторон. Номер, работы, сумма и оплаты останутся прежними. Изменение применяется сразу и записывается в историю.";
            if (MessageBox.Show(owner, text, "Обновить реквизиты", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return null;
            return store.RefreshActRequisites(id, revision);
        }
        internal static bool Delete(IWin32Window owner, Store store, string id)
        {
            store.CheckActDeletion(id);
            Record act = store.Find(id); long revision = store.Revision;
            string text = "Удалить акт № " + act.Get("number") + " от " + act.Get("date") + " на " + act.Get("amount") + " " + act.Get("currency")
                + "?\n\nНесохранённые изменения карточки будут потеряны. Перед удалением будет создана резервная копия. Удаление останется в истории, номер не будет использован повторно.";
            if (MessageBox.Show(owner, text, "Удалить акт", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
            DeleteWithBackup(store, id, revision);
            return true;
        }
        internal static string DeleteWithBackup(Store store, string id, long revision)
        {
            store.CheckActDeletion(id);
            string backup = Path.Combine(store.DataDir, "backups", "before-delete-act-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            BackupArchive.Create(store.DataDir, backup);
            store.DeleteAct(id, revision);
            return backup;
        }
    }
}
