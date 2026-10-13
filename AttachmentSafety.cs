using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace WorkLedger
{
    public static class AttachmentSafety
    {
        private static readonly HashSet<string> DocumentExtensions = new HashSet<string>(new[] {
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".rtf",
            ".odt", ".ods", ".odp", ".txt", ".csv", ".tsv",
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"
        }, StringComparer.OrdinalIgnoreCase);
        public static string ResolveForOpen(Store store, string relativePath)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (String.IsNullOrWhiteSpace(relativePath)) throw new InvalidOperationException("Некорректный путь вложения.");
            string[] parts = relativePath.Replace('\\', '/').Split('/');
            foreach (string part in parts)
            {
                if (String.IsNullOrEmpty(part) || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Any(Char.IsControl))
                    throw new InvalidOperationException("Некорректное имя вложения.");
                string device = part.Split('.')[0].ToUpperInvariant();
                if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" ||
                    (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] >= '1' && device[3] <= '9'))
                    throw new InvalidOperationException("Некорректное имя вложения.");
            }
            string path = store.ResolveAttachment(relativePath);
            string extension = Path.GetExtension(path);
            if (!DocumentExtensions.Contains(extension))
                throw new InvalidOperationException("Файл типа " + (String.IsNullOrEmpty(extension) ? "без расширения" : extension) +
                    " нельзя открыть из приложения. Поддерживаются PDF, документы Word и Excel, презентации, текст и изображения. Вложение сохранено в базе.");
            if (!File.Exists(path)) throw new FileNotFoundException("Прикреплённый файл не найден.", path);
            string current = store.DataDir;
            RejectLink(current);
            foreach (string part in parts) { current = Path.Combine(current, part); RejectLink(current); }
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new InvalidOperationException("Вложение должно быть файлом.");
            return path;
        }
        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Нельзя открыть вложение, являющееся ссылкой на другой файл или папку.");
        }
    }
}
