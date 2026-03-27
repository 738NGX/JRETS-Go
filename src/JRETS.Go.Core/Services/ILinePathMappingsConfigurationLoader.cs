using JRETS.Go.Core.Configuration;

namespace JRETS.Go.Core.Services;

public interface ILinePathMappingsConfigurationLoader
{
    LinePathMappingsConfiguration LoadFromFile(string filePath);
}
