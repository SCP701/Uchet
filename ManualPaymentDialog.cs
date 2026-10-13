using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
namespace WorkLedger
{
    public sealed class ManualPaymentDialog : Form
    {
        private readonly Store store;
        private readonly string actId,operationId=Guid.NewGuid().ToString("N");
        private readonly ManualPaymentView view;
        private readonly Icon applicationIcon;
        private readonly ComboBox source=new ComboBox(),method=new ComboBox();
        private readonly DateTimePicker date=new DateTimePicker();
        private readonly NumericUpDown amount=new NumericUpDown();
        private readonly TextBox number=new TextBox(),notes=new TextBox();
        private readonly Label information=new Label(),error=new Label();
        private readonly Button confirm=new UiButton();
        public Record Payment { get; private set; }
        public ManualPaymentDialog(Store data,string selectedAct)
        {
            store=data;actId=selectedAct;view=store.GetManualPaymentView(actId);
            Text="Подтвердить оплату";Size=new Size(760,620);MinimumSize=new Size(700,560);StartPosition=FormStartPosition.CenterParent;
            Font=new Font("Segoe UI",10);BackColor=UiStyle.Background;ForeColor=UiStyle.Ink;
            applicationIcon=MainForm.LoadApplicationIcon();if(applicationIcon!=null)Icon=applicationIcon;
            var top=new Panel{Dock=DockStyle.Top,Height=94,Padding=new Padding(20,12,20,0)};
            top.Controls.Add(new Label{Text="Подтвердить оплату",Font=new Font("Segoe UI",18,FontStyle.Bold),Dock=DockStyle.Top,Height=36});
            var heading=new Label{Text="Акт № "+view.Balance.Act.Get("number")+" · "+view.Balance.Party.Get("name")+"\r\nОсталось: "+view.Balance.Due.ToString("N2")+" "+view.Balance.Act.Get("currency"),Dock=DockStyle.Bottom,Height=48,AutoEllipsis=true};top.Controls.Add(heading);
            source.Name="paymentSource";source.AccessibleName="Новый или существующий платёж";source.DropDownStyle=ComboBoxStyle.DropDownList;
            source.Items.Add("Новая оплата — внести платёж");
            foreach(var payment in view.Payments)source.Items.Add(ServiceReminderLedger.Date(payment.Get("date")).ToString("dd.MM.yyyy")+" · "+view.Available[payment.Id].ToString("N2")+" "+payment.Get("currency")+" · "+(payment.Get("number")==""?"платёж без номера":"№ "+payment.Get("number")));
            date.Name="paymentDate";date.Format=DateTimePickerFormat.Custom;date.CustomFormat="dd.MM.yyyy";date.MaxDate=MonthlyServiceRuntime.Today;date.Value=MonthlyServiceRuntime.Today;
            amount.Name="paymentAmount";amount.DecimalPlaces=2;amount.ThousandsSeparator=true;amount.Minimum=0.01m;amount.Maximum=1000000000000m;
            method.Name="paymentMethod";method.DropDownStyle=ComboBoxStyle.DropDownList;method.Items.AddRange(new object[]{"Банк","Наличные","Другой способ"});method.SelectedIndex=0;
            number.Name="paymentNumber";number.MaxLength=200;notes.Name="paymentNotes";notes.Multiline=true;notes.ScrollBars=ScrollBars.Vertical;notes.MaxLength=4000;
            var fields=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(20,6,20,6)};
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,142));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Row(fields,"Платёж",source,46);Row(fields,"Дата оплаты",date,40);Row(fields,"Сумма",amount,40);Row(fields,"Способ оплаты",method,40);Row(fields,"Номер платежа",number,40);Row(fields,"Комментарий",notes,68);
            var scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true};scroll.Controls.Add(fields);
            information.Name="paymentInfo";information.Dock=DockStyle.Bottom;information.Height=70;information.Padding=new Padding(20,8,20,0);information.ForeColor=UiStyle.Muted;
            error.Name="paymentError";error.Dock=DockStyle.Bottom;error.Height=42;error.Padding=new Padding(20,0,20,0);error.ForeColor=Color.Firebrick;
            var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=54,Padding=new Padding(20,7,20,8),FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
            var cancel=new UiButton{Text="Отмена",AutoSize=true,MinimumSize=new Size(100,36),DialogResult=DialogResult.Cancel};
            confirm.Name="confirmPayment";confirm.Text="Подтвердить оплату";confirm.AutoSize=true;confirm.MinimumSize=new Size(190,36);((UiButton)confirm).Primary=true;
            footer.Controls.Add(cancel);footer.Controls.Add(confirm);CancelButton=cancel;AcceptButton=confirm;
            Controls.Add(scroll);Controls.Add(information);Controls.Add(error);Controls.Add(footer);Controls.Add(top);
            source.SelectedIndexChanged+=delegate{SelectSource();};confirm.Click+=delegate{SaveConfirmation();};
            source.SelectedIndex=view.Payments.Count>0?1:0;UiStyle.ApplyTo(this);
        }
        private static void Row(TableLayoutPanel table,string text,Control input,int height)
        {
            int row=table.RowCount++;table.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            table.Controls.Add(new Label{Text=text,AutoSize=true,Margin=new Padding(0,8,8,0)},0,row);
            input.Dock=DockStyle.Fill;input.Margin=new Padding(0,4,0,4);input.AccessibleName=text;table.Controls.Add(input,1,row);
        }
        private void SelectSource()
        {
            bool existing=source.SelectedIndex>0;Record payment=existing?view.Payments[source.SelectedIndex-1]:null;
            date.Enabled=method.Enabled=number.Enabled=notes.Enabled=!existing;
            amount.Enabled=!existing||view.Balance.Act.Get("status")=="signed";
            if(existing)
            {
                date.Value=ServiceReminderLedger.Date(payment.Get("date"));method.SelectedIndex=payment.Get("method")=="bank"?0:payment.Get("method")=="cash"?1:2;
                number.Text=payment.Get("number");notes.Text=payment.Get("notes");
                amount.Value=Math.Max(0.01m,amount.Enabled?Math.Min(view.Available[payment.Id],view.Balance.Due):view.Available[payment.Id]);
                information.Text="Будет использован выбранный платёж. Новый платёж не создаётся."+
                    (view.Balance.Act.Get("status")=="signed"?"\r\nСумма зачтётся в оплату этого акта.":"\r\nС актом свяжется вся свободная сумма. Статус подписи сохраняется.");
            }
            else
            {
                date.Value=MonthlyServiceRuntime.Today;method.SelectedIndex=0;number.Clear();notes.Clear();amount.Value=Math.Max(0.01m,view.Balance.Due);
                information.Text="Укажите фактически "+(view.Balance.Act.Get("direction")=="income"?"полученную":"перечисленную")+" сумму. Она попадёт в список оплат."+
                    "\r\nПри полной оплате напоминания прекратятся. Статус подписи акта сохраняется.";
            }
            confirm.Enabled=view.Balance.Due>0;error.Text=view.Balance.Due<=0?"Оплата этого акта уже учтена.":"";
        }
        internal bool SaveConfirmation()
        {
            if(!confirm.Enabled)return false;
            confirm.Enabled=false;
            try
            {
                string paymentId=source.SelectedIndex>0?view.Payments[source.SelectedIndex-1].Id:null;
                Payment=store.ConfirmActPayment(actId,paymentId,amount.Value,date.Value,method.SelectedIndex==0?"bank":method.SelectedIndex==1?"cash":"other",number.Text,notes.Text,view.Revision,operationId);
                DialogResult=DialogResult.OK;Close();return true;
            }
            catch(Exception e){error.Text=e.Message;confirm.Enabled=true;return false;}
        }
        protected override void Dispose(bool disposing)
        { base.Dispose(disposing);if(disposing&&applicationIcon!=null)applicationIcon.Dispose(); }
    }
}
