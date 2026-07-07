namespace ToDo.Api.Dtos;

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

public sealed record UserDto(Guid Id, string Email);

public sealed record ValidationErrorDto(string Id, string Message, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
