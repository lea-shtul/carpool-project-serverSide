using Carpool.Core.Entities;
using Carpool.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Carpool.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUserRepository"/> (spec §52). Registered Scoped
/// alongside <see cref="CarpoolDbContext"/>.
///
/// Repositories only stage changes (<see cref="AddAsync"/>, <see cref="Update"/>); the
/// commit happens through <see cref="IUnitOfWork.SaveChangesAsync"/> so a Service can batch
/// several repositories into one transaction (spec §68). Read-only queries use
/// <c>AsNoTracking</c> (spec §20).
///
/// Email is matched exactly, consistent with the case-sensitive unique index configured in
/// Stage 4. Any normalisation (e.g. lower-casing on write) is a Service-layer concern.
/// </summary>
public class UserRepository : IUserRepository
{
    private readonly CarpoolDbContext _context;

    public UserRepository(CarpoolDbContext context)
    {
        _context = context;
    }

    /// <summary>Tracked load — the admin activate/deactivate flow mutates the returned entity.</summary>
    public Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        _context.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <summary>Read-only load for <c>GET /api/users/me</c> (spec §20).</summary>
    public Task<User?> GetByIdNoTrackingAsync(int id, CancellationToken cancellationToken) =>
        _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <summary>Read-only load for the login path (spec §20, §34).</summary>
    public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken) =>
        _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) =>
        _context.Users.AnyAsync(u => u.Email == email, cancellationToken);

    public async Task<IEnumerable<User>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.Users
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken) =>
        await _context.Users.AddAsync(user, cancellationToken);

    public void Update(User user) =>
        _context.Users.Update(user);
}
