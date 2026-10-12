using System;
using System.Collections.Generic;
using System.Text;

namespace WorkLedger
{
    public static class FilterText
    {
        public static bool Matches(IEnumerable<string> values, string query)
        {
            string normalizedQuery = Normalize(query);
            if (normalizedQuery.Length == 0) return true;
            if (values == null) return false;

            StringBuilder combined = new StringBuilder();
            foreach (string value in values)
            {
                string normalizedValue = Normalize(value);
                if (normalizedValue.Length == 0) continue;
                if (combined.Length > 0) combined.Append(' ');
                combined.Append(normalizedValue);
            }

            string text = combined.ToString();
            string[] tokens = normalizedQuery.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                if (text.IndexOf(token, StringComparison.Ordinal) < 0) return false;
            }
            return true;
        }

        private static string Normalize(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            string normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('\u0451', '\u0435');
            StringBuilder result = new StringBuilder(normalized.Length);
            bool pendingSpace = false;
            foreach (char character in normalized)
            {
                if (Char.IsWhiteSpace(character))
                {
                    pendingSpace = result.Length > 0;
                }
                else
                {
                    if (pendingSpace) result.Append(' ');
                    result.Append(character);
                    pendingSpace = false;
                }
            }
            return result.ToString();
        }
    }
}