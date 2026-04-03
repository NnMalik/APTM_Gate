namespace APTM.Gate.Core.Interfaces;

public interface IGateStatusProvider
{
    bool IsActive { get; }
    void SetActive(bool active);
    event Action? StatusChanged;
}
