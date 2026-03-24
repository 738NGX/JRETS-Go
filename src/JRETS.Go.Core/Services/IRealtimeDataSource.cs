using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public interface IRealtimeDataSource
{
    RealtimeSnapshot GetSnapshot();
}
