namespace Reparatio
{
    /// <summary>
    /// Result of a file-producing API call (Convert, BatchConvert, Merge, Append, Query).
    /// </summary>
    public sealed class ReparatioResult
    {
        /// <summary>Raw bytes of the converted / queried output file.</summary>
        public byte[] Content { get; }

        /// <summary>Suggested output filename from the Content-Disposition header.</summary>
        public string Filename { get; }

        /// <summary>
        /// Warning from X-Reparatio-Warning or X-Reparatio-Errors, or null.
        /// </summary>
        public string Warning { get; }

        public ReparatioResult(byte[] content, string filename, string warning = null)
        {
            Content  = content;
            Filename = filename;
            Warning  = warning;
        }
    }
}
