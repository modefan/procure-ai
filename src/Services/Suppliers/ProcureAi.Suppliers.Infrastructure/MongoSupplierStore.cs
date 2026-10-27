using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using ProcureAi.Suppliers.Application.Abstractions;
using ProcureAi.Suppliers.Domain;

namespace ProcureAi.Suppliers.Infrastructure;

/// <summary>The supplier catalog in MongoDB. Every call goes through <see cref="TenantCollection{T}"/>, so it only ever sees the caller's tenant.</summary>
internal sealed class MongoSupplierStore(TenantCollection<SupplierDocument> suppliers) : ISupplierStore
{
    public async Task<(IReadOnlyList<Supplier> Items, int Total)> ListAsync(
        string? search, int skip, int take, CancellationToken cancellationToken)
    {
        var filter = Builders<SupplierDocument>.Filter.Empty;
        if (search is not null)
        {
            // Escaped, so what the caller types is text and never a pattern.
            var pattern = new BsonRegularExpression(Regex.Escape(search), "i");
            filter = Builders<SupplierDocument>.Filter.Or(
                Builders<SupplierDocument>.Filter.Regex(s => s.Code, pattern),
                Builders<SupplierDocument>.Filter.Regex(s => s.Name, pattern));
        }

        var total = await suppliers.CountAsync(filter, cancellationToken);
        var page = await suppliers.Find(filter).SortBy(s => s.Code).Skip(skip).Limit(take).ToListAsync(cancellationToken);
        return ([.. page.Select(d => d.ToDomain())], (int)total);
    }

    public async Task<Supplier?> GetAsync(string code, CancellationToken cancellationToken)
    {
        var found = await suppliers.Find(s => s.Code == code).FirstOrDefaultAsync(cancellationToken);
        return found?.ToDomain();
    }
}
