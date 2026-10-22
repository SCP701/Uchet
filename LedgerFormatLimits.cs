namespace WorkLedger
{
    internal static class LedgerFormatLimits
    {
        internal const int MaxDocumentCharacters = 32000000;
        // UTF-8 needs at most three bytes per UTF-16 code unit, including escaped JSON text.
        internal const int MaxDocumentBytes = MaxDocumentCharacters * 3;
    }
}
