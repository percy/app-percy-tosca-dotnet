namespace AppPercyTosca.Core
{
    /// Raised when the session cannot serve something the Core asked of it — a capability the
    /// Tosca Mobile engine does not expose, or a driver operation it does not implement. Distinct
    /// from a generic failure so callers can report "not valid for this driver" rather than
    /// implying the snapshot itself was malformed.
    public class PercyException : Exception
    {
        public PercyException(string message) : base(message)
        {
        }

        public PercyException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
