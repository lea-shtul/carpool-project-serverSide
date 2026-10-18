using Carpool.Core.Entities;

namespace Carpool.Core.Interfaces.Repositories;

/// <summary>
/// Persistence contract for <see cref="User"/>. Implemented in <c>Carpool.Data</c>;
/// the Service layer depends only on this interface (spec §52, §53).
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Read-only load (<c>AsNoTracking</c>) for <c>GET /api/users/me</c> (spec §20).</summary>
    Task<User?> GetByIdNoTrackingAsync(int id, CancellationToken cancellationToken);

    Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    Task<IEnumerable<User>> GetAllAsync(CancellationToken cancellationToken);

    Task AddAsync(User user, CancellationToken cancellationToken);

    /// <summary>Marks a tracked user as modified. Persisted via <see cref="IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Update(User user);
}
