using APTM.Gate.Core.Interfaces;

namespace APTM.Gate.Infrastructure.Services;

public sealed class GateStatusProvider : IGateStatusProvider
{
    private volatile bool _isActive;

    public bool IsActive => _isActive;

    public void SetActive(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        StatusChanged?.Invoke();
    }

    public event Action? StatusChanged;
}
