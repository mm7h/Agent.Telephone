namespace Agent.Telephone.Abstractions.Configs
{
    public sealed class TelephoneConfigValidationException : Exception
    {
        public TelephoneConfigValidationException(IReadOnlyList<string> errors)
            : base($"Telephone configuration is invalid:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}")
        {
            Errors = errors;
        }

        public IReadOnlyList<string> Errors { get; }
    }
}
