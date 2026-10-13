using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
namespace WorkLedger
{
    public static class CompactToolbarTests
    {
        private static void Assert(bool condition,string message)
        { if(!condition)throw new InvalidOperationException("Compact toolbar: "+message); }
        private static IEnumerable<Control> Descendants(Control root)
        { yield return root; foreach(Control child in root.Controls)foreach(Control item in Descendants(child))yield return item; }
        private static IEnumerable<ToolStripMenuItem> Items(ToolStripItemCollection items)
        { foreach(ToolStripMenuItem item in items.OfType<ToolStripMenuItem>()){yield return item;foreach(var child in Items(item.DropDownItems))yield return child;} }
        internal static IEnumerable<string> CommandLabels(Control root)
        {
            foreach(Button button in Descendants(root).OfType<Button>())
            {
                yield return button.Text;
                if(button.ContextMenuStrip!=null)foreach(var item in Items(button.ContextMenuStrip.Items))yield return item.Text;
            }
        }
        internal static bool CanReachCommand(Control root,string id)
        {
            return Descendants(root).OfType<Button>().Any(button=>button.Visible&&button.Enabled&&button.ContextMenuStrip!=null
                && Items(button.ContextMenuStrip.Items).Any(item=>item.Name==id&&item.Available&&item.Enabled));
        }
        private static T Field<T>(MainForm form,string name)
        { return (T)typeof(MainForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form); }
        private static ToolStripMenuItem FindCommand(Control root,string name)
        {
            return Descendants(root).OfType<Button>().Where(b=>b.ContextMenuStrip!=null)
                .SelectMany(b=>Items(b.ContextMenuStrip.Items)).Single(i=>i.Name==name);
        }
        private static void RefreshMenu(ContextMenuStrip menu)
        { typeof(ContextMenuStrip).GetMethod("OnOpening",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(menu,new object[]{new CancelEventArgs()}); }
        private static Record Add(Store store,string kind,params string[] values)
        {
            var record=new Record(kind);for(int i=0;i<values.Length;i+=2)record.Set(values[i],values[i+1]);
            store.Upsert(record);return store.Find(record.Id);
        }
        private static void RequireCommands(Control root,IEnumerable<string> labels,string section)
        {
            var available=new HashSet<string>(CommandLabels(root));
            foreach(string label in labels)Assert(available.Contains(label),section+" lost "+label);
        }
        public static void Run(string root)
        {
            string data=Path.Combine(root,"menus-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(data);
            var store=new Store(data);Add(store,"settings","currency","BYN","hourlyRate","35.00");
            var party=Add(store,"party","name","Клиент меню","role","client");
            Add(store,"act","number","МЕНЮ-1","date","2026-10-13","partyId",party.Id,"direction","income","currency","BYN","amount","100.00","status","draft");
            Add(store,"act","number","МЕНЮ-ВНЕ","date","2026-10-13","partyId",party.Id,"direction","income","currency","BYN","amount","25.00","status","draft","accountingScope","external");
            byte[] before=File.ReadAllBytes(store.DataPath);long revision=store.Revision;int sections=0;
            using(var form=new MainForm(store))
            {
                form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
                var routes=UiPreferences.Sections.Where(s=>s.Key!="updates").Select(s=>s.Key).Concat(new[]{"create-external-act"}).ToList();
                foreach(int width in new[]{1120,1460})
                {
                    form.Width=width;Application.DoEvents();
                    foreach(string route in routes)
                    {
                        var previousMenus=Field<FlowLayoutPanel>(form,"actions").Controls.OfType<Button>()
                            .Where(b=>b.ContextMenuStrip!=null).Select(b=>b.ContextMenuStrip).ToList();
                        form.Navigate(route);Application.DoEvents();sections++;
                        Assert(previousMenus.All(m=>m.IsDisposed),"navigation left old command menus alive");
                        Assert(CanReachCommand(form,"interface")&&CanReachCommand(form,"settings")&&CanReachCommand(form,"backup")&&CanReachCommand(form,"restore")&&CanReachCommand(form,"updates"),"global commands inaccessible in "+route);
                        Control utility=form.Controls.Find("programUtilities",true).Single();
                        Assert(utility.Controls.OfType<Button>().Count()==1,"global header is cluttered");
                        var panel=Field<FlowLayoutPanel>(form,"actions");
                        if(!panel.Visible)continue;
                        var visible=panel.Controls.Cast<Control>().Where(c=>c.Visible).ToList();
                        Assert(visible.Count<=5,"too many top-level controls in "+route);
                        Assert(!panel.WrapContents,"command strip can wrap in "+route);
                        if(visible.Count>0)
                        {
                            Assert(visible.Max(c=>c.Top)-visible.Min(c=>c.Top)<12,"commands wrap to another row in "+route);
                            Assert(visible.All(c=>c.Left>=0&&c.Right<=panel.ClientSize.Width),"clipped command in "+route+" at width "+width);
                        }
                        if(Catalog.Get(route)!=null&&route!="settings")
                        {
                            RequireCommands(panel,new[]{"+ Добавить","Изменить","В архив / вернуть","Excel / CSV","Импорт"},route);
                            if(Catalog.IsActSection(route))
                                RequireCommands(panel,new[]{"Позиции акта","Зачесть оплату","Создать счёт","Печатная форма","Акт Word","Акт в Excel","Прикрепить файл",route==Catalog.ExternalActSection?"Все акты вне":"Все акты"},route);
                            if(Catalog.IsPaymentSection(route))RequireCommands(panel,new[]{"Зачесть оплату","Прикрепить файл"},route);
                            if(route=="estimate")RequireCommands(panel,new[]{"Создать акт","Печатная форма","Прикрепить файл"},route);
                            if(route=="contract"||route=="invoice")RequireCommands(panel,new[]{"Печатная форма","Прикрепить файл"},route);
                            if(route=="document")RequireCommands(panel,new[]{"Прикрепить файл","Открыть файл"},route);
                        }
                        if(route=="create-act"||route=="create-external-act")
                            RequireCommands(panel,new[]{"Сохранить акт","Предпросмотр","Настроить стоимость часа",route=="create-act"?"Все акты":"Все акты вне"},route);
                        if(route=="reports")RequireCommands(panel,new[]{"Выгрузить Excel","Печать отчёта"},route);
                        if(route=="audit")RequireCommands(panel,new[]{"Выгрузить Excel"},route);
                    }
                }
                form.Navigate("act");Application.DoEvents();
                var actions=Field<FlowLayoutPanel>(form,"actions");var grid=Field<DataGridView>(form,"grid");
                grid.ClearSelection();grid.CurrentCell=null;
                foreach(var menu in actions.Controls.OfType<Button>().Where(b=>b.ContextMenuStrip!=null).Select(b=>b.ContextMenuStrip))RefreshMenu(menu);
                Assert(!FindCommand(actions,"archive").Enabled&&!FindCommand(actions,"act-word").Enabled&&!FindCommand(actions,"attach").Enabled,"selected-record commands enabled without a record");
                Assert(FindCommand(actions,"export").Enabled&&FindCommand(actions,"import").Enabled&&FindCommand(actions,"allocate").Enabled,"general commands incorrectly disabled");
                grid.CurrentCell=grid.Rows[0].Cells[0];grid.Rows[0].Selected=true;
                foreach(var menu in actions.Controls.OfType<Button>().Where(b=>b.ContextMenuStrip!=null).Select(b=>b.ContextMenuStrip))RefreshMenu(menu);
                Assert(FindCommand(actions,"archive").Enabled&&FindCommand(actions,"act-word").Enabled,"selection does not enable document commands");
                Field<TextBox>(form,"search").Text="совсем нет совпадений";Application.DoEvents();Assert(grid.Rows.Count==0,"search fixture failed");
                FindCommand(actions,"all-acts").PerformClick();Application.DoEvents();
                Assert(Field<TextBox>(form,"search").Text==""&&grid.Rows.Count==1,"menu command does not execute the original all-acts behavior");
                var prefs=new UiPreferences{AnimationsEnabled=false};
                foreach(var section in UiPreferences.Sections)prefs.SetVisible(section.Key,section.Key=="taxes");
                form.ApplyInterfacePreferences(prefs);Application.DoEvents();
                Assert(CanReachCommand(form,"interface")&&CanReachCommand(form,"backup"),"hiding sections blocks global menu");
            }
            Assert(store.Revision==revision&&before.SequenceEqual(File.ReadAllBytes(store.DataPath)),"menus changed accounting records");
            File.WriteAllText(Path.Combine(data,"PASS.txt"),"PASS: "+sections+" view/width combinations; one-row toolbar, all commands preserved, global settings after hiding sections, selected-record availability, command execution and menu disposal; ledger unchanged.",Encoding.UTF8);
            Console.WriteLine("PASS: compact command menus across "+sections+" view/width combinations.");
        }
    }
}
