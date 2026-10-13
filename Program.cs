using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
namespace WorkLedger
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkLedger");
            int di = Array.IndexOf(args, "--data-dir");
            if (di >= 0 && di + 1 < args.Length) dataDir = Path.GetFullPath(args[di + 1]);
            dataDir = Path.GetFullPath(dataDir);
            if (dataDir.Length > Path.GetPathRoot(dataDir).Length) dataDir = dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            try
            {
                if (args.Contains("--send-service-reminders")) return ServiceReminderRuntime.RunBackground(dataDir);
                if (args.Contains("--generate-monthly-acts")) return MonthlyServiceRuntime.RunBackground(dataDir);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (args.Contains("--self-test"))
                {
                    Directory.CreateDirectory(dataDir);
                    if (CoreTests.Run(Path.Combine(dataDir, "core-tests")) != 0) throw new Exception("Не прошли проверки расчётов.");
                    AppTests.Run(Path.Combine(dataDir, "app-tests"));
                    ManualPaymentTests.Run(Path.Combine(dataDir, "manual-payment-tests"));
                    AttachmentSafetyTests.Run(Path.Combine(dataDir, "attachment-safety-tests"));
                    ExportRegressionTests.Run(Path.Combine(dataDir, "export-regression-tests"));
                    EditorTests.Run(Path.Combine(dataDir, "editor-tests"));
                    ReconciliationImportTests.Run(Path.Combine(dataDir, "reconciliation-tests"));
                    ActCardEditorTests.Run(Path.Combine(dataDir, "act-card-editor-tests"));
                    ReferencePickerTests.Run(Path.Combine(dataDir, "reference-picker-tests"));
                    if (HourlyActCoreTests.Run(Path.Combine(dataDir, "hourly-core-tests")) != 0) throw new Exception("Не прошли проверки почасовых актов.");
                    HourlyActComposerTests.Run(Path.Combine(dataDir, "hourly-ui-tests"));
                    HourlyActDocumentTests.Run(Path.Combine(dataDir, "hourly-document-tests"));
                    ActItemsEditorTests.Run(Path.Combine(dataDir, "act-items-tests"));
                    ActDocumentTests.Run(Path.Combine(dataDir, "act-document-tests"));
                    ActHtmlDocumentTests.Run(Path.Combine(dataDir, "act-html-tests"));
                    PaymentAccountingTests.Run(Path.Combine(dataDir, "payment-scope-core-tests"));
                    GrayPaymentEditorTests.Run(Path.Combine(dataDir, "gray-payment-editor-tests"));
                    GrayPaymentIntegrationTests.Run(Path.Combine(dataDir, "gray-payment-integration-tests"));
                    QuarterlyTaxesTests.Run(Path.Combine(dataDir, "quarter-tax-core-tests"));
                    QuarterlyTaxesPageTests.Run(Path.Combine(dataDir, "quarter-tax-page-tests"));
                    QuarterlyTaxesIntegrationTests.Run(Path.Combine(dataDir, "quarter-tax-integration-tests"));
                    UiPreferencesTests.Run(Path.Combine(dataDir, "ui-preferences-tests"));
                    InterfaceUiTests.Run(Path.Combine(dataDir, "interface-ui-tests"));
                    ListLayoutTests.Run(Path.Combine(dataDir, "list-layout-tests"));
                    UiStyleTests.Run();
                    CompactToolbarTests.Run(Path.Combine(dataDir, "compact-toolbar-tests"));
                    FilterTests.Run(Path.Combine(dataDir, "filter-tests"));
                    if (ExternalActCoreTests.Run(Path.Combine(dataDir, "external-act-core-tests")) != 0) throw new Exception("Не прошли проверки актов вне.");
                    ExternalActIntegrationTests.Run(Path.Combine(dataDir, "external-act-integration-tests"));
                    if (MonthlyActCoreTests.Run(Path.Combine(dataDir, "monthly-core-tests")) != 0) throw new Exception("Не прошли проверки ежемесячных актов.");
                    MonthlyServiceEditorTests.Run(Path.Combine(dataDir, "monthly-editor-tests"));
                    MonthlyServiceSchedulerTests.Run(Path.Combine(dataDir, "monthly-scheduler-tests"));
                    ServiceReminderTests.Run(Path.Combine(dataDir,"service-reminder-tests"));
                    MonthlyServiceIntegrationTests.Run(Path.Combine(dataDir, "monthly-integration-tests"));
                    UpdateProtocolTests.Run(Path.Combine(dataDir, "update-protocol-tests"));
                    UpdateManagerTests.Run(Path.Combine(dataDir, "update-manager-tests"));
                    File.WriteAllText(Path.Combine(dataDir, "test-result.txt"), "PASS: accounting, import/export, backup/restore, UI, hourly acts and numbering, act XLSX/print, personal interface settings and draft preservation, monthly service acts and background scheduler, external acts, tax selection and transfers, quarterly taxes, grey and white payment parts, SR total, compact command menus, service payment email reminders, unchecked review lists and responsive tables, manual confirmation of full and partial payments, signed update protocol and update settings", Encoding.UTF8);
                    return 0;
                }
                UpdateJob startupJob = null;
                int updateIndex = Array.IndexOf(args, "--update-job");
                if (updateIndex >= 0)
                {
                    if (updateIndex + 1 >= args.Length) throw new InvalidDataException("Не указаны параметры обновления.");
                    startupJob = UpdateManager.ValidateStartupJob(Path.GetFullPath(args[updateIndex + 1]), dataDir);
                }
                else if (UpdateManager.IsActiveMarker(Path.GetDirectoryName(Application.ExecutablePath)))
                { MessageBox.Show("Сейчас устанавливается обновление. Программа откроется автоматически после завершения.", "Обновление"); return 0; }
                bool first;
                using (var mutex = new Mutex(true, MonthlyServiceRuntime.MutexName(dataDir), out first))
                {
                    if (!first) { MessageBox.Show("Приложение с этой базой уже открыто.", "Акты и оплаты"); return 0; }
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { MessageBox.Show(e.Exception.Message, "Не удалось выполнить действие", MessageBoxButtons.OK, MessageBoxIcon.Warning); };
                    using (var form = new MainForm(new Store(dataDir), true))
                    {
                        form.Shown += delegate
                        {
                            if (startupJob != null)
                            {
                                try { UpdateProtocol.WriteStartupAck(startupJob.SessionDir, startupJob.Nonce, startupJob.Version); }
                                catch { Environment.Exit(2); }
                            }
                            form.StartUpdateCheck();
                        };
                        Application.Run(form);
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { Directory.CreateDirectory(dataDir); File.WriteAllText(Path.Combine(dataDir, "error.txt"), ex.ToString(), Encoding.UTF8); } catch { }
                if (!args.Contains("--self-test") && !args.Contains("--generate-monthly-acts") && !args.Contains("--send-service-reminders")) MessageBox.Show("Не удалось открыть приложение.\n" + ex.Message + "\n\nДанные: " + dataDir, "Акты и оплаты", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}




