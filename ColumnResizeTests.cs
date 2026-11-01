using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WorkLedger
{
    public static class ColumnResizeTests
    {
        private static void Check(bool value, string message)
        { if (!value) throw new Exception("Column resize: " + message); }
        private static void Mouse(DataGridView grid, string method, int x, int y)
        {
            typeof(ResizableDataGridView).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(grid, new object[]{new MouseEventArgs(MouseButtons.Left, 1, x, y, 0)});
        }
        private static void Drag(DataGridView grid, DataGridViewColumn column, int distance)
        {
            int x = grid.GetColumnDisplayRectangle(column.Index, false).Right - 1, y = grid.ColumnHeadersHeight / 2;
            int before = column.Width;
            Mouse(grid, "OnMouseDown", x, y);
            Mouse(grid, "OnMouseMove", x + distance, y);
            Mouse(grid, "OnMouseUp", x + distance, y);
            Application.DoEvents();
            Check(Math.Abs(column.Width - before - distance) <= 5, "divider gesture did not resize " + column.HeaderText + ": " + before + " -> " + column.Width + ", delta " + distance);
        }
        private static Record Add(Store store, string kind, params string[] fields)
        {
            var record = new Record(kind);
            for (int i = 0; i < fields.Length; i += 2) record.Set(fields[i], fields[i + 1]);
            store.Upsert(record); return store.Find(record.Id);
        }
        private static void CheckListPosition(string root)
        {
            var store = new Store(Path.Combine(root, "list-position-" + Guid.NewGuid().ToString("N")));
            for (int i = 0; i < 40; i++) Add(store, "party", "name", "Заказчик " + i.ToString("D2"), "role", "client");
            using (var form = new MainForm(store, false))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.Size = new Size(1280, 800); form.Show();
                form.Navigate("party"); form.ResetFilters(); Application.DoEvents();
                var grid = (DataGridView)typeof(MainForm).GetField("grid", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                var name = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.HeaderText == "Название / ФИО");
                grid.Sort(name, System.ComponentModel.ListSortDirection.Ascending);
                grid.CurrentCell = grid.Rows[15].Cells[name.Index]; grid.FirstDisplayedScrollingRowIndex = 10;
                string selected = Convert.ToString(grid.CurrentRow.Tag), top = Convert.ToString(grid.Rows[grid.FirstDisplayedScrollingRowIndex].Tag);
                Record edited = store.Find(selected); edited.Set("phone", "123456"); store.Upsert(edited);
                byte[] original = File.ReadAllBytes(store.DataPath);
                form.RefreshView(); Application.DoEvents();
                Check(Convert.ToString(grid.CurrentRow.Tag) == selected, "edited row selection jumped");
                Check(Convert.ToString(grid.Rows[grid.FirstDisplayedScrollingRowIndex].Tag) == top, "refresh jumped to top of list");
                Check(grid.SortedColumn.HeaderText == name.HeaderText && grid.SortOrder == SortOrder.Ascending, "text sorting was discarded");
                var search = (TextBox)typeof(MainForm).GetField("search", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                var command = typeof(MainForm).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance);
                command.Invoke(form, new object[]{new Message(), Keys.Control | Keys.F});
                Check(search.Focused, "Ctrl+F did not focus search");
                search.Text = "Заказчик 15"; Application.DoEvents();
                Check(grid.Rows.Count == 1, "search did not filter list");
                command.Invoke(form, new object[]{new Message(), Keys.Escape}); Application.DoEvents();
                Check(search.Text == "" && grid.Rows.Count == 40 && Convert.ToString(grid.CurrentRow.Tag) == selected, "Esc did not clear only the search and preserve selection");
                Check(original.SequenceEqual(File.ReadAllBytes(store.DataPath)), "list navigation changed accounting data");
            }
        }
        public static void Run(string root)
        {
            CheckListPosition(root);
            var store = new Store(Path.Combine(root, Guid.NewGuid().ToString("N")));
            Add(store, "settings", "name", "Тестовый ИП", "currency", "BYN");
            Record party = Add(store, "party", "name", "Длинное название тестового заказчика для изменения ширины", "role", "client");
            Add(store, "act", "number", "RESIZE-500", "date", "2026-11-01", "partyId", party.Id, "amount", "100", "currency", "BYN", "direction", "income", "status", "draft");
            byte[] original = File.ReadAllBytes(store.DataPath); int savedWidth = 0;
            using (var form = new MainForm(store, false))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.Size = new Size(1280, 800); form.Show();
                form.Navigate("act"); form.ResetFilters(); Application.DoEvents();
                var grid = (DataGridView)typeof(MainForm).GetField("grid", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                var column = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.HeaderText == "Контрагент");
                string exported = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(form.BuildExportTable());
                Drag(grid, column, 260); int desired = column.Width;
                savedWidth = desired;
                Check(grid.Controls.OfType<HScrollBar>().Any(bar => bar.Visible), "expanded list has no horizontal scrollbar");
                form.RefreshView(); Application.DoEvents();
                Check(grid.Columns.Cast<DataGridViewColumn>().Single(c => c.HeaderText == "Контрагент").Width == desired, "refresh discarded width");
                form.Navigate("invoice"); form.ResetFilters(); Application.DoEvents();
                Check(grid.Columns.Cast<DataGridViewColumn>().Single(c => c.HeaderText == "Контрагент").Width != desired, "act width leaked into another section");
                form.Navigate("act"); form.ResetFilters(); Application.DoEvents();
                form.Width -= 100; Application.DoEvents();
                Check(grid.Columns.Cast<DataGridViewColumn>().Single(c => c.HeaderText == "Контрагент").Width == desired, "navigation or window resize discarded width");
                Check(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(form.BuildExportTable()) == exported, "resizing changed exported data");
                Check(original.SequenceEqual(File.ReadAllBytes(store.DataPath)), "resizing changed ledger");
            }
            using (var reopened = new MainForm(new Store(store.DataDir), false))
            {
                reopened.ShowInTaskbar = false; reopened.Opacity = 0; reopened.Size = new Size(1280, 800); reopened.Show();
                reopened.Navigate("act"); reopened.ResetFilters(); Application.DoEvents();
                var grid = (ResizableDataGridView)typeof(MainForm).GetField("grid", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(reopened);
                Check(grid.Columns.Cast<DataGridViewColumn>().Single(c => c.HeaderText == "Контрагент").Width == savedWidth, "reopening lost saved width");
                grid.ResetColumnWidths(); Application.DoEvents();
                Check(LedgerGridLayout.Fits(grid) && !grid.HasSavedColumnWidths, "reset did not restore automatic fitting");
                Check(ColumnLayoutPreferences.Load(ColumnLayoutPreferences.PathFor(store.DataDir, "main-list")).Count == 0, "reset was not persisted");
            }
            foreach (DataGridViewAutoSizeColumnsMode mode in new[]{DataGridViewAutoSizeColumnsMode.Fill, DataGridViewAutoSizeColumnsMode.DisplayedCells})
            using (var form = new Form { Size = new Size(700, 400), ShowInTaskbar = false, Opacity = 0 })
            using (var grid = new ResizableDataGridView { Dock = DockStyle.Fill, RowHeadersVisible = false, AllowUserToAddRows = false, AutoSizeColumnsMode = mode })
            {
                grid.Columns.Add("first", "Первый"); grid.Columns.Add("last", "Последний"); grid.Rows.Add("Текст", "Текст");
                form.Controls.Add(grid); form.Show(); Application.DoEvents();
                if (mode == DataGridViewAutoSizeColumnsMode.Fill) Drag(grid, grid.Columns["last"], -100);
                Drag(grid, grid.Columns["last"], 80); int desired = grid.Columns["last"].Width;
                form.Width += 100; Application.DoEvents();
                Check(grid.Columns["last"].Width == desired, mode + ": automatic sizing overrode user width");
                grid.Columns.Clear(); grid.Columns.Add("first", "Первый"); grid.Columns.Add("last", "Последний"); grid.Rows.Add("Текст", "Текст");
                Check(grid.Columns["last"].Width == desired, mode + ": rebuilt dialog columns lost width");
                typeof(ResizableDataGridView).GetMethod("OnColumnDividerDoubleClick", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(grid, new object[]{new DataGridViewColumnDividerDoubleClickEventArgs(1, new HandledMouseEventArgs(MouseButtons.Left, 2, 20, 20, 0))});
                Check(grid.Columns["last"].Width < desired, "double-click did not fit column to content");
            }
            Console.WriteLine("PASS: native divider gestures, horizontal scrolling, refresh and section isolation, editable/automatic grids, double-click and unchanged ledger/export.");
        }
    }
}
