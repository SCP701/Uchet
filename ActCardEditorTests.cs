using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class ActCardEditorTests
    {
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Act card test: " + message); }
        private static T Field<T>(object obj, string key) { return (T)obj.GetType().GetField(key, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj); }
        private static Dictionary<string, Control> Inputs(RecordEditor editor) { return Field<Dictionary<string, Control>>(editor, "inputs"); }
        private static ActWorkItemsControl Works(RecordEditor editor) { return Field<ActWorkItemsControl>(editor, "actWorks"); }
        private static string Selected(ComboBox combo)
        {
            object choice = combo.SelectedItem;
            return choice == null ? "" : Convert.ToString(choice.GetType().GetField("Value").GetValue(choice));
        }
        private static void Select(ComboBox combo, string value)
        {
            for (int i = 0; i < combo.Items.Count; ++i)
            {
                object choice = combo.Items[i];
                if (Convert.ToString(choice.GetType().GetField("Value").GetValue(choice)) == value) { combo.SelectedIndex = i; return; }
            }
            throw new InvalidOperationException("Missing act card option: " + value);
        }
        private static Record Save(Store store, string kind, params string[] fields)
        {
            Record record = new Record(kind); for (int i = 0; i < fields.Length; i += 2) record.Set(fields[i], fields[i + 1]);
            store.Upsert(record); return record;
        }
        private static Record Act(Store store, string party, string number, string amount, string description)
        {
            return Save(store, "act", "partyId", party, "number", number, "date", "2026-10-13", "direction", "income", "currency", "BYN", "status", "draft", "amount", amount, "description", description);
        }
        private static void Commit(RecordEditor editor)
        {
            typeof(RecordEditor).GetMethod("SaveClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, new object[] { null, EventArgs.Empty });
        }
        private static void Reject(Action action, string expected)
        {
            bool failed = false;
            try { action(); } catch (InvalidOperationException error) { failed = expected == null || error.Message.Contains(expected); }
            Assert(failed, "invalid candidate was accepted or wrong error: " + expected);
        }
        private static void Unchanged(Store store, long revision, byte[] bytes, string scenario)
        {
            Assert(store.Revision == revision && bytes.SequenceEqual(File.ReadAllBytes(store.DataPath)), scenario + " wrote the ledger");
        }
        private delegate bool TestWindowVisitor(IntPtr window, IntPtr state);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, TestWindowVisitor visitor, IntPtr state);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        private sealed class DescriptionReadWatchdog : IDisposable
        {
            private readonly IntPtr owner;
            private readonly uint thread;
            private readonly string scenario;
            private readonly System.Threading.Timer timeout;
            private volatile bool finished;
            private volatile bool expired;
            public volatile string Stage = "creating card";
            public DescriptionReadWatchdog(Form editor, string name)
            {
                owner = editor.Handle; thread = GetCurrentThreadId(); scenario = name;
                timeout = new System.Threading.Timer(delegate
                {
                    if (finished) return;
                    expired = true;
                    var windows = new List<string>();
                    EnumThreadWindows(thread, delegate(IntPtr window, IntPtr state)
                    {
                        Control control = Control.FromHandle(window);
                        bool owned = GetWindow(window, 4) == owner;
                        windows.Add(window.ToString("X") + ":" + (control == null ? "native" : control.GetType().Name) + ":owned=" + owned);
                        if (owned && control is ActWorkDescriptionDialog) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                        return true;
                    }, IntPtr.Zero);
                    Console.Error.WriteLine("FAIL: description read timeout: " + scenario + "; stage=" + Stage + "; thread windows=" + String.Join(", ", windows));
                    Console.Error.Flush();
                    System.Threading.Thread.Sleep(2000);
                    if (!finished) Environment.Exit(1);
                }, null, 10000, System.Threading.Timeout.Infinite);
            }
            public void Dispose()
            {
                finished = true; timeout.Dispose();
                Assert(!expired, scenario + " timed out at " + Stage);
            }
        }
        private static ActWorkDescriptionDialog OwnDescriptionDialog(Form editor)
        {
            IntPtr owner = editor.Handle;
            ActWorkDescriptionDialog dialog = Application.OpenForms.OfType<ActWorkDescriptionDialog>()
                .FirstOrDefault(form => form.IsHandleCreated && GetWindow(form.Handle, 4) == owner);
            if (dialog == null)
                EnumThreadWindows(GetCurrentThreadId(), delegate(IntPtr window, IntPtr state)
                {
                    if (GetWindow(window, 4) == owner) dialog = Control.FromHandle(window) as ActWorkDescriptionDialog;
                    return dialog == null;
                }, IntPtr.Zero);
            return dialog;
        }
        private const string DescriptionTail = "END-OF-COMPLETE-DESCRIPTION";
        private static string LongDescription(int prefixLength)
        {
            const string paragraph = "Подробное описание выполненной работы: проверка оборудования и настройка учёта.\r\n";
            var text = new StringBuilder();
            while (text.Length + paragraph.Length <= prefixLength) text.Append(paragraph);
            text.Append('Ш', prefixLength - text.Length);
            return text.Append(DescriptionTail).ToString();
        }
        private static void InViewport(Control control, string scenario)
        {
            Assert(control.Visible && control.ClientSize.Width > 0 && control.ClientSize.Height > 0, scenario + " is not visible");
            Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
            for (Control parent = control.Parent; parent != null; parent = parent.Parent)
                Assert(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds), scenario + " is clipped by " + parent.GetType().Name);
        }
        private static void SelectDescription(DataGridView grid, int row, string expected, string scenario)
        {
            grid.ClearSelection(); grid.CurrentCell = grid.Rows[row].Cells["description"]; grid.Rows[row].Selected = true;
            Application.DoEvents();
            Assert(Convert.ToString(grid.Rows[row].Cells["description"].Value) == expected, scenario + " truncated the underlying work text");
            DataObject content = grid.GetClipboardContent();
            Assert(content != null && content.GetDataPresent(DataFormats.UnicodeText), scenario + " cannot prepare full Unicode clipboard data");
            string copied = Convert.ToString(content.GetData(DataFormats.UnicodeText)).Replace("\r\n", "\n");
            Assert(copied.Contains(expected.Replace("\r\n", "\n")), scenario + " truncated work text in prepared clipboard data");
        }
        private static void OpenFullDescription(ActWorkItemsControl works, string expected, string scenario, DescriptionReadWatchdog watchdog)
        {
            Button button = works.FullDescriptionButton;
            Assert(button.Name == "showFullWorkDescription" && button.Text == "Полное описание" && button.Enabled, scenario + " full-description command unavailable");
            InViewport(button, scenario + " full-description button");
            Form owner = works.FindForm();
            Assert(button.CanSelect, scenario + " full-description button cannot receive a click");
            bool inspected = false; Exception failure = null;
            using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
            {
                timer.Tick += delegate
                {
                    var dialog = OwnDescriptionDialog(owner);
                    if (dialog == null || !dialog.Visible || !dialog.Modal || !dialog.DescriptionBox.IsHandleCreated) return;
                    timer.Stop(); inspected = true; watchdog.Stage = "reading visible modal";
                    try
                    {
                        TextBox box = dialog.DescriptionBox;
                        Assert(dialog.FullDescription == expected && box.Text == expected && box.Text.EndsWith(DescriptionTail), scenario + " viewer lost source text or final marker");
                        Assert(box.ReadOnly && box.Multiline && box.WordWrap && box.ShortcutsEnabled, scenario + " viewer does not allow safe multiline reading and selection");
                        InViewport(box, scenario + " description viewer");
                        box.SelectAll();
                        Assert(box.SelectedText == expected, scenario + " select-all cannot copy the whole description");
                        box.SelectionStart = 0; box.SelectionLength = 0; box.ScrollToCaret();
                        Application.DoEvents();
                        Point initialEnd = box.GetPositionFromCharIndex(box.TextLength - 1);
                        Assert(initialEnd.Y < 0 || initialEnd.Y + box.Font.Height > box.ClientSize.Height, scenario + " did not exercise a description longer than its viewport");
                        box.Focus(); box.SelectionStart = box.TextLength; box.SelectionLength = 0; box.ScrollToCaret();
                        Application.DoEvents();
                        Point end = box.GetPositionFromCharIndex(box.TextLength - 1);
                        Assert(end.X >= 0 && end.X < box.ClientSize.Width && end.Y >= 0 && end.Y + box.Font.Height <= box.ClientSize.Height,
                            scenario + " final text remains outside the viewer after ScrollToCaret: " + end + " / " + box.ClientSize);
                        Assert(box.SelectionStart == box.TextLength && box.Text == expected, scenario + " scrolling changed text or failed to reach its end");
                    }
                    catch (Exception error) { failure = error; }
                    finally { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
                };
                watchdog.Stage = "PerformClick before modal";
                timer.Start(); button.PerformClick(); timer.Stop();
                watchdog.Stage = "PerformClick returned";
            }
            if (failure != null) throw new InvalidOperationException(scenario + " full-description modal failed", failure);
            Assert(inspected && OwnDescriptionDialog(owner) == null, scenario + " did not open and close the real full-description modal");
        }
        private static void SameSource(Store store, Record original, long revision, byte[] bytes, string scenario)
        {
            Unchanged(store, revision, bytes, scenario);
            Record saved = store.Find(original.Id);
            Assert(saved.Kind == original.Kind && saved.Fields.Count == original.Fields.Count && original.Fields.All(field => saved.Get(field.Key) == field.Value),
                scenario + " changed source fields or the confirmed total");
        }
        private static void TestFullDescriptionReadAccess(Store store, string partyId)
        {
            Size[] sizes = { new Size(800, 610), new Size(1020, 760) };
            foreach (int prefixLength in new[] { 2300, 8000 })
            {
                string description = LongDescription(prefixLength);
                Assert(description.Length == prefixLength + 27, "legacy full-description fixture length is incorrect");
                Record source = Act(store, partyId, "Просмотр-" + description.Length, "120.00", description);
                source.Set("sourceArchivePath", "test-source.zip"); source.Set("sourceArchiveEntry", "legacy-act.html");
                source.Set("customField", "Не менять исходные сведения"); store.CaptureActSnapshots(source); store.Upsert(source); source = store.Find(source.Id).Clone();
                long revision = store.Revision; byte[] bytes = File.ReadAllBytes(store.DataPath);
                foreach (Size size in sizes)
                {
                    string scenario = "legacy " + description.Length + " chars at " + size.Width + "x" + size.Height;
                    using (var editor = new RecordEditor(store, Catalog.Get("act"), source))
                    using (var watchdog = new DescriptionReadWatchdog(editor, scenario))
                    {
                        watchdog.Stage = "showing card";
                        editor.ShowInTaskbar = false; editor.Opacity = 0; editor.Size = size; editor.Show(); Application.DoEvents();
                        watchdog.Stage = "copying source row";
                        var works = Works(editor); var grid = works.WorkGrid;
                        Assert(grid.Rows.Count == 1 && works.IsManualPricing && Store.ParseMoney(Inputs(editor)["amount"].Text) == 120m, scenario + " changed source rows or manual total");
                        SelectDescription(grid, 0, description, scenario); OpenFullDescription(works, description, scenario, watchdog);
                        watchdog.Stage = "preparing unchanged candidate";
                        Record candidate = editor.PrepareRecord();
                        Assert(source.Fields.All(field => candidate.Get(field.Key) == field.Value), scenario + " read-only viewing changed the unsaved act candidate");
                        Assert(editor.ResultRecord == null, scenario + " viewing saved the act");
                        SameSource(store, source, revision, bytes, scenario + " modal close"); editor.Close();
                    }
                    SameSource(store, source, revision, bytes, scenario + " card disposal");
                }
            }
            string[] descriptions = { LongDescription(1963), "Второй пункт: проверка печати", "Третий пункт: настройка доступа" };
            Record multiple = Act(store, partyId, "Просмотр-нескольких-работ", "345.67", "Комплекс выполненных работ");
            multiple.Set(ActHtmlDocument.DescriptiveItemsField, ActHtmlDocument.SerializeDescriptions(descriptions));
            multiple.Set("customField", "Все пункты из источника"); store.CaptureActSnapshots(multiple); store.Upsert(multiple); multiple = store.Find(multiple.Id).Clone();
            long multiRevision = store.Revision; byte[] multiBytes = File.ReadAllBytes(store.DataPath);
            foreach (Size size in sizes)
            {
                string scenario = "multiple works at " + size.Width + "x" + size.Height;
                using (var editor = new RecordEditor(store, Catalog.Get("act"), multiple))
                using (var watchdog = new DescriptionReadWatchdog(editor, scenario))
                {
                    watchdog.Stage = "showing card";
                    editor.ShowInTaskbar = false; editor.Opacity = 0; editor.Size = size; editor.Show(); Application.DoEvents();
                    watchdog.Stage = "checking work-row geometry";
                    var works = Works(editor); var grid = works.WorkGrid;
                    Assert(grid.Rows.Count == descriptions.Length && works.IsManualPricing, scenario + " collapsed unpriced work rows");
                    int viewport = grid.ClientSize.Height - grid.ColumnHeadersHeight;
                    Assert(viewport > grid.RowTemplate.Height && grid.Rows[0].Height < viewport, scenario + " long first row monopolizes the work viewport");
                    for (int row = 0; row < descriptions.Length; ++row)
                    {
                        SelectDescription(grid, row, descriptions[row], scenario + " row " + (row + 1));
                        grid.FirstDisplayedScrollingRowIndex = row; Application.DoEvents();
                        Rectangle visible = grid.GetCellDisplayRectangle(grid.Columns["description"].Index, row, true);
                        Assert(visible.Width > 0 && visible.Height >= grid.Rows[row].Height - 2 && visible.Top >= grid.ColumnHeadersHeight && visible.Bottom <= grid.ClientSize.Height,
                            scenario + " cannot scroll to work row " + (row + 1));
                    }
                    SelectDescription(grid, 0, descriptions[0], scenario); OpenFullDescription(works, descriptions[0], scenario, watchdog);
                    watchdog.Stage = "preparing unchanged candidate";
                    Record candidate = editor.PrepareRecord();
                    Assert(candidate.Get("amount") == "345.67" && multiple.Fields.All(field => candidate.Get(field.Key) == field.Value), scenario + " viewing redistributed prices or rewrote source fields");
                    Assert(editor.ResultRecord == null, scenario + " viewing saved the act"); editor.Close();
                }
                SameSource(store, multiple, multiRevision, multiBytes, scenario + " card disposal");
            }
        }
        public static void Run(string tempdir)
        {
            Store store = new Store(Path.Combine(tempdir, "card-" + Guid.NewGuid().ToString("N")));
            Record party = Save(store, "party", "name", "Первый заказчик", "role", "both", "unp", "111", "bankAccount", "BY111");
            Record secondParty = Save(store, "party", "name", "Второй заказчик", "role", "both", "unp", "222");
            Record project = Save(store, "project", "name", "Объект первого", "partyId", party.Id, "status", "active");
            Record secondProject = Save(store, "project", "name", "Объект второго", "partyId", secondParty.Id, "status", "active");
            Record contract = Save(store, "contract", "number", "Д1", "date", "2026-10-13", "partyId", party.Id, "projectId", project.Id, "currency", "BYN", "direction", "income", "amount", "10000.00", "status", "active");
            Record usdContract = Save(store, "contract", "number", "Д2", "date", "2026-10-13", "partyId", secondParty.Id, "projectId", secondProject.Id, "currency", "USD", "direction", "expense", "amount", "10000.00", "status", "active");
            Record settings = Save(store, "settings", "name", "Моя организация", "currency", "BYN", "hourlyRate", "50.00");
            Record longLegacy = Act(store, party.Id, "Старый-длинный", "120.00", new string('я', 2300));
            using (var editor = new RecordEditor(store, Catalog.Get("act"), longLegacy))
            {
                Inputs(editor)["notes"].Text = "Примечание к старому акту";
                var preview = editor.PrepareRecord();
                Assert(preview.Get("description") == longLegacy.Get("description") && preview.Get("amount") == "120.00", "notes-only long legacy description was rejected or rewritten");
            }
            string legacy = "Системное администрирование1. Перенести организацию 2. Настроить учёт 3. Заполнить политику 4. Заполнить справочники 5. Ввести остатки 6. Перенести документы 7. Заполнить договоры";
            Record act = Act(store, party.Id, "Семь-операций", "1050.00", legacy);
            act.Set("customField", "preserve"); store.CaptureActSnapshots(act); store.Upsert(act); act = store.Find(act.Id);
            long revision = store.Revision; byte[] bytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), act))
            {
                var works = Works(editor); var grid = works.WorkGrid;
                Assert(grid.Rows.Count == 7 && works.IsManualPricing, "legacy operations were collapsed or assigned invented prices");
                Assert(Store.ParseMoney(Inputs(editor)["amount"].Text) == 1050m && !((TextBox)Inputs(editor)["amount"]).ReadOnly, "manual legacy amount unavailable");
                var tabs = Field<TabControl>(editor, "sections");
                Assert(tabs.TabPages.Count == 4 && tabs.SelectedIndex == 0 && tabs.TabPages[0].Text == "Работы", "compact card pages missing");
                Assert(!((DateTimePicker)Inputs(editor)["dueDate"]).Checked && !((DateTimePicker)Inputs(editor)["signedDate"]).Checked, "optional dates were inferred");
                var mode = Field<CheckBox>(works, "manualPricing");
                mode.Checked = false; mode.Checked = true;
                Assert(Store.ParseMoney(Inputs(editor)["amount"].Text) == 1050m, "temporary pricing toggle lost confirmed total");
                Record unchanged = editor.PrepareRecord();
                Assert(unchanged.Get("description") == legacy && unchanged.Get("partySnapshot") == act.Get("partySnapshot"), "unchanged preview rewrote source descriptions or snapshots");
                grid.Rows[0].Cells["description"].Value = "Изменить организацию";
                Record preview = editor.PrepareRecord();
                string heading; var display = ActHtmlDocument.ReadDisplayRows(preview, out heading);
                Assert(display.Count == 7 && display[0].Description == "Изменить организацию" && display.All(row => !row.Price.HasValue) && heading == "Системное администрирование"
                    && preview.Get("amount") == "1050.00", "legacy row edit lost count, sum, or nullable pricing");
                Unchanged(store, revision, bytes, "legacy preview/cancel");
            }
            Unchanged(store, revision, bytes, "legacy disposal/cancel");
            using (var editor = new RecordEditor(store, Catalog.Get("act"), act))
            {
                var grid = Works(editor).WorkGrid;
                grid.Rows.Clear(); grid.Rows.Add(1, "Единственная подтверждённая операция", "", "", "", "");
                Inputs(editor)["notes"].Text = "Новое примечание";
                Commit(editor);
                Assert(editor.ResultRecord != null, "descriptive operation joint save failed");
            }
            act = store.Find(act.Id);
            string summary;
            Assert(ActHtmlDocument.ReadDisplayRows(act, out summary).Count == 1 && !ActHtmlDocument.ReadDisplayRows(act, out summary)[0].Price.HasValue &&
                act.Get("customField") == "preserve" && act.Get("notes") == "Новое примечание" && act.Get("amount") == "1050.00", "one unpriced operation or unrelated fields changed");
            revision = store.Revision; bytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), null))
            {
                var inputs = Inputs(editor);
                Select((ComboBox)inputs["partyId"], party.Id);
                Assert(Selected((ComboBox)inputs["contractId"]) == contract.Id && Selected((ComboBox)inputs["projectId"]) == project.Id, "unique compatible contract did not autofill");
                var grid = Works(editor).WorkGrid;
                grid.Rows.Clear(); grid.Rows.Add(1, "Монтаж", "шт", "2", "12,35", ""); grid.Rows.Add(2, "Настройка", "ч", "1,005", "2,00", "");
                Works(editor).RefreshTotals();
                Assert(((TextBox)inputs["amount"]).ReadOnly && Store.ParseMoney(inputs["amount"].Text) == 26.71m, "live per-line rounding or auto sum failed");
                var preview = editor.PrepareRecord();
                Assert(preview.Get("amount") == "26.71" && ActDocument.ReadItems(preview).Count == 2 && preview.Get("description").StartsWith("1. Монтаж"), "new card did not atomically prepare works and amount");
                Assert(preview.Get("partySnapshot") != "" && preview.Get("organizationSnapshot") != "", "new card requisites were not captured");
                var pricingMode = Field<CheckBox>(Works(editor), "manualPricing");
                pricingMode.Checked = true;
                Assert(Store.ParseMoney(inputs["amount"].Text) == 26.71m, "first manual-mode switch lost the current calculated total");
                pricingMode.Checked = false;
                grid.Rows[1].Cells["price"].Value = ""; Reject(delegate { editor.PrepareRecord(); }, "Работа 2");
                grid.Rows[1].Cells["price"].Value = "2,001"; Reject(delegate { editor.PrepareRecord(); }, "Работа 2");
                Unchanged(store, revision, bytes, "new totals/partial validation");
            }
            using (var editor = new RecordEditor(store, Catalog.Get("act"), act))
            {
                var inputs = Inputs(editor); string originalStatus = Selected((ComboBox)inputs["status"]);
                Select((ComboBox)inputs["partyId"], secondParty.Id);
                Assert(Selected((ComboBox)inputs["contractId"]) == "" && Selected((ComboBox)inputs["projectId"]) == "", "party change retained incompatible links");
                Select((ComboBox)inputs["contractId"], usdContract.Id);
                Assert(Selected((ComboBox)inputs["partyId"]) == secondParty.Id && Selected((ComboBox)inputs["projectId"]) == secondProject.Id &&
                    Selected((ComboBox)inputs["currency"]) == "USD" && Selected((ComboBox)inputs["direction"]) == "expense", "explicit contract selection did not autofill references/currency/direction");
                Assert(Selected((ComboBox)inputs["status"]) == originalStatus && !((DateTimePicker)inputs["dueDate"]).Checked &&
                    !((DateTimePicker)inputs["signedDate"]).Checked, "contract inferred payment date or signature/status");
                var preview = editor.PrepareRecord();
                Assert(Store.GetActSnapshot(preview, "partySnapshot").Id == secondParty.Id, "changed party retained old requisites");
                Select((ComboBox)inputs["currency"], "EUR");
                Assert(Selected((ComboBox)inputs["contractId"]) == "", "currency override kept incompatible contract");
            }
            Unchanged(store, revision, bytes, "reference changes/cancel");
            Record detailed = Act(store, party.Id, "Подробный", "100.00", "Старая строка");
            detailed.Set(ActDocument.ItemsField, ActDocument.SerializeItems(new[] { new ActPrintLine { Description = "Подробная работа", Unit = "ч", Quantity = 2m, Price = 50m } }));
            store.CaptureActSnapshots(detailed); store.Upsert(detailed); detailed = store.Find(detailed.Id);
            string savedItems = detailed.Get(ActDocument.ItemsField), savedSnapshot = detailed.Get("partySnapshot");
            party.Set("name", "Новое название справочника"); store.Upsert(party);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), detailed))
            {
                Inputs(editor)["notes"].Text = "Только примечание"; Commit(editor);
                Assert(editor.ResultRecord.Get(ActDocument.ItemsField) == savedItems && editor.ResultRecord.Get("partySnapshot") == savedSnapshot, "notes save rewrote print items or historical requisites");
            }
            detailed = store.Find(detailed.Id);
            revision = store.Revision; bytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), detailed))
            {
                Inputs(editor)["amount"].Text = "130,00"; Commit(editor);
                Assert(editor.ResultRecord == null && Field<Label>(editor, "hint").Text.Contains("Позиции акта"), "tampered auto total was accepted");
            }
            Unchanged(store, revision, bytes, "tampered calculated sum");
            Record hourly = Act(store, party.Id, "Часовой", "100.00", "");
            Store.SetHourlyActLines(hourly, new[] { new HourlyActLine { Description = "Почасовая настройка", Hours = 2, Rate = 50 } }); store.Upsert(hourly);
            revision = store.Revision; bytes = File.ReadAllBytes(store.DataPath);
            using (var editor = new RecordEditor(store, Catalog.Get("act"), hourly))
            {
                var rows = Works(editor).WorkGrid; rows.Rows[0].Cells["qty"].Value = "3"; Works(editor).RefreshTotals();
                var preview = editor.PrepareRecord();
                Assert(preview.Get("amount") == "150.00" && preview.Get("hours") == "3" && Store.GetHourlyActLines(preview).Single().Hours == 3, "hourly edit desynchronized workItems/hours/total");
                rows.Rows[0].Cells["unit"].Value = "усл."; preview = editor.PrepareRecord();
                Assert(preview.Get("workItems") == "" && preview.Get("hours") == "" && preview.Get("hourlyRate") == "" && ActDocument.ReadItems(preview).Single().Unit == "усл.", "general-unit conversion retained hourly authority");
                Unchanged(store, revision, bytes, "hourly preview/cancel");
            }
            using (var editor = new RecordEditor(store, Catalog.Get(Catalog.ExternalActSection), null))
            {
                Select((ComboBox)Inputs(editor)["partyId"], party.Id);
                var rows = Works(editor).WorkGrid;
                rows.Rows[0].Cells["description"].Value = "Новый акт вне"; rows.Rows[0].Cells["price"].Value = "15";
                Works(editor).RefreshTotals();
                Assert(Store.IsExternalAct(editor.PrepareRecord()), "external section with null source created an ordinary act");
            }
            Record external = new Record("act"); external.Set("accountingScope", "external");
            using (var editor = new RecordEditor(store, Catalog.Get(Catalog.ExternalActSection), external))
            {
                Select((ComboBox)Inputs(editor)["partyId"], party.Id);
                var rows = Works(editor).WorkGrid; rows.Rows[0].Cells["description"].Value = "Работа вне"; rows.Rows[0].Cells["price"].Value = "10";
                Works(editor).RefreshTotals(); var preview = editor.PrepareRecord();
                Assert(Store.IsExternalAct(preview) && preview.Get("amount") == "10.00", "external card lost accounting scope");
            }
            TestFullDescriptionReadAccess(store, party.Id);
            Console.WriteLine("PASS: act card full legacy-description reading/selection, reachable work rows, separate rows, legacy nullable prices, live totals/rounding, reference autofill/overrides, optional dates, snapshots, atomic preview/cancel, hourly conversion, external acts.");
        }
    }
}
