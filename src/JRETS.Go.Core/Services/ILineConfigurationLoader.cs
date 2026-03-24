using JRETS.Go.Core.Configuration;

namespace JRETS.Go.Core.Services;

public interface ILineConfigurationLoader
{
    LineConfiguration LoadFromFile(string filePath);
}
