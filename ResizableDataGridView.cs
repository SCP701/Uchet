using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace WorkLedger
{
    public class ResizableDataGridView : DataGridView
    {
        private Dictionary<string, Dictionary<string, int>> savedWidths = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        private string layoutContext = "", draggedColumn;
        private bool applyingWidths;
        private string preferencesPath;
        private bool widthsChanged;
        private DataGridViewAutoSizeColumnsMode? originalSizing;
        private readonly Dictionary<string, DataGridViewAutoSizeColumnMode> originalModes = new Dictionary<string, DataGridViewAutoSizeColumnMode>();
        private readonly Dictionary<string, int> originalWidths = new Dictionary<string, int>();
        private readonly ContextMenuStrip headerMenu = new ContextMenuStrip();
        private int menuColumn = -1;
        public event EventHandler ColumnWidthsReset;

        public ResizableDataGridView()
        {
            AllowUserToResizeColumns = true;
            ScrollBars = ScrollBars.Both;
            headerMenu.Items.Add("Подобрать ширину столбца", null, delegate { FitColumn(menuColumn); });
            headerMenu.Items.Add("Сбросить ширину столбцов", null, delegate { ResetColumnWidths(); });
        }

        internal void BindPreferences(string dataDir, string scope)
        {
            string path;
            try { path = ColumnLayoutPreferences.PathFor(dataDir, scope); }
            catch (System.IO.IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            if (preferencesPath == path) return;
            preferencesPath = path; savedWidths = ColumnLayoutPreferences.Load(path);
            if (IsHandleCreated) RestoreColumnWidths();
        }

        internal void SetLayoutContext(string value) { layoutContext = value ?? ""; }
        internal bool HasSavedColumnWidths { get { return savedWidths.ContainsKey(layoutContext); } }
        private static string ColumnKey(DataGridViewColumn column)
        { return column.Name + "\n" + column.HeaderText; }

        private void RememberWidth(DataGridViewColumn column)
        {
            Dictionary<string, int> widths;
            if (!savedWidths.TryGetValue(layoutContext, out widths))
                savedWidths.Add(layoutContext, widths = new Dictionary<string, int>(StringComparer.Ordinal));
            widths[ColumnKey(column)] = column.Width;
            widthsChanged = true;
        }

        // Freeze the current sizes before the native divider gesture. Fill and
        // DisplayedCells otherwise keep controlling a manually resized column.
        private void FreezeAutomaticWidths()
        {
            var columns = Columns.Cast<DataGridViewColumn>().ToArray();
            var widths = columns.Select(column => column.Width).ToArray();
            if (!originalSizing.HasValue)
            {
                originalSizing = AutoSizeColumnsMode;
                foreach (var column in columns)
                {
                    originalModes[ColumnKey(column)] = column.AutoSizeMode;
                    originalWidths[ColumnKey(column)] = column.Width;
                }
            }
            applyingWidths = true;
            try
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
                for (int i = 0; i < columns.Length; i++)
                {
                    columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    columns[i].Width = widths[i];
                }
            }
            finally { applyingWidths = false; }
        }

        internal void RestoreColumnWidths()
        {
            Dictionary<string, int> widths;
            if (!savedWidths.TryGetValue(layoutContext, out widths)) return;
            FreezeAutomaticWidths();
            applyingWidths = true;
            try
            {
                foreach (DataGridViewColumn column in Columns)
                {
                    int width;
                    if (widths.TryGetValue(ColumnKey(column), out width)) column.Width = Math.Max(column.MinimumWidth, width);
                }
            }
            finally { applyingWidths = false; }
        }

        protected override void OnColumnAdded(DataGridViewColumnEventArgs e)
        {
            e.Column.Resizable = DataGridViewTriState.True;
            if (String.IsNullOrEmpty(e.Column.HeaderCell.ToolTipText))
                e.Column.HeaderCell.ToolTipText = "Перетащите границу, чтобы изменить ширину. Двойной щелчок — по содержимому. Правый щелчок — сброс размеров.";
            base.OnColumnAdded(e);
            // Some dialogs rebuild columns when the list is refreshed.
            if (savedWidths == null || !originalSizing.HasValue) return;
            Dictionary<string, int> widths; int width;
            if (savedWidths.TryGetValue(layoutContext, out widths) && widths.TryGetValue(ColumnKey(e.Column), out width))
            {
                e.Column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                e.Column.Width = Math.Max(e.Column.MinimumWidth, width);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            draggedColumn = null;
            if (e.Button == MouseButtons.Left && ColumnHeadersVisible && e.Y >= 0 && e.Y < ColumnHeadersHeight)
            {
                foreach (DataGridViewColumn column in Columns.Cast<DataGridViewColumn>().Where(column => column.Visible))
                {
                    var rectangle = GetColumnDisplayRectangle(column.Index, false);
                    if (rectangle.Width <= 0 || rectangle.Right < 0 || Math.Abs(e.X - rectangle.Right) > 4) continue;
                    FreezeAutomaticWidths();
                    draggedColumn = ColumnKey(column);
                    break;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnColumnWidthChanged(DataGridViewColumnEventArgs e)
        {
            if (!applyingWidths && draggedColumn != null && ColumnKey(e.Column) == draggedColumn) RememberWidth(e.Column);
            base.OnColumnWidthChanged(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            draggedColumn = null;
            SaveWidths();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) draggedColumn = null;
            base.OnMouseCaptureChanged(e);
            if (!Capture) SaveWidths();
        }

        private void SaveWidths()
        {
            if (!widthsChanged) return;
            ColumnLayoutPreferences.Save(preferencesPath, savedWidths); widthsChanged = false;
        }

        internal void ResetColumnWidths()
        {
            savedWidths.Remove(layoutContext); widthsChanged = true;
            applyingWidths = true;
            try
            {
                foreach (DataGridViewColumn column in Columns)
                {
                    DataGridViewAutoSizeColumnMode mode; int width;
                    if (originalModes.TryGetValue(ColumnKey(column), out mode)) column.AutoSizeMode = mode;
                    if (originalWidths.TryGetValue(ColumnKey(column), out width)) column.Width = Math.Max(column.MinimumWidth, width);
                }
                if (originalSizing.HasValue) AutoSizeColumnsMode = originalSizing.Value;
            }
            finally { applyingWidths = false; }
            SaveWidths();
            if (ColumnWidthsReset != null) ColumnWidthsReset(this, EventArgs.Empty);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RestoreColumnWidths();
        }

        private void FitColumn(int index)
        {
            if (index < 0 || index >= Columns.Count) return;
            FreezeAutomaticWidths();
            AutoResizeColumn(index, DataGridViewAutoSizeColumnMode.DisplayedCells);
            RememberWidth(Columns[index]); SaveWidths();
        }

        protected override void OnColumnHeaderMouseClick(DataGridViewCellMouseEventArgs e)
        {
            base.OnColumnHeaderMouseClick(e);
            if (e.Button != MouseButtons.Right || e.ColumnIndex < 0) return;
            menuColumn = e.ColumnIndex;
            var rectangle = GetColumnDisplayRectangle(e.ColumnIndex, false);
            headerMenu.Show(this, rectangle.Left + e.X, rectangle.Top + e.Y);
        }

        protected override void OnColumnDividerDoubleClick(DataGridViewColumnDividerDoubleClickEventArgs e)
        {
            if (e.ColumnIndex >= 0 && e.ColumnIndex < Columns.Count)
            {
                FitColumn(e.ColumnIndex);
                e.Handled = true;
            }
            base.OnColumnDividerDoubleClick(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { SaveWidths(); headerMenu.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
