namespace Agent.Telephone.Common.Exceptions
{
    internal sealed class SessionNotInitializedException : InvalidOperationException
    {
        public SessionNotInitializedException() : base("The call session has not been initialized.") { }
    }
}
