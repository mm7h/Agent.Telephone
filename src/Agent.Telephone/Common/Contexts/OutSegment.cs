namespace Agent.Telephone.Common.Contexts
{
    internal class OutSegment
    {
        private string _content;

        public OutSegment()
        {
            this._content = string.Empty;
            this.IsFirstSegment = false;
            this.IsLastSegment = false;
        }

        /// <summary>
        /// 段落内容
        /// </summary>
        public string Content => _content;

        /// <summary>
        /// 是否为第一段
        /// </summary>
        public bool IsFirstSegment { get; set; }

        /// <summary>
        /// 是否为最后一段
        /// </summary>
        public bool IsLastSegment { get; set; }

        /// <summary>
        /// 段落Id
        /// </summary>
        public string? ParagraphId { get; set; }

        /// <summary>
        /// 句子Id
        /// </summary>
        public string? SentenceId { get; set; }

        public void Initialize(string content, string? paragraphId = null, string? sentenceId = null)

        {
            this._content = content;
            this.IsFirstSegment = false;
            this.IsLastSegment = false;
            this.ParagraphId = paragraphId;
            this.SentenceId = sentenceId;
        }

        public void Initialize(string content, bool isFirst, bool isLast, string? paragraphId = null, string? sentenceId = null)
        {
            this._content = content;
            this.IsFirstSegment = isFirst;
            this.IsLastSegment = isLast;
            this.ParagraphId = paragraphId;
            this.SentenceId = sentenceId;
        }

        public virtual void Reset()
        {
            this._content = string.Empty;
            this.IsFirstSegment = false;
            this.IsLastSegment = false;
            this.SentenceId = null;
            this.ParagraphId = null;
        }

    }
}
