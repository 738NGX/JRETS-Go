using JRETS.Go.Core.Configuration;

namespace JRETS.Go.Core.Services;

public interface IMemoryOffsetsConfigurationLoader
{
    MemoryOffsetsConfiguration LoadFromFile(string filePath);
}
