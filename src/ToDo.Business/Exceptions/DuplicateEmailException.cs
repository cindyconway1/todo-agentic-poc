namespace ToDo.Business.Exceptions;

public sealed class DuplicateEmailException : Exception
{
    public DuplicateEmailException(string email)
        : base($"Email '{email}' is already registered.")
    {
        Email = email;
    }

    public string Email { get; }
}
