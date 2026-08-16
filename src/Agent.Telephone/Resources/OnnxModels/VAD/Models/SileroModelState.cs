namespace Agent.Telephone.Resources.OnnxModels.VAD.Models
{
    /// <summary>
    /// Per-session state for Silero VAD v4 model inference.
    /// Each client session should have its own instance for lock-free concurrent access.
    /// </summary>
    internal sealed class SileroModelState
    {
        /// <summary>
        /// Hidden state tensor for LSTM. Shape: [2, 1, 64] for 16kHz.
        /// </summary>
        public float[] HiddenState { get; private set; }

        /// <summary>
        /// Cell state tensor for LSTM. Shape: [2, 1, 64] for 16kHz.
        /// </summary>
        public float[] CellState { get; private set; }

        /// <summary>
        /// Last sample rate used for validation.
        /// </summary>
        public int LastSampleRate { get; set; }

        private readonly int _stateSize;

        /// <summary>
        /// Creates a new model state for 16kHz input.
        /// </summary>
        /// <param name="sampleRate">Sample rate (16000)</param>
        public SileroModelState(int sampleRate)
        {
            if (sampleRate != 16000)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "SileroNative 仅支持 16000Hz 输入。");
            }

            this._stateSize = 64;
            this.HiddenState = new float[2 * 1 * this._stateSize];
            this.CellState = new float[2 * 1 * this._stateSize];
            this.LastSampleRate = sampleRate;
        }

        /// <summary>
        /// Updates the hidden state with new values from model output.
        /// </summary>
        public void UpdateHiddenState(float[] newState)
        {
            if (newState.Length != this.HiddenState.Length)
            {
                throw new ArgumentException(string.Format("隐藏状态大小不匹配。预期 {0}，实际 {1}", this.HiddenState.Length, newState.Length));
            }
            Array.Copy(newState, this.HiddenState, newState.Length);
        }

        /// <summary>
        /// Updates the cell state with new values from model output.
        /// </summary>
        public void UpdateCellState(float[] newState)
        {
            if (newState.Length != this.CellState.Length)
            {
                throw new ArgumentException(string.Format("单元状态大小不匹配。预期 {0}，实际 {1}", this.CellState.Length, newState.Length));
            }
            Array.Copy(newState, this.CellState, newState.Length);
        }

        /// <summary>
        /// Resets the model state to initial values (zeros).
        /// Call this when starting a new audio stream or after errors.
        /// </summary>
        public void Reset()
        {
            Array.Clear(this.HiddenState, 0, this.HiddenState.Length);
            Array.Clear(this.CellState, 0, this.CellState.Length);
        }
    }
}
