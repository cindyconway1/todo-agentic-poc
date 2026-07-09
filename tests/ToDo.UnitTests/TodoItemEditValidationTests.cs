using System.Text.Json;
using Csla;
using Csla.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Api.Dtos;
using ToDo.Business;

namespace ToDo.UnitTests;

// AC-mapped: to-do item validation (BE-07 unit test list) — title required + trimmed 1–200
// (AC 21), description optional ≤200 (AC 22), a title-only item is valid (AC 23), and a
// malformed date is rejected (AC 24). Exercises TodoItemEdit's business rules through a CSLA
// local data portal; no database is touched because [Create] only assigns ids and runs rules.
public class TodoItemEditValidationTests
{
    private static async Task<TodoItemEdit> NewItemAsync(string? title, string? description = null, DateOnly? dueDate = null)
    {
        var services = new ServiceCollection();
        services.AddCsla();
        var provider = services.BuildServiceProvider();
        var item = await provider.GetRequiredService<IDataPortal<TodoItemEdit>>().CreateAsync(Guid.NewGuid());
        item.Title = title ?? "";
        item.Description = description;
        item.DueDate = dueDate;
        return item;
    }

    // AC 21: an item without a title is invalid — including whitespace-only titles, which the
    // trimming setter reduces to empty before the Required rule runs.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Title_WhenMissingOrWhitespace_IsRejected(string title)
    {
        var item = await NewItemAsync(title);

        Assert.False(item.IsValid);
        Assert.Contains(item.BrokenRulesCollection, r => r.Property == nameof(TodoItemEdit.Title));
    }

    // AC 21/23 boundary: 1–200 accepted, 201 rejected.
    [Theory]
    [InlineData(1, true)]    // minimum accepted
    [InlineData(200, true)]  // maximum accepted
    [InlineData(201, false)] // above maximum rejected
    public async Task Title_LengthRule_IsEnforced(int length, bool expectedValid)
    {
        var item = await NewItemAsync(new string('a', length));

        Assert.Equal(expectedValid, item.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(item.BrokenRulesCollection, r => r.Property == nameof(TodoItemEdit.Title));
        }
    }

    // "Required + trimmed, 1–200": surrounding whitespace doesn't count against the limit and
    // is not persisted — 200 significant chars padded with spaces is still valid.
    [Fact]
    public async Task Title_IsTrimmed_BeforeRulesJudgeIt()
    {
        var item = await NewItemAsync("  " + new string('a', 200) + "  ");

        Assert.True(item.IsValid);
        Assert.Equal(new string('a', 200), item.Title);
    }

    // AC 22: description is optional but capped at 200.
    [Theory]
    [InlineData(0, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public async Task Description_LengthRule_IsEnforced(int length, bool expectedValid)
    {
        var item = await NewItemAsync("Valid title", new string('d', length));

        Assert.Equal(expectedValid, item.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(item.BrokenRulesCollection, r => r.Property == nameof(TodoItemEdit.Description));
        }
    }

    // AC 23: title alone — no description, no due date — is a valid item.
    [Fact]
    public async Task TitleOnlyItem_IsValid()
    {
        var item = await NewItemAsync("Buy oranges");

        Assert.True(item.IsValid);
        Assert.Null(item.Description);
        Assert.Null(item.DueDate);
    }

    [Fact]
    public async Task TitleWithDescriptionAndDueDate_IsValid()
    {
        var item = await NewItemAsync("Buy oranges", "the small ones", new DateOnly(2026, 8, 1));

        Assert.True(item.IsValid);
    }

    // AC 24: DueDate is DateOnly?, so "must be a valid date" is enforced at the JSON boundary —
    // an impossible or malformed date never reaches the business object; binding rejects it
    // (the pipeline returns 400, see ItemsPipelineSmokeTests).
    [Theory]
    [InlineData("2026-02-30")]  // impossible day
    [InlineData("2026-13-01")]  // impossible month
    [InlineData("not-a-date")]  // malformed
    public void DueDate_MalformedOrImpossibleDate_IsRejectedByJsonBinding(string dueDate)
    {
        var json = $"{{\"title\":\"Valid\",\"dueDate\":\"{dueDate}\"}}";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CreateTodoItemRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void MaxLengths_ConstantsMatchSpec()
    {
        // Guards the spec'd column widths (nvarchar(200) both) against silent drift.
        Assert.Equal(200, TodoItemEdit.MaxTitleLength);
        Assert.Equal(200, TodoItemEdit.MaxDescriptionLength);
    }
}
