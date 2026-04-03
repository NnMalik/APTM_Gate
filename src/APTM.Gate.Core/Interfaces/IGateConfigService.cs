using APTM.Gate.Core.Models;

namespace APTM.Gate.Core.Interfaces;

public interface IGateConfigService
{
    Task<ConfigResult> ApplyConfigAsync(ConfigPackageDto config, CancellationToken ct = default);
}
