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
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (args.Contains("--self-test"))
                {
                    Directory.CreateDirectory(dataDir);
                    if (CoreTests.Run(Path.Combine(dataDir, "core-tests")) != 0) throw new Exception("Не прошли проверки расчётов.");
                    AppTests.Run(Path.Combine(dataDir, "app-tests"));
                    EditorTests.Run(Path.Combine(dataDir, "editor-tests"));
                    if (HourlyActCoreTests.Run(Path.Combine(dataDir, "hourly-core-tests")) != 0) throw new Exception("Не прошли проверки почасовых актов.");
                    HourlyActComposerTests.Run(Path.Combine(dataDir, "hourly-ui-tests"));
                    HourlyActDocumentTests.Run(Path.Combine(dataDir, "hourly-document-tests"));
                    ActItemsEditorTests.Run(Path.Combine(dataDir, "act-items-tests"));
                    ActDocumentTests.Run(Path.Combine(dataDir, "act-document-tests"));
                    UiPreferencesTests.Run(Path.Combine(dataDir, "ui-preferences-tests"));
                    InterfaceUiTests.Run(Path.Combine(dataDir, "interface-ui-tests"));
                    UiStyleTests.Run();
                    UpdateProtocolTests.Run(Path.Combine(dataDir, "update-protocol-tests"));
                    UpdateManagerTests.Run(Path.Combine(dataDir, "update-manager-tests"));
                    File.WriteAllText(Path.Combine(dataDir, "test-result.txt"), "PASS: accounting, import/export, backup/restore, UI, hourly acts and numbering, act XLSX/print, personal interface settings and draft preservation, signed update protocol and update settings", Encoding.UTF8);
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
                string key;
                using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant()))).Replace("-", "");
                bool first;
                using (var mutex = new Mutex(true, "Local\\WorkLedger-" + key, out first))
                {
                    if (!first) { MessageBox.Show("Приложение с этой базой уже открыто.", "Акты и оплаты"); return 0; }
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { MessageBox.Show(e.Exception.Message, "Не удалось выполнить действие", MessageBoxButtons.OK, MessageBoxIcon.Warning); };
                    using (var form = new MainForm(new Store(dataDir)))
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
                if (!args.Contains("--self-test")) MessageBox.Show("Не удалось открыть приложение.\n" + ex.Message + "\n\nДанные: " + dataDir, "Акты и оплаты", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}




