using APTM.Gate.Core.Models;

namespace APTM.Gate.Core.Interfaces;

public interface ITagBufferService
{
    Task InsertRawTagsAsync(IReadOnlyList<RawTagFrame> frames, CancellationToken ct = default);
}
