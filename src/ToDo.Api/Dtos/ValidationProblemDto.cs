namespace ToDo.Api.Dtos;

// Shape mandated by .claude/rules/api.md's error model: { id, message, errors[], warnings[] }.
public sealed class ValidationProblemDto
{
    public string Id { get; set; } = "";
    public string Message { get; set; } = "";
    public List<ValidationErrorDto> Errors { get; set; } = [];
    public List<ValidationErrorDto> Warnings { get; set; } = [];
}

public sealed class ValidationErrorDto
{
    public string? Property { get; set; }
    public string Message { get; set; } = "";
}
