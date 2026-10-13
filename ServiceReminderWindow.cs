using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
namespace WorkLedger
{
    public sealed class ServiceReminderWindow : Form
    {
        private readonly string directory;
        private readonly Store ledger;
        private readonly Button confirmPayment=new UiButton{Text="Подтвердить оплату",AutoSize=true};
        private readonly Action<string> openAct;
        private readonly Icon applicationIcon;
        private ServiceMailSettings settings;
        private readonly TabControl tabs=new TabControl();
        private readonly DataGridView plansGrid=new DataGridView(),historyGrid=new DataGridView();
        private readonly Label summary=new Label(),mailInfo=new Label();
        private readonly CheckBox enabled=new CheckBox{Text="Отправлять напоминания об оплате автоматически",AutoSize=true};
        private readonly CheckBox tls=new CheckBox{Text="Защищённое соединение (STARTTLS)",AutoSize=true};
        private readonly DateTimePicker start=new DateTimePicker{Format=DateTimePickerFormat.Custom,CustomFormat="MMMM yyyy",ShowUpDown=true};
        private readonly TextBox host=new TextBox(),login=new TextBox(),address=new TextBox(),senderName=new TextBox(),password=new TextBox{UseSystemPasswordChar=true};
        private readonly NumericUpDown port=new NumericUpDown{Minimum=1,Maximum=65535,Value=587};
        private readonly TextBox subject=new TextBox(),body=new TextBox{Multiline=true,ScrollBars=ScrollBars.Vertical};
        private readonly Button save=new UiButton{Text="Сохранить",AutoSize=true,Primary=true};
        private readonly Button send=new UiButton{Text="Проверить и отправить",AutoSize=true};
        private List<ServiceMailPlan> plans=new List<ServiceMailPlan>();
        public ServiceReminderWindow(Store store,Action<string> navigate)
        {
            ledger=store;directory=store.DataDir;openAct=navigate;settings=ServiceMailSettings.Load(directory);
            Text="Обслуживание — напоминания об оплате";Width=1080;Height=750;MinimumSize=new Size(980,660);
            StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);
            BackColor=UiStyle.Background;ForeColor=UiStyle.Ink;
            applicationIcon=MainForm.LoadApplicationIcon();if(applicationIcon!=null)Icon=applicationIcon;
            tabs.Dock=DockStyle.Fill;
            var listPage=new TabPage("Оплата обслуживания"){BackColor=UiStyle.Surface};var mailPage=new TabPage("Настройки почты"){BackColor=UiStyle.Surface};var historyPage=new TabPage("История писем"){BackColor=UiStyle.Surface};
            tabs.TabPages.AddRange(new[]{listPage,mailPage,historyPage});Controls.Add(tabs);
            summary.Dock=DockStyle.Top;summary.Height=84;summary.Padding=new Padding(10);
            listPage.Controls.Add(plansGrid);listPage.Controls.Add(summary);
            var commands=Strip();
            commands.Controls.Add(MakeButton("Обновить",RefreshLists));
            confirmPayment.Name="confirmServicePayment";confirmPayment.Click+=delegate{ConfirmSelectedPayment();};commands.Controls.Add(confirmPayment);
            send.Click+=delegate{SendDue();};commands.Controls.Add(send);
            commands.Controls.Add(PaymentActionsMenu());
            plansGrid.SelectionChanged+=delegate{var plan=SelectedPlan();confirmPayment.Enabled=plan!=null&&plan.Balance.Due>0;};
            commands.Dock=DockStyle.Bottom;listPage.Controls.Add(commands);
            ConfigureGrid(plansGrid);ConfigureGrid(historyGrid);
            historyPage.Controls.Add(historyGrid);
            var historyCommands=Strip();historyCommands.Dock=DockStyle.Bottom;
            historyCommands.Controls.Add(MakeButton("Обновить",RefreshLists));
            historyCommands.Controls.Add(MakeButton("Подробности",ShowHistory));
            historyCommands.Controls.Add(MakeButton("Разрешить повтор",Retry));
            historyPage.Controls.Add(historyCommands);
            var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(12)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,210));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            var scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true};scroll.Controls.Add(layout);mailPage.Controls.Add(scroll);
            AddRow(layout,"Рассылка",enabled,38);AddRow(layout,"Первый месяц",start,38);
            AddRow(layout,"Сервер исходящей почты",host,38);AddRow(layout,"Порт",port,38);AddRow(layout,"Соединение",tls,38);
            AddRow(layout,"Email отправителя",address,38);AddRow(layout,"Имя отправителя",senderName,38);
            AddRow(layout,"Логин почты",login,38);AddRow(layout,"Пароль приложения",password,38);
            mailInfo.AutoSize=true;mailInfo.MaximumSize=new Size(710,0);mailInfo.ForeColor=UiStyle.Muted;
            AddRow(layout,"",mailInfo,60);AddRow(layout,"Тема письма",subject,38);AddRow(layout,"Текст письма",body,180);
            var fields=new Label{AutoSize=true,MaximumSize=new Size(720,0),ForeColor=UiStyle.Muted,
                Text="Поля: {контрагент}, {номер}, {дата}, {период}, {сумма}, {оплачено}, {остаток}, {валюта}, {организация}, {реквизиты}.\r\nОбычно используется порт 587 и пароль приложения вашей почты. Компьютер должен быть включён; без интернета отправка не выполняется."};
            AddRow(layout,"",fields,85);
            var test=MakeButton("Отправить тестовое письмо себе",SendTest);AddRow(layout,"Проверка почты",test,40);
            var footer=Strip();footer.Dock=DockStyle.Bottom;footer.Height=50;footer.FlowDirection=FlowDirection.RightToLeft;
            var close=MakeButton("Закрыть",Close);footer.Controls.Add(close);footer.Controls.Add(save);Controls.Add(footer);
            save.Click+=delegate{SaveSettings();};
            enabled.Checked=settings.Enabled;host.Text=settings.Host;port.Value=settings.Port;tls.Checked=settings.Tls;
            login.Text=settings.Login;address.Text=settings.FromAddress;senderName.Text=settings.FromName;subject.Text=settings.Subject;body.Text=settings.Body;
            start.Value=DateTime.ParseExact(settings.StartMonth+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture);
            Record own=store.Of("settings").FirstOrDefault();
            if(address.Text==""&&own!=null)address.Text=own.Get("email");
            if(senderName.Text==""&&own!=null)senderName.Text=own.Get("name");
            mailInfo.Text=String.IsNullOrEmpty(settings.ProtectedPassword)?"Укажите пароль приложения почтового ящика.":"Пароль сохранён. Оставьте поле пустым, чтобы сохранить его, или введите новый.";
            UiStyle.ApplyTo(this);RefreshLists();
            Shown+=delegate{if(plansGrid.Rows.Count>0){plansGrid.CurrentCell=plansGrid.Rows[0].Cells[0];var plan=SelectedPlan();confirmPayment.Enabled=plan!=null&&plan.Balance.Due>0;}};
        }
        private Button PaymentActionsMenu()
        {
            var menu=new ContextMenuStrip{Name="servicePaymentActions",ShowImageMargin=false,Font=Font};
            var preview=new ToolStripMenuItem("Предпросмотр письма"){Name="preview-service-mail"};preview.Click+=delegate{Preview();};
            var act=new ToolStripMenuItem("Открыть акт"){Name="open-service-act"};act.Click+=delegate{OpenSelectedAct();};
            menu.Items.Add(preview);menu.Items.Add(act);
            menu.Opening+=delegate{preview.Enabled=SelectedPlan()!=null;act.Enabled=SelectedPlan()!=null;};
            Button button=null;button=MakeButton("Действия ▾",delegate{menu.Show(button,new Point(0,button.Height));});
            button.Name="servicePaymentActionsButton";button.ContextMenuStrip=menu;button.AccessibleName="Действия с оплатой обслуживания";button.AccessibleRole=AccessibleRole.ButtonMenu;
            button.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Down||e.KeyCode==Keys.F4){menu.Show(button,new Point(0,button.Height));e.Handled=true;e.SuppressKeyPress=true;}};
            button.Disposed+=delegate{menu.Dispose();};return button;
        }
        private static FlowLayoutPanel Strip(){return new FlowLayoutPanel{Height=46,Padding=new Padding(8,5,8,4),WrapContents=false,BackColor=UiStyle.Surface};}
        private static Button MakeButton(string text,Action action){var b=new UiButton{Text=text,AutoSize=true,MinimumSize=new Size(0,34),Padding=new Padding(9,0,9,0),Margin=new Padding(0,0,7,0)};b.Click+=delegate{action();};return b;}
        private static void ConfigureGrid(DataGridView grid){grid.Dock=DockStyle.Fill;grid.ReadOnly=true;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.RowHeadersVisible=false;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;grid.MultiSelect=false;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.DisplayedCells;UiStyle.ApplyGrid(grid);}
        private static void AddRow(TableLayoutPanel table,string text,Control control,int height)
        {
            int row=table.RowCount++;table.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            table.Controls.Add(new Label{Text=text,AutoSize=true,Margin=new Padding(0,7,8,0)},0,row);
            control.Dock=DockStyle.Fill;control.Margin=new Padding(0,3,0,4);table.Controls.Add(control,1,row);
        }
        private ServiceMailSettings Draft()
        {
            return new ServiceMailSettings{Enabled=enabled.Checked,StartMonth=start.Value.ToString("yyyy-MM"),Host=host.Text.Trim(),Port=(int)port.Value,Tls=tls.Checked,
                Login=login.Text.Trim(),FromAddress=address.Text.Trim(),FromName=senderName.Text.Trim(),ProtectedPassword=settings.ProtectedPassword,Subject=subject.Text,Body=body.Text};
        }
        private void SaveSettings()
        {
            try {
                var saved=Draft();saved.Save(directory,password.Text);settings=saved;password.Clear();
                mailInfo.Text="Пароль сохранён для этого пользователя Windows.";
                string result=ServiceReminderScheduler.Ensure(Application.ExecutablePath,directory,settings.Enabled);
                RefreshLists();MessageBox.Show(this,"Настройки сохранены.\r\n"+result,"Рассылка обслуживания");
            }catch(Exception e){Warn(e.Message);}
        }
        private void RefreshLists()
        {
            try {
                settings=ServiceMailSettings.Load(directory);var history=ServiceMailHistory.Load(directory);
                plans=ServiceReminderRuntime.Plans(new Store(directory).Records,settings,history,DateTime.UtcNow);
                plansGrid.Columns.Clear();
                foreach(string label in new[]{"Акт","Заказчик","Осталось","Состояние","Следующее письмо","Email"})plansGrid.Columns.Add(label,label);
                foreach(var plan in plans) {
                    var b=plan.Balance;int row=plansGrid.Rows.Add(b.Act.Get("number"),b.Party.Get("name"),
                        b.Due.ToString("N2")+" "+b.Act.Get("currency"),plan.Status,
                        b.Due<=0||plan.Status=="Два письма отправлены"?"—":(plan.Stage==1?"Первое ":"Повторное ")+plan.DueDate.ToString(plan.DueDate.TimeOfDay==TimeSpan.Zero?"dd.MM.yyyy":"dd.MM.yyyy HH:mm"),plan.Recipient);
                    plansGrid.Rows[row].Tag=plan;
                }
                historyGrid.Columns.Clear();
                foreach(string label in new[]{"Акт","Email","Письмо","Попытка","Отправлено","Состояние"})historyGrid.Columns.Add(label,label);
                foreach(var entry in history.Entries.AsEnumerable().Reverse()) {
                    int row=historyGrid.Rows.Add(entry.ActNumber,entry.Recipient,entry.Stage==1?"Первое":"Повторное",UtcText(entry.AttemptUtc),UtcText(entry.SentUtc),StateLabel(entry.State));
                    historyGrid.Rows[row].Tag=entry;
                }
                summary.Text=(settings.Enabled?"Автоматическая рассылка включена.":"Автоматическая рассылка выключена.")+
                    "\r\nПервое письмо — через 7 дней после акта; второе — через 3 дня после первого, если остаётся неоплаченная сумма. Всего два письма."+
                    "\r\nОплаты учитываются по выписке и ручному подтверждению. Спорную банковскую оплату можно привязать к акту.";
                var selected=SelectedPlan();confirmPayment.Enabled=selected!=null&&selected.Balance.Due>0;send.Enabled=true;
            }catch(Exception e){summary.Text=e.Message;send.Enabled=false;confirmPayment.Enabled=false;}
        }
        private static string DateText(string text){return ServiceReminderLedger.Date(text).ToString("dd.MM.yyyy");}
        private static string UtcText(string text){DateTime date;return DateTime.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out date)?date.ToUniversalTime().AddHours(3).ToString("dd.MM.yyyy HH:mm"):"";}
        private static string StateLabel(string state){return state=="sent"?"Отправлено":state=="sending"?"Отправлялось":state=="failed"?"Ошибка":state=="retry"?"Ожидает подключения":"Не подтверждено";}
        private ServiceMailPlan SelectedPlan(){return plansGrid.CurrentRow==null?null:plansGrid.CurrentRow.Tag as ServiceMailPlan;}
        private void Preview()
        {
            var plan=SelectedPlan();if(plan==null)return;
            ShowText("Предпросмотр письма","Кому: "+plan.Recipient+"\r\nТема: "+plan.Subject+"\r\n\r\n"+plan.Body);
        }
        private void ConfirmSelectedPayment()
        {
            var plan=SelectedPlan();if(plan==null)return;
            try
            {
                ledger.Reload();
                using(var dialog=new ManualPaymentDialog(ledger,plan.Balance.Act.Id))
                    if(dialog.ShowDialog(this)==DialogResult.OK)RefreshLists();
            }catch(Exception e){Warn(e.Message);}
        }
        private void OpenSelectedAct(){var plan=SelectedPlan();if(plan==null||openAct==null)return;Close();openAct(plan.Balance.Act.Id);}
        private void ShowHistory()
        {
            var entry=historyGrid.CurrentRow==null?null:historyGrid.CurrentRow.Tag as ServiceMailEntry;if(entry==null)return;
            ShowText("История отправки","Акт: "+entry.ActNumber+"\r\nКому: "+entry.Recipient+"\r\nСостояние: "+StateLabel(entry.State)+"\r\n"+entry.Error);
        }
        private void Retry()
        {
            var entry=historyGrid.CurrentRow==null?null:historyGrid.CurrentRow.Tag as ServiceMailEntry;if(entry==null)return;
            if(entry.State=="sent"||entry.State=="sending"){Warn("Это письмо нельзя отправить повторно.");return;}
            if(entry.State!="retry"&&MessageBox.Show(this,"Если сервер уже принял письмо, повтор может создать второе письмо. Проверьте почту отправителя. Разрешить новую попытку?","Повтор отправки",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            try{ServiceReminderRuntime.AllowRetry(directory,entry.ActId,entry.Stage);RefreshLists();}catch(Exception e){Warn(e.Message);}
        }
        private void SendDue()
        {
            try {
                settings=ServiceMailSettings.Load(directory);
                if(!settings.Enabled){Warn("Включите рассылку в настройках почты и сохраните их.");return;}
                settings.Validate(true);
                RunAsync(delegate{return "Отправлено писем: "+ServiceReminderRuntime.Process(directory,DateTime.UtcNow,new SmtpServiceMailSender(),false,delegate{return DateTime.UtcNow;})+".";});
            }catch(Exception e){Warn(e.Message);}
        }
        private void SendTest()
        {
            try {
                var testSettings=Draft();testSettings.SetPassword(password.Text);testSettings.Validate(true);
                var plan=new ServiceMailPlan{Recipient=testSettings.FromAddress,Subject="Проверка почты — Акты и оплаты",Body="Настройки почты работают. Это тестовое письмо отправлено из программы «Акты и оплаты»."};
                RunAsync(delegate{new SmtpServiceMailSender().Send(testSettings,plan,Guid.NewGuid().ToString("N"));return "Тестовое письмо отправлено на "+plan.Recipient+".";});
            }catch(Exception e){Warn(e.Message);}
        }
        private void RunAsync(Func<string> action)
        {
            save.Enabled=false;send.Enabled=false;
            ThreadPool.QueueUserWorkItem(delegate {
                string message;
                try{message=action();}catch{message="Отправка не подтверждена. Проверьте настройки почты и соединение.";}
                try{if(!IsDisposed&&IsHandleCreated)BeginInvoke((Action)delegate{save.Enabled=true;send.Enabled=true;RefreshLists();MessageBox.Show(this,message,"Рассылка обслуживания");});}catch(InvalidOperationException){}
            });
        }
        private void ShowText(string caption,string text)
        {
            using(var dialog=new Form{Text=caption,Width=760,Height=600,StartPosition=FormStartPosition.CenterParent,Font=Font}) {
                dialog.Controls.Add(new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Text=text});dialog.ShowDialog(this);
            }
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if(disposing&&applicationIcon!=null)applicationIcon.Dispose();
        }
        private void Warn(string text){MessageBox.Show(this,text,"Рассылка обслуживания",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    }
}
