using AutoMapper;
using Carpool.Core.Dtos.Users;
using Carpool.Core.Exceptions;
using Carpool.Core.Interfaces.Repositories;
using Carpool.Core.Interfaces.Services;

namespace Carpool.Service.Services;

/// <summary>
/// Implements <see cref="IUserService"/> (spec §41, §42). User self-service
/// (<c>GET /api/users/me</c>) and Admin user management. The Admin-only restriction on
/// <see cref="GetAllAsync"/> and <see cref="SetStatusAsync"/> is enforced by
/// <c>[Authorize(Roles = "Admin")]</c> on the controller (spec §41); this layer performs
/// existence checks and persistence.
/// </summary>
public class UserService : IUserService
{
    private readonly IUserRepository _userRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public UserService(IUserRepository userRepository, IUnitOfWork unitOfWork, IMapper mapper)
    {
        _userRepository = userRepository;
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<UserResponse> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdNoTrackingAsync(id, cancellationToken)
            ?? throw new NotFoundException($"User {id} was not found.");

        return _mapper.Map<UserResponse>(user);
    }

    public async Task<IEnumerable<UserResponse>> GetAllAsync(CancellationToken cancellationToken)
    {
        var users = await _userRepository.GetAllAsync(cancellationToken);
        return _mapper.Map<IEnumerable<UserResponse>>(users);
    }

    public async Task<UserResponse> SetStatusAsync(int userId, bool isActive, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException($"User {userId} was not found.");

        user.IsActive = isActive;
        _userRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return _mapper.Map<UserResponse>(user);
    }
}
