using Microsoft.EntityFrameworkCore;

public static class ServiceExtensions
{
    public static async Task WithDb<TContext>(
        this IServiceScopeFactory factory,
        Func<TContext, Task> action)
        where TContext : DbContext
    {
        using var scope = factory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        await action(db);
    }
}