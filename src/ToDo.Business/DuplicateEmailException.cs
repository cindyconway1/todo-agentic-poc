namespace ToDo.Business;

public sealed class DuplicateEmailException : Exception
{
    public DuplicateEmailException(string email)
        : base($"Email '{email}' is already in use.")
    {
        Email = email;
    }

    public string Email { get; }
}
