using Csla;
using Csla.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Api.Controllers;
using ToDo.Api.Dtos;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped (BE-10): a non-null PriorityId that is not in the Priorities lookup is rejected by
// the business layer (defense-in-depth ahead of the DB FK) and surfaces as the contractual 422
// { id, message, errors[], warnings[] } shape at the API; null and each seeded id (1/2/3) are
// accepted. The data-portal tests assert the InvalidPriorityException (and that nothing was
// written); the controller tests assert the exception maps to 422 with a PriorityId error entry
// on both create and update.
public class TodoItemPriorityValidationTests
{
    private static readonly Guid CurrentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OwnedListId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(CurrentUserId));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("priorityvalidation_" + Guid.NewGuid().ToString("N")),
            ServiceLifetime.Singleton);

        var provider = services.BuildServiceProvider();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        // EnsureCreated applies the Priorities HasData seed to the in-memory store.
        await ctx.Database.EnsureCreatedAsync();
        ctx.TodoLists.Add(new TodoList
        {
            Id = OwnedListId,
            OwnerUserId = CurrentUserId,
            ScopeTypeId = 1,
            ScopeEntityId = Guid.NewGuid(),
        });
        await ctx.SaveChangesAsync();

        return provider;
    }

    private static ItemsController BuildController(ServiceProvider provider) => new(
        provider.GetRequiredService<IDataPortal<TodoItemEdit>>(),
        provider.GetRequiredService<IDataPortal<TodoItemInfoList>>(),
        provider.GetRequiredService<IDataPortal<CompleteItemCommand>>(),
        provider.GetRequiredService<IDataPortal<AllItemsList>>());

    // AC "accepts null + each valid id": every legal PriorityId saves through the data portal.
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SaveItem_WithValidPriorityId_Succeeds(int? priorityId)
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var item = await portal.CreateAsync(OwnedListId);
        item.Title = "Valid priority";
        item.PriorityId = priorityId;
        item = await item.SaveAsync();

        Assert.Equal(priorityId, item.PriorityId);
    }

    // AC "rejects an unknown priorityId": the data portal throws InvalidPriorityException and
    // the item is NOT silently written.
    [Fact]
    public async Task CreateItem_WithUnknownPriorityId_ThrowsAndWritesNothing()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var item = await portal.CreateAsync(OwnedListId);
        item.Title = "Bad priority";
        item.PriorityId = 99;

        var ex = await Assert.ThrowsAsync<DataPortalException>(() => item.SaveAsync());
        var invalid = Assert.IsType<InvalidPriorityException>(ex.BusinessException);
        Assert.Equal(99, invalid.PriorityId);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.TodoItems.ToListAsync());
    }

    // Same defense on update: the existing row keeps its old priority.
    [Fact]
    public async Task UpdateItem_WithUnknownPriorityId_ThrowsAndKeepsOldValue()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var created = await portal.CreateAsync(OwnedListId);
        created.Title = "Keeps priority";
        created.PriorityId = 1;
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        fetched.PriorityId = 42;
        var ex = await Assert.ThrowsAsync<DataPortalException>(() => fetched.SaveAsync());
        Assert.IsType<InvalidPriorityException>(ex.BusinessException);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var row = await ctx.TodoItems.SingleAsync(i => i.Id == created.Id);
        Assert.Equal(1, row.PriorityId);
    }

    // AC "(422)": the controller maps the business-layer rejection to the contractual
    // { id, message, errors[], warnings[] } shape with a PriorityId error entry — on create...
    [Fact]
    public async Task CreateItem_ViaController_WithUnknownPriorityId_Returns422Shape()
    {
        var provider = await BuildProviderAsync();
        var controller = BuildController(provider);

        var result = await controller.CreateItem(OwnedListId, new CreateTodoItemRequest
        {
            Title = "Bad priority",
            PriorityId = 99,
        });

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var problem = Assert.IsType<ValidationProblemDto>(unprocessable.Value);
        Assert.NotEmpty(problem.Id);
        Assert.Equal("Validation failed.", problem.Message);
        var error = Assert.Single(problem.Errors);
        Assert.Equal(nameof(TodoItemEdit.PriorityId), error.Property);
        Assert.Contains("99", error.Message);
        Assert.Empty(problem.Warnings);
    }

    // ...and on update, where the item already exists.
    [Fact]
    public async Task UpdateItem_ViaController_WithUnknownPriorityId_Returns422Shape()
    {
        var provider = await BuildProviderAsync();
        var controller = BuildController(provider);

        var created = Assert.IsType<CreatedAtActionResult>(await controller.CreateItem(
            OwnedListId, new CreateTodoItemRequest { Title = "Starts valid", PriorityId = 2 }));
        var createdDto = Assert.IsType<TodoItemDto>(created.Value);

        var result = await controller.UpdateItem(createdDto.Id, new UpdateTodoItemRequest
        {
            Title = "Starts valid",
            PriorityId = 42,
        });

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var problem = Assert.IsType<ValidationProblemDto>(unprocessable.Value);
        var error = Assert.Single(problem.Errors);
        Assert.Equal(nameof(TodoItemEdit.PriorityId), error.Property);

        // The stored row still has the original priority.
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var row = await ctx.TodoItems.SingleAsync(i => i.Id == createdDto.Id);
        Assert.Equal(2, row.PriorityId);
    }

    // A valid create via the controller returns both priorityId and priorityName in the DTO.
    [Fact]
    public async Task CreateItem_ViaController_WithValidPriorityId_ReturnsIdAndName()
    {
        var provider = await BuildProviderAsync();
        var controller = BuildController(provider);

        var result = await controller.CreateItem(OwnedListId, new CreateTodoItemRequest
        {
            Title = "Good priority",
            PriorityId = 1,
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var dto = Assert.IsType<TodoItemDto>(created.Value);
        Assert.Equal(1, dto.PriorityId);
        Assert.Equal("High", dto.PriorityName);
    }
}
