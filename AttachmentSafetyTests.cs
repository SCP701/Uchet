using System;
using System.IO;
using System.Linq;
namespace WorkLedger
{
    public static class AttachmentSafetyTests
    {
        private static void Assert(bool value, string reason) { if (!value) throw new Exception("Attachment safety test failed: " + reason); }
        private static void Reject(Store store, string relative)
        {
            bool rejected = false;
            try { AttachmentSafety.ResolveForOpen(store, relative); }
            catch (InvalidOperationException) { rejected = true; }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "unsafe launch rejected: " + relative);
        }
        public static void Run(string root)
        {
            Directory.CreateDirectory(root);
            var store = new Store(Path.Combine(root, "data"));
            foreach (string ext in new[] { ".txt", ".PDF", ".doc", ".docx", ".xlsx", ".jpg", ".csv", ".rtf", ".ods" })
            {
                string input = Path.Combine(root, "ordinary-act" + ext);
                File.WriteAllText(input, "Benign format fixture, never execute.");
                string relative = store.ImportAttachment(input);
                Assert(AttachmentSafety.ResolveForOpen(store, relative) == store.ResolveAttachment(relative), "ordinary document resolves: " + ext);
            }
            string unsafeRelative = "";
            foreach (string ext in new[] { ".cmd", ".EXE", ".bat", ".ps1", ".vbs", ".js", ".lnk", ".url", ".html", ".hta", ".scr", ".msi", ".svg", ".zip", ".pdf.cmd", "" })
            {
                string input = Path.Combine(root, "blocked" + ext);
                File.WriteAllText(input, "Benign fixture, never execute.");
                string relative = store.ImportAttachment(input);
                if (ext == ".pdf.cmd") unsafeRelative = relative;
                Reject(store, relative);
                Assert(File.Exists(store.ResolveAttachment(relative)), "blocked attachment retained");
            }
            foreach (string relative in new[] {
                "attachments/a.cmd.", "attachments/a.cmd ", "attachments/a.exe:document.pdf",
                "attachments/a.pdf:payload.cmd", "attachments/a.txt::$DATA", "attachments/NUL.txt",
                "attachments/a.exe\".txt", "attachments/../attachments/a.txt", "attachments//a.txt",
                "attachments/a.txt\n", "attachments/a\u0001.txt" }) Reject(store, relative);
            var doc = new Record("document");
            doc.Set("title", "Скан акта.pdf"); doc.Set("date", "2026-10-13");
            doc.Set("status", "draft"); doc.Set("filePath", unsafeRelative); store.Upsert(doc);
            byte[] ledger = File.ReadAllBytes(store.DataPath);
            long revision = store.Revision;
            var legacy = new Store(store.DataDir);
            Reject(legacy, unsafeRelative);
            Assert(ledger.SequenceEqual(File.ReadAllBytes(store.DataPath)) && revision == legacy.Revision, "opening rejection preserves bytes and revision");
            string backup = Path.Combine(root, "legacy-backup.zip"), restored = Path.Combine(root, "restored");
            BackupArchive.Create(store.DataDir, backup);
            BackupArchive.Restore(backup, restored, MainForm.ValidateSnapshot);
            var restoredStore = new Store(restored);
            Assert(restoredStore.Find(doc.Id).Get("title") == "Скан акта.pdf", "legacy metadata restored");
            Assert(File.ReadAllBytes(restoredStore.ResolveAttachment(unsafeRelative)).SequenceEqual(File.ReadAllBytes(store.ResolveAttachment(unsafeRelative))), "blocked attachment backup bytes preserved");
            Reject(restoredStore, unsafeRelative);
            string ordinary = restoredStore.Find(doc.Id).Get("filePath");
            Assert(ordinary == unsafeRelative, "existing record remains associated with retained file");
        }
    }
}
