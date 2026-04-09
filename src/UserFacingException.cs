namespace CodexMulti;

internal sealed class UserFacingException : Exception
{
    public UserFacingException(string message)
        : base(message)
    {
    }
}
